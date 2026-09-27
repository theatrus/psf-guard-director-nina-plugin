using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class CoordinatorDeliveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "director-delivery-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Endpoint = CoordinatorProgramTests.Endpoint;
    private static readonly CoordinatorBinding Binding = CoordinatorProgramTests.Binding;
    private readonly LedgerIdentity ledger = new(Guid.NewGuid().ToString("D"), "assignment", 1, Binding.RigId.ToString("D"), "config");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private CoordinatorCheckpointClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null) =>
        new(root, Endpoint, Binding, ledger, _ => ValueTask.FromResult<string?>("psfdrc_test"), new Handler(send), timeout ?? TimeSpan.FromSeconds(2));
    private LedgerEventPage Page(ulong after, int count) => new(ledger,
        Enumerable.Range(1, count).Select(i => new LedgerEvent(after + (ulong)i,
            new("capture-" + i, "goal", 100, new LedgerEvidence.Saved("image-" + i, 200)))).ToImmutableArray(), after + (ulong)count);
    private Task<LedgerEventPage> OnePage(ulong after, int limit, CancellationToken token) => Task.FromResult(Page(after, after == 0 ? 2 : 0));
    private JsonObject Ack(ulong through, int count = 2) => new()
    {
        ["success"] = true,
        ["error"] = null,
        ["status"] = "ready",
        ["data"] = new JsonObject
        {
            ["coordinator_instance_id"] = Binding.CoordinatorInstanceId,
            ["catalog_id"] = Binding.CatalogId,
            ["rig_id"] = Binding.RigId,
            ["ledger_id"] = ledger.LedgerId,
            ["acknowledged_through"] = through,
            ["highest_seen"] = through,
            ["outcomes"] = new JsonArray(Enumerable.Repeat("applied", count).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["applied"] = count,
            ["duplicates"] = 0,
            ["conflicts"] = new JsonArray(),
            ["program_revision"] = null,
            ["program_changed"] = false,
            ["received_at_ms"] = 1000
        }
    };
    private static HttpResponseMessage Response(JsonNode body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task DeliversCaptureWireAndRestartsAtDurableCursor()
    {
        using (var client = Client(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/prefix/api/director/v1/rigs/{Binding.RigId:D}/checkin", request.RequestUri!.AbsolutePath);
            Assert.Equal(Binding.ProfileId.ToString("D"), request.Headers.GetValues("X-PSF-Director-Profile").Single());
            var data = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(ledger.LedgerId, data["ledger_id"]!.GetValue<string>());
            Assert.Equal(RuntimeContract.EngineVersion, data["events"]![0]!["engine_version"]!.GetValue<string>());
            Assert.Equal("saved", data["events"]![0]!["attempt"]!["evidence"]!["state"]!.GetValue<string>());
            return Response(Ack(2));
        }))
        {
            var result = await client.DeliverAsync(OnePage, maxPages: 1);
            Assert.Equal(2UL, result.AcknowledgedThrough);
            Assert.False(result.CaughtUp);
        }
        using var restarted = Client((_, _) => throw new Exception("No network expected"));
        var done = await restarted.DeliverAsync((after, _, _) => { Assert.Equal(2UL, after); return Task.FromResult(Page(after, 0)); });
        Assert.True(done.CaughtUp);
        Assert.Equal(0, done.DeliveredEvents);
    }

    [Theory]
    [InlineData("rig")]
    [InlineData("catalog")]
    [InlineData("coordinator")]
    [InlineData("ledger")]
    [InlineData("gap")]
    [InlineData("counts")]
    [InlineData("unknown")]
    [InlineData("changed")]
    [InlineData("null")]
    [InlineData("conflict")]
    public async Task InvalidOrConflictingAcknowledgementDoesNotAdvance(string fault)
    {
        var body = Ack(2);
        var data = body["data"]!;
        switch (fault)
        {
            case "rig": data["rig_id"] = Guid.NewGuid(); break;
            case "catalog": data["catalog_id"] = Guid.NewGuid(); break;
            case "coordinator": data["coordinator_instance_id"] = Guid.NewGuid(); break;
            case "ledger": data["ledger_id"] = Guid.NewGuid().ToString(); break;
            case "gap": data["acknowledged_through"] = 1; break;
            case "counts": data["applied"] = 1; break;
            case "unknown": data["extra"] = true; break;
            case "changed": data["program_changed"] = true; break;
            case "null": data["outcomes"] = null; break;
            case "conflict": data["outcomes"]![0] = "conflict"; data["conflicts"]!.AsArray().Add(1); data["applied"] = 1; break;
        }
        using (var client = Client((_, _) => Task.FromResult(Response(body))))
        {
            var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.DeliverAsync(OnePage));
            Assert.Equal(fault == "conflict" ? CoordinatorIntakeFailure.ReceiptConflict : CoordinatorIntakeFailure.InvalidAcknowledgement, error.Failure);
        }
        using var next = Client((_, _) => Task.FromResult(Response(Ack(2))));
        var retried = await next.DeliverAsync((after, limit, token) => { Assert.Equal(0UL, after); return OnePage(after, limit, token); }, maxPages: 1);
        Assert.Equal(2, retried.DeliveredEvents);
    }

    [Fact]
    public async Task LostResponseResendsIdenticalPayloadAndAcceptsDuplicates()
    {
        string? first = null;
        using (var client = Client(async (request, token) =>
        {
            first = await request.Content!.ReadAsStringAsync(token);
            throw new HttpRequestException("lost response");
        })) await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.DeliverAsync(OnePage));
        using var retry = Client(async (request, token) =>
        {
            Assert.Equal(first, await request.Content!.ReadAsStringAsync(token));
            var ack = Ack(10);
            ack["data"]!["outcomes"] = new JsonArray("duplicate", "duplicate");
            ack["data"]!["applied"] = 0; ack["data"]!["duplicates"] = 2;
            return Response(ack);
        });
        var result = await retry.DeliverAsync(OnePage);
        Assert.Equal(2UL, result.AcknowledgedThrough); // Do not trust the server's later cursor for unsent local events.
        Assert.True(result.CaughtUp);
    }

    [Fact]
    public async Task ConcurrentDeliveryIsRefusedAndCancellationReleasesLease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        using var first = Client(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception(); });
        var pending = first.DeliverAsync(OnePage, token: cancel.Token);
        await entered.Task;
        using var second = Client((_, _) => Task.FromResult(Response(Ack(2))));
        await Assert.ThrowsAsync<IOException>(() => second.DeliverAsync(OnePage));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True((await second.DeliverAsync(OnePage)).CaughtUp);
    }

    [Fact]
    public async Task DeadlineCoversLedgerReadAndDoesNotAdvanceCursor()
    {
        using var client = Client((_, _) => throw new Exception(), TimeSpan.FromMilliseconds(20));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.DeliverAsync(async (_, _, token) =>
        { await Task.Delay(Timeout.Infinite, token); throw new Exception(); }));
        Assert.Equal(CoordinatorIntakeFailure.Timeout, error.Failure);
        Assert.Empty(Directory.GetFiles(root, "*.json"));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("sequence")]
    [InlineData("cursor")]
    public async Task InvalidLocalPagesNeverReachNetwork(string fault)
    {
        using var client = Client((_, _) => throw new Exception("No network expected"));
        var page = Page(0, 2);
        page = fault switch
        {
            "identity" => page with { Identity = ledger with { RigId = Guid.NewGuid().ToString() } },
            "sequence" => page with { Events = [page.Events[1]] },
            _ => page with { NextCursor = 10 }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DeliverAsync((_, _, _) => Task.FromResult(page)));
    }

    [Fact]
    public async Task ProgramChangeSurvivesPartialFailureAndEmptyRetry()
    {
        var revision = new string('c', 64);
        using (var client = Client((_, _) =>
        {
            var ack = Ack(2);
            ack["data"]!["program_revision"] = revision;
            ack["data"]!["program_changed"] = true;
            return Task.FromResult(Response(ack));
        }))
            await Assert.ThrowsAsync<IOException>(() => client.DeliverAsync((after, limit, token) => after == 0
                ? OnePage(after, limit, token) : throw new IOException("ledger unavailable")));
        using var restarted = Client((_, _) => throw new Exception("No network expected"));
        var result = await restarted.DeliverAsync((after, _, _) => Task.FromResult(Page(after, 0)));
        Assert.True(result.ProgramChanged);
        Assert.Equal(2UL, result.AcknowledgedThrough);
        Assert.False((await restarted.DeliverAsync((after, _, _) => Task.FromResult(Page(after, 0)), revision)).ProgramChanged);
    }

    [Fact]
    public async Task CorruptedCursorCannotSkipLocalEvents()
    {
        using var client = Client((_, _) => Task.FromResult(Response(Ack(2))));
        await client.DeliverAsync(OnePage);
        var path = Directory.GetFiles(root, "*.json").Single();
        var state = JsonNode.Parse(File.ReadAllText(path))!;
        state["through"] = 10;
        File.WriteAllText(path, state.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DeliverAsync(OnePage));
    }

    [Fact]
    public void CacheSurvivesRestartAndRejectsImmutableDrift()
    {
        var envelope = CoordinatorProgramTests.Envelope();
        var preview = Preview(envelope);
        new CoordinatorPreviewCache(root, Endpoint, Binding, CoordinatorProgramTests.Configuration).Store(preview);
        var cache = new CoordinatorPreviewCache(root, Endpoint, Binding, CoordinatorProgramTests.Configuration);
        Assert.Equal(preview.Fingerprint, cache.ReadPrevious()!.Fingerprint);
        var altered = Preview(envelope with { IssuedAtMs = envelope.IssuedAtMs + 1 });
        Assert.Throws<CoordinatorIntakeException>(() => cache.Store(altered));
        Assert.Equal(preview.Fingerprint, cache.ReadPrevious()!.Fingerprint);
        Assert.Null(new CoordinatorPreviewCache(root, Endpoint, Binding with { ProfileId = Guid.NewGuid() }, CoordinatorProgramTests.Configuration).ReadPrevious());
        Assert.Null(new CoordinatorPreviewCache(root, new("https://elsewhere.example/"), Binding, CoordinatorProgramTests.Configuration).ReadPrevious());
    }

    [Fact]
    public void CacheDetectsCorruptionAndDoesNotSilentlyEraseHistory()
    {
        var cache = new CoordinatorPreviewCache(root, Endpoint, Binding, CoordinatorProgramTests.Configuration);
        var preview = Preview(CoordinatorProgramTests.Envelope());
        cache.Store(preview);
        var path = Directory.GetFiles(root, "*.json").Single();
        File.WriteAllText(path, "{}");
        Assert.Throws<InvalidDataException>(() => cache.ReadPrevious());
        Assert.Throws<InvalidDataException>(() => cache.Store(preview));
    }

    private static CoordinatorProgramPreview Preview(CoordinatorProgramEnvelope envelope) => CoordinatorProgramContract.Read(
        JsonSerializer.SerializeToUtf8Bytes(new { success = true, data = envelope, error = (string?)null, status = "ready" }, CoordinatorProgramContract.Options),
        $"\"{envelope.Revision}\"", Endpoint.AbsoluteUri, Binding, CoordinatorProgramTests.Configuration, envelope.IssuedAtMs, null);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
