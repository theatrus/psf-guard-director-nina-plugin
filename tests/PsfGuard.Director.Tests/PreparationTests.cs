using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class PreparationTests
{
    private static PreparationContext Context() => new("goal", "target", "recipe", "target", "L", 0, false, false, false, 0, null, 0);
    private static PreparationEstimates Estimates() => new(0, 0, 0, 0, 0, 0, 0);
    private static readonly LedgerIdentity Identity = new("3924de93-0f42-4b12-8036-66aa03fabdd0", "assignment", 9007199254740993UL, "rig-test", "config");

    private static JsonObject Opened() => new() { ["status"] = "opened", ["info"] = JsonSerializer.SerializeToNode(Identity, PlannerContract.Options) };
    private static JsonObject Command() => new()
    {
        ["preparation_id"] = "prep",
        ["ordinal"] = 1,
        ["goal_id"] = "goal",
        ["target_id"] = "target",
        ["recipe_id"] = "recipe",
        ["operation"] = new JsonObject { ["operation"] = "switch_filter", ["filter_id"] = "L" }
    };
    private static JsonObject Record() => new()
    {
        ["preparation_id"] = "prep",
        ["lifecycle"] = "active",
        ["goal_id"] = "goal",
        ["pending"] = Command(),
        ["halted"] = null,
        ["observations"] = new JsonArray(),
        ["capture_id"] = null
    };
    private static JsonObject Page() => new()
    {
        ["status"] = "preparation_events",
        ["next_cursor"] = 1,
        ["events"] = new JsonArray(new JsonObject
        {
            ["schema_version"] = 1,
            ["contract_version"] = RuntimeContract.ContractVersion,
            ["engine_version"] = RuntimeContract.EngineVersion,
            ["ledger_id"] = Identity.LedgerId,
            ["assignment_id"] = Identity.AssignmentId,
            ["assignment_revision"] = Identity.AssignmentRevision,
            ["rig_id"] = Identity.RigId,
            ["configuration_id"] = Identity.ConfigurationId,
            ["sequence"] = 1,
            ["preparation_id"] = "prep",
            ["event"] = new JsonObject { ["kind"] = "started", ["context"] = JsonSerializer.SerializeToNode(Context(), PlannerContract.Options), ["estimates"] = JsonSerializer.SerializeToNode(Estimates(), PlannerContract.Options) }
        })
    };

    [Fact]
    public async Task RealPreparationSurvivesProcessDeathWithoutRedispatchAndKeepsExactReceipts()
    {
        var directory = Directory.CreateTempSubdirectory("director-preparation-");
        try
        {
            var request = PlannerTests.Request();
            PreparationCommand issued;
            LedgerIdentity identity;
            await using (var session = await RuntimeSession.StartAsync(ProcessTests.BundleDirectory, "rig-test", default, directory.FullName))
            {
                identity = (await session.OpenLedgerAsync(request, default)).Value!;
                Assert.Equal(PlannerAction.Acquire, (await session.EvaluateLedgerAsync(request.State, default)).Value!.Action);
                Assert.Empty((await session.ReadEventsAsync(0, 64, default)).Value!.Events);
                Assert.Null((await session.FindActivePreparationAsync(default)).Value!.Record);
                Assert.Null((await session.FindUnresolvedAttemptAsync(default)).Value!.Attempt);
                Assert.True((await session.BeginPreparationAsync("prep", Context(), Estimates(), request.State, default)).Value!.Created);
                Assert.False((await session.BeginPreparationAsync("prep", Context(), Estimates(), request.State, default)).Value!.Created);
                issued = Assert.IsType<PreparationNext.Run>((await session.AdvancePreparationAsync("prep", request.State, default)).Value).Command;
                Assert.Equal(new PreparationOperation.SwitchFilter("L"), issued.Operation);
                using var child = Process.GetProcessById(session.ProcessId!.Value);
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await using (var controller = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName))
            {
                await controller.StartAsync("rig-test");
                Assert.Equal(RuntimeState.Ready, controller.Status.State);
                Assert.Equal(identity, (await controller.OpenLedgerAsync(request)).Value);
                Assert.Equal(issued, (await controller.FindActivePreparationAsync()).Value!.Record!.Pending);
                Assert.IsType<PreparationNext.InFlight>((await controller.AdvancePreparationAsync("prep", request.State)).Value);
                Assert.Equal(LedgerError.ConflictingEvidence, (await controller.ClosePreparationAsync("prep")).Error);
                var completion = new PreparationCompletion("prep", issued.Ordinal, 2100, 9007199254740993UL, new PreparationOutcome.Succeeded());
                Assert.Equal(completion, (await controller.CompletePreparationAsync(completion)).Value!.Observations.Single().Completion);
                Assert.Single((await controller.CompletePreparationAsync(completion)).Value!.Observations);
                Assert.Equal(LedgerError.ConflictingEvidence, (await controller.CompletePreparationAsync(completion with { ElapsedMs = 1 })).Error);
                var state = request.State with { NowMs = 2100 };
                var second = Assert.IsType<PreparationNext.Run>((await controller.AdvancePreparationAsync("prep", state)).Value).Command;
                Assert.Equal(new PreparationOperation.SetReadoutMode(0), second.Operation);
                await controller.CompletePreparationAsync(new("prep", second.Ordinal, 2200, 100, new PreparationOutcome.Succeeded()));
                state = state with { NowMs = 2200 };
                Assert.IsType<PreparationNext.ReadyToReserve>((await controller.AdvancePreparationAsync("prep", state)).Value);
                Assert.Equal(ReservationKind.Created, (await controller.ReservePreparedAsync("prep", "capture", state)).Value!.Kind);
                await controller.StopAsync();
                await controller.StartAsync("rig-test");
                Assert.Equal(identity, (await controller.OpenLedgerAsync(request)).Value);
                Assert.Null((await controller.FindActivePreparationAsync()).Value!.Record);
                Assert.Equal("capture", (await controller.FindUnresolvedAttemptAsync()).Value!.Attempt!.CaptureId);
                Assert.Equal(ReservationKind.Existing, (await controller.ReservePreparedAsync("prep", "capture", state)).Value!.Kind);
                await controller.RecordAsync("capture", new LedgerEvidence.Saved("image", 1000));
                Assert.Equal(new(PlannerAction.Wait, "pending_assessment"), (await controller.EvaluateLedgerAsync(state)).Value);
                var page = (await controller.ReadPreparationEventsAsync(0)).Value!;
                Assert.Equal(identity, page.Identity);
                Assert.Equal(6UL, page.NextCursor);
                Assert.Equal(Context(), Assert.IsType<PreparationEventData.Started>(page.Events[0].Data).Context);
                Assert.Equal(9007199254740993UL, Assert.IsType<PreparationEventData.Completed>(page.Events[2].Data).Observation.Completion.ElapsedMs);
                Assert.IsType<PreparationEventData.Captured>(page.Events[5].Data);
                Assert.Equal(2UL, (await controller.ReadEventsAsync(0)).Value!.NextCursor);
                Assert.Empty((await controller.ReadPreparationEventsAsync(page.NextCursor)).Value!.Events);
                Assert.Equal(PreparationLifecycle.Captured, (await controller.FindPreparationAsync("prep")).Value!.Record!.Lifecycle);
                Assert.Equal(PreparationLifecycle.Captured, (await controller.ClosePreparationAsync("prep")).Value!.Lifecycle);
                Assert.Equal(RuntimeState.Ready, controller.Status.State);
                await controller.StopAsync();
            }
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealFailedOrUncertainPreparationCannotProceed(bool uncertain)
    {
        var directory = Directory.CreateTempSubdirectory("director-preparation-");
        try
        {
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await controller.StartAsync("rig-test");
            var request = PlannerTests.Request();
            await controller.OpenLedgerAsync(request);
            await controller.BeginPreparationAsync("prep", Context(), Estimates(), request.State);
            var command = Assert.IsType<PreparationNext.Run>((await controller.AdvancePreparationAsync("prep", request.State)).Value).Command;
            PreparationOutcome outcome = uncertain ? new PreparationOutcome.Uncertain("unknown") : new PreparationOutcome.Failed("failed");
            var completed = (await controller.CompletePreparationAsync(new("prep", command.Ordinal, 2000, 1, outcome))).Value!;
            Assert.Equal(PlannerAction.CheckIn, completed.Halted!.Action);
            Assert.IsType<PreparationNext.Decision>((await controller.AdvancePreparationAsync("prep", request.State)).Value);
            var closed = await controller.ClosePreparationAsync("prep");
            if (uncertain) Assert.Equal(LedgerError.ConflictingEvidence, closed.Error);
            else Assert.Equal(PreparationLifecycle.Closed, closed.Value!.Lifecycle);
            var events = (await controller.ReadPreparationEventsAsync(0)).Value!.Events;
            Assert.Equal(outcome, Assert.IsType<PreparationEventData.Completed>(events[2].Data).Observation.Completion.Outcome);
            if (!uncertain) Assert.IsType<PreparationEventData.Closed>(events[^1].Data);
            await controller.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("missing-null")]
    [InlineData("id")]
    [InlineData("goal")]
    [InlineData("ordinal")]
    [InlineData("unknown-operation")]
    [InlineData("operation-extra")]
    [InlineData("closed-pending")]
    [InlineData("capture-link")]
    [InlineData("halted-acquire")]
    [InlineData("extra")]
    public async Task MalformedRecoveryRecordFaultsSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open") return Opened();
            var record = Record();
            switch (fault)
            {
                case "missing-null": record.Remove("halted"); break;
                case "id": record["pending"]!["preparation_id"] = "wrong"; break;
                case "goal": record["pending"]!["goal_id"] = "wrong"; break;
                case "ordinal": record["pending"]!["ordinal"] = 0; break;
                case "unknown-operation": record["pending"]!["operation"]!["operation"] = "capture"; break;
                case "operation-extra": record["pending"]!["operation"]!["permit"] = true; break;
                case "closed-pending": record["lifecycle"] = "closed"; break;
                case "capture-link": record["capture_id"] = "capture"; break;
                case "halted-acquire": record["halted"] = new JsonObject { ["action"] = "acquire", ["goal_id"] = "goal", ["reason"] = "wrong" }; break;
                case "extra": record["dispatch"] = true; break;
            }
            return new JsonObject { ["status"] = "preparation_found", ["record"] = record };
        });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.FindActivePreparationAsync(default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("rig")]
    [InlineData("version")]
    [InlineData("cursor")]
    [InlineData("sequence")]
    [InlineData("required-null")]
    [InlineData("extra-context")]
    [InlineData("unknown-event")]
    public async Task MalformedPreparationPageFaultsSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open") return Opened();
            var page = Page();
            var item = page["events"]![0]!;
            switch (fault)
            {
                case "rig": item["rig_id"] = "wrong"; break;
                case "version": item["engine_version"] = "old"; break;
                case "cursor": page["next_cursor"] = 2; break;
                case "sequence": item["sequence"] = 2; break;
                case "required-null": item["event"]!["context"]!.AsObject().Remove("dither_override"); break;
                case "extra-context": item["event"]!["context"]!["extra"] = true; break;
                case "unknown-event": item["event"]!["kind"] = "new"; break;
            }
            return page;
        });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ReadPreparationEventsAsync(0, 32, default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("preparation_not_selected", LedgerError.PreparationNotSelected)]
    [InlineData("invalid_completion", LedgerError.InvalidCompletion)]
    [InlineData("clock_regression", LedgerError.ClockRegression)]
    public async Task DomainErrorsRemainTypedWithoutPoisoningThePipe(string code, LedgerError expected)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation => operation.GetProperty("action").GetString() == "open" ? Opened()
            : new JsonObject { ["status"] = "error", ["code"] = code });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        Assert.Equal(expected, (await peer.Session.AdvancePreparationAsync("prep", PlannerTests.Request().State, default)).Error);
        await peer.Session.PingAsync(default);
    }

    [Fact]
    public async Task InputErrorsBeforeAdmissionDoNotPoisonThePipe()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => Opened());
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.AdvancePreparationAsync("bad id", PlannerTests.Request().State, default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.EvaluateLedgerAsync(PlannerTests.Request().State with { RigId = "other" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.BeginPreparationAsync("prep", Context() with { TargetId = null! }, Estimates(), PlannerTests.Request().State, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => peer.Session.ReadPreparationEventsAsync(0, 33, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => peer.Session.ReadPreparationEventsAsync(ulong.MaxValue, 32, default));
        await peer.Session.PingAsync(default);
    }

    [Fact]
    public async Task UnsafeBoundaryHaltsWithoutIssuingWorkAndRecordsTheStop()
    {
        var directory = Directory.CreateTempSubdirectory("director-preparation-");
        try
        {
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await controller.StartAsync("rig-test");
            var request = PlannerTests.Request();
            await controller.OpenLedgerAsync(request);
            await controller.BeginPreparationAsync("prep", Context(), Estimates(), request.State);
            var unsafeState = request.State with { Safety = PlannerSafety.Unsafe };
            Assert.Equal(PlannerAction.Stop, (await controller.EvaluateLedgerAsync(unsafeState)).Value!.Action);
            Assert.Equal(PlannerAction.Stop, Assert.IsType<PreparationNext.Decision>((await controller.AdvancePreparationAsync("prep", unsafeState)).Value).Value.Action);
            var page = (await controller.ReadPreparationEventsAsync(0)).Value!;
            Assert.Equal(2, page.Events.Length);
            Assert.Equal(PlannerAction.Stop, Assert.IsType<PreparationEventData.Halted>(page.Events[1].Data).Decision.Action);
            await controller.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("run-id")]
    [InlineData("run-ordinal")]
    [InlineData("run-extra")]
    [InlineData("in-flight-zero")]
    [InlineData("ready-goal")]
    [InlineData("acquire")]
    [InlineData("unknown")]
    public async Task InvalidAdvancementNeverBecomesANativeOperation(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open") return Opened();
            var next = new JsonObject { ["status"] = "run", ["value"] = Command() };
            switch (fault)
            {
                case "run-id": next["value"]!["preparation_id"] = "wrong"; break;
                case "run-ordinal": next["value"]!["ordinal"] = 0; break;
                case "run-extra": next["value"]!["extra"] = true; break;
                case "in-flight-zero": next["status"] = "in_flight"; next["value"] = new JsonObject { ["ordinal"] = 0 }; break;
                case "ready-goal": next["status"] = "ready_to_reserve"; next["value"] = new JsonObject { ["goal_id"] = "wrong" }; break;
                case "acquire": next["status"] = "decision"; next["value"] = new JsonObject { ["action"] = "acquire", ["goal_id"] = "goal", ["reason"] = "wrong" }; break;
                case "unknown": next["status"] = "retry"; break;
            }
            return new JsonObject { ["status"] = "preparation_advanced", ["next"] = next };
        });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.AdvancePreparationAsync("prep", PlannerTests.Request().State, default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task CompletionMustEchoTheExactReceipt()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation => operation.GetProperty("action").GetString() == "open" ? Opened()
            : new JsonObject { ["status"] = "preparation_recorded", ["record"] = Record() });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.CompletePreparationAsync(new("prep", 1, 2000, 1, new PreparationOutcome.Succeeded()), default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("unpark")]
    [InlineData("center")]
    [InlineData("before_target")]
    [InlineData("dither")]
    [InlineData("switch_filter")]
    [InlineData("set_readout_mode")]
    public void NativeOperationVariantsDecodeWithoutSelectingPolicy(string kind)
    {
        var command = Command();
        var operation = new JsonObject { ["operation"] = kind };
        if (kind == "center") operation["rotate"] = true;
        if (kind == "switch_filter") operation["filter_id"] = "L";
        if (kind == "set_readout_mode") operation["mode"] = 3;
        command["operation"] = operation;
        using var document = JsonDocument.Parse(command.ToJsonString());
        var decoded = PreparationContract.ReadCommand(document.RootElement, PlannerTests.Request().Assignment, "prep");
        Assert.Equal("recipe", decoded.RecipeId);
        Assert.Equal("target", decoded.TargetId);
        Assert.Equal(1U, decoded.Ordinal);
    }

    [Fact]
    public void DuplicateNativeOperationFieldsAreRejected()
    {
        var json = Command().ToJsonString().Replace("\"ordinal\":1", "\"ordinal\":1,\"ordinal\":2");
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => PreparationContract.ReadCommand(document.RootElement, PlannerTests.Request().Assignment, "prep"));
    }
}
