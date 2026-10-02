using System.ComponentModel.Composition;
using Moq;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Utility;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DirectorSessionContainerTests
{
    [Fact]
    public void MoonWindowsCanCloseAfterCleanPreparationWithoutBecomingAnEquipmentFailure()
    {
        var decision = new PlannerDecision(PlannerAction.CheckIn, "no_authorized_feasible_work");
        Assert.False(DirectorAcquisition.CanReselect(decision));
        Assert.True(DirectorAcquisition.CanReselect(decision, moonScheduling: true));
        Assert.False(DirectorAcquisition.CanReselect(new(PlannerAction.Stop, "unsafe"), moonScheduling: true));
    }
    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        PreserveReferencesHandling = PreserveReferencesHandling.Objects
    };
    private static readonly IProgress<ApplicationStatus> Progress = new Progress<ApplicationStatus>();

    [Fact]
    public async Task BackgroundConnectivityUpdatesKeepTheCurrentLocalTarget()
    {
        var session = new DirectorSessionContainer();
        var target = Task.Run(() => { for (var i = 0; i < 1000; i++) session.UpdateDisplay(d => d with { Target = i.ToString() }); });
        var checkin = Task.Run(() => { for (var i = 0; i < 1000; i++) session.UpdateDisplay(d => d with { QueueDepth = i.ToString() }); });
        await Task.WhenAll(target, checkin);
        Assert.Equal("999", session.Display.Target);
        Assert.Equal("999", session.Display.QueueDepth);
    }

    [Theory]
    [InlineData(PlannerAction.Wait, "future_window", true)]
    [InlineData(PlannerAction.CheckIn, "preparation_goal_changed", true)]
    [InlineData(PlannerAction.CheckIn, "conditions_stale", false)]
    [InlineData(PlannerAction.CheckIn, "configuration_mismatch", false)]
    [InlineData(PlannerAction.Stop, "safety_not_confirmed", false)]
    [InlineData(PlannerAction.Acquire, "highest_priority_feasible_goal", false)]
    public void OnlyCleanLocalSelectionBoundariesMayReselect(PlannerAction action, string reason, bool expected) =>
        Assert.Equal(expected, DirectorAcquisition.CanReselect(new(action, reason)));

    [Fact]
    public async Task EquipmentReportingCannotRunWithoutNativeServicesOrSerializeAuthority()
    {
        var session = new DirectorSessionContainer();
        session.Options.SlewCenter = session.Options.Focus = session.Options.Guiding = session.Options.Dither =
            session.Options.MeridianFlip = DirectorOperationOwner.Sequence;
        Assert.Empty(DirectorAcquisition.PolicyIssues(session.Options, false));
        Assert.False(session.Options.EnableAcquisition);
        Assert.False(session.ReportEquipmentCommand.CanExecute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(session.ReportEquipmentAsync);
        Assert.False(session.ReportEquipmentCommand.CanExecute(null));
        Assert.DoesNotContain("ReportEquipmentCommand", JsonConvert.SerializeObject(session, Settings));
    }

    [Fact]
    public void SessionIsExportedAsANativeContainerAndItem()
    {
        var exports = typeof(DirectorSessionContainer).GetCustomAttributes(typeof(ExportAttribute), false).Cast<ExportAttribute>();
        Assert.Equal(new[] { typeof(ISequenceItem), typeof(ISequenceContainer) }, exports.Select(e => e.ContractType));
        var session = new DirectorSessionContainer();
        Assert.Equal(7, session.InstructionBlocks.Count);
        Assert.All(session.InstructionBlocks, block => Assert.Same(session, block.Parent));
        Assert.Empty(session.Options.ValidateSettings());
    }

    [Fact]
    public void SequenceRoundTripKeepsAllFieldsAndSevenIndependentSlots()
    {
        var session = new DirectorSessionContainer();
        session.Options.MaximumHours = 6;
        session.Options.LocalTargetScheduling = true;
        session.Options.AutomaticWorkloads = true;
        session.Options.MinimumAltitude = 30;
        session.Options.MaximumAltitude = 85;
        session.Options.MeridianBeforeMinutes = 60;
        session.Options.MeridianAfterMinutes = 4;
        session.Options.Safety = DirectorSafetyPolicy.Attended;
        session.Options.Enclosure = DirectorEnclosurePolicy.RequireOpenShutter;
        session.Options.OnAbort = DirectorAbortPolicy.StopMount;
        session.Options.Horizon = DirectorHorizonPolicy.MinimumAltitude;
        session.Options.Startup = session.Options.SlewCenter = session.Options.Focus = session.Options.Guiding =
            session.Options.Dither = session.Options.MeridianFlip = session.Options.Shutdown = DirectorOperationOwner.Sequence;
        session.Options.ParkOnWait = session.Options.AllowOffline = session.Options.CheckInAtStart =
            session.Options.CheckInAtEnd = session.Options.CheckInOnTarget = session.Options.LiveStatus = false;
        session.Options.DitherEveryExposures = 7;
        session.Options.HookTimeoutSeconds = 20;
        session.Options.SaveTimeoutSeconds = 45;
        session.Options.CheckInMinutes = 17;
        session.Options.StatusSeconds = 35;
        foreach (var block in session.InstructionBlocks)
            block.Add(new WaitForTimeSpan { Time = 1, Attempts = 2 });
        var json = JsonConvert.SerializeObject(session, Settings);
        var restored = JsonConvert.DeserializeObject<DirectorSessionContainer>(json, Settings)!;
        Assert.Equal(JsonConvert.SerializeObject(session.Options), JsonConvert.SerializeObject(restored.Options));
        Assert.All(restored.InstructionBlocks, block =>
        {
            Assert.Same(restored, block.Parent);
            var item = Assert.IsType<WaitForTimeSpan>(Assert.Single(block.Items));
            Assert.Same(block, item.Parent);
            Assert.Equal(2, item.Attempts);
        });
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SessionStatus", json);
        Assert.DoesNotContain("ledger", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(restored.Validate());
    }

    [Fact]
    public void CloningAndResetNeverCopyRuntimeAuthorityOrLoseDisabledConfiguration()
    {
        var session = new DirectorSessionContainer { Attempts = 4, ErrorBehavior = InstructionErrorBehavior.SkipInstructionSetOnError };
        session.BeforeNewTarget.Add(new WaitForTimeSpan { Time = 2, Status = SequenceEntityStatus.DISABLED });
        session.BeforeNewTarget.IsExpanded = false;
        session.Report(DirectorSessionDisplay.Empty with { ProgramRevision = "runtime-only-revision", Target = "M31" });
        session.Options.MeridianBeforeMinutes = 50;
        session.Options.OnAbort = DirectorAbortPolicy.StopMount;
        var clone = Assert.IsType<DirectorSessionContainer>(session.Clone());
        Assert.NotSame(session.Options, clone.Options);
        Assert.Equal(DirectorAbortPolicy.StopMount, clone.Options.OnAbort);
        Assert.NotSame(session.BeforeNewTarget, clone.BeforeNewTarget);
        Assert.Same(clone.BeforeNewTarget, clone.BeforeNewTarget.Items[0].Parent);
        Assert.All(clone.InstructionBlocks, block => Assert.Same(clone, block.Parent));
        Assert.Equal(4, clone.Attempts);
        Assert.Equal(session.ErrorBehavior, clone.ErrorBehavior);
        Assert.Equal(SequenceEntityStatus.DISABLED, clone.BeforeNewTarget.Items[0].Status);
        Assert.False(clone.BeforeNewTarget.IsExpanded);
        Assert.Equal(DirectorSessionDisplay.Empty, clone.Display);
        Assert.DoesNotContain("runtime-only-revision", JsonConvert.SerializeObject(session, Settings));
        clone.Options.MeridianBeforeMinutes = 5;
        Assert.Equal(50, session.Options.MeridianBeforeMinutes);
        clone.ResetProgress();
        Assert.Equal(SequenceEntityStatus.DISABLED, clone.BeforeNewTarget.Items[0].Status);
        Assert.False(clone.Validate());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Options\":null,\"InstructionSlots\":{}}")]
    public void MissingOrNullConfigurationCannotDeserializeAsReady(string json) =>
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<DirectorSessionContainer>(json, Settings));

    [Fact]
    public async Task PublicExecutionRefusesBeforeAnyUserHookRuns()
    {
        var called = false;
        var session = new DirectorSessionContainer();
        session.BeforeNewTarget.Add(new Callback(() => called = true));
        Assert.False(session.Validate());
        Assert.Contains(DirectorSessionContainer.AcquisitionGate, session.Issues);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Execute(Progress, default));
        Assert.False(called);
    }

    [Fact]
    public void NumericAndUnknownEnumValuesAreNotSilentlyAccepted()
    {
        var options = new DirectorSessionOptions
        {
            MaximumHours = double.NaN,
            MinimumAltitude = 80,
            MaximumAltitude = 20,
            CheckInMinutes = 0,
            Safety = (DirectorSafetyPolicy)99,
            Focus = (DirectorOperationOwner)99
        };
        Assert.Equal(5, options.ValidateSettings().Count);
        var future = JsonConvert.DeserializeObject<DirectorSessionOptions>("{\"SchemaVersion\":2}")!;
        Assert.Single(future.ValidateSettings());
    }

    [Fact]
    public async Task HookCadencePreservesTargetVisitsWaitsAndSavedExposureBoundaries()
    {
        var events = new List<NinaInstructionSlot>();
        var session = Session(events);
        var target = new SequentialContainer();
        target.AttachNewParent(session);
        var hooks = new NinaSessionHooks(session, TimeProvider.System);
        // Edits after session start must not change this invocation's configuration.
        session.BeforeNewTarget.Add(new Callback(() => throw new InvalidOperationException("late edit")));
        await hooks.SelectTargetAsync("one", target, Progress, default);
        await hooks.SelectTargetAsync("one", target, Progress, default);
        await hooks.ExposureSavedAsync("capture-1", Progress, default);
        await hooks.ExposureSavedAsync("capture-2", Progress, default);
        await hooks.WaitAsync(_ => Task.CompletedTask, Progress, default);
        await hooks.SelectTargetAsync("one", target, Progress, default);
        await hooks.TargetCompletedAsync(Progress, default);
        await hooks.FinishAsync(Progress, default);
        Assert.Equal(new[] { NinaInstructionSlot.BeforeNewTarget, NinaInstructionSlot.AfterEachExposure, NinaInstructionSlot.AfterEachExposure,
            NinaInstructionSlot.AfterNewTarget, NinaInstructionSlot.AfterEachTarget, NinaInstructionSlot.BeforeWait, NinaInstructionSlot.AfterWait,
            NinaInstructionSlot.BeforeNewTarget, NinaInstructionSlot.AfterTargetComplete, NinaInstructionSlot.AfterNewTarget, NinaInstructionSlot.AfterEachTarget }, events);
        Assert.Equal(events.Count, hooks.Receipts.Count);
        Assert.All(hooks.Receipts, receipt => { Assert.True(receipt.Completed); Assert.True(receipt.ElapsedMs >= 0); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => hooks.SelectTargetAsync("two", target, Progress, default));
    }

    [Fact]
    public async Task DuplicateCaptureAndCanceledWaitCannotReplayHooks()
    {
        var events = new List<NinaInstructionSlot>();
        var session = Session(events);
        var target = new SequentialContainer();
        target.AttachNewParent(session);
        var hooks = new NinaSessionHooks(session, TimeProvider.System);
        await hooks.SelectTargetAsync("one", target, Progress, default);
        await hooks.ExposureSavedAsync("capture-1", Progress, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hooks.ExposureSavedAsync("capture-1", Progress, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hooks.FinishAsync(Progress, default));
        Assert.Single(events, e => e == NinaInstructionSlot.AfterEachExposure);
        hooks = new NinaSessionHooks(session, TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hooks.WaitAsync(token =>
        {
            canceled.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }, Progress, canceled.Token));
        Assert.DoesNotContain(NinaInstructionSlot.AfterWait, events);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hooks.FinishAsync(Progress, default));
    }

    private static DirectorSessionContainer Session(List<NinaInstructionSlot> events)
    {
        var session = new DirectorSessionContainer();
        foreach (var slot in Enum.GetValues<NinaInstructionSlot>())
            session.Slots[slot].Add(new Callback(() => events.Add(slot)));
        return session;
    }

    [Fact]
    public void NestedUnreservedExposuresAreRejectedBeforeInstructionsRun()
    {
        var session = new DirectorSessionContainer();
        var nested = new SequentialContainer();
        var exposure = new Mock<IExposureItem>();
        exposure.SetupGet(item => item.Status).Returns(SequenceEntityStatus.CREATED);
        nested.Add(exposure.Object);
        session.BeforeNewTarget.Add(nested);
        Assert.Throws<InvalidOperationException>(() => new NinaSessionHooks(session, TimeProvider.System));
        session.Validate();
        Assert.Contains(session.Issues, issue => issue.Contains("reserved-capture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HookTimeoutPoisonsTheVisitAndRecordsFailure()
    {
        var session = new DirectorSessionContainer();
        session.Options.HookTimeoutSeconds = 1;
        session.BeforeWait.Add(new WaitForTimeSpan { Time = 30 });
        var hooks = new NinaSessionHooks(session, TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hooks.WaitAsync(_ => Task.CompletedTask, Progress, default));
        Assert.False(Assert.Single(hooks.Receipts).Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hooks.FinishAsync(Progress, default));
    }

    [Fact]
    public async Task FailedNativeItemCannotBeMistakenForSuccessfulHook()
    {
        var session = new DirectorSessionContainer();
        session.BeforeWait.Add(new Callback(() => throw new InvalidOperationException("failed")) { Attempts = 1 });
        var hooks = new NinaSessionHooks(session, TimeProvider.System);
        var waited = false;
        await Assert.ThrowsAnyAsync<Exception>(() => hooks.WaitAsync(_ => { waited = true; return Task.CompletedTask; }, Progress, default));
        Assert.False(waited);
        Assert.False(Assert.Single(hooks.Receipts).Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hooks.FinishAsync(Progress, default));
    }

    private sealed class Callback(Action callback) : SequenceItem
    {
        public override object Clone() => new Callback(callback);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); callback(); return Task.CompletedTask;
        }
    }
}
