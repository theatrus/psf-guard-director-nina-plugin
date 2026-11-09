using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DirectorUploadClientTests : IDisposable
{
    private static readonly Guid Profile = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Rig = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly DirectorUploadDestination Destination = new(new Uri("https://psf.example/base/"), Profile, Rig);
    private readonly string root = Directory.CreateTempSubdirectory("director-upload-client-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }

    private static DirectorUploadClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(Destination, _ => ValueTask.FromResult<string?>("psfdrc_test"), new Handler(send));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task StateReadsEnabledReasonAndLimitWithTheRigPairingHeaders()
    {
        HttpRequestMessage? seen = null;
        using var client = Client(request =>
        {
            seen = request;
            return Task.FromResult(Json(HttpStatusCode.OK, """{"success":true,"data":{"enabled":false,"reason":"Image uploads are off for this database.","max_bytes":537919488,"later":1},"error":null}"""));
        });

        var state = await client.GetStateAsync(default);

        Assert.Equal(new DirectorUploadState(DirectorUploadStateKind.Disabled, "Image uploads are off for this database.", 537919488), state);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal($"https://psf.example/base/api/director/v1/rigs/{Rig:D}/uploads", seen.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer psfdrc_test", seen.Headers.Authorization!.ToString());
        Assert.Equal(Profile.ToString("D"), Assert.Single(seen.Headers.GetValues("X-PSF-Director-Profile")));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"success":true,"data":{"enabled":true,"max_bytes":100},"error":null}""", DirectorUploadStateKind.Enabled)]
    [InlineData(HttpStatusCode.Forbidden, """{"success":false,"data":null,"error":"no"}""", DirectorUploadStateKind.Unsupported)]
    [InlineData(HttpStatusCode.NotFound, "", DirectorUploadStateKind.Unsupported)]
    [InlineData(HttpStatusCode.MethodNotAllowed, "", DirectorUploadStateKind.Unsupported)]
    [InlineData(HttpStatusCode.OK, """{"success":true,"data":{"rig":"x"},"error":null}""", DirectorUploadStateKind.Unsupported)]
    [InlineData(HttpStatusCode.OK, """{"success":true,"data":{"enabled":"yes"},"error":null}""", DirectorUploadStateKind.Unsupported)]
    [InlineData(HttpStatusCode.OK, "not json", DirectorUploadStateKind.Unsupported)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", DirectorUploadStateKind.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "", DirectorUploadStateKind.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, "", DirectorUploadStateKind.Unavailable)]
    public async Task StateClassifiesEachAnswer(HttpStatusCode status, string body, DirectorUploadStateKind expected)
    {
        using var client = Client(_ => Task.FromResult(Json(status, body)));
        Assert.Equal(expected, (await client.GetStateAsync(default)).Kind);
    }

    [Fact]
    public async Task AConnectionFailureLeavesTheStateUnavailable()
    {
        using var client = Client(_ => throw new HttpRequestException("refused"));
        Assert.Equal(DirectorUploadStateKind.Unavailable, (await client.GetStateAsync(default)).Kind);
    }

    [Fact]
    public async Task UploadSendsOneImagePartWithItsDigest()
    {
        var path = Path.Combine(root, "M31_L_300s_0001.fits");
        var bytes = Encoding.ASCII.GetBytes("SIMPLE  =                    T");
        await File.WriteAllBytesAsync(path, bytes);
        string? digest = null, body = null;
        HttpRequestMessage? seen = null;
        using var client = Client(async request =>
        {
            seen = request;
            digest = Assert.Single(request.Headers.GetValues("X-Content-SHA256"));
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"success":true,"data":{"filename":"M31_L_300s_0001.fits","already_present":true},"error":null}""");
        });

        var answer = await client.UploadAsync(path, default);

        Assert.Equal(DirectorUploadOutcome.Stored, answer.Outcome);
        Assert.True(answer.AlreadyPresent);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal($"https://psf.example/base/api/director/v1/rigs/{Rig:D}/images", seen.RequestUri!.AbsoluteUri);
        Assert.Equal(Profile.ToString("D"), Assert.Single(seen.Headers.GetValues("X-PSF-Director-Profile")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), digest);
        Assert.Contains("name=image", body);
        Assert.Contains("filename=M31_L_300s_0001.fits", body);
        Assert.DoesNotContain(root, body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, DirectorUploadOutcome.Refused)]
    [InlineData(HttpStatusCode.Gone, DirectorUploadOutcome.Refused)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, DirectorUploadOutcome.Refused)]
    [InlineData(HttpStatusCode.Conflict, DirectorUploadOutcome.Retry)]
    [InlineData(HttpStatusCode.Forbidden, DirectorUploadOutcome.Paused)]
    [InlineData(HttpStatusCode.Unauthorized, DirectorUploadOutcome.Paused)]
    [InlineData(HttpStatusCode.NotFound, DirectorUploadOutcome.Unsupported)]
    [InlineData(HttpStatusCode.MethodNotAllowed, DirectorUploadOutcome.Unsupported)]
    [InlineData(HttpStatusCode.InternalServerError, DirectorUploadOutcome.Retry)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DirectorUploadOutcome.Retry)]
    [InlineData(HttpStatusCode.TooManyRequests, DirectorUploadOutcome.Retry)]
    public async Task UploadMapsEachStatusToTheContractsAction(HttpStatusCode status, DirectorUploadOutcome expected)
    {
        var path = Path.Combine(root, "frame.fits");
        await File.WriteAllTextAsync(path, "x");
        using var client = Client(_ => Task.FromResult(Json(status, """{"success":false,"data":null,"error":"frame was removed on purpose"}""")));

        var answer = await client.UploadAsync(path, default);

        Assert.Equal(expected, answer.Outcome);
        Assert.Contains($"HTTP {(int)status}: frame was removed on purpose", answer.Detail);
    }

    [Fact]
    public async Task AConnectionFailureOrTimeoutIsRetried()
    {
        var path = Path.Combine(root, "frame.fits");
        await File.WriteAllTextAsync(path, "x");
        using var failing = Client(_ => throw new HttpRequestException("reset"));
        Assert.Equal(DirectorUploadOutcome.Retry, (await failing.UploadAsync(path, default)).Outcome);
    }

    [Theory]
    [InlineData("a:b.fits")]
    [InlineData("a<b.fits")]
    [InlineData("a|b.fits")]
    [InlineData("frame.fits.")]
    [InlineData("frame.fits ")]
    [InlineData("tab\there.fits")]
    public void NamesTheServerWouldRefuseAreCaught(string name)
    {
        Assert.NotNull(DirectorUploadPolicy.FileNameProblem(name));
        Assert.Null(DirectorUploadPolicy.FileNameProblem("M31_L_300s_2026-11-08_0001.fits"));
        Assert.Null(DirectorUploadPolicy.FileNameProblem(new string('a', 235) + ".fits"));
        Assert.NotNull(DirectorUploadPolicy.FileNameProblem(new string('a', 236) + ".fits"));
    }

    [Fact]
    public async Task ANameTheServerWouldRefuseIsRefusedWithoutARequest()
    {
        var requests = 0;
        using var client = Client(_ => { requests++; return Task.FromResult(Json(HttpStatusCode.OK, "{}")); });
        // The client checks the name before it opens the file.
        var answer = await client.UploadAsync(Path.Combine(root, new string('a', 241) + ".fits"), default);
        Assert.Equal(DirectorUploadOutcome.Refused, answer.Outcome);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void RetryDelayStartsAtFiveSecondsAndDoublesToTenMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), DirectorUploadPolicy.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), DirectorUploadPolicy.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(320), DirectorUploadPolicy.RetryDelay(7));
        Assert.Equal(TimeSpan.FromMinutes(10), DirectorUploadPolicy.RetryDelay(8));
        Assert.Equal(TimeSpan.FromMinutes(10), DirectorUploadPolicy.RetryDelay(int.MaxValue));
    }
}

