using System.Net;
using System.Collections.Immutable;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class CoordinatorProgramTests
{
    private const ulong Now = 1790409600000;
    internal static readonly Uri Endpoint = new("https://coordinator.example/prefix/");
    internal static readonly CoordinatorBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Goal = Guid.NewGuid(), Target = Guid.NewGuid(), Contribution = Guid.NewGuid();
    internal static readonly DirectorConfiguration Configuration = new(Binding.RigId.ToString("D"), "config", "camera", "wheel",
        [new("filter-0", 0)], [new(1, 1)], [0], new CameraControl.Unsupported(), new CameraControl.Unsupported(), 1, 600000, false, 0);

    internal static CoordinatorProgramEnvelope Envelope() => new(Binding.CoordinatorInstanceId, Binding.CatalogId, Binding.RigId,
        new string('a', 64), Now - 1000,
        new(1, new("assignment", 1, Binding.RigId.ToString("D"), "config", Now - 1000, Now + 60000,
            [new(Goal.ToString("D"), 1, 10, 0, 0, 15, 1000, 1000, [new(Now - 1000, Now + 60000)])]), Configuration,
            [new($"target-{Target:D}", "Target", 1000, 2000, null)],
            [new($"recipe-{Contribution:D}", 1000, "filter-0", new(1, 1), null, null, 0, null)],
            [new(Goal.ToString("D"), $"target-{Target:D}", $"recipe-{Contribution:D}")]),
        [new(Goal.ToString("D"), Guid.NewGuid(), "Project", 1, Guid.NewGuid(), Contribution, "r1c1", Guid.NewGuid(), Target, Goal, "red", "detail")],
        new(1, new(35, -120, 100), new DirectorHorizon.FixedMinimum(), new(20, 89, new(0, 0)), new CoordinatorRotation.Fixed(15)), []);

    private static JsonObject Body(CoordinatorProgramEnvelope envelope) => new()
    {
        ["success"] = true,
        ["error"] = null,
        ["status"] = "ready",
        ["data"] = ProgramContract.Encode(envelope)
    };
    private static HttpResponseMessage Response(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        Headers = { ETag = new EntityTagHeaderValue($"\"{body["data"]!["revision"]!.GetValue<string>()}\"") }
    };
    private sealed class Clock(Action? onRead = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            onRead?.Invoke();
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Now);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class BodyStream(byte[] bytes, bool stall = false) : Stream
    {
        private readonly MemoryStream content = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (stall) await Task.Delay(Timeout.Infinite, token);
            return await content.ReadAsync(buffer, token);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) content.Dispose(); base.Dispose(disposing); }
    }
    private static CoordinatorProgramClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TimeSpan? timeout = null) => new(Endpoint, _ => ValueTask.FromResult<string?>("secret-test-token"), new Handler(send), new Clock(), timeout ?? TimeSpan.FromSeconds(2));

    [Fact]
    public async Task ReadsExactProgramWithoutOpeningLedgerAndBindsEveryRequest()
    {
        var body = Body(Envelope());
        using var client = Client((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/prefix/api/director/v1/rigs/{Binding.RigId:D}/program", request.RequestUri!.AbsolutePath);
            Assert.Contains($"catalog_id={Binding.CatalogId:D}", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("secret-test-token", request.Headers.Authorization.Parameter);
            Assert.Empty(request.Headers.IfNoneMatch);
            return Task.FromResult(Response(body));
        });
        var first = await client.ReadPreviewAsync(Binding, Configuration);
        var second = await client.ReadPreviewAsync(Binding, Configuration, first);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal("filter-0", second.Envelope.Program.Recipes[0].FilterId);
        Assert.Equal(15, Assert.IsType<CoordinatorRotation.Fixed>(second.Envelope.Rig.Rotation).AngleDegrees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservingPoliciesRoundTripAndNullPolicyIsAControlledFailure(bool corrupt)
    {
        var weights = new Dictionary<string, ushort>
        {
            ["importance"] = 30,
            ["window_urgency"] = 25,
            ["altitude"] = 15,
            ["moon_opportunity"] = 15,
            ["completion"] = 5,
            ["efficiency"] = 5,
            ["continuity"] = 5
        }.ToImmutableDictionary();
        var source = new DirectorPreferenceSource("global", "global", 1);
        var policy = new DirectorObservingPolicy(weights, 50, 600000, 500);
        var resolved = new DirectorResolvedPolicy(1, policy, new(weights.Keys.ToImmutableDictionary(k => k, _ => source), source, source, source), policy, source, []);
        var preferences = new DirectorObservingPreferences(1,
            ImmutableDictionary<string, DirectorResolvedPolicy>.Empty.Add("policy", resolved),
            ImmutableDictionary<string, string>.Empty.Add(Goal.ToString("D"), "policy"));
        var original = Envelope();
        var body = Body(original with { Program = original.Program with { ObservingPreferences = preferences } });
        Assert.Equal(weights.Keys.Order(StringComparer.Ordinal), body["data"]!["program"]!["observing_preferences"]!["policies"]!["policy"]!["policy"]!["weights"]!.AsObject().Select(entry => entry.Key));
        if (corrupt) body["data"]!["program"]!["observing_preferences"]!["policies"]!["policy"] = null;
        using var client = Client((_, _) => Task.FromResult(Response(body)));
        if (corrupt)
            await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        else
        {
            var read = await client.ReadPreviewAsync(Binding, Configuration);
            Assert.Equal(50, read.Envelope.Program.ObservingPreferences!.Policies["policy"].Policy.Importance);
            Assert.Equal(body["data"]!.ToJsonString(), ProgramContract.Encode(read.Envelope).ToJsonString());
        }
    }

    [Theory]
    [InlineData(401, CoordinatorIntakeFailure.AuthenticationRequired)]
    [InlineData(403, CoordinatorIntakeFailure.Forbidden)]
    [InlineData(404, CoordinatorIntakeFailure.NotFound)]
    [InlineData(422, CoordinatorIntakeFailure.NotReady)]
    [InlineData(429, CoordinatorIntakeFailure.Busy)]
    [InlineData(503, CoordinatorIntakeFailure.Busy)]
    [InlineData(500, CoordinatorIntakeFailure.ServerUnavailable)]
    [InlineData(302, CoordinatorIntakeFailure.RedirectRefused)]
    [InlineData(304, CoordinatorIntakeFailure.UnexpectedNotModified)]
    [InlineData(204, CoordinatorIntakeFailure.UnexpectedStatus)]
    public async Task StatusFailuresDoNotEchoServerBodies(int status, CoordinatorIntakeFailure failure)
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("credential-and-private-path") }));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain("credential", error.ToString());
    }

    [Theory]
    [InlineData("coordinator")]
    [InlineData("catalog")]
    [InlineData("rig")]
    public async Task RejectsCrossRigResponses(string fault)
    {
        var body = Body(Envelope());
        body["data"]![fault == "coordinator" ? "coordinator_instance_id" : fault + "_id"] = Guid.NewGuid().ToString();
        using var client = Client((_, _) => Task.FromResult(Response(body)));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        Assert.Equal(CoordinatorIntakeFailure.IdentityMismatch, error.Failure);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("null-target")]
    [InlineData("duplicate-target")]
    [InlineData("missing-link")]
    [InlineData("wrong-target-link")]
    [InlineData("fractional-time")]
    [InlineData("wrong-etag")]
    [InlineData("null-program")]
    public async Task StrictWireAndIdentityLinksRejectMalformedResponses(string fault)
    {
        var body = Body(Envelope());
        var data = body["data"]!;
        var program = data["program"]!;
        switch (fault)
        {
            case "missing": data.AsObject().Remove("issued_at_ms"); break;
            case "unknown": data["extra"] = true; break;
            case "null-target": program["targets"]!.AsArray()[0] = null; break;
            case "duplicate-target": program["targets"]!.AsArray().Add(program["targets"]![0]!.DeepClone()); break;
            case "missing-link": data["links"]!.AsArray().Clear(); break;
            case "wrong-target-link": data["links"]![0]!["target_guid"] = Guid.NewGuid().ToString(); break;
            case "fractional-time": data["issued_at_ms"] = 12.5; break;
            case "null-program": data["program"] = null; break;
        }
        using var client = Client((_, _) =>
        {
            var response = Response(body);
            if (fault == "wrong-etag") response.Headers.ETag = new("\"wrong\"");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        Assert.Equal(CoordinatorIntakeFailure.MalformedResponse, error.Failure);
    }

    [Theory]
    [InlineData("configuration", CoordinatorIntakeFailure.ConfigurationMismatch)]
    [InlineData("version", CoordinatorIntakeFailure.UnsupportedProgram)]
    [InlineData("expired", CoordinatorIntakeFailure.ExpiredOrFutureProgram)]
    [InlineData("future", CoordinatorIntakeFailure.ExpiredOrFutureProgram)]
    public async Task RejectsIncompatibleOrExpiredPrograms(string fault, CoordinatorIntakeFailure failure)
    {
        var body = Body(Envelope());
        switch (fault)
        {
            case "configuration": body["data"]!["program"]!["configuration"]!["camera_id"] = "other"; break;
            case "version": body["data"]!["program"]!["schema_version"] = 2; break;
            case "expired": body["data"]!["program"]!["assignment"]!["expires_at_ms"] = Now; break;
            case "future": body["data"]!["issued_at_ms"] = Now + 1; break;
        }
        using var client = Client((_, _) => Task.FromResult(Response(body)));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        Assert.Equal(failure, error.Failure);
    }

    [Fact]
    public async Task SameIdentityCannotChangeValidityAndPreviousCannotCrossProfiles()
    {
        var body = Body(Envelope());
        using var client = Client((_, _) => Task.FromResult(Response(body)));
        var first = await client.ReadPreviewAsync(Binding, Configuration);
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() =>
            client.ReadPreviewAsync(Binding with { ProfileId = Guid.NewGuid() }, Configuration, first));
        Assert.Equal(CoordinatorIntakeFailure.IdentityMismatch, error.Failure);
        body["data"]!["program"]!["assignment"]!["expires_at_ms"] = Now + 120000;
        error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration, first));
        Assert.Equal(CoordinatorIntakeFailure.ChangedImmutableProgram, error.Failure);
    }

    [Fact]
    public async Task ChangingETagCannotHideMutationsToTheSameAssignment()
    {
        var body = Body(Envelope());
        using var client = Client((_, _) => Task.FromResult(Response(body)));
        var previous = await client.ReadPreviewAsync(Binding, Configuration);
        body["data"]!["revision"] = new string('b', 64);
        body["data"]!["program"]!["assignment"]!["expires_at_ms"] = Now + 120000;
        Assert.Equal(CoordinatorIntakeFailure.ChangedImmutableProgram,
            (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration, previous))).Failure);
    }

    [Fact]
    public async Task PreviousPreviewCannotCrossOriginsBeforeRequestAdmission()
    {
        using var first = Client((_, _) => Task.FromResult(Response(Body(Envelope()))));
        var previous = await first.ReadPreviewAsync(Binding, Configuration);
        var called = false;
        using var second = new CoordinatorProgramClient(new("https://different.example/"), _ => ValueTask.FromResult<string?>(null),
            new Handler((_, _) => { called = true; throw new InvalidOperationException(); }), new Clock(), TimeSpan.FromSeconds(2));
        Assert.Equal(CoordinatorIntakeFailure.IdentityMismatch,
            (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => second.ReadPreviewAsync(Binding, Configuration, previous))).Failure);
        Assert.False(called);
    }

    [Fact]
    public async Task CancellationDuringResponseValidationDoesNotReturnPreview()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new CoordinatorProgramClient(Endpoint, _ => ValueTask.FromResult<string?>(null),
            new Handler((_, _) => Task.FromResult(Response(Body(Envelope())))), new Clock(cancellation.Cancel), TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadPreviewAsync(Binding, Configuration, token: cancellation.Token));
    }

    [Fact]
    public async Task RejectsDuplicateKeysAndOversizedChunkedBodies()
    {
        foreach (var content in new[] { "{\"success\":true,\"success\":true}", new string(' ', CoordinatorProgramContract.MaximumBytes + 1) })
        {
            using var client = Client((_, _) =>
            {
                var response = Response(Body(Envelope()));
                response.Content = new StreamContent(new BodyStream(Encoding.UTF8.GetBytes(content)));
                response.Content.Headers.ContentType = new("application/json");
                return Task.FromResult(response);
            });
            var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
            Assert.Equal(content[0] == ' ' ? CoordinatorIntakeFailure.ResponseTooLarge : CoordinatorIntakeFailure.MalformedResponse, error.Failure);
        }
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationRemainDistinct()
    {
        using var client = Client(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }, TimeSpan.FromMilliseconds(30));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        Assert.Equal(CoordinatorIntakeFailure.Timeout, error.Failure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadPreviewAsync(Binding, Configuration, token: cancellation.Token));
    }

    [Fact]
    public async Task BodyTimeoutIsBoundedEvenAfterHeadersArrive()
    {
        using var client = Client((_, _) =>
        {
            var response = Response(Body(Envelope()));
            response.Content = new StreamContent(new BodyStream([], stall: true));
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }, TimeSpan.FromMilliseconds(30));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        Assert.Equal(CoordinatorIntakeFailure.Timeout, error.Failure);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("header")]
    public async Task CredentialErrorsNeverEnterRequestsOrExceptions(string fault)
    {
        var called = false;
        using var client = new CoordinatorProgramClient(Endpoint,
            _ => fault == "provider" ? throw new InvalidOperationException("private-secret") : ValueTask.FromResult<string?>("private-secret\n"),
            new Handler((_, _) => { called = true; throw new InvalidOperationException(); }), new Clock(), TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration));
        Assert.Equal(fault == "provider" ? CoordinatorIntakeFailure.CredentialUnavailable : CoordinatorIntakeFailure.AuthenticationRequired, error.Failure);
        Assert.False(called);
        Assert.DoesNotContain("private-secret", error.ToString());
    }

    [Fact]
    public async Task ExactUnsignedTimesAndRevisionsAreNotRoundedThroughDouble()
    {
        var body = Body(Envelope());
        const ulong revision = 9007199254740993;
        body["data"]!["program"]!["assignment"]!["revision"] = revision;
        body["data"]!["rig"]!["profile_revision"] = revision;
        using var client = Client((_, _) => Task.FromResult(Response(body)));
        var result = await client.ReadPreviewAsync(Binding, Configuration);
        Assert.Equal(revision, result.Envelope.Program.Assignment.Revision);
        Assert.Equal(revision, result.Envelope.Rig.ProfileRevision);
    }

    [Theory]
    [InlineData("content-type")]
    [InlineData("weak-etag")]
    [InlineData("compressed")]
    public async Task RejectsUnexpectedRepresentations(string fault)
    {
        using var client = Client((_, _) =>
        {
            var response = Response(Body(Envelope()));
            if (fault == "content-type") response.Content.Headers.ContentType = new("text/html");
            if (fault == "weak-etag") response.Headers.ETag = new(response.Headers.ETag!.Tag, isWeak: true);
            if (fault == "compressed") response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
        Assert.Equal(CoordinatorIntakeFailure.MalformedResponse,
            (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration))).Failure);
    }

    [Theory]
    [InlineData("http://remote.example/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("file:///C:/private")]
    public void RejectsUnsafeEndpointDefaults(string url) => Assert.Throws<ArgumentException>(() =>
        new CoordinatorProgramClient(new Uri(url), _ => ValueTask.FromResult<string?>(null)));

    [Fact]
    public async Task RealLoopbackRedirectNeverReceivesBearerAtDestination()
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        destination.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var connection = await source.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(connection.GetStream(), leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
            var redirect = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await connection.GetStream().WriteAsync(redirect, deadline.Token);
        }, deadline.Token);
        using var client = new CoordinatorProgramClient(new Uri($"http://127.0.0.1:{((IPEndPoint)source.LocalEndpoint).Port}/"),
            _ => ValueTask.FromResult<string?>("secret-test-token"));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ReadPreviewAsync(Binding, Configuration, token: deadline.Token));
        Assert.Equal(CoordinatorIntakeFailure.RedirectRefused, error.Failure);
        await server;
        Assert.False(destination.Pending());
    }
}
