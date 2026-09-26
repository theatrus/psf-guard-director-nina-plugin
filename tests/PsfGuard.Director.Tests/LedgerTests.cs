using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class LedgerTests
{
    private const string LedgerId = "3924de93-0f42-4b12-8036-66aa03fabdd0";

    private static JsonObject Opened() => new()
    {
        ["status"] = "opened",
        ["info"] = new JsonObject
        {
            ["ledger_id"] = LedgerId,
            ["assignment_id"] = "assignment",
            ["assignment_revision"] = PlannerTests.Request().Assignment.Revision,
            ["rig_id"] = "rig-test",
            ["configuration_id"] = "config"
        }
    };

    private static JsonObject Attempt() => new()
    {
        ["capture_id"] = "capture",
        ["goal_id"] = "goal",
        ["reserved_at_ms"] = 2000,
        ["evidence"] = new JsonObject { ["state"] = "reserved" }
    };

    private static JsonObject Reserved() => new()
    {
        ["status"] = "reserved",
        ["outcome"] = new JsonObject { ["status"] = "created", ["value"] = Attempt() }
    };

    private static JsonObject Page() => new()
    {
        ["status"] = "events",
        ["next_cursor"] = 1,
        ["events"] = new JsonArray(new JsonObject
        {
            ["schema_version"] = 1,
            ["ledger_id"] = LedgerId,
            ["sequence"] = 1,
            ["contract_version"] = RuntimeContract.ContractVersion,
            ["engine_version"] = RuntimeContract.EngineVersion,
            ["assignment_id"] = "assignment",
            ["assignment_revision"] = PlannerTests.Request().Assignment.Revision,
            ["rig_id"] = "rig-test",
            ["configuration_id"] = "config",
            ["attempt"] = Attempt()
        })
    };

    [Fact]
    public async Task RealLedgerRecoversUncertainCaptureAndCreditsSavedImageOnlyOnce()
    {
        var directory = Directory.CreateTempSubdirectory("director-ledger-test-");
        try
        {
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, TimeSpan.FromMilliseconds(20), directory.FullName);
            var request = PlannerTests.Request();
            await controller.StartAsync("rig-test");
            Assert.Equal(RuntimeState.Ready, controller.Status.State);
            var identity = (await controller.OpenLedgerAsync(request)).Value!;
            Assert.Equal(request.Assignment.Revision, identity.AssignmentRevision);
            Assert.Equal(ReservationKind.Created, (await controller.ReserveAsync("capture", request.State)).Value!.Kind);
            Assert.Equal(ReservationKind.Existing, (await controller.ReserveAsync("capture", request.State)).Value!.Kind);
            var blocked = (await controller.ReserveAsync("another", request.State)).Value!;
            Assert.Equal(ReservationKind.RecoveryRequired, blocked.Kind);
            Assert.Equal("capture", blocked.Attempt!.CaptureId);
            Assert.Null((await controller.FindAttemptAsync("missing")).Value!.Attempt);
            Assert.Equal(LedgerError.UnknownCapture, (await controller.RecordAsync("missing", new LedgerEvidence.Failed("not-started"))).Error);
            await controller.RecordAsync("capture", new LedgerEvidence.Uncertain("save-unconfirmed"));
            await controller.StopAsync();
            await controller.StartAsync("rig-test");
            Assert.Equal(identity, (await controller.OpenLedgerAsync(request)).Value);
            Assert.IsType<LedgerEvidence.Uncertain>((await controller.FindAttemptAsync("capture")).Value!.Attempt!.Evidence);
            Assert.Equal(ReservationKind.RecoveryRequired, (await controller.ReserveAsync("another", request.State)).Value!.Kind);
            var saved = new LedgerEvidence.Saved("image", 9007199254740993UL);
            Assert.Equal(saved, (await controller.RecordAsync("capture", saved)).Value!.Evidence);
            Assert.Equal(saved, (await controller.RecordAsync("capture", saved)).Value!.Evidence);
            Assert.Equal(LedgerError.ConflictingEvidence, (await controller.RecordAsync("capture", new LedgerEvidence.Failed("late"))).Error);
            var decision = (await controller.ReserveAsync("another", request.State)).Value!;
            Assert.Equal(ReservationKind.Decision, decision.Kind);
            Assert.Equal(new(PlannerAction.Wait, "pending_assessment"), decision.Decision);
            var first = (await controller.ReadEventsAsync(0, 2)).Value!;
            Assert.Equal(identity, first.Identity);
            Assert.Equal(2UL, first.NextCursor);
            Assert.IsType<LedgerEvidence.Reserved>(first.Events[0].Attempt.Evidence);
            Assert.IsType<LedgerEvidence.Uncertain>(first.Events[1].Attempt.Evidence);
            var last = (await controller.ReadEventsAsync(first.NextCursor)).Value!;
            Assert.Single(last.Events);
            Assert.Equal(saved, last.Events[0].Attempt.Evidence);
            Assert.Equal(3UL, last.NextCursor);
            Assert.Empty((await controller.ReadEventsAsync(last.NextCursor)).Value!.Events);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.EvaluateAsync(request));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.OpenLedgerAsync(request));
            Assert.Equal(RuntimeState.Ready, controller.Status.State);
            await controller.StopAsync();
            await controller.StartAsync("rig-test");
            Assert.Equal(identity, (await controller.OpenLedgerAsync(request)).Value);
            Assert.Equal(saved, (await controller.FindAttemptAsync("capture")).Value!.Attempt!.Evidence);
            Assert.Equal(PlannerAction.Wait, (await controller.ReserveAsync("another", request.State)).Value!.Decision!.Action);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task RealLedgerExclusiveOwnerAndAllocationIdentitySurviveRestart()
    {
        var directory = Directory.CreateTempSubdirectory("director-ledger-test-");
        try
        {
            await using var owner = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await using var contender = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await owner.StartAsync("rig-test");
            var request = PlannerTests.Request();
            var identity = (await owner.OpenLedgerAsync(request)).Value;
            await contender.StartAsync("rig-test");
            Assert.Equal(RuntimeState.Faulted, contender.Status.State);
            Assert.Equal(ReservationKind.Created, (await owner.ReserveAsync("capture", request.State)).Value!.Kind);
            await owner.StopAsync();
            await contender.StartAsync("rig-test");
            var changed = request with { Assignment = request.Assignment with { Revision = 1 } };
            Assert.Equal(LedgerError.AssignmentMismatch, (await contender.OpenLedgerAsync(changed)).Error);
            Assert.Equal(RuntimeState.Ready, contender.Status.State);
            Assert.Equal(identity, (await contender.OpenLedgerAsync(request)).Value);
            Assert.Equal(ReservationKind.Existing, (await contender.ReserveAsync("capture", request.State)).Value!.Kind);
            Assert.Equal(ReservationKind.RecoveryRequired, (await contender.ReserveAsync("retry", request.State)).Value!.Kind);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("ledger_id")]
    [InlineData("assignment_id")]
    [InlineData("assignment_revision")]
    [InlineData("rig_id")]
    [InlineData("configuration_id")]
    [InlineData("extra")]
    public async Task UncorrelatedOpenFaultsSession(string field)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ =>
        {
            var value = Opened();
            value["info"]![field] = field == "assignment_revision" ? JsonValue.Create(1) : JsonValue.Create("wrong");
            return value;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.OpenLedgerAsync(PlannerTests.Request(), default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("goal")]
    [InlineData("time")]
    [InlineData("terminal")]
    [InlineData("kind")]
    [InlineData("acquire")]
    [InlineData("recovery-same")]
    [InlineData("extra")]
    public async Task MalformedReservationCannotBecomeADispatchDecision(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open") return Opened();
            var value = Reserved();
            var outcome = value["outcome"]!;
            var attempt = outcome["value"]!;
            switch (fault)
            {
                case "capture": attempt["capture_id"] = "other"; break;
                case "goal": attempt["goal_id"] = "other"; break;
                case "time": attempt["reserved_at_ms"] = 1999; break;
                case "terminal": attempt["evidence"] = new JsonObject { ["state"] = "failed", ["reason"] = "error" }; break;
                case "kind": outcome["status"] = "new"; break;
                case "acquire": outcome["status"] = "decision"; outcome["value"] = new JsonObject { ["action"] = "acquire", ["reason"] = "unsafe" }; break;
                case "recovery-same": outcome["status"] = "recovery_required"; break;
                case "extra": attempt["permit"] = true; break;
            }
            return value;
        });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ReserveAsync("capture", PlannerTests.Request().State, default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("ledger_id")]
    [InlineData("assignment_revision")]
    [InlineData("rig_id")]
    [InlineData("configuration_id")]
    [InlineData("schema_version")]
    [InlineData("contract_version")]
    [InlineData("engine_version")]
    [InlineData("sequence")]
    [InlineData("cursor")]
    [InlineData("extra")]
    public async Task MalformedEventPagesFaultSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open") return Opened();
            var page = Page();
            if (fault == "cursor") page["next_cursor"] = 2;
            else page["events"]![0]![fault] = fault is "assignment_revision" or "schema_version" or "contract_version" or "sequence"
                ? JsonValue.Create(99) : JsonValue.Create("wrong");
            return page;
        });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ReadEventsAsync(0, 64, default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task RecordMustEchoSubmittedEvidence()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
            operation.GetProperty("action").GetString() == "open" ? Opened()
                : new JsonObject { ["status"] = "recorded", ["attempt"] = Attempt() });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.RecordAsync("capture", new LedgerEvidence.Saved("image", 1), default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task CallerErrorsAndQueuedCancellationPreserveSessionButAdmittedCancellationDoesNot()
    {
        await using var peer = await TestPeer.CreateAsync("stall-ledger", ledger: _ => Opened());
        var request = PlannerTests.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.ReserveAsync("capture", request.State, default));
        await peer.Session.OpenLedgerAsync(request, default);
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.ReserveAsync("bad id", request.State, default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.ReserveAsync("capture", request.State with { RigId = "wrong" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.RecordAsync("capture", new LedgerEvidence.Reserved(), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => peer.Session.ReadEventsAsync(ulong.MaxValue, 64, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => peer.Session.ReadEventsAsync(0, 65, default));
        await peer.Session.PingAsync(default);
        using var cancellation = new CancellationTokenSource();
        var active = peer.Session.ReserveAsync("capture", request.State, cancellation.Token);
        await peer.ReservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var queuedCancellation = new CancellationTokenSource();
        var queued = peer.Session.FindAttemptAsync("capture", queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(peer.Session.IsReady);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task TimeoutInvalidatesSessionWithoutReplayingReservation()
    {
        var opens = 0;
        await using var peer = await TestPeer.CreateAsync("stall-ledger", ledger: _ => { opens++; return Opened(); });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.Session.ReserveAsync("capture", PlannerTests.Request().State, default));
        Assert.Equal(1, opens);
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task StorageIsExplicitAndHandshakeMustMatch()
    {
        await using var peer = await TestPeer.CreateAsync("none");
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.OpenLedgerAsync(PlannerTests.Request(), default));
        await peer.Session.PingAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => TestPeer.CreateAsync("storage"));
        Assert.Throws<ArgumentException>(() => new RuntimeController(ProcessTests.BundleDirectory, "relative"));
    }

    [Fact]
    public void DuplicateFieldsAreRejected()
    {
        using var response = JsonDocument.Parse("{\"status\":\"error\",\"code\":\"busy\",\"code\":\"unknown_capture\"}");
        Assert.Throws<InvalidDataException>(() => LedgerContract.Decode<LedgerIdentity>(response.RootElement, "opened", "info", _ => throw new Exception()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbruptSidecarExitPreservesAttemptAndNeverCreatesAReplacement(bool saved)
    {
        var directory = Directory.CreateTempSubdirectory("director-ledger-test-");
        try
        {
            var request = PlannerTests.Request();
            LedgerIdentity identity;
            await using (var session = await RuntimeSession.StartAsync(ProcessTests.BundleDirectory, "rig-test", default, directory.FullName))
            {
                identity = (await session.OpenLedgerAsync(request, default)).Value!;
                await session.ReserveAsync("capture", request.State, default);
                if (saved) await session.RecordAsync("capture", new LedgerEvidence.Saved("image", 1000), default);
                using var child = Process.GetProcessById(session.ProcessId!.Value);
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<IOException>(() => session.FindAttemptAsync("capture", default));
                Assert.False(session.IsReady);
            }
            await using var reopened = await RuntimeSession.StartAsync(ProcessTests.BundleDirectory, "rig-test", default, directory.FullName);
            Assert.Equal(identity, (await reopened.OpenLedgerAsync(request, default)).Value);
            Assert.Equal(ReservationKind.Existing, (await reopened.ReserveAsync("capture", request.State, default)).Value!.Kind);
            var next = (await reopened.ReserveAsync("replacement", request.State, default)).Value!;
            Assert.Equal(saved ? ReservationKind.Decision : ReservationKind.RecoveryRequired, next.Kind);
            if (saved) Assert.Equal(new(PlannerAction.Wait, "pending_assessment"), next.Decision);
            Assert.Equal(saved ? 2 : 1, (await reopened.ReadEventsAsync(0, 64, default)).Value!.Events.Length);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("busy", LedgerError.Busy)]
    [InlineData("unavailable", LedgerError.Unavailable)]
    [InlineData("unsupported_schema", LedgerError.UnsupportedSchema)]
    [InlineData("foreign_database", LedgerError.ForeignDatabase)]
    public async Task StorageErrorsAreTypedAndDoNotPoisonThePipe(string code, LedgerError expected)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => new JsonObject { ["status"] = "error", ["code"] = code });
        var result = await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        Assert.Null(result.Value);
        Assert.Equal(expected, result.Error);
        await peer.Session.PingAsync(default);
    }

    [Fact]
    public async Task UnknownErrorCodeFaultsSession()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => new JsonObject { ["status"] = "error", ["code"] = "new_error" });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.OpenLedgerAsync(PlannerTests.Request(), default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task OversizedCallerInputDoesNotOpenLedgerOrPoisonSession()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => Opened());
        var request = PlannerTests.Request();
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.OpenLedgerAsync(request with
        { Assignment = request.Assignment with { Id = new string('x', PlannerContract.MaxRequestBytes) } }, default));
        Assert.NotNull((await peer.Session.OpenLedgerAsync(request, default)).Value);
    }

    [Fact]
    public async Task DisposalCancelsAdmittedLedgerWorkBeforeWaitingForTheGate()
    {
        await using var peer = await TestPeer.CreateAsync("stall-ledger", ledger: _ => Opened());
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        var active = peer.Session.ReserveAsync("capture", PlannerTests.Request().State, default);
        await peer.ReservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await peer.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.False(peer.Session.IsReady);
    }
}
