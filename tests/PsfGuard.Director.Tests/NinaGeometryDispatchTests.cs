using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaGeometryDispatchTests
{
    private const ulong Start = 1790409600000;
    private static readonly IProgress<ApplicationStatus> Progress = new Progress<ApplicationStatus>();

    [Theory]
    [InlineData("ready")]
    [InlineData("late")]
    [InlineData("unsafe")]
    [InlineData("paused")]
    [InlineData("constraints")]
    public async Task NativeBeforeHookChangesAreCheckedByRustBeforeReadout(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var guard = f.Guard();
        var check = guard.Pending(f.Next);
        var factory = new NinaPreparationItems(f.Native.Profiles.Object, f.Native.CameraMediator.Object,
            f.Native.WheelMediator.Object, f.Equipment, TimeProvider.System);
        var issued = factory.Create(f.Next, f.Program, f.Native.Binding, check);
        var container = new SequentialContainer();
        container.Add(issued.Item);
        Before(container, issued.Item, () =>
        {
            if (change == "late") f.State = f.State with { NowMs = Start + 59000 };
            if (change == "unsafe") f.State = f.State with { Safety = PlannerSafety.Unsafe };
            if (change == "paused") f.State = f.State with { AtBoundary = false };
            if (change == "constraints") f.Constraints = f.Constraints with { Rig = f.Constraints.Rig with { MinimumAltitudeDegrees = -88 } };
        });
        await container.Run(Progress, default);
        var success = change == "ready";
        Assert.Equal(success ? SequenceEntityStatus.FINISHED : SequenceEntityStatus.FAILED, issued.Item.Status);
        Assert.Equal(success, issued.Fence.Completion!.Outcome is PreparationOutcome.Succeeded);
        f.Native.CameraMediator.Verify(m => m.SetReadoutModeForNormalImages(1), success ? Times.Once : Times.Never);
        await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        var record = (await f.Runtime.FindPreparationAsync("prep")).Value!.Record!;
        Assert.Equal(f.Next.Command, record.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartCannotReviveAnOriginalDispatchCallback(bool capture)
    {
        await using var f = await Fixture.CreateAsync();
        var guard = f.Guard();
        var check = capture ? guard.Capture("prep", await f.ReserveAsync()) : guard.Pending(f.Next);
        await f.Runtime.StopAsync();
        await f.Runtime.StartAsync("rig-test");
        await f.Runtime.OpenGeometryAsync(f.Program, f.Constraints, f.State);
        await Assert.ThrowsAsync<IOException>(() => check(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        Assert.Throws<IOException>(() => guard.Pending(f.Next));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureCheckKeepsReservationAndNeverRefundsIt(bool unsafeState)
    {
        await using var f = await Fixture.CreateAsync();
        var reservation = await f.ReserveAsync();
        var check = f.Guard().Capture("prep", reservation);
        if (unsafeState) f.State = f.State with { Safety = PlannerSafety.Unsafe };
        if (unsafeState) await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        else await check(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        Assert.Equal(reservation.Attempt, (await f.Runtime.FindAttemptAsync("capture")).Value!.Attempt);
        Assert.Equal(ReservationKind.Existing,
            (await f.Runtime.ReserveGeometryPreparedAsync("prep", "capture", f.Program.Configuration, f.Constraints, f.State)).Value!.Kind);
        Assert.Throws<InvalidOperationException>(() => f.Guard().Capture("prep", reservation with { Kind = ReservationKind.Existing }));
        Assert.Throws<InvalidOperationException>(() => f.Guard().Pending(new PreparationNext.InFlight(1)));
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("constraints")]
    [InlineData("conditions")]
    [InlineData("expired")]
    [InlineData("clock")]
    [InlineData("native-context")]
    public async Task LocalChangesAcrossTheAsyncExchangeCannotPass(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var reads = 0;
        var validations = 0;
        var guard = new NinaGeometryDispatch(f.Runtime, () =>
        {
            var value = f.Snapshot();
            if (++reads != 2) return value;
            return change switch
            {
                "configuration" => value with { Configuration = value.Configuration with { Id = "changed" } },
                "constraints" => value with { Constraints = value.Constraints with { Rig = value.Constraints.Rig with { Revision = 2 } } },
                "conditions" => value with { State = value.State with { Safety = PlannerSafety.Unsafe } },
                "expired" => value with { State = value.State with { NowMs = value.State.ConditionsValidUntilMs } },
                "clock" => value with { State = value.State with { NowMs = Start - 1 } },
                _ => value
            };
        }, () =>
        {
            if (++validations == 2 && change == "native-context") throw new InvalidOperationException("Changed target context.");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.Pending(f.Next)(default));
        f.Native.CameraMediator.Verify(m => m.SetReadoutModeForNormalImages(It.IsAny<short>()), Times.Never);
    }

    [Fact]
    public async Task DuplicateNativeWrappersCannotReuseAnIssuedIdentity()
    {
        await using var f = await Fixture.CreateAsync();
        var guard = f.Guard();
        guard.Pending(f.Next);
        Assert.Throws<InvalidOperationException>(() => guard.Pending(f.Next));
        Assert.Throws<InvalidOperationException>(() => guard.Pending(new PreparationNext.Run(f.Next.Command with { RecipeId = "other" })));
        var reservation = await f.ReserveAsync();
        guard.Capture("prep", reservation);
        Assert.Throws<InvalidOperationException>(() => guard.Capture("prep", reservation));
        Assert.Throws<InvalidOperationException>(() => guard.Capture("other", reservation));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DelayedSuccessfulReplyCannotDispatchEvenWithOtherwiseFreshConditions(bool capture, bool wallClock)
    {
        await using var f = await Fixture.CreateAsync();
        var clock = new DispatchClock();
        var reads = 0;
        var guard = new NinaGeometryDispatch(f.Runtime, () =>
        {
            var snapshot = f.Snapshot();
            if (++reads == 2)
            {
                if (wallClock) snapshot = snapshot with { State = snapshot.State with { NowMs = Start + 59999 } };
                else clock.Ticks = TimeSpan.FromSeconds(61).Ticks;
            }
            return snapshot;
        }, () => { }, clock);
        var check = capture ? guard.Capture("prep", await f.ReserveAsync()) : guard.Pending(f.Next);
        await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        f.Native.CameraMediator.Verify(m => m.SetReadoutModeForNormalImages(It.IsAny<short>()), Times.Never);
        if (capture) Assert.IsType<LedgerEvidence.Reserved>((await f.Runtime.FindAttemptAsync("capture")).Value!.Attempt!.Evidence);
        else Assert.Equal(f.Next.Command, (await f.Runtime.FindPreparationAsync("prep")).Value!.Record!.Pending);
    }

    private sealed class DispatchClock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)Start + Ticks / TimeSpan.TicksPerMillisecond);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("stop")]
    [InlineData("unsafe")]
    [InlineData("expired")]
    [InlineData("recipe")]
    public async Task ProbeRequiresFreshDurableRecoveryAndIsNeverAScienceReservation(string change)
    {
        await using var f = await Fixture.CreateAsync(recovery: true);
        var reservation = await f.ReserveAsync();
        Assert.Null((await f.Runtime.RecordAsync("capture", new LedgerEvidence.Saved("capture", 1))).Error);
        var context = new RecoveryQualityContext("target", "filter-l", 1000, 1, 1, "reference", "nina-pixels", "analysis");
        var identity = new RecoveryIdentity("rig-test", f.Program.Configuration.Id, "night", Start, Start + 60000);
        var poor = await f.Runtime.ApplyRecoveryAsync(new("night", identity.ConfigurationId, "poor", 1, Start,
            new(PlannerSafety.Safe, RecoveryMotion.Permitted), new RecoveryEvent.Quality(
                new("rig-test", identity.ConfigurationId, "science", Start, context, RecoveryVerdict.CorroboratedPoor))));
        f.State = f.State with { NowMs = Start + 1 };
        var grant = (await f.Runtime.ApplyRecoveryAsync(new("night", identity.ConfigurationId, "probe", poor.Value!.Record.Revision, Start + 1,
            new(PlannerSafety.Safe, RecoveryMotion.Permitted), new RecoveryEvent.BeginRecovery("probe-attempt")))).Value!;
        var clock = new DispatchClock();
        var guard = new NinaGeometryDispatch(f.Runtime, f.Snapshot, () => { }, clock);
        var recipe = f.Program.Recipes[0];
        if (change == "recipe") recipe = recipe with { ExposureMs = 2000 };
        var check = guard.Probe("goal", recipe, grant, () => { });
        Assert.Throws<InvalidOperationException>(() => guard.Probe("goal", recipe, grant, () => { }));
        if (change == "stop") await f.Runtime.ApplyRecoveryAsync(new("night", identity.ConfigurationId, "stop", grant.Record.Revision, Start + 1,
            new(PlannerSafety.Safe, RecoveryMotion.Permitted), new RecoveryEvent.StopNight()));
        if (change == "unsafe") f.State = f.State with { Safety = PlannerSafety.Unsafe };
        if (change == "expired") clock.Ticks = TimeSpan.FromSeconds(11).Ticks;
        if (change == "ready")
        {
            var final = await check(default);
            final();
            Assert.Throws<InvalidOperationException>(final);
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => check(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        if (change == "stop")
        {
            await f.Runtime.StopAsync();
            await f.Runtime.StartAsync("rig-test");
            Assert.Null((await f.Runtime.OpenGeometryAsync(f.Program, f.Constraints, f.State)).Error);
        }
        Assert.Null((await f.Runtime.FindAttemptAsync("probe-attempt")).Value!.Attempt);
        Assert.IsType<LedgerEvidence.Saved>((await f.Runtime.FindAttemptAsync(reservation.Attempt!.CaptureId)).Value!.Attempt!.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalDispatchGuardIsOneUseAndRechecksAfterItWasReturned(bool capture)
    {
        await using var f = await Fixture.CreateAsync();
        var clock = new DispatchClock();
        var guard = new NinaGeometryDispatch(f.Runtime, f.Snapshot, () => { }, clock);
        var check = capture ? guard.Capture("prep", await f.ReserveAsync()) : guard.Pending(f.Next);
        var final = await check(default);
        clock.Ticks = TimeSpan.FromSeconds(61).Ticks;
        Assert.Throws<InvalidOperationException>(final);
        clock.Ticks = 0;
        Assert.Throws<InvalidOperationException>(final);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulFinalGuardCannotBeUsedTwice(bool capture)
    {
        await using var f = await Fixture.CreateAsync();
        var guard = f.Guard();
        var check = capture ? guard.Capture("prep", await f.ReserveAsync()) : guard.Pending(f.Next);
        var final = await check(default);
        final();
        Assert.Throws<InvalidOperationException>(final);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReturnedGuardCannotSurviveCancellationOrSessionRestart(bool capture, bool restart)
    {
        await using var f = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var guard = f.Guard();
        var check = capture ? guard.Capture("prep", await f.ReserveAsync()) : guard.Pending(f.Next);
        var final = await check(cancellation.Token);
        if (restart)
        {
            await f.Runtime.StopAsync();
            await f.Runtime.StartAsync("rig-test");
            await f.Runtime.OpenGeometryAsync(f.Program, f.Constraints, f.State);
            Assert.Throws<IOException>(final);
        }
        else
        {
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(final);
        }
        Assert.Throws<InvalidOperationException>(final);
    }

    [Fact]
    public async Task SlowEquipmentValidationAfterAsyncCheckCannotEnterNativePreparation()
    {
        await using var f = await Fixture.CreateAsync();
        var clock = new DispatchClock();
        var guard = new NinaGeometryDispatch(f.Runtime, f.Snapshot, () => { }, clock);
        var check = guard.Pending(f.Next);
        var returned = false;
        f.Native.CameraMediator.Setup(x => x.GetInfo()).Returns(() =>
        {
            if (returned) clock.Ticks = TimeSpan.FromSeconds(61).Ticks;
            return f.Native.Camera;
        });
        var factory = new NinaPreparationItems(f.Native.Profiles.Object, f.Native.CameraMediator.Object,
            f.Native.WheelMediator.Object, f.Equipment, clock);
        var issued = factory.Create(f.Next, f.Program, f.Native.Binding, async token =>
        {
            var final = await check(token);
            returned = true;
            return final;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => issued.Item.Execute(Progress, default));
        Assert.IsType<PreparationOutcome.Failed>(issued.Fence.Completion!.Outcome);
        f.Native.CameraMediator.Verify(x => x.SetReadoutModeForNormalImages(It.IsAny<short>()), Times.Never);
    }

    [Fact]
    public async Task CanceledRuntimeLifetimeInvalidatesOriginalReadyStatus()
    {
        using var lifetime = new CancellationTokenSource();
        await using var f = await Fixture.CreateAsync(lifetime.Token);
        var status = f.Runtime.Status;
        var check = f.Guard().Pending(f.Next);
        Assert.True(f.Runtime.IsCurrentReadySession(status));
        Assert.False(f.Runtime.IsCurrentReadySession(status with { }));
        lifetime.Cancel();
        Assert.False(f.Runtime.IsCurrentReadySession(status));
        await Assert.ThrowsAsync<IOException>(() => check(default));
    }

    [Fact]
    public async Task CanceledCallbackIsConsumedWithoutRunningNativeValidation()
    {
        await using var f = await Fixture.CreateAsync();
        var calls = 0;
        var guard = new NinaGeometryDispatch(f.Runtime, f.Snapshot, () => calls++);
        var check = guard.Pending(f.Next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check(new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => check(default));
        Assert.Equal(0, calls);
    }

    private static void Before(SequentialContainer container, ISequenceItem item, Action action)
    {
        var trigger = new Mock<ISequenceTrigger>();
        trigger.Setup(x => x.ShouldTrigger(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>()))
            .Returns<ISequenceItem, ISequenceItem>((_, next) => ReferenceEquals(next, item));
        trigger.Setup(x => x.Run(It.IsAny<ISequenceContainer>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .Callback(action).Returns(Task.CompletedTask);
        container.Add(trigger.Object);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("director-native-dispatch-");
        internal readonly NinaEquipmentTests.Fixture Native = new();
        internal readonly NinaEquipmentSnapshot Equipment;
        internal readonly RuntimeController Runtime;
        internal readonly DirectorProgram Program;
        internal DirectorConstraints Constraints;
        internal PlannerState State;
        internal PreparationNext.Run Next = null!;

        private Fixture(bool recovery)
        {
            Runtime = recovery ? new(ProcessTests.BundleDirectory, directory.FullName,
                Directory.CreateDirectory(Path.Combine(directory.FullName, "recovery")).FullName) : new(ProcessTests.BundleDirectory, directory.FullName);
            Equipment = new(Native.Profiles.Object, Native.CameraMediator.Object, Native.WheelMediator.Object);
            Native.CameraMediator.Setup(m => m.SetReadoutModeForNormalImages(1)).Callback(() => Native.Camera.ReadoutModeForNormalImages = 1);
            var configuration = Equipment.Read(Native.Binding);
            var baseline = PlannerTests.Request();
            var assignment = baseline.Assignment with
            {
                ConfigurationId = configuration.Id,
                ValidFromMs = Start,
                ExpiresAtMs = Start + 60000,
                Goals = [baseline.Assignment.Goals[0] with { AttemptsRemaining = 1, EligibleWindows = [new(Start, Start + 60000)] }]
            };
            Program = new(1, assignment, configuration, [new("target", "M42", 298800000, -18000000, null)],
                [new("recipe", 1000, "filter-l", new(1, 1), 40, null, 1, null)], [new("goal", "target", "recipe")]);
            Constraints = new(1, new("rig-test", configuration.Id, 1, new(35, -120, 1000),
                new(0, 0, 0, Start, Start + 120001), new DirectorHorizon.FixedMinimum(), -89, 89, new(0, 0)), [new("goal", -89, 89, 0)]);
            State = baseline.State with { ConfigurationId = configuration.Id, NowMs = Start, ConditionsValidUntilMs = Start + 120000 };
        }

        internal static async Task<Fixture> CreateAsync(CancellationToken token = default, bool recovery = false)
        {
            var f = new Fixture(recovery);
            try
            {
                await f.Runtime.StartAsync("rig-test", token);
                if (recovery)
                {
                    var identity = new RecoveryIdentity("rig-test", f.Program.Configuration.Id, "night", Start, Start + 60000);
                    var policy = new RecoveryPolicy(1, RecoveryQualityMode.Pause, 1, 1, 1, 20000, 2, 10000, 30000, Start + 60000, 3, 3, true);
                    Assert.Null((await f.Runtime.OpenRecoveryAsync(identity, policy, Start)).Error);
                    Assert.Null((await f.Runtime.ApplyRecoveryAsync(new("night", identity.ConfigurationId, "tick", 0, Start,
                        new(PlannerSafety.Safe, RecoveryMotion.Permitted), new RecoveryEvent.Tick()))).Error);
                }
                Assert.Null((await f.Runtime.OpenGeometryAsync(f.Program, f.Constraints, f.State)).Error);
                var local = new ProgramLocalState(f.Program.Configuration, new(f.Program.Configuration.Id, f.Program.Targets[0]), false, false, 0);
                Assert.True((await f.Runtime.BeginGeometryPreparationAsync("prep", "goal", local, new(0, 0, 0, 0, 0, 2000, 1000), f.Constraints, f.State)).Value!.Created);
                var filter = Assert.IsType<PreparationNext.Run>((await f.Runtime.AdvanceGeometryPreparationAsync("prep", f.Program.Configuration, f.Constraints, f.State)).Value);
                Assert.IsType<PreparationOperation.SwitchFilter>(filter.Command.Operation);
                await f.Runtime.CompletePreparationAsync(new("prep", filter.Command.Ordinal, Start, 0, new PreparationOutcome.Succeeded()));
                f.Next = Assert.IsType<PreparationNext.Run>((await f.Runtime.AdvanceGeometryPreparationAsync("prep", f.Program.Configuration, f.Constraints, f.State)).Value);
                Assert.IsType<PreparationOperation.SetReadoutMode>(f.Next.Command.Operation);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal NinaDispatchSnapshot Snapshot() => new(Equipment.Read(Native.Binding), Constraints, State);
        internal NinaGeometryDispatch Guard() => new(Runtime, Snapshot, () => { });
        internal async Task<LedgerReservation> ReserveAsync()
        {
            await Runtime.CompletePreparationAsync(new("prep", Next.Command.Ordinal, Start, 0, new PreparationOutcome.Succeeded()));
            var result = (await Runtime.ReserveGeometryPreparedAsync("prep", "capture", Program.Configuration, Constraints, State)).Value!;
            Assert.Equal(ReservationKind.Created, result.Kind);
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            directory.Delete(true);
        }
    }
}
