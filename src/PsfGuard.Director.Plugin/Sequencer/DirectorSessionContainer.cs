using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.Serialization;
using System.Windows.Input;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Sequencer;

public sealed record DirectorActionEntry(DateTimeOffset Time, string Target, string Action, string Outcome, ulong? ElapsedMs);

[Export(typeof(ISequenceItem))]
[Export(typeof(ISequenceContainer))]
[ExportMetadata("Name", "Director Session")]
[ExportMetadata("Description", "Experimental Director local target scheduling with native sequence hooks.")]
[ExportMetadata("Icon", "TelescopeSVG")]
[ExportMetadata("Category", "PSF Guard Director")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class DirectorSessionContainer : SequentialContainer
{
    internal const string AcquisitionGate = "Enable acquisition to run this session.";
    private readonly DirectorAcquisition? acquisition;
    private readonly object executionLock = new();
    private readonly object displayLock = new();
    private readonly List<(DirectorActionEntry Entry, long Timestamp)> activeActions = [];
    private CancellationTokenSource? executionCancellation;
    private Task? execution;
    private DirectorSessionOptions options = new() { AutomaticWorkloads = true, LocalTargetScheduling = true };
    private NinaInstructionSlots slots = new();
    private readonly AsyncCommand reportEquipmentCommand;
    private bool reportingEquipment;
    private bool confirmAttendedRun;
    private bool resumeRecordedNight;
    private const string AttendedGate = "Confirm that you will supervise this attended run before starting the sequence.";

    public bool ConfirmAttendedRun
    {
        get { lock (executionLock) return confirmAttendedRun; }
        set
        {
            lock (executionLock)
            {
                if (execution is { IsCompleted: false } || Options.Safety != DirectorSafetyPolicy.Attended) return;
                confirmAttendedRun = value;
            }
            RaisePropertyChanged();
            Validate();
        }
    }
    // Operator intent is one-use and deliberately absent from saved sequences.
    public bool ResumeRecordedNight
    {
        get { lock (executionLock) return resumeRecordedNight; }
        set { lock (executionLock) { if (execution is { IsCompleted: false }) return; resumeRecordedNight = value; } RaisePropertyChanged(); }
    }
    internal bool ConsumeRestartRequest()
    {
        bool requested;
        lock (executionLock) { requested = resumeRecordedNight; resumeRecordedNight = false; }
        RaisePropertyChanged(nameof(ResumeRecordedNight));
        return requested;
    }
    private readonly HashSet<ISequenceTrigger> runtimeTriggers = new(ReferenceEqualityComparer.Instance);

    // Native execution sees the complete base collection. Sequence JSON keeps
    // only user-authored triggers, never the defaults installed for a grant.
    [JsonProperty]
    public new IList<ISequenceTrigger> Triggers
    {
        get { lock (runtimeTriggers) return runtimeTriggers.Count == 0 ? base.Triggers : ConfiguredTriggersSnapshot(); }
        private set => base.Triggers = value;
    }

    internal ISequenceTrigger[] ConfiguredTriggersSnapshot()
    {
        lock (runtimeTriggers) return base.GetTriggersSnapshot().Where(trigger => !runtimeTriggers.Contains(trigger)).ToArray();
    }

    internal void AddRuntimeTrigger(ISequenceTrigger trigger)
    {
        lock (runtimeTriggers) { runtimeTriggers.Add(trigger); Add(trigger); }
    }

    internal void RemoveRuntimeTrigger(ISequenceTrigger trigger)
    {
        lock (runtimeTriggers) { Remove(trigger); runtimeTriggers.Remove(trigger); }
    }

    public DirectorSessionContainer()
    {
        Name = "Director Session";
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
        reportEquipmentCommand = new(ReportEquipmentAsync, CanReportEquipment, error =>
        {
            NINA.Core.Utility.Logger.Error(error);
            var detail = error switch
            {
                InvalidOperationException validation when !string.IsNullOrWhiteSpace(validation.Message) => validation.Message,
                CoordinatorIntakeException failure => $"Could not confirm the report with PSF Guard ({failure.Failure}). Check the coordinator connection and pairing.",
                _ => "See the N.I.N.A. log for details."
            };
            UpdateDisplay(display => display with { Phase = $"Equipment report failed. {detail}", Operation = "" });
        });
        options.PropertyChanged += SettingsChanged;
        AttachSlots();
    }

    [ImportingConstructor]
    public DirectorSessionContainer(DirectorAcquisition acquisition) : this() => this.acquisition = acquisition;

    [JsonProperty(Required = Required.Always, ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public DirectorSessionOptions Options
    {
        get => options;
        private set
        {
            ArgumentNullException.ThrowIfNull(value);
            // Public sessions always request coordinator work and select targets
            // locally. Keep old JSON fields readable without exposing old modes.
            value.AutomaticWorkloads = true;
            value.LocalTargetScheduling = true;
            value.AllowSettledRestart = false;
            options.PropertyChanged -= SettingsChanged;
            options = value;
            options.PropertyChanged += SettingsChanged;
        }
    }

    [JsonProperty("InstructionSlots", Required = Required.Always, ObjectCreationHandling = ObjectCreationHandling.Replace)]
    internal NinaInstructionSlots Slots { get => slots; private set => slots = value ?? throw new ArgumentNullException(nameof(value)); }

    public SequentialContainer BeforeWait => slots.BeforeWait;
    public SequentialContainer AfterWait => slots.AfterWait;
    public SequentialContainer BeforeNewTarget => slots.BeforeNewTarget;
    public SequentialContainer AfterEachExposure => slots.AfterEachExposure;
    public SequentialContainer AfterNewTarget => slots.AfterNewTarget;
    public SequentialContainer AfterEachTarget => slots.AfterEachTarget;
    public SequentialContainer AfterTargetComplete => slots.AfterTargetComplete;
    public IReadOnlyList<SequentialContainer> InstructionBlocks => Enum.GetValues<NinaInstructionSlot>().Select(slot => slots[slot]).ToArray();
    public DirectorSessionDisplay Display { get; private set; } = DirectorSessionDisplay.Empty;
    public IReadOnlyList<DirectorActionEntry> ActionHistory { get; private set; } = [];
    public NINA.Astrometry.Interfaces.IDeepSkyObject? SkyTarget { get; private set; }
    public NINA.Astrometry.NighttimeData? SkyNighttime { get; private set; }

    internal void ShowSky(NINA.Astrometry.Interfaces.IDeepSkyObject? target, NINA.Astrometry.NighttimeData? nighttime)
    {
        SkyTarget = target;
        SkyNighttime = nighttime;
        RaisePropertyChanged(nameof(SkyTarget));
        RaisePropertyChanged(nameof(SkyNighttime));
    }

    internal void RecordAction(string target, string action, string outcome, ulong? elapsedMs = null)
    {
        static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(512).ToArray());
        var entry = new DirectorActionEntry(DateTimeOffset.Now, Clean(target), Clean(action), Clean(outcome), elapsedMs);
        lock (displayLock)
        {
            ActionHistory = new[] { entry }.Concat(ActionHistory).Take(200).ToArray();
            if (outcome == "Started")
            {
                if (activeActions.Count == 32) activeActions.RemoveAt(0);
                activeActions.Add((entry, System.Diagnostics.Stopwatch.GetTimestamp()));
            }
            else
            {
                var index = activeActions.FindLastIndex(item => item.Entry.Action == entry.Action && item.Entry.Target == entry.Target);
                if (index >= 0) activeActions.RemoveRange(index, activeActions.Count - index);
            }
        }
        NINA.Core.Utility.Logger.Info($"Director action: {entry.Target}; {entry.Action}; {entry.Outcome}; elapsed_ms={elapsedMs}");
        RaisePropertyChanged(nameof(ActionHistory));
    }
    public ICommand ReportEquipmentCommand => reportEquipmentCommand;
    internal (DirectorSessionDisplay Display, string? Operation, long? StartedMs, ulong? ElapsedMs) TelemetrySnapshot()
    {
        lock (displayLock)
        {
            if (activeActions.Count == 0) return (Display, null, null, null);
            var active = activeActions[^1];
            return (Display, active.Entry.Action, active.Entry.Time.ToUnixTimeMilliseconds(),
                (ulong)System.Diagnostics.Stopwatch.GetElapsedTime(active.Timestamp).TotalMilliseconds);
        }
    }
    public string SessionStatus => Display.Phase != DirectorSessionDisplay.Empty.Phase ? Display.Phase :
        Options.EnableAcquisition
            ? "Not started. Equipment and permission to image are checked when you start the sequence."
            : "Acquisition is off. Turn on Enable acquisition in the Session tab to allow imaging.";
    public string Readiness =>
        (Options.LocalTargetScheduling ? "Director selects targets from the workload." : "Each workload uses one prepared target.") + " " +
        (Options.AutomaticWorkloads ? "Director requests work from PSF Guard automatically when the sequence runs." :
            "Assign a workload in PSF Guard before starting the sequence.");
    public IEnumerable<string> ConfigurationIssues => Issues.Where(issue => issue != AcquisitionGate);

    private bool CanReportEquipment()
    {
        lock (executionLock) return acquisition is not null && !PsfGuard.Director.Runtime.AcquisitionLease.IsActive && !reportingEquipment && execution is not { IsCompleted: false }
            && Options.ValidateSettings().Count == 0 && DirectorAcquisition.PolicyIssues(Options, false).Count == 0;
    }

    internal async Task ReportEquipmentAsync()
    {
        lock (executionLock)
        {
            if (!CanReportEquipment()) throw new InvalidOperationException("Equipment reporting requires an idle session with supported settings.");
            reportingEquipment = true;
        }
        reportEquipmentCommand.Refresh();
        try { await acquisition!.ReportEquipmentAsync(this, CancellationToken.None); }
        finally { lock (executionLock) reportingEquipment = false; reportEquipmentCommand.Refresh(); }
    }

    internal void Report(DirectorSessionDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        UpdateDisplay(_ => display);
    }

    internal void UpdateDisplay(Func<DirectorSessionDisplay, DirectorSessionDisplay> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (displayLock)
        {
            Display = update(Display) ?? throw new InvalidOperationException("Missing Director status.");
            if (Display.Operation.Length == 0) activeActions.Clear();
        }
        RaisePropertyChanged(nameof(Display));
        RaisePropertyChanged(nameof(SessionStatus));
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
        token.ThrowIfCancellationRequested();
        if (!Validate())
        {
            var error = new InvalidOperationException(string.Join(" ", Issues));
            ReportExecutionFailure(error);
            throw error;
        }
        if (Options.AllowSettledRestart) NinaRestartAdmission.ValidateLifecycle(this);
        lock (executionLock)
        {
            if (reportingEquipment) throw new InvalidOperationException("Wait for the equipment report before starting acquisition.");
            if (execution is { IsCompleted: false }) throw new InvalidOperationException("Director session is already running.");
            var attended = Options.Safety == DirectorSafetyPolicy.Attended;
            if (attended)
            {
                if (!confirmAttendedRun) throw new InvalidOperationException(AttendedGate);
                confirmAttendedRun = false;
                RaisePropertyChanged(nameof(ConfirmAttendedRun));
            }
            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancellation = executionCancellation;
            execution = Task.Run(async () =>
            {
                var conditions = GetConditionsSnapshot();
                var triggers = GetTriggersSnapshot();
                Exception? executionError = null;
                try
                {
                    Iterations = 0;
                    foreach (var condition in conditions) { condition.SequenceBlockInitialize(); condition.SequenceBlockStarted(); }
                    foreach (var trigger in triggers) { trigger.SequenceBlockInitialize(); trigger.SequenceBlockStarted(); }
                    await (acquisition ?? throw new InvalidOperationException("Director acquisition services unavailable.")).ExecuteAsync(this, progress, attended, cancellation.Token);
                    foreach (var condition in conditions) condition.SequenceBlockFinished();
                    foreach (var trigger in triggers) trigger.SequenceBlockFinished();
                }
                catch (Exception error)
                {
                    executionError = error;
                    if (error is OperationCanceledException && token.IsCancellationRequested)
                        UpdateDisplay(display => display with { Phase = "Session canceled", Operation = "", WaitReason = "Sequence canceled" });
                    else ReportExecutionFailure(error);
                    throw;
                }
                finally
                {
                    var cleanupErrors = new List<Exception>();
                    try
                    {
                        foreach (var condition in conditions)
                            try { condition.SequenceBlockTeardown(); } catch (Exception error) { cleanupErrors.Add(error); }
                        foreach (var trigger in triggers)
                            try { trigger.SequenceBlockTeardown(); } catch (Exception error) { cleanupErrors.Add(error); }
                    }
                    finally { lock (executionLock) { executionCancellation = null; cancellation.Dispose(); } reportEquipmentCommand.Refresh(); }
                    if (cleanupErrors.Count != 0)
                    {
                        if (executionError is not null) cleanupErrors.Insert(0, executionError);
                        var error = new AggregateException("Director session teardown failed.", cleanupErrors);
                        ReportExecutionFailure(error);
                        throw error;
                    }
                }
            }, CancellationToken.None);
            return execution;
        }
    }

    private void ReportExecutionFailure(Exception error)
    {
        var primary = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;
        var detail = primary is CoordinatorIntakeException failure
            ? $"PSF Guard could not authorize the work ({failure.Failure}). Check the rig's Director setup in PSF Guard."
            : primary.Message;
        UpdateDisplay(display => display with { Phase = $"Session stopped. {detail}", Operation = "", WaitReason = detail });
        RecordAction("", "Session", $"Failed. {detail}");
    }

    public override async Task Interrupt()
    {
        Task? pending;
        CancellationTokenSource? cancellation;
        lock (executionLock) { cancellation = executionCancellation; pending = execution; }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (pending is not null)
        {
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    public override bool Validate()
    {
        var valid = base.Validate();
        var issues = new List<string>(Options.ValidateSettings());
        if (Items.Count != 0) issues.Add("Place Director instructions in a named instruction slot, not the session body.");
        foreach (var block in InstructionBlocks)
            if (!block.Validate()) issues.Add($"{block.Name} contains invalid instructions.");
        try { NinaHookAdmission.Validate(InstructionBlocks, this); }
        catch (InvalidOperationException error) { issues.Add(error.Message); }
        if (Options.EnableAcquisition)
        {
            if (Options.Safety == DirectorSafetyPolicy.Attended && !ConfirmAttendedRun && execution is not { IsCompleted: false })
                issues.Add(AttendedGate);
            try { NinaNativeImaging.ValidateOwnership(this, Options); }
            catch (InvalidOperationException error) { issues.Add(error.Message); }
        }
        issues.AddRange(DirectorAcquisition.PolicyIssues(Options));
        if (Options.EnableAcquisition && acquisition is null) issues.Add("Director acquisition services unavailable.");
        if (Options.EnableAcquisition && Attempts != 1) issues.Add("Director acquisition requires one attempt; allocation launches cannot be retried.");
        if (!valid) issues.Add("A session trigger or condition is invalid.");
        Issues = new ObservableCollection<string>(issues);
        RaisePropertyChanged(nameof(Issues));
        RaisePropertyChanged(nameof(ConfigurationIssues));
        RaisePropertyChanged(nameof(Readiness));
        RaisePropertyChanged(nameof(SessionStatus));
        return issues.Count == 0;
    }

    public override object Clone()
    {
        var clone = acquisition is null ? new DirectorSessionContainer() : new DirectorSessionContainer(acquisition);
        clone.Options = Options.Clone();
        clone.Slots = Slots.Clone();
        clone.CopyMetaData(this);
        clone.Attempts = Attempts;
        clone.ErrorBehavior = ErrorBehavior;
        clone.IsExpanded = IsExpanded;
        clone.Status = Status == SequenceEntityStatus.DISABLED ? SequenceEntityStatus.DISABLED : SequenceEntityStatus.CREATED;
        // Reuse the native deep-clone checks, including trigger runner settings.
        var native = (SequentialContainer)base.Clone();
        NinaInstructionSlots.CopyExecutionSettings(this, native);
        var originals = GetTriggersSnapshot().ToArray();
        var clonedTriggers = native.GetTriggersSnapshot().ToArray();
        lock (runtimeTriggers)
            for (var i = 0; i < originals.Length; i++)
                if (runtimeTriggers.Contains(originals[i])) native.Remove(clonedTriggers[i]);
        foreach (var item in native.Items.ToArray()) { native.Remove(item); clone.Add(item); }
        foreach (var trigger in native.Triggers.ToArray()) { native.Remove(trigger); clone.Add(trigger); }
        foreach (var condition in native.Conditions.ToArray()) { native.Remove(condition); clone.Add(condition); }
        clone.AttachSlots();
        return clone;
    }

    public override void ResetProgress()
    {
        base.ResetProgress();
        foreach (var block in InstructionBlocks) block.ResetAll();
        Report(DirectorSessionDisplay.Empty);
        lock (displayLock) ActionHistory = [];
        ShowSky(null, null);
        RaisePropertyChanged(nameof(ActionHistory));
    }

    public override void AfterParentChanged()
    {
        base.AfterParentChanged();
        foreach (var block in InstructionBlocks) block.AfterParentChanged();
    }

    [OnDeserialized]
    private void RestoreParents(StreamingContext _)
    {
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
        AttachSlots();
    }

    private void AttachSlots()
    {
        foreach (var block in InstructionBlocks) block.AttachNewParent(this);
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DirectorSessionOptions.Safety) || !Options.EnableAcquisition)
        {
            lock (executionLock) confirmAttendedRun = false;
            RaisePropertyChanged(nameof(ConfirmAttendedRun));
        }
        Validate();
        reportEquipmentCommand.Refresh();
    }
}
