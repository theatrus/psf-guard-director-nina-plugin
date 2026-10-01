using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using PsfGuard.Director.Plugin.Acquisition;

namespace PsfGuard.Director.Plugin.Sequencer;

[Export(typeof(ISequenceItem))]
[Export(typeof(ISequenceContainer))]
[ExportMetadata("Name", "Director Session")]
[ExportMetadata("Description", "Experimental Director session configuration. Acquisition is not yet armed.")]
[ExportMetadata("Icon", "TelescopeSVG")]
[ExportMetadata("Category", "PSF Guard Director")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class DirectorSessionContainer : SequentialContainer
{
    internal const string AcquisitionGate = "Acquisition unavailable: commissioned safety, Earth orientation and durable assignment admission are not connected yet.";
    private DirectorSessionOptions options = new();
    private NinaInstructionSlots slots = new();

    [ImportingConstructor]
    public DirectorSessionContainer()
    {
        Name = "Director Session";
        options.PropertyChanged += SettingsChanged;
        AttachSlots();
    }

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
    public string SessionStatus => Display.Phase;
    public string Readiness => AcquisitionGate;
    public IEnumerable<string> ConfigurationIssues => Issues.Where(issue => issue != AcquisitionGate);

    internal void Report(DirectorSessionDisplay display)
    {
        Display = display ?? throw new ArgumentNullException(nameof(display));
        RaisePropertyChanged(nameof(Display));
        RaisePropertyChanged(nameof(SessionStatus));
    }

    // A saved sequence cannot manufacture an acquisition permit. The simulator
    // exercises the same editable slots through internal, explicitly bound calls.
    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException(AcquisitionGate);
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
        issues.Add(AcquisitionGate);
        if (!valid) issues.Add("A session trigger or condition is invalid.");
        Issues = new ObservableCollection<string>(issues);
        RaisePropertyChanged(nameof(Issues));
        RaisePropertyChanged(nameof(ConfigurationIssues));
        return false;
    }

    public override object Clone()
    {
        var clone = new DirectorSessionContainer { Options = Options.Clone(), Slots = Slots.Clone() };
        clone.CopyMetaData(this);
        clone.Attempts = Attempts;
        clone.ErrorBehavior = ErrorBehavior;
        clone.IsExpanded = IsExpanded;
        clone.Status = Status == SequenceEntityStatus.DISABLED ? SequenceEntityStatus.DISABLED : SequenceEntityStatus.CREATED;
        // Reuse the native deep-clone checks, including trigger runner settings.
        var native = (SequentialContainer)base.Clone();
        NinaInstructionSlots.CopyExecutionSettings(this, native);
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

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args) => Validate();
}
