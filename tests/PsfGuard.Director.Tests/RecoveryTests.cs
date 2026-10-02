using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class RecoveryTests
{
    private static readonly RecoveryIdentity Identity = new("rig-test", "config", "night", 1000, 100000);
    private static readonly RecoveryPolicy Policy = new(1, RecoveryQualityMode.Pause, 2, 1, 100, 10000, 3, 1000, 500, 90000, 3, 10, true);
    private static readonly RecoveryConditions Conditions = new(PlannerSafety.Safe, RecoveryMotion.Permitted);
    private static readonly RecoveryQualityContext Context = new("target", "L", 1000, 1, 1, "reference", "test", "v1");
    private static RecoveryRecord Record(ulong revision = 0, RecoveryPhase? phase = null) => new(revision,
        new(1, Identity, Policy, 1000 + revision, phase ?? new RecoveryPhase.Acquiring(), 0, null, null, [], 0, 0, 0));
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, RecoveryContract.Options)!;
    private static JsonObject Current(RecoveryRecord record) => new() { ["status"] = "current", ["record"] = Node(record) };
    private static RecoveryRequest Request(RecoveryEvent input, ulong revision = 0, ulong now = 1001) =>
        new("night", "config", "event-" + revision, revision, now, Conditions, input);

    [Theory]
    [InlineData("recovery-mode")]
    [InlineData("recovery-version")]
    public async Task IncompatibleRecoveryHandshakeFails(string fault) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestPeer.CreateAsync(fault));

    [Fact]
    public async Task RecoveryIsOptIn()
    {
        await using var peer = await TestPeer.CreateAsync("none");
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.ReadRecoveryAsync(default));
        Assert.True(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("enum")]
    [InlineData("enum-case")]
    [InlineData("enum-number")]
    [InlineData("revision")]
    [InlineData("negative")]
    [InlineData("duplicate")]
    public async Task MalformedSnapshotsAbortTheSession(string fault)
    {
        var reply = Current(Record());
        var state = reply["record"]!["snapshot"]!;
        switch (fault)
        {
            case "identity": state["identity"]!["rig_id"] = "other"; break;
            case "missing": state.AsObject().Remove("quality_context"); break;
            case "null": state["identity"] = null; break;
            case "unknown": state["surprise"] = true; break;
            case "enum": state["policy"]!["quality_mode"] = "unexpected"; break;
            case "enum-case": state["policy"]!["quality_mode"] = "PAUSE"; break;
            case "enum-number": state["policy"]!["quality_mode"] = 2; break;
            case "revision": reply["record"]!["revision"] = 100001; break;
            case "negative": state["total_hold_ms"] = -1; break;
            case "duplicate":
                using (var json = JsonDocument.Parse("{\"status\":\"current\",\"status\":\"current\"}"))
                    Assert.Throws<InvalidDataException>(() => RecoveryContract.CheckJson(json.RootElement));
                return;
        }
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ => reply);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ReadRecoveryAsync(default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("regression")]
    [InlineData("same-revision")]
    [InlineData("policy")]
    [InlineData("disappeared")]
    [InlineData("reopen")]
    public async Task CachedProgressCannotBeRewritten(string fault)
    {
        var reads = 0;
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ =>
        {
            if (++reads == 1) return Current(Record(1));
            return fault switch
            {
                "regression" => Current(Record()),
                "same-revision" => Current(Record(1, new RecoveryPhase.Stopped(new RecoveryCause.Operator(), RecoveryShutdown.Parked))),
                "policy" => Current(Record(2) with { Snapshot = Record(2).Snapshot with { Policy = Policy with { Revision = 2 } } }),
                "disappeared" => new() { ["status"] = "current", ["record"] = null },
                _ => new() { ["status"] = "opened", ["created"] = true, ["record"] = Node(Record()) }
            };
        });
        await peer.Session.ReadRecoveryAsync(default);
        if (fault == "reopen")
            await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.OpenRecoveryAsync(Identity, Policy, 1000, default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ReadRecoveryAsync(default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("replay")]
    [InlineData("attempt")]
    [InlineData("deadline")]
    [InlineData("motion")]
    [InlineData("event")]
    public async Task InvalidIssuanceCannotAuthorizeWork(string fault)
    {
        var reads = 0;
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ => ++reads == 1 ? Current(Record()) : new()
        {
            ["status"] = "applied",
            ["newly_applied"] = fault != "replay",
            ["record"] = Node(Record(1, new RecoveryPhase.Stopping(new RecoveryCause.Operator(), 2001, "park"))),
            ["issued"] = Node(new RecoveryIssued("park", fault == "attempt" ? "wrong" : "park", fault == "deadline" ? 1001UL : 2001UL))
        });
        await peer.Session.ReadRecoveryAsync(default);
        var request = Request(fault == "event" ? new RecoveryEvent.Tick() : new RecoveryEvent.BeginPark("park"));
        if (fault == "motion") request = request with { Conditions = Conditions with { Motion = RecoveryMotion.Unknown } };
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ApplyRecoveryAsync(request, default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("night")]
    [InlineData("cursor")]
    public async Task InvalidEventPagesAbort(string fault)
    {
        var request = Request(new RecoveryEvent.Tick());
        if (fault == "night") request = request with { NightId = "other" };
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ => new()
        {
            ["status"] = "events",
            ["events"] = new JsonArray(Node(new RecoveryJournalEvent(fault == "gap" ? 2UL : 1UL, request))),
            ["next_cursor"] = fault == "cursor" ? 2 : 1
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ReadRecoveryEventsAsync("night", 0, 16, default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task TypedDomainErrorsDoNotFaultTheSession()
    {
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ => new() { ["status"] = "error", ["code"] = "busy" });
        Assert.Equal(RecoveryError.Busy, (await peer.Session.ReadRecoveryAsync(default)).Error);
        Assert.True(peer.Session.IsReady);
    }

    [Fact]
    public void AllEventVariantsRoundTripWithoutLosingDiscriminators()
    {
        var sample = new RecoveryQualitySample("rig-test", "config", "capture", 1001, Context, RecoveryVerdict.ConfirmedGood);
        RecoveryEvent[] events = [new RecoveryEvent.Tick(), new RecoveryEvent.Quality(sample),
            new RecoveryEvent.OperationFailure(new("failure", RecoveryOperation.Guide, "guider", "target", false)),
            new RecoveryEvent.BeginRecovery("probe"), new RecoveryEvent.RecoveryCompleted("probe", new RecoveryOutcome.Quality(sample)),
            new RecoveryEvent.RecoveryCompleted("probe", new RecoveryOutcome.EquipmentVerified()),
            new RecoveryEvent.RecoveryCompleted("probe", new RecoveryOutcome.Failed()),
            new RecoveryEvent.RecoveryCompleted("probe", new RecoveryOutcome.Uncertain()),
            new RecoveryEvent.StopNight(), new RecoveryEvent.BeginPark("park"), new RecoveryEvent.ParkCompleted("park", RecoveryParkResult.Parked)];
        foreach (var input in events)
            Assert.Equal(input, RecoveryContract.Read<RecoveryEvent>(JsonSerializer.SerializeToElement(input, RecoveryContract.Options)));
    }

    [Fact]
    public async Task InterruptedApplyFaultsInsteadOfRetryingUnknownWork()
    {
        await using var peer = await TestPeer.CreateAsync("stall-recovery-apply", recovery: _ => Current(Record()));
        await peer.Session.ReadRecoveryAsync(default);
        using var cancel = new CancellationTokenSource();
        var pending = peer.Session.ApplyRecoveryAsync(Request(new RecoveryEvent.StopNight()), cancel.Token);
        await peer.ReservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task RealRuntimeQualityHoldAndProbeUseTheSharedPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "director-recovery-test-" + Guid.NewGuid().ToString("N"));
        var ledger = Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName;
        var recovery = Directory.CreateDirectory(Path.Combine(root, "recovery")).FullName;
        try
        {
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, ledger, recovery);
            await controller.StartAsync("rig-test");
            Assert.Equal(RuntimeState.Ready, controller.Status.State);
            Assert.True((await controller.OpenRecoveryAsync(Identity, Policy, 1000)).Value!.Created);
            RecoveryQualitySample Sample(string id, ulong time, RecoveryVerdict verdict) => new("rig-test", "config", id, time, Context, verdict);
            await controller.ApplyRecoveryAsync(Request(new RecoveryEvent.Quality(Sample("a", 1001, RecoveryVerdict.CorroboratedPoor))));
            var hold = (await controller.ApplyRecoveryAsync(Request(new RecoveryEvent.Quality(Sample("b", 1002, RecoveryVerdict.CorroboratedPoor)), 1, 1002))).Value!;
            Assert.IsType<RecoveryPhase.Holding>(hold.Record.Snapshot.Phase);
            var probe = (await controller.ApplyRecoveryAsync(Request(new RecoveryEvent.BeginRecovery("probe"), 2, 1102))).Value!;
            Assert.Equal("probe", probe.Issued!.Operation);
            Assert.IsType<RecoveryPhase.Recovering>(probe.Record.Snapshot.Phase);
            var done = (await controller.ApplyRecoveryAsync(Request(new RecoveryEvent.RecoveryCompleted("probe",
                new RecoveryOutcome.Quality(Sample("good", 1103, RecoveryVerdict.ConfirmedGood))), 3, 1103))).Value!;
            Assert.IsType<RecoveryPhase.Acquiring>(done.Record.Snapshot.Phase);
            Assert.Null(done.Issued);
            var page = (await controller.ReadRecoveryEventsAsync("night", 0, 16)).Value!;
            Assert.Equal(4, page.Events.Length);
            Assert.IsType<RecoveryEvent.Quality>(page.Events[0].Request.Event);
            await controller.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RealRuntimePersistsStopAndNeverReissuesParkAfterReplayOrRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "director-recovery-test-" + Guid.NewGuid().ToString("N"));
        var ledger = Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName;
        var recovery = Directory.CreateDirectory(Path.Combine(root, "recovery")).FullName;
        try
        {
            await using (var session = await RuntimeSession.StartAsync(ProcessTests.BundleDirectory, "rig-test", default, ledger, recovery))
            {
                Assert.Null((await session.ReadRecoveryAsync(default)).Value!.Record);
                Assert.True((await session.OpenRecoveryAsync(Identity, Policy, 1000, default)).Value!.Created);
                var stop = await session.ApplyRecoveryAsync(Request(new RecoveryEvent.StopNight()), default);
                Assert.IsType<RecoveryPhase.Stopping>(stop.Value!.Record.Snapshot.Phase);
                var begin = Request(new RecoveryEvent.BeginPark("park"), 1, 1002);
                var issued = (await session.ApplyRecoveryAsync(begin, default)).Value!;
                Assert.Equal("park", issued.Issued!.Operation);
                var replay = (await session.ApplyRecoveryAsync(begin, default)).Value!;
                Assert.False(replay.NewlyApplied);
                Assert.Null(replay.Issued);
                var page = (await session.ReadRecoveryEventsAsync("night", 0, 16, default)).Value!;
                Assert.Equal(2UL, page.NextCursor);
                Assert.Equal(begin, page.Events[1].Request);
                await session.ShutdownAsync(default);
            }
            await using (var session = await RuntimeSession.StartAsync(ProcessTests.BundleDirectory, "rig-test", default, ledger, recovery))
            {
                var current = (await session.ReadRecoveryAsync(default)).Value!.Record!;
                Assert.Equal("park", Assert.IsType<RecoveryPhase.Stopping>(current.Snapshot.Phase).ParkAttemptId);
                var replay = (await session.ApplyRecoveryAsync(Request(new RecoveryEvent.BeginPark("park"), 1, 1002), default)).Value!;
                Assert.False(replay.NewlyApplied);
                Assert.Null(replay.Issued);
                var done = (await session.ApplyRecoveryAsync(Request(new RecoveryEvent.ParkCompleted("park", RecoveryParkResult.Parked), 2, 1003), default)).Value!;
                Assert.Equal(RecoveryShutdown.Parked, Assert.IsType<RecoveryPhase.Stopped>(done.Record.Snapshot.Phase).Shutdown);
                await session.ShutdownAsync(default);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
