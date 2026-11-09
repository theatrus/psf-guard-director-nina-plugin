using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Utility;
using PsfGuard.Director.Plugin.Acquisition;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaSequenceFailureTests
{
    [Fact]
    public async Task SuccessfulRetryDoesNotHideLaterFailureOrTurnCancellationIntoAnError()
    {
        var root = new SequenceRootContainer();
        var block = new SequentialContainer();
        var focus = new WaitForTimeSpan { Name = "Autofocus" };
        var guide = new WaitForTimeSpan { Name = "Start guiding" };
        root.Add(block); block.Add(focus); block.Add(guide);
        using var observer = new NinaSequenceFailure(block);
        await root.RaiseFailureEvent(focus, new Exception("First attempt failed"));
        focus.Status = NINA.Core.Enum.SequenceEntityStatus.FINISHED;
        Assert.Null(observer.Failure);
        await root.RaiseFailureEvent(guide, new Exception("No guide star available"));
        await root.RaiseFailureEvent(guide, new Exception("Guiding did not settle"));
        await root.RaiseFailureEvent(block, new Exception("Target setup failed"));
        Assert.Equal("Start guiding: Guiding did not settle", observer.Failure!.Message);
    }

    [Fact]
    public async Task TriggerErrorsAreCapturedAsWellAsInstructionErrors()
    {
        var root = new SequenceRootContainer();
        var block = new SequentialContainer();
        root.Add(block);
        var trigger = new Moq.Mock<NINA.Sequencer.Trigger.ISequenceTrigger>();
        trigger.SetupGet(t => t.Parent).Returns(block);
        trigger.SetupGet(t => t.Name).Returns("Restore guiding");
        using var observer = new NinaSequenceFailure(block);
        await root.RaiseFailureEvent(trigger.Object, new Exception("No guide star available"));
        Assert.Equal("Restore guiding: No guide star available", observer.Failure!.Message);
    }

    [Fact]
    public async Task OriginalNestedFailureSurvivesParentCancellationAndIgnoresOtherBranches()
    {
        var root = new SequenceRootContainer();
        var block = new SequentialContainer();
        var nested = new SequentialContainer();
        var guide = new WaitForTimeSpan { Name = "Start guiding" };
        var sibling = new WaitForTimeSpan { Name = "Other telescope" };
        root.Add(block); root.Add(sibling); block.Add(nested); nested.Add(guide);
        using var observer = new NinaSequenceFailure(block);
        await root.RaiseFailureEvent(sibling, new Exception("unrelated failure"));
        Assert.Null(observer.Failure);
        var original = new InvalidOperationException("Could not find a guide star");
        await root.RaiseFailureEvent(guide, original);
        await root.RaiseFailureEvent(block, new OperationCanceledException());
        Assert.Equal("Start guiding: Could not find a guide star.", DirectorSessionText.Failure(observer.Failure!));
        Assert.Same(original, observer.Failure!.InnerException);
        observer.Dispose();
        await root.RaiseFailureEvent(guide, new Exception("late failure"));
        Assert.Same(original, observer.Failure.InnerException);
    }
}
