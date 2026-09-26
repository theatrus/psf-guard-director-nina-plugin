using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class GeometryTests
{
    private const ulong Start = 1790409600000;
    private static PlannerState State() => PlannerTests.Request().State with { NowMs = Start, ConditionsValidUntilMs = Start + 120000 };
    private static DirectorProgram Program()
    {
        var assignment = PlannerTests.Request().Assignment;
        return new(1, assignment with
        {
            ValidFromMs = Start,
            ExpiresAtMs = Start + 60000,
            Goals = [assignment.Goals[0] with { EligibleWindows = [new(Start, Start + 60000)] }]
        }, new("rig-test", "config", "camera", "wheel", [new("L", 2)], [new(1, 1)], [0],
            new CameraControl.Values([0, 40, 100]), new CameraControl.Unsupported(), 1, 600000, true, 0),
            [new("target", "M42", 298800000, -18000000, null)],
            [new("recipe", 1000, "L", new(1, 1), 40, null, 0, null)], [new("goal", "target", "recipe")]);
    }
    private static DirectorConstraints Constraints() => new(1,
        new("rig-test", "config", 9007199254740993UL, new(35, -120, 1000), new(0, 0, 0, Start, Start + 120001),
            new DirectorHorizon.FixedMinimum(), -89, 89, new(0, 0)), [new("goal", -89, 89, 0)]);
    private static ProgramLocalState Local() => new(Program().Configuration, null, false, false, 0);
    private static PreparationEstimates Estimates() => new(0, 0, 0, 0, 0, 0, 0);
    private static JsonObject Opened() => new()
    {
        ["status"] = "geometry_opened",
        ["program_version"] = 1,
        ["constraints_version"] = 1,
        ["info"] = ProgramContract.Encode(new LedgerIdentity("3924de93-0f42-4b12-8036-66aa03fabdd0", "assignment", 9007199254740993UL, "rig-test", "config"))
    };

    [Fact]
    public async Task RealGeometryRecoveryKeepsOperationAndCaptureEvidence()
    {
        var directory = Directory.CreateTempSubdirectory("director-geometry-client-");
        var program = Program();
        var constraints = Constraints() with
        {
            Rig = Constraints().Rig with
            {
                Horizon = new DirectorHorizon.Custom([
            new(0, -89), new(46.901276090801716, -89), new(double.BitIncrement(46.901276090801716), -89), new(360, -89)])
            }
        };
        var state = State();
        PreparationCommand issued;
        LedgerIdentity identity;
        try
        {
            await using (var session = await RuntimeSession.StartAsync(ProcessTests.BundleDirectory, "rig-test", default, directory.FullName))
            {
                identity = (await session.OpenGeometryAsync(program, constraints, state, default)).Value!;
                Assert.Equal(PlannerAction.Acquire, (await session.EvaluateGeometryAsync(constraints, state, default)).Value!.Action);
                Assert.True((await session.BeginGeometryPreparationAsync("prep", "goal", Local(), Estimates(), constraints, state, default)).Value!.Created);
                issued = Assert.IsType<PreparationNext.Run>((await session.AdvanceGeometryPreparationAsync("prep", program.Configuration, constraints, state, default)).Value).Command;
                Assert.IsType<PreparationOperation.Center>(issued.Operation);
                using var process = Process.GetProcessById(session.ProcessId!.Value);
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await controller.StartAsync("rig-test");
            Assert.Equal(LedgerError.AssignmentMismatch, (await controller.OpenProgramAsync(program, state)).Error);
            var changed = constraints with { Rig = constraints.Rig with { Site = constraints.Rig.Site with { LatitudeDegrees = 36 } } };
            Assert.Equal(LedgerError.AssignmentMismatch, (await controller.OpenGeometryAsync(program, changed, state)).Error);
            Assert.Equal(identity, (await controller.OpenGeometryAsync(program, constraints, state)).Value);
            Assert.Equal(issued, (await controller.FindActivePreparationAsync()).Value!.Record!.Pending);
            Assert.IsType<PreparationNext.InFlight>((await controller.AdvanceGeometryPreparationAsync("prep", program.Configuration, constraints, state)).Value);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.EvaluateLedgerAsync(state));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ReserveProgramPreparedAsync("prep", "capture", program.Configuration, state));
            await controller.CompletePreparationAsync(new("prep", issued.Ordinal, Start, 1, new PreparationOutcome.Succeeded()));
            await Ready(controller, constraints, state);
            Assert.Equal(ReservationKind.Created, (await controller.ReserveGeometryPreparedAsync("prep", "capture", program.Configuration, constraints, state)).Value!.Kind);
            await controller.StopAsync();
            await controller.StartAsync("rig-test");
            Assert.Equal(identity, (await controller.OpenGeometryAsync(program, constraints, state)).Value);
            Assert.Equal(ReservationKind.Existing, (await controller.ReserveGeometryPreparedAsync("prep", "capture", program.Configuration, constraints, state)).Value!.Kind);
            Assert.Equal(program.Recipes[0], (await controller.FindCaptureBindingAsync("capture")).Value!.Binding!.Recipe);
            await controller.RecordAsync("capture", new LedgerEvidence.Saved("image", ulong.MaxValue));
            Assert.Equal(new(PlannerAction.Wait, "pending_assessment"), (await controller.EvaluateGeometryAsync(constraints, state)).Value);
            Assert.Equal(10UL, (await controller.ReadPreparationEventsAsync(0)).Value!.NextCursor);
            await controller.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    private static async Task Ready(RuntimeController controller, DirectorConstraints constraints, PlannerState state)
    {
        for (var i = 0; i < 10; i++)
        {
            var next = (await controller.AdvanceGeometryPreparationAsync("prep", Program().Configuration, constraints, state)).Value;
            if (next is PreparationNext.ReadyToReserve) return;
            var command = Assert.IsType<PreparationNext.Run>(next).Command;
            await controller.CompletePreparationAsync(new("prep", command.Ordinal, state.NowMs, 1, new PreparationOutcome.Succeeded()));
        }
        Assert.Fail("Preparation failed to reach readiness.");
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("late")]
    [InlineData("unsafe")]
    public async Task RealFinalReservationUsesFreshConstraintsAndState(string fault)
    {
        var directory = Directory.CreateTempSubdirectory("director-geometry-boundary-");
        try
        {
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await controller.StartAsync("rig-test");
            var constraints = Constraints();
            var state = State();
            await controller.OpenGeometryAsync(Program(), constraints, state);
            await controller.BeginGeometryPreparationAsync("prep", "goal", Local(), Estimates(), constraints, state);
            await Ready(controller, constraints, state);
            if (fault == "changed") constraints = constraints with { Rig = constraints.Rig with { MinimumAltitudeDegrees = -88 } };
            if (fault == "late") state = state with { NowMs = Start + 59500 };
            if (fault == "unsafe") state = state with { Safety = PlannerSafety.Unsafe };
            var reserved = (await controller.ReserveGeometryPreparedAsync("prep", "capture", Program().Configuration, constraints, state)).Value!;
            Assert.Equal(ReservationKind.Decision, reserved.Kind);
            Assert.NotEqual(PlannerAction.Acquire, reserved.Decision!.Action);
            Assert.Null((await controller.FindUnresolvedAttemptAsync()).Value!.Attempt);
            await controller.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("program-version")]
    [InlineData("constraints-version")]
    [InlineData("missing-version")]
    [InlineData("extra")]
    [InlineData("identity")]
    [InlineData("legacy-mode")]
    public async Task MalformedGeometryOpenRevokesSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ =>
        {
            var opened = Opened();
            switch (fault)
            {
                case "program-version": opened["program_version"] = 2; break;
                case "constraints-version": opened["constraints_version"] = 2; break;
                case "missing-version": opened.Remove("constraints_version"); break;
                case "extra": opened["fallback"] = true; break;
                case "identity": opened["info"]!["configuration_id"] = "other"; break;
                case "legacy-mode": opened["status"] = "program_opened"; break;
            }
            return opened;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.OpenGeometryAsync(Program(), Constraints(), State(), default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task GeometryWireKeepsExactVerticesAndWideRevisionOnEveryBoundary()
    {
        var az = 46.901276090801716;
        var constraints = Constraints() with
        {
            Rig = Constraints().Rig with
            {
                Horizon = new DirectorHorizon.Custom([
            new(0, -89), new(double.BitDecrement(az), -89), new(az, 89), new(double.BitIncrement(az), -89), new(360, -89)])
            }
        };
        var actions = new List<string>();
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            var action = operation.GetProperty("action").GetString()!;
            actions.Add(action);
            var sent = operation.GetProperty("constraints").GetProperty("rig");
            Assert.Equal(9007199254740993UL, sent.GetProperty("revision").GetUInt64());
            var points = sent.GetProperty("horizon").GetProperty("points");
            Assert.Equal(double.BitDecrement(az), points[1].GetProperty("azimuth_degrees").GetDouble());
            Assert.Equal(az, points[2].GetProperty("azimuth_degrees").GetDouble());
            Assert.Equal(double.BitIncrement(az), points[3].GetProperty("azimuth_degrees").GetDouble());
            Assert.Equal(0, sent.GetProperty("orientation").GetProperty("ut1_minus_utc_seconds").GetDouble());
            Assert.Equal(0, sent.GetProperty("orientation").GetProperty("polar_motion_x_radians").GetDouble());
            if (action == "open_geometry") return Opened();
            return new JsonObject { ["status"] = "error", ["code"] = "invalid_snapshot" };
        });
        await peer.Session.OpenGeometryAsync(Program(), constraints, State(), default);
        await peer.Session.EvaluateGeometryAsync(constraints, State(), default);
        await peer.Session.BeginGeometryPreparationAsync("prep", "goal", Local(), Estimates(), constraints, State(), default);
        await peer.Session.AdvanceGeometryPreparationAsync("prep", Program().Configuration, constraints, State(), default);
        await peer.Session.ReserveGeometryPreparedAsync("prep", "capture", Program().Configuration, constraints, State(), default);
        Assert.Equal(new[] { "open_geometry", "evaluate_geometry", "begin_geometry_preparation", "advance_geometry_preparation", "reserve_geometry_prepared" }, actions);
        Assert.True(peer.Session.IsReady);
    }

    [Fact]
    public async Task CallerErrorsAndModeMistakesDoNotSendOrFault()
    {
        var sent = 0;
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => { sent++; return Opened(); });
        var wrong = Constraints() with { Rig = Constraints().Rig with { RigId = "other" } };
        var huge = Constraints() with { Rig = Constraints().Rig with { Horizon = new DirectorHorizon.Custom(Enumerable.Repeat(new DirectorHorizonPoint(123, 45), 20000).ToImmutableArray()) } };
        var nonfinite = Constraints() with { Rig = Constraints().Rig with { MinimumAltitudeDegrees = double.NaN } };
        foreach (var invalid in new[] { wrong, huge, nonfinite })
            await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.OpenGeometryAsync(Program(), invalid, State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.EvaluateGeometryAsync(Constraints(), State(), default));
        Assert.Equal(0, sent);
        await peer.Session.OpenGeometryAsync(Program(), Constraints(), State(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.EvaluateLedgerAsync(State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.AdvanceProgramPreparationAsync("prep", Program().Configuration, State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.AdvancePreparationAsync("prep", State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.ReserveProgramPreparedAsync("prep", "capture", Program().Configuration, State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.ReservePreparedAsync("prep", "capture", State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.ReserveAsync("capture", State(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.EvaluateGeometryAsync(wrong, State(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.BeginGeometryPreparationAsync("prep", "goal", Local(), Estimates(), wrong, State(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.AdvanceGeometryPreparationAsync("prep", Program().Configuration, wrong, State(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.ReserveGeometryPreparedAsync("prep", "capture", Program().Configuration, wrong, State(), default));
        Assert.Equal(1, sent);
        Assert.True(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("recipe")]
    [InlineData("filter")]
    public async Task GeometryReplyCannotSubstituteOperationSettings(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", ledger: operation =>
        {
            if (operation.GetProperty("action").GetString() == "open_geometry") return Opened();
            var command = new JsonObject
            {
                ["preparation_id"] = "prep",
                ["ordinal"] = 1,
                ["goal_id"] = "goal",
                ["target_id"] = "target",
                ["recipe_id"] = "recipe",
                ["operation"] = new JsonObject { ["operation"] = "switch_filter", ["filter_id"] = "L" }
            };
            if (fault == "filter") command["operation"]!["filter_id"] = "R";
            else command[$"{fault}_id"] = "other";
            return new JsonObject { ["status"] = "preparation_advanced", ["next"] = new JsonObject { ["status"] = "run", ["value"] = command } };
        });
        await peer.Session.OpenGeometryAsync(Program(), Constraints(), State(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.AdvanceGeometryPreparationAsync("prep", Program().Configuration, Constraints(), State(), default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task GeometryCommandsCannotAdoptAProgramOnlySession()
    {
        var sent = 0;
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ =>
        {
            sent++;
            var opened = Opened();
            opened["status"] = "program_opened";
            opened.Remove("constraints_version");
            return opened;
        });
        await peer.Session.OpenProgramAsync(Program(), State(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.EvaluateGeometryAsync(Constraints(), State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.BeginGeometryPreparationAsync("prep", "goal", Local(), Estimates(), Constraints(), State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.AdvanceGeometryPreparationAsync("prep", Program().Configuration, Constraints(), State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.ReserveGeometryPreparedAsync("prep", "capture", Program().Configuration, Constraints(), State(), default));
        Assert.Equal(1, sent);
        Assert.True(peer.Session.IsReady);
    }
}
