using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Model;
using NINA.Sequencer.Container;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed partial class NinaCaptureTests
{
    [Theory]
    [InlineData("unsafe-hook")]
    [InlineData("late-hook")]
    [InlineData("late-progress")]
    [InlineData("late-journal")]
    [InlineData("cancel-final")]
    public async Task NativeExposureRefusalNeverCallsCameraOrRefundsReservation(string fault)
    {
        using var f = new Fixture();
        var source = Bound(f);
        source = source with { Target = source.Target with { IcrsRaMas = 298800000, IcrsDecMas = -18000000, PositionAngleMas = null } };
        const ulong start = 1790409600000;
        var baseline = PlannerTests.Request();
        var assignment = baseline.Assignment with
        {
            RigId = "rig",
            ConfigurationId = source.Configuration.Id,
            ValidFromMs = start,
            ExpiresAtMs = start + 60000,
            Goals = [baseline.Assignment.Goals[0] with { ExposureMs = 1500, AttemptsRemaining = 1, EligibleWindows = [new(start, start + 60000)] }]
        };
        var program = new DirectorProgram(1, assignment, source.Configuration, [source.Target], [source.Recipe], [new("goal", "target", "recipe")]);
        var state = baseline.State with { RigId = "rig", ConfigurationId = source.Configuration.Id, NowMs = start, ConditionsValidUntilMs = start + 120000 };
        var constraints = new DirectorConstraints(1, new("rig", source.Configuration.Id, 1, new(35, -120, 1000),
            new(0, 0, 0, start, start + 120001), new DirectorHorizon.FixedMinimum(), -89, 89, new(0, 0)), [new("goal", -89, 89, 0)]);
        var directory = Directory.CreateTempSubdirectory("director-native-exposure-check-");
        try
        {
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await runtime.StartAsync("rig");
            Assert.Null((await runtime.OpenGeometryAsync(program, constraints, state)).Error);
            var guard = new NinaGeometryDispatch(runtime, () => new(f.Equipment.Read(f.Local), constraints, state), () => { }, f.Clock);
            var local = new ProgramLocalState(source.Configuration, new(source.Configuration.Id, source.Target), false, false, 0);
            Assert.True((await runtime.BeginGeometryPreparationAsync("prep", "goal", local, new(0, 0, 0, 0, 0, 0, 1000), constraints, state)).Value!.Created);
            for (var i = 0; i < 2; i++)
            {
                var command = Assert.IsType<PreparationNext.Run>((await runtime.AdvanceGeometryPreparationAsync("prep", source.Configuration, constraints, state)).Value).Command;
                Assert.Null((await runtime.CompletePreparationAsync(new("prep", command.Ordinal, start, 0, new PreparationOutcome.Succeeded()))).Error);
            }
            var reservation = (await runtime.ReserveGeometryPreparedAsync("prep", source.Attempt.CaptureId, source.Configuration, constraints, state)).Value!;
            Assert.Equal(ReservationKind.Created, reservation.Kind);
            var binding = (await runtime.FindCaptureBindingAsync(source.Attempt.CaptureId)).Value!.Binding!;
            using var cancellation = new CancellationTokenSource();
            var check = guard.Capture("prep", reservation);
            var finalReached = false;
            var item = new NinaExposureItem(f.BoundCapture, reservation, binding, f.Local, async token =>
            {
                var final = await check(token);
                if (fault == "late-journal") f.Clock.OnTimestamp = () =>
                {
                    // The final guard samples elapsed time after the durable marker.
                    Assert.Equal(CapturePhase.Capturing, f.Read().Phase);
                    f.Clock.Advance(61000);
                    f.Clock.OnTimestamp = null;
                };
                return () =>
                {
                    finalReached = true;
                    Assert.Equal(CapturePhase.Capturing, f.Read().Phase);
                    final();
                };
            });
            f.Progress.OnReport = value =>
            {
                if (value.Status != "Exposing and downloading") return;
                if (fault == "late-progress") f.Clock.Advance(61000);
                if (fault == "cancel-final") cancellation.Cancel();
            };
            var container = new SequentialContainer();
            container.Add(item);
            AddExposureHook(container, item, () =>
            {
                if (fault == "unsafe-hook") state = state with { Safety = PlannerSafety.Unsafe };
                if (fault == "late-hook") state = state with { NowMs = start + 59000 };
            });
            if (fault == "cancel-final") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => container.Run(f.Progress, cancellation.Token));
            else
            {
                await container.Run(f.Progress, cancellation.Token);
                Assert.Equal(SequenceEntityStatus.FAILED, item.Status);
            }
            Assert.NotNull(item.ExecutionError);
            f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Never);
            Assert.Equal(reservation.Attempt, (await runtime.FindAttemptAsync(source.Attempt.CaptureId)).Value!.Attempt);
            var record = (await runtime.FindPreparationAsync("prep")).Value!.Record!;
            Assert.Equal(PreparationLifecycle.Captured, record.Lifecycle);
            if (fault.EndsWith("hook", StringComparison.Ordinal)) Assert.NotNull(record.Halted);
            else
            {
                Assert.True(finalReached);
                Assert.Null(record.Halted);
            }
            Assert.Equal(fault == "cancel-final" ? CapturePhase.Interrupted : CapturePhase.Failed, f.Read().Phase);
            Assert.Null(record.Pending);
            await runtime.StopAsync();
        }
        finally { directory.Delete(true); }
    }
}
