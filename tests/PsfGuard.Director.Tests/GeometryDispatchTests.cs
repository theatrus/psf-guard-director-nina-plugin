using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed partial class GeometryTests
{
    private static PreparationCommand Command() => new("prep", 1, "goal", "target", "recipe", new PreparationOperation.Center(false));
    private static LedgerAttempt Attempt() => new("capture", "goal", Start, new LedgerEvidence.Reserved());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealDispatchRefusalSurvivesRestartAndKeepsEvidence(bool capture)
    {
        var directory = Directory.CreateTempSubdirectory("director-dispatch-client-");
        try
        {
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await controller.StartAsync("rig-test");
            var state = State();
            var constraints = Constraints();
            var configuration = Program().Configuration;
            await controller.OpenGeometryAsync(Program(), constraints, state);
            await controller.BeginGeometryPreparationAsync("prep", "goal", Local(), Estimates(), constraints, state);
            var command = Assert.IsType<PreparationNext.Run>((await controller.AdvanceGeometryPreparationAsync("prep", configuration, constraints, state)).Value).Command;
            LedgerAttempt? attempt = null;
            if (capture)
            {
                await controller.CompletePreparationAsync(new("prep", command.Ordinal, Start, 1, new PreparationOutcome.Succeeded()));
                await Ready(controller, constraints, state);
                var reservation = (await controller.ReserveGeometryPreparedAsync("prep", "capture", configuration, constraints, state)).Value!;
                Assert.Equal(ReservationKind.Created, reservation.Kind);
                attempt = reservation.Attempt!;
            }
            Task<LedgerResult<PlannerDecision>> Check(DirectorConstraints current) => capture
                ? controller.CheckGeometryCaptureDispatchAsync("prep", attempt!, configuration, current, state)
                : controller.CheckGeometryPendingDispatchAsync(command, configuration, current, state);
            Assert.Equal(PlannerAction.Acquire, (await Check(constraints)).Value!.Action);
            var changed = constraints with { Rig = constraints.Rig with { Site = constraints.Rig.Site with { LatitudeDegrees = 36 } } };
            Assert.Equal(PlannerAction.CheckIn, (await Check(changed)).Value!.Action);
            await controller.StopAsync();
            await controller.StartAsync("rig-test");
            await controller.OpenGeometryAsync(Program(), constraints, state);
            Assert.Equal(PlannerAction.CheckIn, (await Check(constraints)).Value!.Action);
            var record = (await controller.FindPreparationAsync("prep")).Value!.Record!;
            Assert.Equal(PlannerAction.CheckIn, record.Halted!.Action);
            if (capture)
            {
                Assert.Equal(PreparationLifecycle.Captured, record.Lifecycle);
                Assert.Equal("capture", record.CaptureId);
                Assert.Null(record.Pending);
                Assert.All(record.Observations, o => Assert.IsType<PreparationOutcome.Succeeded>(o.Completion.Outcome));
                Assert.Equal(ReservationKind.Existing, (await controller.ReserveGeometryPreparedAsync("prep", "capture", configuration, constraints, state)).Value!.Kind);
                await controller.RecordAsync("capture", new LedgerEvidence.Saved("image", 1000));
                Assert.Equal(LedgerError.ConflictingEvidence, (await Check(constraints)).Error);
            }
            else
            {
                Assert.Equal(command, record.Pending);
                Assert.IsType<PreparationNext.InFlight>((await controller.AdvanceGeometryPreparationAsync("prep", configuration, constraints, state)).Value);
            }
            await controller.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false, "other-goal")]
    [InlineData(true, "other-goal")]
    [InlineData(false, "extra")]
    [InlineData(true, "extra")]
    [InlineData(false, "status")]
    [InlineData(true, "status")]
    [InlineData(false, "missing-reason")]
    [InlineData(true, "missing-reason")]
    public async Task MalformedDispatchReplyRevokesSession(bool capture, string fault)
    {
        var program = Program();
        program = program with
        {
            Assignment = program.Assignment with { Goals = [program.Assignment.Goals[0], program.Assignment.Goals[0] with { Id = "other" }] },
            Bindings = [program.Bindings[0], new("other", "target", "recipe")]
        };
        await using var peer = await TestPeer.CreateAsync("none", ledger: op =>
        {
            if (op.GetProperty("action").GetString() == "open_geometry") return Opened();
            var reply = new JsonObject { ["status"] = "dispatch_checked", ["decision"] = new JsonObject { ["action"] = "acquire", ["goal_id"] = "goal", ["reason"] = "ready" } };
            if (fault == "other-goal") reply["decision"]!["goal_id"] = "other";
            if (fault == "extra") reply["permit"] = true;
            if (fault == "status") reply["status"] = "evaluated";
            if (fault == "missing-reason") reply["decision"]!.AsObject().Remove("reason");
            return reply;
        });
        await peer.Session.OpenGeometryAsync(program, Constraints(), State(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => capture
            ? peer.Session.CheckGeometryCaptureDispatchAsync("prep", Attempt(), program.Configuration, Constraints(), State(), default)
            : peer.Session.CheckGeometryPendingDispatchAsync(Command(), program.Configuration, Constraints(), State(), default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task DispatchCallerErrorsDoNotSendOrFaultTheSession()
    {
        var sent = 0;
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => { sent++; return Opened(); });
        await peer.Session.OpenGeometryAsync(Program(), Constraints(), State(), default);
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.CheckGeometryPendingDispatchAsync(Command() with { Ordinal = 0 }, Program().Configuration, Constraints(), State(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.CheckGeometryPendingDispatchAsync(Command() with { Operation = new PreparationOperation.SwitchFilter("") }, Program().Configuration, Constraints(), State(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.CheckGeometryCaptureDispatchAsync("prep", Attempt() with { Evidence = new LedgerEvidence.Uncertain("lost") }, Program().Configuration, Constraints(), State(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.CheckGeometryCaptureDispatchAsync("prep", Attempt(), Program().Configuration with { RigId = "other" }, Constraints(), State(), default));
        Assert.Equal(1, sent);
        Assert.True(peer.Session.IsReady);
    }

    [Fact]
    public async Task GeometryDispatchCannotUseAProgramOnlySession()
    {
        var sent = 0;
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ =>
        {
            sent++;
            var reply = Opened();
            reply["status"] = "program_opened";
            reply.Remove("constraints_version");
            return reply;
        });
        await peer.Session.OpenProgramAsync(Program(), State(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.CheckGeometryPendingDispatchAsync(Command(), Program().Configuration, Constraints(), State(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.Session.CheckGeometryCaptureDispatchAsync("prep", Attempt(), Program().Configuration, Constraints(), State(), default));
        Assert.Equal(1, sent);
        Assert.True(peer.Session.IsReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchCancelledBeforeAdmissionDoesNotSendOrFault(bool capture)
    {
        var sent = 0;
        await using var peer = await TestPeer.CreateAsync("none", ledger: _ => { sent++; return Opened(); });
        await peer.Session.OpenGeometryAsync(Program(), Constraints(), State(), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture
            ? peer.Session.CheckGeometryCaptureDispatchAsync("prep", Attempt(), Program().Configuration, Constraints(), State(), cancellation.Token)
            : peer.Session.CheckGeometryPendingDispatchAsync(Command(), Program().Configuration, Constraints(), State(), cancellation.Token));
        Assert.Equal(1, sent);
        Assert.True(peer.Session.IsReady);
    }

    [Fact]
    public void EveryNativeCommandEncodesWithoutDroppingOperationSettings()
    {
        PreparationOperation[] operations = [new PreparationOperation.Unpark(), new PreparationOperation.Center(true), new PreparationOperation.BeforeTarget(),
            new PreparationOperation.Dither(), new PreparationOperation.SwitchFilter("L"), new PreparationOperation.SetReadoutMode(3)];
        foreach (var operation in operations)
        {
            var command = Command() with { Operation = operation };
            Assert.Equal(command, PreparationContract.ReadCommand(JsonSerializer.SerializeToElement(PreparationContract.EncodeCommand(command)), Program().Assignment, "prep"));
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("uncertain")]
    [InlineData("acquire")]
    [InlineData("continue")]
    public void CapturedHaltStillRejectsInconsistentPreparationEvidence(string fault)
    {
        var record = new JsonObject
        {
            ["preparation_id"] = "prep",
            ["lifecycle"] = "captured",
            ["goal_id"] = "goal",
            ["capture_id"] = "capture",
            ["pending"] = null,
            ["halted"] = new JsonObject { ["action"] = "stop", ["reason"] = "unsafe" },
            ["observations"] = new JsonArray(new JsonObject
            {
                ["command"] = PreparationContract.EncodeCommand(Command()),
                ["issued_at_ms"] = Start,
                ["completion"] = new JsonObject
                {
                    ["preparation_id"] = "prep",
                    ["ordinal"] = 1,
                    ["ended_at_ms"] = Start,
                    ["elapsed_ms"] = 0,
                    ["outcome"] = new JsonObject { ["outcome"] = "succeeded" }
                }
            })
        };
        Assert.Equal(PreparationLifecycle.Captured, PreparationContract.ReadRecord(JsonSerializer.SerializeToElement(record), Program().Assignment).Lifecycle);
        if (fault == "pending") record["pending"] = PreparationContract.EncodeCommand(Command() with { Ordinal = 2 });
        if (fault is "failed" or "uncertain") record["observations"]![0]!["completion"]!["outcome"] = new JsonObject { ["outcome"] = fault, ["reason"] = "error" };
        if (fault is "acquire" or "continue")
        {
            record["halted"]!["action"] = fault;
            if (fault == "acquire") record["halted"]!["goal_id"] = "goal";
        }
        Assert.Throws<InvalidDataException>(() => PreparationContract.ReadRecord(JsonSerializer.SerializeToElement(record), Program().Assignment));
    }
}
