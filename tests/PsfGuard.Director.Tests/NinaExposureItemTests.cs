using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Guider;
using PsfGuard.Director.Plugin.Acquisition;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed partial class NinaCaptureTests
{
    [Fact]
    public async Task ExposureItemRunsInheritedHooksBeforeFinalValidationAndAfterSave()
    {
        using var f = new Fixture();
        var order = new List<string>();
        var binding = Bound(f);
        var item = new NinaExposureItem(f.BoundCapture, Created(binding), binding, f.Local,
            _ => { order.Add("validate"); return Task.CompletedTask; });
        var outer = new SequentialContainer();
        var inner = new SequentialContainer();
        outer.Add(inner);
        inner.Add(item);
        AddExposureHook(inner, item, () => order.Add("inner-before"), () => { Assert.NotNull(item.Evidence); order.Add("inner-after"); });
        AddExposureHook(outer, item, () => order.Add("outer-before"), () => order.Add("outer-after"));
        Assert.IsAssignableFrom<IExposureItem>(item);
        Assert.Equal(TimeSpan.FromSeconds(1.5), item.GetEstimatedDuration());
        var run = outer.Run(f.Progress, default);
        await f.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(item.Evidence);
        Assert.Equal(new[] { "inner-before", "outer-before", "validate" }, order);
        f.Saved();
        await run;
        Assert.Equal(SequenceEntityStatus.FINISHED, item.Status);
        Assert.Equal(CapturePhase.Saved, item.Evidence!.Phase);
        Assert.Null(item.ExecutionError);
        Assert.Equal(new[] { "inner-before", "outer-before", "validate", "inner-after", "outer-after" }, order);
    }

    [Fact]
    public void NativeRestoreGuidingRecognizesDirectorAsALightExposure()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        var item = new NinaExposureItem(f.BoundCapture, Created(binding), binding, f.Local, _ => Task.CompletedTask);
        var guider = new Mock<IGuiderMediator>();
        guider.Setup(x => x.GetInfo()).Returns(new NINA.Equipment.Equipment.MyGuider.GuiderInfo { Connected = true });
        var trigger = new RestoreGuiding(guider.Object, Mock.Of<ISafetyMonitorMediator>());
        Assert.True(trigger.ShouldTrigger(null!, item));
        item.ImageType = CaptureSequence.ImageTypes.DARK;
        Assert.False(trigger.ShouldTrigger(null!, item));
    }

    [Fact]
    public async Task ExposureConditionSkipHasNoCaptureOrSaveReceipt()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        var item = new NinaExposureItem(f.BoundCapture, Created(binding), binding, f.Local, _ => throw new Exception("Skipped"));
        var container = new SequentialContainer();
        container.Add(item);
        var condition = new Mock<ISequenceCondition>();
        condition.Setup(x => x.RunCheck(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>())).Returns(false);
        container.Add(condition.Object);
        await container.Run(f.Progress, default);
        Assert.Null(item.Evidence);
        Assert.Null(item.ExecutionError);
        Assert.Empty(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories));
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Never);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("gain")]
    [InlineData("offset")]
    [InlineData("binning")]
    [InlineData("type")]
    public async Task InheritedHookCannotChangeReservedExposureRecipe(string fault)
    {
        using var f = new Fixture();
        var binding = Bound(f);
        var item = new NinaExposureItem(f.BoundCapture, Created(binding), binding, f.Local, _ => Task.CompletedTask);
        var container = new SequentialContainer();
        container.Add(item);
        AddExposureHook(container, item, () =>
        {
            switch (fault)
            {
                case "duration": item.ExposureTime = 0; break;
                case "gain": item.Gain = -1; break;
                case "offset": item.Offset = -1; break;
                case "binning": item.Binning.X = 2; break;
                case "type": item.ImageType = CaptureSequence.ImageTypes.DARK; break;
            }
        });
        await container.Run(f.Progress, default);
        Assert.Equal(SequenceEntityStatus.FAILED, item.Status);
        Assert.IsType<InvalidOperationException>(item.ExecutionError);
        Assert.Null(item.Evidence);
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Never);
    }

    [Fact]
    public async Task SettingsAreRecheckedAfterAsynchronousBoundaryValidation()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        NinaExposureItem? item = null;
        item = new(f.BoundCapture, Created(binding), binding, f.Local, _ => { item!.Gain = 40; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => item.Execute(f.Progress, default));
        Assert.Null(item.Evidence);
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Never);
    }

    [Fact]
    public async Task ReservedExposureCannotCloneRetryOrRepeatAfterSave()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        var item = new NinaExposureItem(f.BoundCapture, Created(binding), binding, f.Local, _ => Task.CompletedTask);
        Assert.Throws<NotSupportedException>(() => item.Clone());
        Assert.Throws<InvalidOperationException>(() => item.Attempts = 2);
        var run = item.Run(f.Progress, default);
        await f.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => item.Execute(f.Progress, default));
        f.Saved();
        await run;
        var saved = item.Evidence;
        item.ResetProgress();
        await item.Run(f.Progress, default);
        Assert.Equal(SequenceEntityStatus.FAILED, item.Status);
        Assert.Same(saved, item.Evidence);
        f.Imaging.Verify(x => x.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), f.Progress, f.Intent.TargetName), Times.Once);
    }

    [Fact]
    public async Task CanceledExposureCannotReportASaveOrRetryOnReset()
    {
        using var f = new Fixture();
        var binding = Bound(f);
        using var cts = new CancellationTokenSource();
        var item = new NinaExposureItem(f.BoundCapture, Created(binding), binding, f.Local, _ => Task.CompletedTask);
        var run = item.Execute(f.Progress, cts.Token);
        await f.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Null(item.Evidence);
        Assert.IsAssignableFrom<OperationCanceledException>(item.ExecutionError);
        Assert.Equal(CapturePhase.SaveUncertain, f.Read().Phase);
        item.ResetProgress();
        await Assert.ThrowsAsync<InvalidOperationException>(() => item.Execute(f.Progress, default));
        f.AssertDetached();
    }

    private static void AddExposureHook(SequentialContainer container, ISequenceItem item, Action before, Action? after = null)
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
}
