using System.Net;
using System.Net.Http;
using System.Text.Json;
using PsfGuard.Director.Runtime;
using Xunit;
using static PsfGuard.Director.Tests.CoordinatorProgramTests;

namespace PsfGuard.Director.Tests;

public sealed class CoordinatorAllocationTests
{
    private const ulong Now = 1790409600000;
    private static readonly Guid ClientId = Guid.NewGuid();
    private static CoordinatorAllocationEnvelope Allocation()
    {
        var snapshot = Envelope();
        var id = Guid.NewGuid();
        return new(1, id, Binding.CoordinatorInstanceId, Binding.CatalogId, Binding.RigId, ClientId, Binding.ProfileId,
            snapshot.Revision, Now - 500, snapshot with
            {
                Program = snapshot.Program with
                { Assignment = snapshot.Program.Assignment with { Id = $"allocation-{id:D}" } }
            });
    }
    private static CoordinatorAllocation Read(CoordinatorAllocationEnvelope value, ulong now = Now) =>
        CoordinatorAllocation.Read(CoordinatorAllocation.Wrap(value), Endpoint.AbsoluteUri, Binding, ClientId, Configuration, now);

    [Theory]
    [InlineData("client")]
    [InlineData("profile")]
    [InlineData("rig")]
    [InlineData("catalog")]
    [InlineData("instance")]
    [InlineData("schema")]
    [InlineData("preview-id")]
    [InlineData("admitted-before-issue")]
    [InlineData("admitted-after-expiry")]
    [InlineData("expired")]
    [InlineData("changed-scope")]
    [InlineData("unknown-field")]
    public void RefusesWrongIdentityPreviewSubstitutionAndMalformedGrants(string fault)
    {
        var a = Allocation();
        a = fault switch
        {
            "client" => a with { ClientId = Guid.NewGuid() },
            "profile" => a with { ProfileId = Guid.NewGuid() },
            "rig" => a with { RigId = Guid.NewGuid() },
            "catalog" => a with { CatalogId = Guid.NewGuid() },
            "instance" => a with { CoordinatorInstanceId = Guid.NewGuid() },
            "schema" => a with { SchemaVersion = 2 },
            "preview-id" => a with { Snapshot = Envelope() },
            "admitted-before-issue" => a with { AdmittedAtMs = a.Snapshot.IssuedAtMs - 1 },
            "admitted-after-expiry" => a with { AdmittedAtMs = a.Snapshot.Program.Assignment.ExpiresAtMs },
            "changed-scope" => a with { Snapshot = a.Snapshot with { CatalogId = Guid.NewGuid() } },
            _ => a
        };
        var bytes = CoordinatorAllocation.Wrap(a);
        if (fault == "unknown-field")
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
            json["data"]!["token"] = "do-not-log";
            bytes = JsonSerializer.SerializeToUtf8Bytes(json);
        }
        var error = Assert.Throws<CoordinatorIntakeException>(() => CoordinatorAllocation.Read(bytes, Endpoint.AbsoluteUri,
            Binding, ClientId, Configuration, fault == "expired" ? Now + 60000 : Now));
        Assert.DoesNotContain("do-not-log", error.ToString());
    }

    [Fact]
    public void ServerAdmissionTimestampCanBeAheadOfAnAlreadyOpenObservingWindow()
    {
        var grant = Allocation() with { AdmittedAtMs = Now + 80 };
        var allocation = Read(grant);
        Assert.Equal(grant.AdmittedAtMs, allocation.Envelope.AdmittedAtMs);
    }

    [Fact]
    public void CacheSurvivesRestartRefusesChangedAllocationAndKeepsExpiredBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"director-allocation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var a = Read(Allocation());
            var cache = new CoordinatorAllocationCache(root, Endpoint, Binding, ClientId, Configuration);
            cache.Store(a, Now);
            cache = new(root, Endpoint, Binding, ClientId, Configuration);
            Assert.Equal(a.Fingerprint, cache.Read(Now)!.Fingerprint);
            cache.Store(a, Now);
            Assert.Throws<CoordinatorIntakeException>(() => cache.Store(Read(Allocation()), Now));
            Assert.Throws<CoordinatorIntakeException>(() => cache.Read(Now + 60000));
            var next = Allocation();
            next = next with
            {
                AdmittedAtMs = Now + 70000,
                Snapshot = next.Snapshot with
                {
                    IssuedAtMs = Now + 70000,
                    Program = next.Snapshot.Program with
                    {
                        Assignment = next.Snapshot.Program.Assignment with
                        { ValidFromMs = Now + 70000, ExpiresAtMs = Now + 120000 }
                    }
                }
            };
            Assert.Throws<CoordinatorIntakeException>(() => cache.Store(Read(next, Now + 70000), Now + 70000));
            var changedConfiguration = new CoordinatorAllocationCache(root, Endpoint, Binding, ClientId, Configuration with { Id = "changed" });
            Assert.Throws<CoordinatorIntakeException>(() => changedConfiguration.Read(Now));
            Assert.Equal(a.Fingerprint, cache.Read(Now)!.Fingerprint);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Clock(int offsetMs = 0) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)Now + offsetMs);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("configuration")]
    [InlineData("revision")]
    [InlineData("client")]
    public async Task LaunchRejectsMismatchedLedgerBeforePosting(string fault)
    {
        var allocation = Read(Allocation());
        var a = allocation.Envelope.Snapshot.Program.Assignment;
        var ledger = new LedgerIdentity(Guid.NewGuid().ToString("D"), a.Id, a.Revision, a.RigId, a.ConfigurationId);
        ledger = fault switch
        {
            "ledger" => ledger with { LedgerId = Guid.Empty.ToString("D") },
            "configuration" => ledger with { ConfigurationId = "changed" },
            "revision" => ledger with { AssignmentRevision = a.Revision + 1 },
            _ => ledger
        };
        using var client = new CoordinatorAllocationClient(Endpoint, _ => ValueTask.FromResult<string?>("test-token"),
            new Handler(_ => throw new InvalidOperationException("Must not post")), new Clock());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.StartOnceAsync(Binding, fault == "client" ? Guid.NewGuid() : ClientId, allocation, ledger));
    }

    [Theory]
    [InlineData(false, -7200000)]
    [InlineData(false, 7200000)]
    [InlineData(true, 0)]
    public async Task LaunchPostsOnceAndNeverRetriesConflict(bool conflict, int rigOffsetMs)
    {
        var allocation = Read(Allocation());
        var a = allocation.Envelope.Snapshot.Program.Assignment;
        var ledger = new LedgerIdentity(Guid.NewGuid().ToString("D"), a.Id, a.Revision, a.RigId, a.ConfigurationId);
        var calls = 0;
        using var client = new CoordinatorAllocationClient(Endpoint, _ => ValueTask.FromResult<string?>("test-token"), new Handler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/allocation/start", request.RequestUri!.AbsolutePath);
            if (conflict) return new(HttpStatusCode.Conflict);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(System.Text.Encoding.UTF8.GetString(CoordinatorAllocation.Wrap(allocation.Envelope)), System.Text.Encoding.UTF8, "application/json") };
            response.Headers.Date = DateTimeOffset.FromUnixTimeMilliseconds((long)Now);
            return response;
        }), new Clock(rigOffsetMs));
        if (conflict) await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.StartOnceAsync(Binding, ClientId, allocation, ledger));
        else await client.StartOnceAsync(Binding, ClientId, allocation, ledger);
        Assert.Equal(1, calls);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request)); }

    [Theory]
    [InlineData("valid")]
    [InlineData("json")]
    [InlineData("missing")]
    [InlineData("rejected")]
    [InlineData("unknown")]
    [InlineData("changed")]
    public async Task StatusAcknowledgementIsStrictAndDoesNotLeakResponse(string fault)
    {
        var allocation = Read(Allocation());
        var reply = System.Text.Json.Nodes.JsonNode.Parse(CoordinatorAllocation.Wrap(new
        { accepted = fault != "rejected", program_revision = allocation.Envelope.PreviewRevision, program_changed = false, received_at_ms = Now }))!;
        if (fault == "unknown") reply["data"]!["secret"] = "do-not-log";
        if (fault == "changed") reply["data"]!["program_changed"] = true;
        if (fault == "missing") reply["data"]!.AsObject().Remove("accepted");
        using var reporter = new CoordinatorSessionReporter(Endpoint, Binding, _ => ValueTask.FromResult<string?>("test-token"),
            new Handler(request => new(HttpStatusCode.OK)
            { Content = new StringContent(fault == "json" ? "do-not-log" : reply.ToJsonString(), System.Text.Encoding.UTF8, "application/json") }));
        var work = reporter.ReportAsync(allocation, Guid.NewGuid().ToString("D"), "Acquiring", "Target", "Safe", default);
        if (fault == "valid") await work;
        else
        {
            var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => work);
            Assert.Equal(CoordinatorIntakeFailure.InvalidAcknowledgement, error.Failure);
            Assert.DoesNotContain("do-not-log", error.ToString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SequenceActivityUsesScopedStatusWithoutAnAllocation(bool running)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)Now);
        var session = Guid.NewGuid().ToString("D");
        using var reporter = new CoordinatorSessionReporter(Endpoint, Binding, _ => ValueTask.FromResult<string?>("test-token"),
            new Handler(request =>
            {
                Assert.EndsWith($"/rigs/{Binding.RigId:D}/status", request.RequestUri!.AbsolutePath);
                Assert.Equal(Binding.ProfileId.ToString("D"), Assert.Single(request.Headers.GetValues("X-PSF-Director-Profile")));
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var body = json.RootElement;
                var status = body.GetProperty("status");
                Assert.Equal(session, body.GetProperty("session_id").GetString());
                Assert.Equal((long)Now, body.GetProperty("reported_at_ms").GetInt64());
                Assert.Equal(JsonValueKind.Null, body.GetProperty("program_revision").ValueKind);
                Assert.Equal(JsonValueKind.Null, status.GetProperty("allocation_id").ValueKind);
                Assert.Equal(running ? "N.I.N.A. sequence running" : "N.I.N.A. sequence stopped", status.GetProperty("phase").GetString());
                Assert.Equal(running ? "Start Sequence › Cool Camera" : null, status.GetProperty("operation").GetString());
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(System.Text.Encoding.UTF8.GetString(CoordinatorAllocation.Wrap(new
                    {
                        accepted = true,
                        program_revision = new string('a', 64),
                        program_changed = true,
                        received_at_ms = Now
                    })), System.Text.Encoding.UTF8, "application/json")
                };
            }));
        await reporter.ReportSequenceAsync(session, running, running ? "Start Sequence › Cool Camera" : null, at, default);
    }

    [Fact]
    public async Task IntakeUsesScopedCredentialAndDoesNotFallbackOnRevocation()
    {
        var envelope = Allocation();
        using var client = new CoordinatorAllocationClient(Endpoint, _ => ValueTask.FromResult<string?>("test-token"), new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/allocation", request.RequestUri!.AbsolutePath);
            Assert.Contains($"catalog_id={Binding.CatalogId:D}", request.RequestUri.Query);
            Assert.Equal(Binding.ProfileId.ToString("D"), Assert.Single(request.Headers.GetValues("X-PSF-Director-Profile")));
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Encoding.UTF8.GetString(CoordinatorAllocation.Wrap(envelope)), System.Text.Encoding.UTF8, "application/json") };
        }), new Clock());
        Assert.Equal(envelope.AllocationId, (await client.ReadAsync(Binding, ClientId, Configuration)).Envelope.AllocationId);
        using var revoked = new CoordinatorAllocationClient(Endpoint, _ => ValueTask.FromResult<string?>("test-token"),
            new Handler(_ => new(HttpStatusCode.Unauthorized)), new Clock());
        Assert.Equal(CoordinatorIntakeFailure.AuthenticationRequired,
            (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => revoked.ReadAsync(Binding, ClientId, Configuration))).Failure);
    }
}