public sealed class DirectorUploadQueueTests : IDisposable
{
    private static readonly DirectorUploadDestination Destination = new(new Uri("https://psf.example/"),
        Guid.Parse("11111111-2222-4333-8444-555555555555"), Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"));
    private readonly string root = Directory.CreateTempSubdirectory("director-upload-queue-").FullName;
    private readonly FakeClock clock = new();
    private readonly FakeServer server = new();
    private readonly List<string> log = [];

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string QueueRoot => Path.Combine(root, "queue");

    private DirectorUploadQueue Queue(Func<DirectorUploadDestination, IDirectorUploadServer?>? connect = null) =>
        new(QueueRoot, connect ?? (_ => server), log.Add, clock);

    private string Frame(string name = "frame.fits", int bytes = 10)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private int Jobs(string? folder = null) => Directory.Exists(Path.Combine(QueueRoot, folder ?? ""))
        ? Directory.GetFiles(Path.Combine(QueueRoot, folder ?? ""), "*.json").Length : 0;

    [Fact]
    public async Task AQueuedFrameUploadsAndItsJobIsRemoved()
    {
        await using var queue = Queue();
        var frame = Frame();
        Assert.True(await queue.EnqueueAsync(Destination, frame, "capture-1", default));
        Assert.Equal(1, Jobs());

        await queue.ProcessDueJobsAsync(default);

        Assert.Equal([frame], server.Uploads);
        Assert.Equal(0, Jobs());
        Assert.True(File.Exists(frame));
    }

    [Fact]
    public async Task TheStateIsAskedAtMostOnceAMinute()
    {
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(1, server.StateCalls);
        Assert.Equal(2, server.Uploads.Count);

        clock.Advance(TimeSpan.FromSeconds(30));
        await queue.EnqueueAsync(Destination, Frame("c.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(1, server.StateCalls);

        clock.Advance(TimeSpan.FromSeconds(31));
        await queue.EnqueueAsync(Destination, Frame("d.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, server.StateCalls);
    }

    [Fact]
    public async Task AnOlderServerGetsNoUploadAndNoNewJobsUntilARecheck()
    {
        server.States.Enqueue(new(DirectorUploadStateKind.Unsupported));
        await using var queue = Queue();
        Assert.True(await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default));

        await queue.ProcessDueJobsAsync(default);
        clock.Advance(TimeSpan.FromHours(2));
        await queue.ProcessDueJobsAsync(default);

        Assert.Empty(server.Uploads);
        Assert.Equal(1, server.StateCalls);
        Assert.Equal(1, Jobs());
        Assert.False(await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default));
        Assert.Equal(1, Jobs());
        Assert.Single(log, line => line.Contains("does not take uploads from Director"));

        queue.RequestRecheck();
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, server.StateCalls);
        Assert.Single(server.Uploads);
    }

    [Fact]
    public async Task UploadsOffKeepsQueuedFramesAndQueuesNoNewOnes()
    {
        server.States.Enqueue(new(DirectorUploadStateKind.Disabled, "Image uploads are off for this database."));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);

        await queue.ProcessDueJobsAsync(default);

        Assert.Empty(server.Uploads);
        Assert.Equal(1, Jobs());
        Assert.False(await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default));
        Assert.Equal("Image uploads are off for this database.", queue.Summarize(Destination).State!.Reason);

        clock.Advance(TimeSpan.FromSeconds(61));
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(server.Uploads);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task AnUnreachableServerKeepsFramesAndQueuesNewOnes()
    {
        server.States.Enqueue(new(DirectorUploadStateKind.Unavailable, "timeout"));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);

        await queue.ProcessDueJobsAsync(default);
        Assert.True(await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default));
        Assert.Equal(2, Jobs());
        Assert.Empty(server.Uploads);

