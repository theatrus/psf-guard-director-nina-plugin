using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed partial class GeometryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedCoreSelectsAndPreparesBothTargetsLocally(bool slowPreparation)
    {
        var directory = Directory.CreateTempSubdirectory("director-local-targets-");
        try
        {
            var p = Program();
            p = p with
            {
                Configuration = p.Configuration with { EnableSlewCenter = false },
                Assignment = p.Assignment with
                {
                    Goals = [
                    p.Assignment.Goals[0] with { Priority = 1 },
                    p.Assignment.Goals[0] with { Id = "priority-goal", Priority = 10,
                        EligibleWindows = [new(slowPreparation ? Start + 2000 : Start, Start + 60000)] }]
                },
                Targets = [p.Targets[0], p.Targets[0] with { Id = "priority-target", Name = "High priority" }],
                Bindings = [p.Bindings[0], new("priority-goal", "priority-target", "recipe")]
            };
            var constraints = Constraints() with { Goals = [Constraints().Goals[0], Constraints().Goals[0] with { GoalId = "priority-goal" }] };
            var state = State();
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await runtime.StartAsync("rig-test");
            Assert.NotNull((await runtime.OpenGeometryAsync(p, constraints, state)).Value);
            var first = (await runtime.EvaluateGeometryAsync(constraints, state)).Value!;
            Assert.Equal(slowPreparation ? "goal" : "priority-goal", first.GoalId);
            Assert.NotNull((await runtime.BeginGeometryPreparationAsync("first", first.GoalId!, new(p.Configuration, null, false, false, 0),
                Estimates(), constraints, state)).Value);
            if (slowPreparation)
            {
                var command = Assert.IsType<PreparationNext.Run>((await runtime.AdvanceGeometryPreparationAsync("first", p.Configuration, constraints, state)).Value).Command;
                Assert.IsType<PreparationOperation.BeforeTarget>(command.Operation);
                state = state with { NowMs = Start + 3000 };
                Assert.NotNull((await runtime.CompletePreparationAsync(new("first", command.Ordinal, state.NowMs, 3000, new PreparationOutcome.Succeeded()))).Value);
                var changed = Assert.IsType<PreparationNext.Decision>((await runtime.AdvanceGeometryPreparationAsync("first", p.Configuration, constraints, state)).Value);
                Assert.Equal("preparation_goal_changed", changed.Value.Reason);
                Assert.Equal(PreparationLifecycle.Closed, (await runtime.ClosePreparationAsync("first")).Value!.Lifecycle);
                Assert.Null((await runtime.FindUnresolvedAttemptAsync()).Value!.Attempt);
            }
            else Assert.Equal(PreparationLifecycle.Closed, (await runtime.ClosePreparationAsync("first")).Value!.Lifecycle);

            DirectorPointing? previous = null;
            foreach (var goal in new[] { "priority-goal", "goal" })
            {
                Assert.Equal(goal, (await runtime.EvaluateGeometryAsync(constraints, state)).Value!.GoalId);
                var target = p.Targets.Single(t => t.Id == p.Bindings.Single(b => b.GoalId == goal).TargetId);
                Assert.True((await runtime.BeginGeometryPreparationAsync(goal, goal, new(p.Configuration, previous, false, false, 0),
                    Estimates(), constraints, state)).Value!.Created);
                var operations = new List<PreparationOperation>();
                for (var i = 0; i < 8; i++)
                {
                    var next = (await runtime.AdvanceGeometryPreparationAsync(goal, p.Configuration, constraints, state)).Value;
                    if (next is PreparationNext.ReadyToReserve) break;
                    var command = Assert.IsType<PreparationNext.Run>(next).Command;
                    operations.Add(command.Operation);
                    Assert.Equal(target.Id, command.TargetId);
                    Assert.NotNull((await runtime.CompletePreparationAsync(new(goal, command.Ordinal, state.NowMs, 1, new PreparationOutcome.Succeeded()))).Value);
                }
                Assert.IsType<PreparationOperation.BeforeTarget>(operations[0]);
                var captureId = $"capture-{goal}";
                Assert.Equal(ReservationKind.Created, (await runtime.ReserveGeometryPreparedAsync(goal, captureId, p.Configuration, constraints, state)).Value!.Kind);
                Assert.Equal(target, (await runtime.FindCaptureBindingAsync(captureId)).Value!.Binding!.Target);
                Assert.NotNull((await runtime.RecordAsync(captureId, new LedgerEvidence.Saved(captureId, 1))).Value);
                previous = new(p.Configuration.Id, target);
            }
            Assert.Equal(new(PlannerAction.Wait, "pending_assessment"), (await runtime.EvaluateGeometryAsync(constraints, state)).Value);
            Assert.Equal(4UL, (await runtime.ReadEventsAsync(0)).Value!.NextCursor);
        }
        finally { directory.Delete(true); }
    }
}
