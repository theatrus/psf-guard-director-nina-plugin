using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PsfGuard.Director.Runtime;
using Xunit;
using static PsfGuard.Director.Tests.CoordinatorProgramTests;

namespace PsfGuard.Director.Tests;

public sealed class CoordinatorWorkloadTests
{
    [Theory]
    [InlineData("not-released")]
    [InlineData("ledger")]
    [InlineData("cursor")]
    [InlineData("allocation")]
    public async Task ArchivedReleaseRequiresExactTerminalAcknowledgement(string fault)
    {
        var root = Root();
        var grant = Grant(Guid.NewGuid());
        var allocation = CoordinatorAllocation.Read(CoordinatorAllocation.Wrap(grant), Endpoint.AbsoluteUri, Binding, Client, Configuration, Now);
        var a = grant.Snapshot.Program.Assignment;
        var ledger = new LedgerIdentity(Guid.NewGuid().ToString("D"), a.Id, a.Revision, a.RigId, a.ConfigurationId);
        using var client = Create(root, new(_ => Task.FromResult(Response(new
        {
            allocation = fault == "allocation" ? Grant(Guid.NewGuid()) : grant,
            released = fault != "not-released",
            ledger_id = fault == "ledger" ? Guid.NewGuid() : Guid.Parse(ledger.LedgerId),
            terminal_sequence = fault == "cursor" ? 7 : 6
        }))));
        try
        {
            var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.ConfirmArchivedReleaseAsync(allocation, ledger, 6));
            Assert.Equal(CoordinatorIntakeFailure.InvalidAcknowledgement, error.Failure);
            Assert.Empty(Directory.GetFiles(root, "workload-request-*.json"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ArchivedReleaseDoesNotReplaceOrAdvanceANewerRequest()
    {
        var root = Root();
        Guid requestId = default;
        var grant = Grant(Guid.NewGuid());
        var historical = CoordinatorAllocation.Read(CoordinatorAllocation.Wrap(grant), Endpoint.AbsoluteUri, Binding, Client, Configuration, Now);
        var assignment = grant.Snapshot.Program.Assignment;
        var ledger = new LedgerIdentity(Guid.NewGuid().ToString("D"), assignment.Id, assignment.Revision, assignment.RigId, assignment.ConfigurationId);
        using var client = Create(root, new(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                Assert.Equal(grant.AllocationId, body.GetProperty("allocation_id").GetGuid());
                Assert.True(body.GetProperty("operations_quiescent").GetBoolean());
                Assert.True(body.GetProperty("parked").GetBoolean());
                return Response(new { allocation = grant, released = true, ledger_id = Guid.Parse(ledger.LedgerId), terminal_sequence = 6 });
            }
            var id = body.GetProperty("request_id").GetGuid();
            if (requestId == Guid.Empty) requestId = id; else Assert.Equal(requestId, id);
            return Response(new { request_id = id, state = "waiting", workload = (object?)null, retry_after_seconds = 30 });
        }));
        try
        {
            await client.RequestAsync();
            var path = Directory.GetFiles(root, "workload-request-*.json").Single();
            var before = await File.ReadAllTextAsync(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ReleaseAsync(historical, ledger, 6));
            await client.ConfirmArchivedReleaseAsync(historical, ledger, 6);
            await client.ConfirmArchivedReleaseAsync(historical, ledger, 6);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            await client.RequestAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    private const ulong Now = 1790409600000;
    private static readonly Guid Client = Guid.NewGuid();
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)Now); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => send(r); }
    private static HttpResponseMessage Response(object data) => new(HttpStatusCode.OK)
    { Content = new StringContent(Encoding.UTF8.GetString(CoordinatorAllocation.Wrap(data)), Encoding.UTF8, "application/json") };
    private static CoordinatorAllocationEnvelope Grant(Guid id)
    {
        var e = Envelope();
        return new(1, id, Binding.CoordinatorInstanceId, Binding.CatalogId, Binding.RigId, Client, Binding.ProfileId,
            e.Revision, Now - 500, e with { Program = e.Program with { Assignment = e.Program.Assignment with { Id = $"allocation-{id:D}" } } });
    }
    private static string Root() => Path.Combine(Path.GetTempPath(), $"director-workload-{Guid.NewGuid():N}");
    private static CoordinatorWorkloadClient Create(string root, Handler handler, bool localTargets = false) => new(root, Endpoint, Binding, Client, Configuration,
        _ => ValueTask.FromResult<string?>("test-token"), handler, new Clock(), localTargetScheduling: localTargets);