        clock.Advance(TimeSpan.FromSeconds(61));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, server.Uploads.Count);
    }

    [Fact]
    public async Task ARefusedFrameIsHeldWithItsReasonAndCanBeRetriedOrForgotten()
    {
        server.Answers.Enqueue(new(DirectorUploadOutcome.Refused, "PSF Guard answered HTTP 410: removed on purpose"));
        await using var queue = Queue();
        var frame = Frame();
        await queue.EnqueueAsync(Destination, frame, null, default);

        await queue.ProcessDueJobsAsync(default);
        await queue.ProcessDueJobsAsync(default);

        Assert.Single(server.Uploads);
        Assert.Equal(0, Jobs());
        Assert.Equal(1, Jobs(DirectorUploadQueue.RefusedFolderName));
        Assert.Contains("removed on purpose", File.ReadAllText(Directory.GetFiles(Path.Combine(QueueRoot, "refused"))[0]));
        Assert.Equal(new DirectorUploadSummary(0, 1, new(DirectorUploadStateKind.Enabled, null, null)), queue.Summarize(Destination));
        Assert.Contains(log, line => line.Contains("refused frame.fits"));

        Assert.Equal(1, await queue.RetryRefusedAsync(default));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, server.Uploads.Count);
        Assert.Equal(0, Jobs());

        server.Answers.Enqueue(new(DirectorUploadOutcome.Refused, "bad name"));
        await queue.EnqueueAsync(Destination, frame, null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(1, await queue.ForgetRefusedAsync(default));
        Assert.Equal(0, Jobs(DirectorUploadQueue.RefusedFolderName));
        Assert.True(File.Exists(frame));
    }

    [Fact]
    public async Task APausedServerKeepsTheJobAndStopsSendingForAMinute()
    {
        server.Answers.Enqueue(new(DirectorUploadOutcome.Paused, "PSF Guard answered HTTP 403"));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default);

        await queue.ProcessDueJobsAsync(default);
        Assert.Single(server.Uploads);
        Assert.Equal(2, Jobs());

        clock.Advance(TimeSpan.FromSeconds(30));
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(server.Uploads);

        clock.Advance(TimeSpan.FromSeconds(31));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(3, server.Uploads.Count);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task ARouteAnOlderServerLacksStopsUploadsAndKeepsTheQueue()
    {
        server.Answers.Enqueue(new(DirectorUploadOutcome.Unsupported, "PSF Guard answered HTTP 404"));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default);

        await queue.ProcessDueJobsAsync(default);
        clock.Advance(TimeSpan.FromHours(1));
        await queue.ProcessDueJobsAsync(default);

        Assert.Single(server.Uploads);
        Assert.Equal(2, Jobs());
        Assert.Equal(DirectorUploadStateKind.Unsupported, queue.Summarize(Destination).State!.Kind);
    }

    [Fact]
    public async Task APassingFailureBacksOffAndSurvivesARestart()
    {
        server.Answers.Enqueue(new(DirectorUploadOutcome.Retry, "PSF Guard answered HTTP 503"));
        server.Answers.Enqueue(new(DirectorUploadOutcome.Retry, "PSF Guard answered HTTP 503"));
        var frame = Frame();
        await using (var first = Queue())
        {
            await first.EnqueueAsync(Destination, frame, null, default);
            await first.ProcessDueJobsAsync(default);
            Assert.Single(server.Uploads);
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.True(await first.ProcessDueJobsAsync(default) <= TimeSpan.FromSeconds(1));
            Assert.Single(server.Uploads);
        }

        await using var restarted = Queue();
        clock.Advance(TimeSpan.FromSeconds(1));
        await restarted.ProcessDueJobsAsync(default);
        Assert.Equal(2, server.Uploads.Count);
        clock.Advance(TimeSpan.FromSeconds(9));
        await restarted.ProcessDueJobsAsync(default);
        Assert.Equal(2, server.Uploads.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await restarted.ProcessDueJobsAsync(default);
        Assert.Equal(3, server.Uploads.Count);
        Assert.Equal(0, Jobs());
        // Once for each queue instance, that is once per N.I.N.A. session.
        Assert.Equal(2, log.Count(line => line.Contains("tries again")));
    }

    [Fact]
    public async Task AFrameGoneFromDiskDropsItsJob()
    {
        await using var queue = Queue();
        var frame = Frame();
        await queue.EnqueueAsync(Destination, frame, null, default);
        File.Delete(frame);

        await queue.ProcessDueJobsAsync(default);

        Assert.Empty(server.Uploads);
        Assert.Equal(0, Jobs());
        Assert.Single(log, line => line.Contains("no longer on disk"));
    }

    [Fact]
    public async Task AFrameLargerThanTheServerTakesIsRefusedOrNotQueued()
    {
        server.States.Enqueue(new(DirectorUploadStateKind.Enabled, null, 100));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("big.fits", 200), null, default);

        await queue.ProcessDueJobsAsync(default);

        Assert.Empty(server.Uploads);
        Assert.Equal(1, Jobs(DirectorUploadQueue.RefusedFolderName));
        Assert.False(await queue.EnqueueAsync(Destination, Frame("big2.fits", 200), null, default));
        Assert.True(await queue.EnqueueAsync(Destination, Frame("small.fits", 50), null, default));
    }

    [Fact]
    public async Task AnUnpairedOrFailingConnectionLeavesJobsWaiting()
    {
        await using (var unpaired = Queue(_ => null))
        {
            await unpaired.EnqueueAsync(Destination, Frame(), null, default);
            await unpaired.ProcessDueJobsAsync(default);
        }
        await using (var failing = Queue(_ => throw new InvalidOperationException("Director credential is invalid.")))
            await failing.ProcessDueJobsAsync(default);
        Assert.Equal(1, Jobs());
        Assert.Empty(server.Uploads);
    }

    [Fact]
    public async Task AnUnreadableJobIsSetAsideAndOthersStillUpload()
    {
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame(), null, default);
        await File.WriteAllTextAsync(Path.Combine(QueueRoot, "00000000000000000000000000000000.json"), "{ not json");

        await queue.ProcessDueJobsAsync(default);

        Assert.Single(server.Uploads);
        Assert.Equal(1, Jobs(DirectorUploadQueue.BlockedFolderName));
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task OfferNeverThrowsAndQueuesInTheBackground()
    {
        await using var queue = Queue();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => changed.TrySetResult();
        queue.Offer(Destination, Frame(), "capture-1");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Jobs());
        queue.Offer(Destination, "\0bad", null);
    }

    private sealed class FakeServer : IDirectorUploadServer
    {
        public Queue<DirectorUploadState> States { get; } = new();
        public Queue<DirectorUploadAnswer> Answers { get; } = new();
        public List<string> Uploads { get; } = [];
        public int StateCalls { get; private set; }

        public Task<DirectorUploadState> GetStateAsync(CancellationToken token)
        {
            StateCalls++;
            return Task.FromResult(States.Count > 0 ? States.Dequeue() : new DirectorUploadState(DirectorUploadStateKind.Enabled));
        }

        public Task<DirectorUploadAnswer> UploadAsync(string path, CancellationToken token)
        {
            Uploads.Add(path);
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new DirectorUploadAnswer(DirectorUploadOutcome.Stored, "Stored"));
        }

        public void Dispose() { }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
