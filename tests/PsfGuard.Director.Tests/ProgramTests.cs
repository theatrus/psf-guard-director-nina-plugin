using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class ProgramTests
{
    private static DirectorProgram Program() => new(1, PlannerTests.Request().Assignment,
        new("rig-test", "config", "camera", "wheel", [new("L", 2)], [new(1, 1)], [0],
            new CameraControl.Values([0, 40, 100]), new CameraControl.Unsupported(), 1, 600000, true, 0),
        [new("target", "M42", 298800000, -18000000, null)],
        [new("recipe", 1000, "L", new(1, 1), 40, null, 0, null)], [new("goal", "target", "recipe")]);
    private static ProgramLocalState Local() => new(Program().Configuration, new("config", Program().Targets[0]), false, false, 0);
    private static PreparationEstimates Estimates() => new(0, 0, 0, 0, 0, 0, 0);
    private static readonly LedgerIdentity Identity = new("3924de93-0f42-4b12-8036-66aa03fabdd0", "assignment", 9007199254740993UL, "rig-test", "config");
    private static JsonObject Opened() => new() { ["status"] = "program_opened", ["program_version"] = 1, ["info"] = ProgramContract.Encode(Identity) };
    private static JsonObject Binding() => new()
    {
        ["ledger"] = ProgramContract.Encode(Identity),
        ["attempt"] = new JsonObject { ["capture_id"] = "capture", ["goal_id"] = "goal", ["reserved_at_ms"] = 2000, ["evidence"] = new JsonObject { ["state"] = "saved", ["image_id"] = "image", ["elapsed_ms"] = 9007199254740993UL } },
        ["target"] = ProgramContract.Encode(Program().Targets[0]),
        ["recipe"] = ProgramContract.Encode(Program().Recipes[0]),
        ["configuration"] = ProgramContract.Encode(Program().Configuration)
    };

    [Fact]
    public async Task RealProgramRecoveryKeepsRecipesAndRejectsRedispatch()
    {
        var directory = Directory.CreateTempSubdirectory("director-program-");
        try
        {
            var program = Program();
            var state = PlannerTests.Request().State;
            PreparationCommand issued;
            LedgerIdentity identity;
            await using (var session = await RuntimeSession.StartAsync(ProcessTests.BundleDirectory, "rig-test", default, directory.FullName))
            {
                identity = (await session.OpenProgramAsync(program, state, default)).Value!;
                Assert.True((await session.BeginProgramPreparationAsync("prep", "goal", Local(), Estimates(), state, default)).Value!.Created);
                issued = Assert.IsType<PreparationNext.Run>((await session.AdvanceProgramPreparationAsync("prep", program.Configuration, state, default)).Value).Command;
                Assert.Equal(new PreparationOperation.SwitchFilter("L"), issued.Operation);
                using var child = Process.GetProcessById(session.ProcessId!.Value);
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await using (var controller = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName))
            {
                await controller.StartAsync("rig-test");
                Assert.Equal(LedgerError.AssignmentMismatch, (await controller.OpenLedgerAsync(PlannerTests.Request())).Error);
                var changed = program with { Recipes = [program.Recipes[0] with { Gain = 100 }] };
                Assert.Equal(LedgerError.AssignmentMismatch, (await controller.OpenProgramAsync(changed, state)).Error);
                Assert.Equal(identity, (await controller.OpenProgramAsync(program, state)).Value);
                Assert.Equal(issued, (await controller.FindActivePreparationAsync()).Value!.Record!.Pending);
                Assert.IsType<PreparationNext.InFlight>((await controller.AdvanceProgramPreparationAsync("prep", program.Configuration, state)).Value);
                Assert.Equal(LedgerError.ConflictingEvidence, (await controller.AdvancePreparationAsync("prep", state)).Error);
                Assert.Equal(LedgerError.ConflictingEvidence, (await controller.ClosePreparationAsync("prep")).Error);
                await controller.CompletePreparationAsync(new("prep", issued.Ordinal, 2100, ulong.MaxValue, new PreparationOutcome.Succeeded()));
                state = state with { NowMs = 2100 };
                var second = Assert.IsType<PreparationNext.Run>((await controller.AdvanceProgramPreparationAsync("prep", program.Configuration, state)).Value).Command;
                await controller.CompletePreparationAsync(new("prep", second.Ordinal, 2200, 100, new PreparationOutcome.Succeeded()));
                state = state with { NowMs = 2200 };
                var changedConfig = program.Configuration with { Filters = [new("L", 3)] };
                Assert.Equal(LedgerError.AssignmentMismatch, (await controller.ReserveProgramPreparedAsync("prep", "capture", changedConfig, state)).Error);
                Assert.Equal(ReservationKind.Created, (await controller.ReserveProgramPreparedAsync("prep", "capture", program.Configuration, state)).Value!.Kind);
                var binding = (await controller.FindCaptureBindingAsync("capture")).Value!.Binding!;
                Assert.Equal(program.Recipes[0], binding.Recipe);
                Assert.Equal(program.Targets[0], binding.Target);
                Assert.Equal(identity, binding.Ledger);
                await controller.StopAsync();
                await controller.StartAsync("rig-test");
                Assert.Equal(identity, (await controller.OpenProgramAsync(program, state)).Value);
                Assert.Equal(ReservationKind.Existing, (await controller.ReserveProgramPreparedAsync("prep", "capture", program.Configuration, state)).Value!.Kind);
                await controller.RecordAsync("capture", new LedgerEvidence.Saved("image", 9007199254740993UL));
                Assert.Equal(new LedgerEvidence.Saved("image", 9007199254740993UL), (await controller.FindCaptureBindingAsync("capture")).Value!.Binding!.Attempt.Evidence);
                Assert.Equal(new(PlannerAction.Wait, "pending_assessment"), (await controller.EvaluateLedgerAsync(state)).Value);
                Assert.Null((await controller.FindCaptureBindingAsync("missing")).Value!.Binding);
                Assert.Equal(6UL, (await controller.ReadPreparationEventsAsync(0)).Value!.NextCursor);
                await controller.StopAsync();
            }
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("missing-version")]
    [InlineData("identity")]
    public async Task MalformedProgramOpenFaultsSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ =>
        {
            var opened = Opened();
            if (fault == "version") opened["program_version"] = 2;
            if (fault == "missing-version") opened.Remove("program_version");
            if (fault == "identity") opened["info"]!["configuration_id"] = "wrong";
            return opened;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.OpenProgramAsync(Program(), PlannerTests.Request().State, default));
        Assert.False(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("coordinate")]
    [InlineData("gain")]
    [InlineData("exposure")]
    [InlineData("filter-slot")]
    [InlineData("camera")]
    [InlineData("capabilities")]
    [InlineData("missing-null")]
    [InlineData("extra")]
    [InlineData("ledger")]
    [InlineData("capture")]
    [InlineData("goal")]
    [InlineData("fraction")]
    public async Task ChangedCaptureBindingFaultsSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open_program") return Opened();
            var binding = Binding();
            switch (fault)
            {
                case "target": binding["target"]!["id"] = "wrong"; break;
                case "coordinate": binding["target"]!["icrs_ra_mas"] = 298800001; break;
                case "gain": binding["recipe"]!["gain"] = 100; break;
                case "exposure": binding["recipe"]!["exposure_ms"] = 1001; break;
                case "filter-slot": binding["configuration"]!["filters"]![0]!["position"] = 3; break;
                case "camera": binding["configuration"]!["camera_id"] = "replacement"; break;
                case "capabilities": binding["configuration"]!["gain"]!["values"]![1] = 41; break;
                case "missing-null": binding["recipe"]!.AsObject().Remove("offset"); break;
                case "extra": binding["configuration"]!["offset"]!["guess"] = true; break;
                case "ledger": binding["ledger"]!["ledger_id"] = Guid.NewGuid().ToString("D"); break;
                case "capture": binding["attempt"]!["capture_id"] = "different"; break;
                case "goal": binding["attempt"]!["goal_id"] = "different"; break;
                case "fraction": binding["recipe"]!["exposure_ms"] = 1000.1; break;
            }
            return new JsonObject { ["status"] = "capture_binding_found", ["binding"] = binding };
        });
        await peer.Session.OpenProgramAsync(Program(), PlannerTests.Request().State, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.FindCaptureBindingAsync("capture", default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task CaptureBindingPreservesExactNumbersAndArrayContents()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation => operation.GetProperty("action").GetString() == "open_program"
            ? Opened() : new JsonObject { ["status"] = "capture_binding_found", ["binding"] = Binding() });
        var program = Program();
        await peer.Session.OpenProgramAsync(program, PlannerTests.Request().State, default);
        var binding = (await peer.Session.FindCaptureBindingAsync("capture", default)).Value!.Binding!;
        Assert.Equal(9007199254740993UL, binding.Ledger.AssignmentRevision);
        Assert.Equal(9007199254740993UL, Assert.IsType<LedgerEvidence.Saved>(binding.Attempt.Evidence).ElapsedMs);
        Assert.Same(program.Configuration, binding.Configuration);
        Assert.Equal(-18000000, binding.Target.IcrsDecMas);
        await peer.Session.PingAsync(default);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("recipe")]
    [InlineData("filter")]
    [InlineData("readout")]
    [InlineData("rotation")]
    public async Task PreparationRunMustMatchProgramBinding(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open_program") return Opened();
            var command = new JsonObject
            {
                ["preparation_id"] = "prep",
                ["ordinal"] = 1,
                ["goal_id"] = "goal",
                ["target_id"] = "target",
                ["recipe_id"] = "recipe",
                ["operation"] = new JsonObject { ["operation"] = "switch_filter", ["filter_id"] = "L" }
            };
            switch (fault)
            {
                case "target": command["target_id"] = "other"; break;
                case "recipe": command["recipe_id"] = "other"; break;
                case "filter": command["operation"]!["filter_id"] = "R"; break;
                case "readout": command["operation"] = new JsonObject { ["operation"] = "set_readout_mode", ["mode"] = 1 }; break;
                case "rotation": command["operation"] = new JsonObject { ["operation"] = "center", ["rotate"] = true }; break;
            }
            return new JsonObject { ["status"] = "preparation_advanced", ["next"] = new JsonObject { ["status"] = "run", ["value"] = command } };
        });
        await peer.Session.OpenProgramAsync(Program(), PlannerTests.Request().State, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.AdvanceProgramPreparationAsync("prep", Program().Configuration, PlannerTests.Request().State, default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task CallerRigAndSizeErrorsDoNotFaultSession()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => Opened());
        var program = Program();
        var state = PlannerTests.Request().State;
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.OpenProgramAsync(program with { Configuration = program.Configuration with { RigId = "other" } }, state, default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.OpenProgramAsync(program, state with { RigId = "other" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.OpenProgramAsync(program with { Targets = [program.Targets[0] with { Name = new string('x', 262145) }] }, state, default));
        Assert.True(peer.Session.IsReady);
        await peer.Session.OpenProgramAsync(program, state, default);
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.AdvanceProgramPreparationAsync("prep", program.Configuration with { RigId = "other" }, state, default));
        await peer.Session.PingAsync(default);
    }

    [Fact]
    public async Task ProgramErrorsAreTypedAndAllowValidOpenRetry()
    {
        var attempts = 0;
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => ++attempts == 1
            ? new JsonObject { ["status"] = "error", ["code"] = "invalid_program" } : Opened());
        Assert.Equal(LedgerError.InvalidProgram, (await peer.Session.OpenProgramAsync(Program(), PlannerTests.Request().State, default)).Error);
        Assert.Equal(Identity, (await peer.Session.OpenProgramAsync(Program(), PlannerTests.Request().State, default)).Value);
        Assert.True(peer.Session.IsReady);
    }

    [Fact]
    public void DuplicateBindingFieldsAreRejectedRatherThanOverwritten()
    {
        var raw = Binding().ToJsonString().Replace("\"gain\":40", "\"gain\":40,\"gain\":40", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        Assert.Throws<InvalidDataException>(() => ProgramContract.ReadBinding(document.RootElement, Program(), Identity, "capture"));
    }

    [Fact]
    public void RecoveryRecordsAndPagesMustMatchProgramSettings()
    {
        var command = new PreparationCommand("prep", 1, "goal", "target", "recipe", new PreparationOperation.SwitchFilter("R"));
        Assert.Throws<InvalidDataException>(() => ProgramContract.CheckRecord(new("prep", PreparationLifecycle.Active, "goal", command, null, [], null), Program()));
        var context = new PreparationContext("goal", "target", "recipe", "target", "L", 0, false, false, true, 0, 2, 0);
        var page = new PreparationEventPage(Identity, [new(1, "prep", new PreparationEventData.Started(context, Estimates()))], 1);
        Assert.Throws<InvalidDataException>(() => ProgramContract.CheckPage(page, Program()));
    }

    [Fact]
    public async Task BoundCallsOnUnboundSessionAreCallerErrors()
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => new JsonObject { ["status"] = "opened", ["info"] = ProgramContract.Encode(Identity) });
        await peer.Session.OpenLedgerAsync(PlannerTests.Request(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.FindCaptureBindingAsync("capture", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.BeginProgramPreparationAsync("prep", "goal", Local(), Estimates(), PlannerTests.Request().State, default));
        await peer.Session.PingAsync(default);
        Assert.True(peer.Session.IsReady);
    }
}