    [Fact]
    public async Task LostReplyCannotChangeTheExecutorsMode()
    {
        var root = Root();
        try
        {
            using (var first = Create(root, new(_ => throw new HttpRequestException())))
                await Assert.ThrowsAsync<CoordinatorIntakeException>(() => first.RequestAsync());
            using var changed = Create(root, new(_ => throw new Xunit.Sdk.XunitException("Changed mode must not send HTTP.")), true);
            await Assert.ThrowsAsync<InvalidDataException>(() => changed.RequestAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConfirmedWaitingCanChangeModeWithoutDiscardingItsRequestIdentity()
    {
        var root = Root(); Guid id = default;
        try
        {
            Handler Wire(string mode) => new(async r =>
            {
                var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement;
                Assert.Equal(mode, body.GetProperty("execution_mode").GetString());
                var requested = body.GetProperty("request_id").GetGuid();
                if (id == Guid.Empty) id = requested; else Assert.Equal(id, requested);
                return Response(new { request_id = id, state = "waiting", workload = (object?)null, retry_after_seconds = 30 });
            });
            using (var first = Create(root, Wire("prepared_target_v2"))) Assert.Null((await first.RequestAsync()).Allocation);
            using var changed = Create(root, Wire("local_sequence_v2"), true);
            Assert.Null((await changed.RequestAsync()).Allocation);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeRetriesAmbiguousLegacyRequestBeforeAdvertisingNewCapabilities(bool localTargets)
    {
        var root = Root();
        try
        {
            using (var first = Create(root, new(_ => throw new HttpRequestException()), localTargets))
                await Assert.ThrowsAsync<CoordinatorIntakeException>(() => first.RequestAsync());
            var path = Directory.GetFiles(root, "workload-request-*.json").Single();
            var state = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var legacy = localTargets ? "local_sequence_v1" : "prepared_target_v1";
            state["execution_mode"] = legacy;
            await File.WriteAllTextAsync(path, state.ToJsonString());
            var requests = 0;
            using var restarted = Create(root, new(async request =>
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                Assert.Equal(requests++ == 0 ? legacy : localTargets ? "local_sequence_v2" : "prepared_target_v2",
                    body.GetProperty("execution_mode").GetString());
                Assert.Equal(state["request_id"]!.GetValue<string>(), body.GetProperty("request_id").GetString());
                return Response(new { request_id = body.GetProperty("request_id").GetGuid(), state = "waiting", workload = (object?)null, retry_after_seconds = 30 });
            }), localTargets);
            Assert.Null((await restarted.RequestAsync()).Allocation);
            Assert.Null((await restarted.RequestAsync()).Allocation);
            Assert.Equal(2, requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LostResponseAndRestartKeepRequestIdentityUntilVerifiedRelease()
    {
        var root = Root(); Guid first = default; var calls = 0; CoordinatorAllocation? allocation = null;
        var grants = new Dictionary<Guid, CoordinatorAllocationEnvelope>();
        Handler Wire(bool lose) => new(async r =>
        {
            var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal(Binding.ProfileId.ToString("D"), Assert.Single(r.Headers.GetValues("X-PSF-Director-Profile")));
            if (r.RequestUri!.AbsolutePath.EndsWith("/request"))
            {
                var id = body.GetProperty("request_id").GetGuid(); calls++;
                if (first == Guid.Empty) first = id;
                if (calls <= 3) Assert.Equal(first, id); else Assert.NotEqual(first, id);
                if (lose) throw new HttpRequestException("secret-do-not-log");
                if (!grants.TryGetValue(id, out var grant)) grants[id] = grant = Grant(id);
                return Response(new { request_id = id, state = "issued", workload = new { allocation = grant, released = false, ledger_id = (Guid?)null, terminal_sequence = (ulong?)null }, retry_after_seconds = 0 });
            }
            var ledger = body.GetProperty("ledger_id").GetGuid();
            return Response(new { allocation = allocation!.Envelope, released = true, ledger_id = ledger, terminal_sequence = body.GetProperty("terminal_sequence").GetUInt64() });
        });
        try
        {
            using (var c = Create(root, Wire(true)))
            {
                var e = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => c.RequestAsync());
                Assert.DoesNotContain("secret-do-not-log", e.ToString());
            }
            using (var c = Create(root, Wire(false)))
            {
                allocation = (await c.RequestAsync()).Allocation!;
                Assert.Equal(allocation.Fingerprint, (await c.RequestAsync()).Allocation!.Fingerprint);
                var a = allocation.Envelope.Snapshot.Program.Assignment;
                await c.ReleaseAsync(allocation, new(Guid.NewGuid().ToString("D"), a.Id, a.Revision, a.RigId, a.ConfigurationId), 2);
            }
            using (var c = Create(root, Wire(false))) Assert.NotEqual(first, (await c.RequestAsync()).Allocation!.Envelope.AllocationId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("missing-field")]
    [InlineData("bad-wait")]
    [InlineData("unexpected-release")]
    [InlineData("null-allocation")]
    [InlineData("null-snapshot")]
    public async Task MalformedResponsesCannotAdvanceRequestOrAuthorizeWork(string fault)
    {
        var root = Root(); Guid id = default;
        try
        {
            using var c = Create(root, new(async r =>
            {
                var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement;
                var requested = body.GetProperty("request_id").GetGuid();
                if (id == Guid.Empty) id = requested; else Assert.Equal(id, requested);
                if (fault == "missing-field") return Response(new { request_id = id, state = "waiting", retry_after_seconds = 30 });
                if (fault == "unexpected-release") return Response(new { request_id = id, state = "released", workload = new { allocation = Grant(id), released = true, ledger_id = (Guid?)null, terminal_sequence = (ulong?)null }, retry_after_seconds = 0 });
                if (fault == "null-allocation") return Response(new { request_id = id, state = "issued", workload = new { allocation = (object?)null, released = false, ledger_id = (Guid?)null, terminal_sequence = (ulong?)null }, retry_after_seconds = 0 });
                if (fault == "null-snapshot")
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(CoordinatorAllocation.Wrap(new { request_id = id, state = "issued", workload = new { allocation = Grant(id), released = false, ledger_id = (Guid?)null, terminal_sequence = (ulong?)null }, retry_after_seconds = 0 }))!;
                    json["data"]!["workload"]!["allocation"]!["snapshot"] = null;
                    return new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
                }
                return Response(new { request_id = fault == "wrong-id" ? Guid.NewGuid() : id, state = "waiting", workload = (object?)null, retry_after_seconds = 0 });
            }));
            await Assert.ThrowsAsync<CoordinatorIntakeException>(() => c.RequestAsync());
            await Assert.ThrowsAsync<CoordinatorIntakeException>(() => c.RequestAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WaitingUsesBoundedBackoffWithoutCreatingAnotherRequest()
    {
        var root = Root(); Guid id = default;
        try
        {
            using var c = Create(root, new(async r =>
            {
                var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement;
                var requested = body.GetProperty("request_id").GetGuid();
                if (id == Guid.Empty) id = requested; else Assert.Equal(id, requested);
                return Response(new { request_id = id, state = "waiting", workload = (object?)null, retry_after_seconds = 30 });
            }));
            for (var i = 0; i < 3; i++) { var result = await c.RequestAsync(); Assert.Null(result.Allocation); Assert.Equal(30, result.RetryAfterSeconds); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LostReleaseReplyRecoversTerminalHistoryWithoutLaunchingAgain()
    {
        var root = Root(); CoordinatorAllocationEnvelope? grant = null; Guid ledgerId = default; var released = false; Guid next = default;
        try
        {
            using var c = Create(root, new(async r =>
            {
                var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement;
                if (r.RequestUri!.AbsolutePath.EndsWith("/release"))
                {
                    ledgerId = body.GetProperty("ledger_id").GetGuid(); released = true;
                    throw new HttpRequestException("ambiguous reply");
                }
                var id = body.GetProperty("request_id").GetGuid();
                if (grant is null) grant = Grant(id);
                if (id != grant.AllocationId)
                {
                    next = id;
                    return Response(new { request_id = id, state = "waiting", workload = (object?)null, retry_after_seconds = 30 });
                }
                return Response(new
                {
                    request_id = id,
                    state = released ? "released" : "issued",
                    workload = new { allocation = grant, released, ledger_id = released ? ledgerId : (Guid?)null, terminal_sequence = released ? 2UL : (ulong?)null },
                    retry_after_seconds = 0
                });
            }));
            var a = (await c.RequestAsync()).Allocation!;
            var p = a.Envelope.Snapshot.Program.Assignment;
            var ledger = new LedgerIdentity(Guid.NewGuid().ToString("D"), p.Id, p.Revision, p.RigId, p.ConfigurationId);
            await Assert.ThrowsAsync<CoordinatorIntakeException>(() => c.ReleaseAsync(a, ledger, 2));
            Assert.Null((await c.RequestAsync()).Allocation);
            Assert.Null((await c.RequestAsync()).Allocation);
            Assert.NotEqual(Guid.Empty, next);
            Assert.NotEqual(a.Envelope.AllocationId, next);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AReceivedGrantCannotChangeUnderTheSameRequestId()
    {
        var root = Root(); CoordinatorAllocationEnvelope? grant = null;
        try
        {
            using var c = Create(root, new(async r =>
            {
                var id = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.GetProperty("request_id").GetGuid();
                if (grant is null) grant = Grant(id);
                else grant = grant with { PreviewRevision = new string('b', 64) };
                return Response(new { request_id = id, state = "issued", workload = new { allocation = grant, released = false, ledger_id = (Guid?)null, terminal_sequence = (ulong?)null }, retry_after_seconds = 0 });
            }));
            Assert.NotNull((await c.RequestAsync()).Allocation);
            var e = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => c.RequestAsync());
            Assert.Equal(CoordinatorIntakeFailure.IdentityMismatch, e.Failure);
        }
        finally { Directory.Delete(root, true); }
    }
}
