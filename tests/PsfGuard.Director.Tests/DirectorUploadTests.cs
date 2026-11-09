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
    private static readonly DirectorUploadDestination Destination = new(new Uri("https://psf.example/base/"), new(Guid.NewGuid(), Guid.NewGuid(), Rig, Profile));
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

    [Theory]
    [InlineData("<html>Sign in</html>")]
    [InlineData("{}")]
    [InlineData("{\"success\":false,\"data\":null}")]
    [InlineData("{\"success\":true,\"data\":{\"filename\":\"other.fits\",\"already_present\":false}}")]
    [InlineData("{\"success\":true,\"data\":{\"filename\":\"frame.fits\"}}")]
    public async Task AnUnconfirmedSuccessRetainsTheUpload(string reply)
    {
        var path = Path.Combine(root, "frame.fits");
        await File.WriteAllTextAsync(path, "x");
        using var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, reply)));
        Assert.Equal(DirectorUploadOutcome.Retry, (await client.UploadAsync(path, default)).Outcome);
    }

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
    [InlineData(HttpStatusCode.Forbidden, """{"success":false,"data":null,"error":"no"}""", DirectorUploadStateKind.Unavailable)]
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

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "", "PSF Guard answered HTTP 404.")]
    [InlineData(HttpStatusCode.OK, "<html>proxy</html>", "PSF Guard's answer was not an upload state.")]
    public async Task AnUnsupportedStateSaysWhy(HttpStatusCode status, string body, string reason)
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") }));
        Assert.Equal(new DirectorUploadState(DirectorUploadStateKind.Unsupported, reason), await client.GetStateAsync(default));
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
    [InlineData(HttpStatusCode.Conflict, DirectorUploadOutcome.Refused)]
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

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ABusyServersRetryAfterIsKept(HttpStatusCode status)
    {
        var path = Path.Combine(root, "frame.fits");
        await File.WriteAllTextAsync(path, "x");
        using var delta = Client(_ =>
        {
            var response = Json(status, """{"success":false,"data":null,"error":"another image import is already running for this database"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        var answer = await delta.UploadAsync(path, default);
        Assert.Equal(DirectorUploadOutcome.Retry, answer.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), answer.RetryAfter);

        using var none = Client(_ => Task.FromResult(Json(status, "")));
        Assert.Null((await none.UploadAsync(path, default)).RetryAfter);
    }

    [Fact]
    public async Task ARefusalCarriesNoRetryAfter()
    {
        var path = Path.Combine(root, "frame.fits");
        await File.WriteAllTextAsync(path, "x");
        using var client = Client(_ =>
        {
            var response = Json(HttpStatusCode.Conflict, """{"success":false,"data":null,"error":"frame.fits already exists in the receive directory with different content"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        var answer = await client.UploadAsync(path, default);
        Assert.Equal(DirectorUploadOutcome.Refused, answer.Outcome);
        Assert.Null(answer.RetryAfter);
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
        Assert.Equal(TimeSpan.FromSeconds(30), DirectorUploadPolicy.RetryDelay(9, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(1), DirectorUploadPolicy.RetryDelay(1, TimeSpan.FromSeconds(-5)));
        Assert.Equal(TimeSpan.FromMinutes(10), DirectorUploadPolicy.RetryDelay(1, TimeSpan.FromHours(3)));
    }
}

public sealed class DirectorUploadQueueTests : IDisposable
{
    private static readonly DirectorUploadDestination Destination = new(new Uri("https://psf.example/"),
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), Guid.Parse("11111111-2222-4333-8444-555555555555")));
    private static readonly DirectorUploadDestination OtherRig = Destination with { Binding = Destination.Binding with { RigId = Guid.Parse("bbbbbbbb-bbbb-4ccc-8ddd-eeeeeeeeeeee") } };
    private readonly string root = Directory.CreateTempSubdirectory("director-upload-queue-").FullName;
    private readonly FakeClock clock = new();
    private readonly FakeConnector connector = new();
    private readonly List<string> log = [];

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string QueueRoot => Path.Combine(root, "queue");
    private FakeServer Server => connector.Server;

    private DirectorUploadQueue Queue()
    {
        var queue = new DirectorUploadQueue(QueueRoot, connector, log.Add, clock);
        queue.SetDestination(Destination);
        return queue;
    }

    private string Frame(string name = "frame.fits", int bytes = 10)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private int Jobs(string? folder = null) => Directory.Exists(Path.Combine(QueueRoot, folder ?? ""))
        ? Directory.GetFiles(Path.Combine(QueueRoot, folder ?? ""), "*.json").Length : 0;

    [Fact]
    public async Task OffIsTheDefaultAndPersistsAcrossWorkerRestart()
    {
        var frame = Frame();
        await using (var queue = new DirectorUploadQueue(QueueRoot, connector, log.Add, clock))
        {
            Assert.False(await queue.EnqueueAsync(Destination, frame, null, default));
            queue.SetDestination(Destination);
            Assert.True(await queue.EnqueueAsync(Destination, frame, null, default));
            queue.SetDestination(null);
            await queue.ProcessDueJobsAsync(default);
            Assert.Null(await queue.RefreshStateAsync(Destination, default));
            Assert.False(await queue.EnqueueAsync(Destination, Frame("off.fits"), null, default));
        }
        await using var restarted = new DirectorUploadQueue(QueueRoot, connector, log.Add, clock);
        await restarted.ProcessDueJobsAsync(default);
        Assert.Empty(Server.Uploads);
        Assert.Equal(0, Server.StateCalls);
        Assert.Equal(1, Jobs());
        restarted.SetDestination(Destination);
        await restarted.ProcessDueJobsAsync(default);
        Assert.Equal([frame], Server.Uploads);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task SwitchingOffCancelsTheTransferAndKeepsTheFileForLater()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Server.OnUpload = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation did not stop the upload.");
        };
        await using var queue = Queue();
        var frame = Frame();
        await queue.EnqueueAsync(Destination, frame, null, default);
        var processing = queue.ProcessDueJobsAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SetDestination(null);
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Jobs());
        Assert.True(File.Exists(frame));
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(Server.Uploads);
        Server.OnUpload = null;
        queue.SetDestination(Destination);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, Server.Uploads.Count);
        Assert.Equal(0, Jobs());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("catalog")]
    [InlineData("server")]
    [InlineData("endpoint")]
    public async Task AChangedConnectionCannotSendOrRemoveThePreviousConnectionsFiles(string change)
    {
        var other = Destination with
        {
            Endpoint = change == "endpoint" ? new Uri("https://other.example/") : Destination.Endpoint,
            Binding = Destination.Binding with
            {
                ProfileId = change == "profile" ? Guid.NewGuid() : Destination.ProfileId,
                CatalogId = change == "catalog" ? Guid.NewGuid() : Destination.Binding.CatalogId,
                CoordinatorInstanceId = change == "server" ? Guid.NewGuid() : Destination.Binding.CoordinatorInstanceId,
            }
        };
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame(), null, default);
        queue.SetDestination(other);
        await queue.ProcessDueJobsAsync(default);
        Assert.Empty(Server.Uploads);
        Assert.Equal(0, queue.Summarize(other).Queued);
        Assert.Equal(1, queue.Summarize(Destination).Queued);

        queue.SetDestination(Destination);
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Refused, "refused"));
        await queue.ProcessDueJobsAsync(default);
        queue.SetDestination(other);
        Assert.Equal(0, await queue.RetryRefusedAsync(other, default));
        Assert.Equal(0, await queue.ForgetHeldAsync(other, default));
        Assert.Equal(1, queue.Summarize(Destination).Refused);
        Assert.Equal(1, await queue.ForgetHeldAsync(Destination, default));
    }

    [Fact]
    public async Task AQueuedFrameUploadsAndItsJobIsRemoved()
    {
        await using var queue = Queue();
        var frame = Frame();
        Assert.True(await queue.EnqueueAsync(Destination, frame, "capture-1", default));
        Assert.Equal(1, Jobs());

        await queue.ProcessDueJobsAsync(default);

        Assert.Equal([frame], Server.Uploads);
        Assert.Equal(0, Jobs());
        Assert.True(File.Exists(frame));
    }

    public static TheoryData<string> KnownStates() => ["disabled", "unsupported", "paused", "unavailable", "unknown"];

    [Theory]
    [MemberData(nameof(KnownStates))]
    public async Task EveryOfferedFrameIsWrittenWhateverTheServerSaid(string known)
    {
        await using var queue = Queue();
        switch (known)
        {
            case "disabled": Server.States.Enqueue(new(DirectorUploadStateKind.Disabled, "off")); break;
            case "unsupported": Server.States.Enqueue(new(DirectorUploadStateKind.Unsupported, "PSF Guard answered HTTP 404.")); break;
            case "unavailable": Server.States.Enqueue(new(DirectorUploadStateKind.Unavailable, "timeout")); break;
            case "paused": Server.Answers.Enqueue(new(DirectorUploadOutcome.Paused, "PSF Guard answered HTTP 403.")); break;
        }
        if (known != "unknown")
        {
            await queue.EnqueueAsync(Destination, Frame("first.fits"), null, default);
            await queue.ProcessDueJobsAsync(default);
        }

        Assert.True(await queue.EnqueueAsync(Destination, Frame("next.fits"), null, default));

        Assert.Equal(known == "unknown" ? 1 : 2, Jobs());
        Assert.Equal(0, Jobs(DirectorUploadQueue.RefusedFolderName));
    }

    [Fact]
    public async Task OfferWritesTheJobOnAPoolThreadEvenWhileTheWorkerIsStopped()
    {
        await using var queue = Queue();
        queue.Start();
        await queue.StopAsync();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => changed.TrySetResult();
        queue.Offer(Destination, Frame(), "capture-1");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Jobs());
        queue.Offer(Destination, "\0bad", null);

        // The next start uploads it.
        queue.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Server.Uploads.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Single(Server.Uploads);
        await queue.StopAsync();
        Assert.Equal(0, Jobs());
        Assert.True(connector.Disposed > 0);
    }

    [Fact]
    public async Task TheStateIsAskedAtMostOnceAMinute()
    {
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(1, Server.StateCalls);
        Assert.Equal(2, Server.Uploads.Count);

        clock.Advance(TimeSpan.FromSeconds(30));
        await queue.EnqueueAsync(Destination, Frame("c.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(1, Server.StateCalls);

        clock.Advance(TimeSpan.FromSeconds(31));
        await queue.EnqueueAsync(Destination, Frame("d.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, Server.StateCalls);
        Assert.Equal(1, connector.Connects);
    }

    [Fact]
    public async Task AServerWithoutUploadsIsAskedAgainEveryTenMinutesAndOnRecheck()
    {
        Server.States.Enqueue(new(DirectorUploadStateKind.Unsupported, "PSF Guard answered HTTP 404."));
        Server.States.Enqueue(new(DirectorUploadStateKind.Unsupported, "PSF Guard answered HTTP 404."));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);

        // The worker still wakes every minute; the cached answer decides when to ask.
        Assert.Equal(TimeSpan.FromMinutes(1), await queue.ProcessDueJobsAsync(default));
        clock.Advance(TimeSpan.FromMinutes(9));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(1, Server.StateCalls);
        Assert.Empty(Server.Uploads);

        clock.Advance(TimeSpan.FromMinutes(1));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, Server.StateCalls);
        Assert.Empty(Server.Uploads);
        Assert.Equal(1, Jobs());
        // The same answer twice is one change, logged once.
        Assert.Single(log, line => line.Contains("does not take uploads from Director now"));

        queue.RequestRecheck();
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(3, Server.StateCalls);
        Assert.Single(Server.Uploads);
        Assert.Single(log, line => line.Contains("takes uploads from this rig again"));
    }

    [Fact]
    public async Task UploadsOffKeepFramesUntilTheyAreOn()
    {
        Server.States.Enqueue(new(DirectorUploadStateKind.Disabled, "Image uploads are off for this database."));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);

        await queue.ProcessDueJobsAsync(default);

        Assert.Empty(Server.Uploads);
        Assert.Equal(1, Jobs());
        Assert.Equal("Image uploads are off for this database.", queue.Summarize(Destination).State!.Reason);

        clock.Advance(TimeSpan.FromSeconds(61));
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(Server.Uploads);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task ARefusedFrameIsHeldWithItsReasonAndCanBeRetriedOrForgotten()
    {
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Refused, "PSF Guard answered HTTP 410: removed on purpose"));
        await using var queue = Queue();
        var frame = Frame();
        await queue.EnqueueAsync(Destination, frame, null, default);

        await queue.ProcessDueJobsAsync(default);
        await queue.ProcessDueJobsAsync(default);

        Assert.Single(Server.Uploads);
        Assert.Equal(0, Jobs());
        Assert.Equal(1, Jobs(DirectorUploadQueue.RefusedFolderName));
        Assert.Contains("removed on purpose", File.ReadAllText(Directory.GetFiles(Path.Combine(QueueRoot, "refused"))[0]));
        Assert.Equal(new DirectorUploadSummary(0, 0, 1, new(DirectorUploadStateKind.Enabled, null, null)), queue.Summarize(Destination));
        Assert.Contains(log, line => line.Contains("refused frame.fits"));

        Assert.Equal(1, await queue.RetryRefusedAsync(Destination, default));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, Server.Uploads.Count);
        Assert.Equal(0, Jobs());

        Server.Answers.Enqueue(new(DirectorUploadOutcome.Refused, "bad name"));
        await queue.EnqueueAsync(Destination, frame, null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(1, await queue.ForgetHeldAsync(Destination, default));
        Assert.Equal(0, Jobs(DirectorUploadQueue.RefusedFolderName));
        Assert.True(File.Exists(frame));
    }

    [Fact]
    public async Task APausedServerStopsSendingForAMinuteWhileFramesStillQueue()
    {
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Paused, "PSF Guard answered HTTP 403."));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default);

        await queue.ProcessDueJobsAsync(default);
        Assert.Single(Server.Uploads);
        Assert.True(await queue.EnqueueAsync(Destination, Frame("c.fits"), null, default));
        Assert.Equal(3, Jobs());

        clock.Advance(TimeSpan.FromSeconds(59));
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(Server.Uploads);

        clock.Advance(TimeSpan.FromSeconds(1));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(4, Server.Uploads.Count);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task ARetryAfterHoldsEveryFrameForThatDestination()
    {
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Retry, "PSF Guard answered HTTP 503", RetryAfter: TimeSpan.FromSeconds(20)));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default);

        Assert.Equal(TimeSpan.FromSeconds(20), await queue.ProcessDueJobsAsync(default));
        Assert.Single(Server.Uploads);
        clock.Advance(TimeSpan.FromSeconds(19));
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(Server.Uploads);

        clock.Advance(TimeSpan.FromSeconds(1));
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(3, Server.Uploads.Count);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task ARouteAnOlderServerLacksStopsUploadsAndKeepsTheQueue()
    {
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Unsupported, "PSF Guard answered HTTP 404"));
        Server.States.Enqueue(new(DirectorUploadStateKind.Enabled));
        Server.States.Enqueue(new(DirectorUploadStateKind.Unsupported, "PSF Guard answered HTTP 404."));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        await queue.EnqueueAsync(Destination, Frame("b.fits"), null, default);

        await queue.ProcessDueJobsAsync(default);
        clock.Advance(TimeSpan.FromMinutes(9));
        await queue.ProcessDueJobsAsync(default);

        Assert.Single(Server.Uploads);
        Assert.Equal(2, Jobs());
        Assert.Equal(DirectorUploadStateKind.Unsupported, queue.Summarize(Destination).State!.Kind);
    }

    [Fact]
    public async Task APassingFailureBacksOffAndSurvivesARestart()
    {
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Retry, "PSF Guard answered HTTP 500"));
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Retry, "PSF Guard answered HTTP 500"));
        var frame = Frame();
        await using (var first = Queue())
        {
            await first.EnqueueAsync(Destination, frame, null, default);
            await first.ProcessDueJobsAsync(default);
            Assert.Single(Server.Uploads);
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.True(await first.ProcessDueJobsAsync(default) <= TimeSpan.FromSeconds(1));
            Assert.Single(Server.Uploads);
        }

        await using var restarted = Queue();
        clock.Advance(TimeSpan.FromSeconds(1));
        await restarted.ProcessDueJobsAsync(default);
        Assert.Equal(2, Server.Uploads.Count);
        clock.Advance(TimeSpan.FromSeconds(9));
        await restarted.ProcessDueJobsAsync(default);
        Assert.Equal(2, Server.Uploads.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await restarted.ProcessDueJobsAsync(default);
        Assert.Equal(3, Server.Uploads.Count);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task AFrameGoneFromDiskDropsItsJob()
    {
        await using var queue = Queue();
        var frame = Frame();
        await queue.EnqueueAsync(Destination, frame, null, default);
        File.Delete(frame);

        await queue.ProcessDueJobsAsync(default);

        Assert.Empty(Server.Uploads);
        Assert.Equal(0, Jobs());
        Assert.Single(log, line => line.Contains("no longer on disk"));
    }

    [Fact]
    public async Task AFileLargerThanMaxBytesIsRefused()
    {
        // max_bytes is the largest image file the server takes.
        Server.States.Enqueue(new(DirectorUploadStateKind.Enabled, null, 200));
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("big.fits", 201), null, default);

        await queue.ProcessDueJobsAsync(default);

        Assert.Empty(Server.Uploads);
        Assert.Equal(1, Jobs(DirectorUploadQueue.RefusedFolderName));
        // Once max_bytes is known, a frame that cannot fit goes straight to refused.
        Assert.False(await queue.EnqueueAsync(Destination, Frame("big2.fits", 201), null, default));
        Assert.Equal(2, Jobs(DirectorUploadQueue.RefusedFolderName));
        Assert.Equal(0, Jobs());
        // A file exactly at the limit fits.
        Assert.True(await queue.EnqueueAsync(Destination, Frame("exact.fits", 200), null, default));
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(Server.Uploads);
    }

    [Fact]
    public async Task FramesForAnEarlierPairingAreCountedApartAndForgotten()
    {
        connector.Covers = destination => destination == Destination;
        await using var queue = Queue();
        queue.SetDestination(OtherRig);
        await queue.EnqueueAsync(OtherRig, Frame("old.fits"), null, default);
        queue.SetDestination(Destination);
        await queue.EnqueueAsync(Destination, Frame("new.fits"), null, default);
        Server.Answers.Enqueue(new(DirectorUploadOutcome.Retry, "PSF Guard answered HTTP 500"));

        await queue.ProcessDueJobsAsync(default);

        Assert.Single(Server.Uploads);
        Assert.Equal(new DirectorUploadSummary(1, 0, 0, new(DirectorUploadStateKind.Enabled)), queue.Summarize(Destination));
        Assert.Equal(0, await queue.ForgetHeldAsync(Destination, default));
        Assert.Equal(1, queue.Summarize(OtherRig).EarlierPairing);
        Assert.Equal(1, await queue.ForgetHeldAsync(OtherRig, default));
        Assert.Equal(1, Jobs());
        Assert.Equal(new DirectorUploadSummary(1, 0, 0, new(DirectorUploadStateKind.Enabled)), queue.Summarize(Destination));
    }

    [Fact]
    public async Task OneClientServesADestinationUntilItsPairingChanges()
    {
        await using var queue = Queue();
        for (var i = 0; i < 3; i++)
        {
            await queue.EnqueueAsync(Destination, Frame($"{i}.fits"), null, default);
            await queue.ProcessDueJobsAsync(default);
        }
        Assert.Equal(1, connector.Connects);

        connector.Key = "re-paired";
        await queue.EnqueueAsync(Destination, Frame("after.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, connector.Connects);
        Assert.Equal(1, connector.Disposed);
        Assert.Equal(4, Server.Uploads.Count);
    }

    [Fact]
    public async Task TheIndexIsReadFromDiskWhenTheQueueStarts()
    {
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame("a.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        // A job file written behind the queue's back is not read again on each pass.
        await using (var other = Queue()) await other.EnqueueAsync(Destination, Frame("b.fits"), null, default);
        await queue.ProcessDueJobsAsync(default);
        Assert.Single(Server.Uploads);

        // A start reads the folder again.
        queue.Start();
        await queue.StopAsync();
        await queue.ProcessDueJobsAsync(default);
        Assert.Equal(2, Server.Uploads.Count);
        Assert.Equal(0, Jobs());
    }

    [Fact]
    public async Task AnUnpairedOrFailingConnectorLeavesJobsWaiting()
    {
        connector.Covers = _ => false;
        await using (var unpaired = Queue())
        {
            await unpaired.EnqueueAsync(Destination, Frame(), null, default);
            await unpaired.ProcessDueJobsAsync(default);
        }
        connector.Covers = _ => throw new InvalidOperationException("Director credential is invalid.");
        await using (var failing = Queue())
        {
            await failing.ProcessDueJobsAsync(default);
            // An unreadable credential does not make a frame an earlier pairing's.
            Assert.Equal(new DirectorUploadSummary(1, 0, 0, null), failing.Summarize(Destination));
        }
        Assert.Equal(1, Jobs());
        Assert.Empty(Server.Uploads);
    }

    [Fact]
    public async Task AnUnreadableJobIsSetAsideAndOthersStillUpload()
    {
        await using var queue = Queue();
        await queue.EnqueueAsync(Destination, Frame(), null, default);
        await File.WriteAllTextAsync(Path.Combine(QueueRoot, "00000000000000000000000000000000.json"), "{ not json");
        queue.Start();
        await queue.StopAsync();

        await queue.ProcessDueJobsAsync(default);

        Assert.Single(Server.Uploads);
        Assert.Equal(1, Jobs(DirectorUploadQueue.BlockedFolderName));
        Assert.Equal(0, Jobs());
    }

    private sealed class FakeConnector : IDirectorUploadConnector
    {
        public FakeServer Server { get; } = new();
        public Func<DirectorUploadDestination, bool> Covers { get; set; } = _ => true;
        public string Key { get; set; } = "pairing";
        public int Connects { get; private set; }
        public int Disposed => Server.Disposed;

        public string? PairingKey(DirectorUploadDestination destination) => Covers(destination) ? Key : null;

        public IDirectorUploadServer Connect(DirectorUploadDestination destination)
        {
            Connects++;
            return Server;
        }
    }

    private sealed class FakeServer : IDirectorUploadServer
    {
        public Queue<DirectorUploadState> States { get; } = new();
        public Queue<DirectorUploadAnswer> Answers { get; } = new();
        public List<string> Uploads { get; } = [];
        public int StateCalls { get; private set; }
        public int Disposed { get; private set; }
        public Func<CancellationToken, Task<DirectorUploadAnswer>>? OnUpload { get; set; }

        public Task<DirectorUploadState> GetStateAsync(CancellationToken token)
        {
            StateCalls++;
            return Task.FromResult(States.Count > 0 ? States.Dequeue() : new DirectorUploadState(DirectorUploadStateKind.Enabled));
        }

        public Task<DirectorUploadAnswer> UploadAsync(string path, CancellationToken token)
        {
            lock (Uploads) Uploads.Add(path);
            if (OnUpload is not null) return OnUpload(token);
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new DirectorUploadAnswer(DirectorUploadOutcome.Stored, "Stored"));
        }

        public void Dispose() => Disposed++;
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
