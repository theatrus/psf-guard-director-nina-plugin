using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.Trigger;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaPreparationItemTests
{
    private static readonly IProgress<ApplicationStatus> Progress = new Progress<ApplicationStatus>();

    [Fact]
    public async Task NativeContainerKeepsInheritedTriggersAndFencesAfterThem()
    {
        var f = new Fixture();
        var order = new List<string>();
        var issued = f.Create(_ => { order.Add("fence"); return Task.CompletedTask; });
        Assert.IsAssignableFrom<SetReadoutMode>(issued.Item);
        f.Native.CameraMediator.Setup(x => x.SetReadoutModeForNormalImages(1)).Callback(() =>
        {
            order.Add("native");
            f.Native.Camera.ReadoutModeForNormalImages = 1;
        });
        var outer = new SequentialContainer();
        var inner = new SequentialContainer();
        outer.Add(inner);
        inner.Add(issued.Item);
        AddTrigger(inner, issued.Item, () => order.Add("inner-before"), () => order.Add("inner-after"));
        AddTrigger(outer, issued.Item, () => order.Add("outer-before"), () => order.Add("outer-after"));
        await outer.Run(Progress, default);
        Assert.Equal(new[] { "inner-before", "outer-before", "fence", "native", "inner-after", "outer-after" }, order);
        Assert.Equal(SequenceEntityStatus.FINISHED, issued.Item.Status);
        Assert.IsType<PreparationOutcome.Succeeded>(issued.Fence.Completion!.Outcome);
    }

    [Fact]
    public async Task InheritedConditionPreventsDispatchAndLeavesNoSuccessReceipt()
    {
        var f = new Fixture();
        var issued = f.Create();
        var outer = new SequentialContainer();
        var inner = new SequentialContainer();
        outer.Add(inner);
        inner.Add(issued.Item);
        var condition = new Mock<ISequenceCondition>();
        condition.Setup(x => x.RunCheck(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>())).Returns(false);
        outer.Add(condition.Object);
        await outer.Run(Progress, default);
        Assert.Null(issued.Fence.Completion);
        f.Native.CameraMediator.Verify(x => x.SetReadoutModeForNormalImages(It.IsAny<short>()), Times.Never);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("readout-setting")]
    public async Task ChangesInInheritedTriggerFailBeforeHardware(string fault)
    {
        var f = new Fixture();
        var issued = f.Create();
        var outer = new SequentialContainer();
        outer.Add(issued.Item);
        AddTrigger(outer, issued.Item, () =>
        {
            if (fault == "configuration") f.Native.Camera.DriverVersion = "changed";
            else ((SetReadoutMode)issued.Item).Mode = 0;
        });
        await outer.Run(Progress, default);
        Assert.Equal(SequenceEntityStatus.FAILED, issued.Item.Status);
        Assert.IsType<PreparationOutcome.Failed>(issued.Fence.Completion!.Outcome);
        f.Native.CameraMediator.Verify(x => x.SetReadoutModeForNormalImages(It.IsAny<short>()), Times.Never);
    }

    [Fact]
    public async Task CancellationFromInheritedTriggerCannotReachHardware()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        var issued = f.Create();
        var container = new SequentialContainer();
        container.Add(issued.Item);
        AddTrigger(container, issued.Item, cts.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => container.Run(Progress, cts.Token));
        Assert.IsType<PreparationOutcome.Failed>(issued.Fence.Completion!.Outcome);
        f.Native.CameraMediator.Verify(x => x.SetReadoutModeForNormalImages(It.IsAny<short>()), Times.Never);
    }

    [Fact]
    public async Task NativeFailureIsUncertainEvenWhenNinaRunSwallowsTheException()
    {
        var f = new Fixture();
        var issued = f.Create();
        f.Native.CameraMediator.Setup(x => x.SetReadoutModeForNormalImages(1)).Throws(new IOException("Driver lost reply"));
        await issued.Item.Run(Progress, default);
        Assert.Equal(SequenceEntityStatus.FAILED, issued.Item.Status);
        Assert.IsType<PreparationOutcome.Uncertain>(issued.Fence.Completion!.Outcome);
        issued.Item.ResetProgress();
        await issued.Item.Run(Progress, default);
        f.Native.CameraMediator.Verify(x => x.SetReadoutModeForNormalImages(1), Times.Once);
        Assert.IsType<PreparationOutcome.Uncertain>(issued.Fence.Completion!.Outcome);
    }

    [Fact]
    public async Task ResetAndCloneCannotReplayACompletedOperation()
    {
        var f = new Fixture();
        var issued = f.Create();
        Assert.Throws<InvalidOperationException>(() => issued.Item.Attempts = 3);
        Assert.Throws<NotSupportedException>(() => issued.Item.Clone());
        await issued.Item.Run(Progress, default);
        var completion = issued.Fence.Completion;
        issued.Item.ResetProgress();
        await issued.Item.Run(Progress, default);
        Assert.Equal(SequenceEntityStatus.FAILED, issued.Item.Status);
        Assert.Same(completion, issued.Fence.Completion);
        f.Native.CameraMediator.Verify(x => x.SetReadoutModeForNormalImages(1), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterItemRetainsNativeIdentityButRejectsChangedExpressions(bool currentFilter)
    {
        var f = new Fixture();
        var issued = f.Create(operation: new PreparationOperation.SwitchFilter("filter-l"));
        var filter = Assert.IsAssignableFrom<SwitchFilter>(issued.Item);
        Assert.Equal(2, filter.Xfilter);
        if (currentFilter) filter.ComboBoxText = NullFilter.Instance.Name;
        else filter.Xfilter = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => filter.Execute(Progress, default));
        Assert.IsType<PreparationOutcome.Failed>(issued.Fence.Completion!.Outcome);
        f.Native.WheelMediator.Verify(x => x.ChangeFilter(It.IsAny<NINA.Core.Model.Equipment.FilterInfo>(),
            It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Never);
    }

    [Fact]
    public void RecoveryAndUnboundOperationsCannotCreateItems()
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => f.Factory.Create(new PreparationNext.InFlight(1), f.Program, f.Native.Binding, _ => Task.CompletedTask));
        var wrong = new PreparationNext.Run(f.Command with { TargetId = "wrong" });
        Assert.Throws<InvalidDataException>(() => f.Factory.Create(wrong, f.Program, f.Native.Binding, _ => Task.CompletedTask));
        Assert.Throws<NotSupportedException>(() => f.Create(operation: new PreparationOperation.SetReadoutMode(0)));
    }

    [Fact]
    public async Task NativeReadoutNoOpCannotBecomeSuccessfulCompletion()
    {
        var f = new Fixture();
        f.Native.CameraMediator.Setup(x => x.SetReadoutModeForNormalImages(1));
        var issued = f.Create();
        await issued.Item.Run(Progress, default);
        Assert.Equal(SequenceEntityStatus.FAILED, issued.Item.Status);
        Assert.IsType<PreparationOutcome.Uncertain>(issued.Fence.Completion!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterReceiptRequiresTheRequestedSettledNativeState(bool moving)
    {
        var f = new Fixture();
        f.Native.Wheel.SelectedFilter = f.Native.Filters[0];
        f.Native.Wheel.IsMoving = moving;
        f.Native.WheelMediator.Setup(x => x.ChangeFilter(f.Native.Filters[0], It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .ReturnsAsync(f.Native.Filters[0]);
        var issued = f.Create(operation: new PreparationOperation.SwitchFilter("filter-l"));
        await issued.Item.Run(Progress, default);
        if (moving)
        {
            Assert.Equal(SequenceEntityStatus.FAILED, issued.Item.Status);
            Assert.IsType<PreparationOutcome.Uncertain>(issued.Fence.Completion!.Outcome);
        }
        else
        {
            Assert.Equal(SequenceEntityStatus.FINISHED, issued.Item.Status);
            Assert.IsType<PreparationOutcome.Succeeded>(issued.Fence.Completion!.Outcome);
        }
    }

    [Fact]
    public async Task FixedFilterStillUsesOneShotRevalidationWithoutMovingAWheel()
    {
        var f = new Fixture();
        f.Native.WheelSettings.SetupGet(x => x.Id).Returns("No_Device");
        f.Native.Wheel.Connected = false;
        var local = f.Native.Binding with { FilterWheelDeviceId = null, Filters = [new("filter-l", null, null)] };
        var configuration = new NinaEquipmentSnapshot(f.Native.Profiles.Object, f.Native.CameraMediator.Object, f.Native.WheelMediator.Object).Read(local);
        var program = f.Program with { Configuration = configuration, Assignment = f.Program.Assignment with { ConfigurationId = configuration.Id } };
        var issued = f.Factory.Create(new PreparationNext.Run(f.Command with { Operation = new PreparationOperation.SwitchFilter("filter-l") }),
            program, local, _ => Task.CompletedTask);
        await issued.Item.Run(Progress, default);
        Assert.IsType<PreparationOutcome.Succeeded>(issued.Fence.Completion!.Outcome);
        f.Native.WheelMediator.Verify(x => x.ChangeFilter(It.IsAny<NINA.Core.Model.Equipment.FilterInfo>(),
            It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentEntryDoesNotReplaceInFlightEvidenceOrRepeatWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock();
        var f = new Fixture();
        var fence = new NinaOperationFence(f.Command, _ => Task.CompletedTask, clock);
        var run = fence.ExecuteAsync(async () => { entered.SetResult(); await finish.Task; clock.Milliseconds += 31; }, default);
        await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fence.ExecuteAsync(() => throw new Exception("Repeated"), default));
        Assert.Null(fence.Completion);
        finish.SetResult();
        await run;
        Assert.Equal(31UL, fence.Completion!.ElapsedMs);
        Assert.Equal(1031UL, fence.Completion.EndedAtMs);
        Assert.Equal(f.Command.PreparationId, fence.Completion.PreparationId);
        Assert.Equal(f.Command.Ordinal, fence.Completion.Ordinal);
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)2)]
    public async Task NativeFilterUsesExactTypedSlot(short slot)
    {
        var f = new Fixture();
        f.Native.Filters[0].Position = slot;
        f.Native.Binding = f.Native.Binding with { Filters = [new("filter-l", slot, "L")] };
        f.Native.Wheel.SelectedFilter = f.Native.Filters[0];
        f.Native.WheelMediator.Setup(x => x.ChangeFilter(f.Native.Filters[0], It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .ReturnsAsync(f.Native.Filters[0]);
        var configuration = f.Native.Read();
        var program = f.Program with { Configuration = configuration, Assignment = f.Program.Assignment with { ConfigurationId = configuration.Id } };
        var issued = f.Factory.Create(new PreparationNext.Run(f.Command with { Operation = new PreparationOperation.SwitchFilter("filter-l") }),
            program, f.Native.Binding, _ => Task.CompletedTask);
        var filter = Assert.IsAssignableFrom<SwitchFilter>(issued.Item);
        Assert.True(filter.Validate(), string.Join(", ", filter.Issues));
        Assert.Equal(slot, filter.Xfilter);
        await filter.Run(Progress, default);
        Assert.IsType<PreparationOutcome.Succeeded>(issued.Fence.Completion!.Outcome);
        f.Native.WheelMediator.Verify(x => x.ChangeFilter(f.Native.Filters[0], It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Once);
    }

    private static void AddTrigger(SequentialContainer container, ISequenceItem item, Action before, Action? after = null)
    {
        var trigger = new Mock<ISequenceTrigger>();
        trigger.Setup(x => x.ShouldTrigger(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>()))
            .Returns<ISequenceItem, ISequenceItem>((_, next) => ReferenceEquals(next, item));
        trigger.Setup(x => x.ShouldTriggerAfter(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>()))
            .Returns<ISequenceItem, ISequenceItem>((previous, _) => after is not null && ReferenceEquals(previous, item));
        var calls = 0;
        trigger.Setup(x => x.Run(It.IsAny<ISequenceContainer>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .Callback(() => { if (calls++ == 0) before(); else after?.Invoke(); }).Returns(Task.CompletedTask);
        container.Add(trigger.Object);
    }

    private sealed class ManualClock : TimeProvider
    {
        internal long Milliseconds = 1000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds);
    }

    private sealed class Fixture
    {
        internal readonly NinaEquipmentTests.Fixture Native = new();
        internal readonly DirectorProgram Program;
        internal readonly NinaPreparationItems Factory;
        internal readonly PreparationCommand Command = new("prep", 1, "goal", "target", "recipe", new PreparationOperation.SetReadoutMode(1));
        internal Fixture()
        {
            Native.CameraMediator.Setup(x => x.SetReadoutModeForNormalImages(1)).Callback(() => Native.Camera.ReadoutModeForNormalImages = 1);
            var configuration = Native.Read();
            Program = new(1, PlannerTests.Request().Assignment with { ConfigurationId = configuration.Id }, configuration,
                [new("target", "Target", 648000000, 72000000, null)],
                [new("recipe", 1000, "filter-l", new(1, 1), 40, null, 1, null)], [new("goal", "target", "recipe")]);
            Factory = new(Native.Profiles.Object, Native.CameraMediator.Object, Native.WheelMediator.Object,
                new(Native.Profiles.Object, Native.CameraMediator.Object, Native.WheelMediator.Object), TimeProvider.System);
        }
        internal NinaIssuedItem Create(Func<CancellationToken, Task>? validate = null, PreparationOperation? operation = null) =>
            Factory.Create(new PreparationNext.Run(operation is null ? Command : Command with { Operation = operation }),
                Program, Native.Binding, validate ?? (_ => Task.CompletedTask));
    }
}
