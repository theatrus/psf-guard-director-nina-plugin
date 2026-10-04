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
using PsfGuard.Director.Plugin.Acquisition;

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
    private CancellationTokenSource? executionCancellation;
    private Task? execution;
    private DirectorSessionOptions options = new();
    private NinaInstructionSlots slots = new();
    private readonly AsyncCommand reportEquipmentCommand;
    private bool reportingEquipment;
    private readonly HashSet<ISequenceTrigger> runtimeTriggers = new(ReferenceEqualityComparer.Instance);

    // Native execution sees the complete base collection. Sequence JSON keeps
    // only user-authored triggers, never the defaults installed for a grant.
    [JsonProperty]
    public new IList<ISequenceTrigger> Triggers
    {
        get { lock (runtimeTriggers) return runtimeTriggers.Count == 0 ? base.Triggers : base.GetTriggersSnapshot().Where(t => !runtimeTriggers.Contains(t)).ToArray(); }
        private set => base.Triggers = value;
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
        reportEquipmentCommand = new(ReportEquipmentAsync, CanReportEquipment, error =>
        {
            NINA.Core.Utility.Logger.Error(error);
            Report(Display with { Phase = "Equipment report failed; check pairing, connected devices and session settings", Operation = "" });
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
        lock (displayLock) ActionHistory = new[] { entry }.Concat(ActionHistory).Take(200).ToArray();
        NINA.Core.Utility.Logger.Info($"Director action: {entry.Target}; {entry.Action}; {entry.Outcome}; elapsed_ms={elapsedMs}");
        RaisePropertyChanged(nameof(ActionHistory));
    }
    public ICommand ReportEquipmentCommand => reportEquipmentCommand;
    public string SessionStatus => Display.Phase;
    public string Readiness => !Options.EnableAcquisition ? AcquisitionGate :
        $"{(Options.LocalTargetScheduling ? "Local target scheduling; target setup in Before New Target" : "Prepared target")}; " +
        (Options.AutomaticWorkloads ? "commissioned automatic workloads" : "online one-shot allocation admission required");
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
        lock (displayLock) Display = update(Display) ?? throw new InvalidOperationException("Missing Director status.");
        RaisePropertyChanged(nameof(Display));
        RaisePropertyChanged(nameof(SessionStatus));
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Validate()) throw new InvalidOperationException(string.Join(" ", Issues));
        lock (executionLock)
        {
            if (reportingEquipment) throw new InvalidOperationException("Wait for the equipment report before starting acquisition.");
            if (execution is { IsCompleted: false }) throw new InvalidOperationException("Director session is already running.");
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
                    await (acquisition ?? throw new InvalidOperationException("Director acquisition services unavailable.")).ExecuteAsync(this, progress, cancellation.Token);
                    foreach (var condition in conditions) condition.SequenceBlockFinished();
                    foreach (var trigger in triggers) trigger.SequenceBlockFinished();
                }
                catch (Exception error) { executionError = error; throw; }
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
                        throw new AggregateException("Director session teardown failed.", cleanupErrors);
                    }
                }
            }, CancellationToken.None);
            return execution;
        }
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
        issues.AddRange(DirectorAcquisition.PolicyIssues(Options));
        if (Options.EnableAcquisition && acquisition is null) issues.Add("Director acquisition services unavailable.");
        if (Options.EnableAcquisition && Attempts != 1) issues.Add("Director acquisition requires one attempt; allocation launches cannot be retried.");
        if (!valid) issues.Add("A session trigger or condition is invalid.");
        Issues = new ObservableCollection<string>(issues);
        RaisePropertyChanged(nameof(Issues));
        RaisePropertyChanged(nameof(ConfigurationIssues));
        RaisePropertyChanged(nameof(Readiness));
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
    private void RestoreParents(StreamingContext _) => AttachSlots();

    private void AttachSlots()
    {
        foreach (var block in InstructionBlocks) block.AttachNewParent(this);
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args) { Validate(); reportEquipmentCommand.Refresh(); }
}
