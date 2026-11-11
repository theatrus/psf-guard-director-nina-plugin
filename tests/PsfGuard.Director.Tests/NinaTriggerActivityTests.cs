using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using Xunit;

namespace PsfGuard.Director.Tests;

[Collection("Director equipment reporting")]
public sealed class NinaTriggerActivityTests
{
    [Fact]
    public async Task SilentGlobalTriggerOverridesExposureInToolbarSessionAndServerSnapshotThenClears()
    {
        var root = new SequenceRootContainer();
        var session = new DirectorSessionContainer();
        root.Add(session);
        session.Status = SequenceEntityStatus.RUNNING;
        var trigger = new SilentTrigger { Name = "Inject Autofocus" };
        root.Add(trigger);
        var reports = new List<ApplicationStatus>();
        using var progress = new DirectorSessionProgress(session, new InlineProgress<ApplicationStatus>(reports.Add));
        session.UpdateDisplay(d => d with { Phase = "Acquiring", Operation = "HA · 120 s exposure", Target = "M31" });
        session.RecordAction("M31", "Exposure", "Started");
        var running = trigger.Run(session, progress, CancellationToken.None);
        Assert.False(running.IsCompleted);
        session.SetTriggerActivity(NinaTriggerActivity.Read(session));
        Assert.Equal("Running N.I.N.A. trigger: Inject Autofocus", session.SessionStatus);
        Assert.Equal("Inject Autofocus", session.CurrentDisplay.Operation);
        var telemetry = session.TelemetrySnapshot();
        Assert.Equal("Running N.I.N.A. trigger", telemetry.Display.Phase);
        Assert.Equal("Inject Autofocus", telemetry.Operation);
        Assert.NotNull(telemetry.StartedMs);
        progress.Report(new ApplicationStatus { Status = "Exposing and downloading" });
        Assert.StartsWith("Inject Autofocus — M31", reports[^1].Status);
        // Planner refreshes cannot overwrite a running trigger.
        session.UpdateDisplay(d => d with { QueueDepth = "3" });
        Assert.Equal("Inject Autofocus", session.TelemetrySnapshot().Operation);
        trigger.Complete.SetResult();
        await running;
        session.SetTriggerActivity(NinaTriggerActivity.Read(session));
        Assert.Equal("Acquiring", session.SessionStatus);
        Assert.Equal("Exposure", session.TelemetrySnapshot().Operation);
        Assert.StartsWith("HA · 120 s exposure — M31", reports[^1].Status);
    }

    [Theory]
    [InlineData(SequenceEntityStatus.CREATED)]
    [InlineData(SequenceEntityStatus.FINISHED)]
    [InlineData(SequenceEntityStatus.FAILED)]
    [InlineData(SequenceEntityStatus.DISABLED)]
    public void FinishedFailedDisabledAndOtherBranchTriggersCannotLeaveAStaleStatus(SequenceEntityStatus status)
    {
        var root = new SequenceRootContainer();
        var session = new DirectorSessionContainer();
        root.Add(session);
        var trigger = new SilentTrigger { Name = "Global autofocus", Status = status };
        root.Add(trigger);
        var other = new SequentialContainer();
        root.Add(other);
        other.Add(new SilentTrigger { Name = "Other target", Status = SequenceEntityStatus.RUNNING });
        Assert.Null(NinaTriggerActivity.Read(session));
        trigger.Status = SequenceEntityStatus.RUNNING;
        Assert.Equal("Global autofocus", NinaTriggerActivity.Read(session));
        root.Remove(trigger);
        Assert.Null(NinaTriggerActivity.Read(session));
    }

    [Fact]
    public void LatestSessionObservationIsScopedToTheProfile()
    {
        var profile = Guid.NewGuid();
        var session = new DirectorSessionContainer();
        DirectorSessionObservation.Observe(profile, session);
        Assert.Same(session, DirectorSessionObservation.Read(profile));
        Assert.Null(DirectorSessionObservation.Read(Guid.NewGuid()));
    }

    [Fact]
    public void TriggerOutsideDirectorAlsoOverridesSequenceActivity()
    {
        var root = new SequenceRootContainer();
        var instruction = new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Name = "Next exposure", Status = SequenceEntityStatus.RUNNING };
        root.Add(instruction);
        var trigger = new SilentTrigger { Name = "Inject Autofocus", Status = SequenceEntityStatus.RUNNING };
        root.Add(trigger);
        var mediator = new Moq.Mock<NINA.Sequencer.Interfaces.Mediator.ISequenceMediator>();
        mediator.SetupGet(m => m.Initialized).Returns(true);
        mediator.Setup(m => m.IsAdvancedSequenceRunning()).Returns(true);
        mediator.Setup(m => m.GetAdvancedSequencerCurrentRunningItems()).Returns([instruction]);
        Assert.Equal("N.I.N.A. trigger: Inject Autofocus", NinaSequenceActivity.Read(mediator.Object)!.Operation);
        trigger.Status = SequenceEntityStatus.FINISHED;
        Assert.Contains("Next exposure", NinaSequenceActivity.Read(mediator.Object)!.Operation);
    }

    private sealed class SilentTrigger : SequenceTrigger
    {
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override object Clone() => new SilentTrigger { Name = Name };
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => true;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => Complete.Task.WaitAsync(token);
    }
}
