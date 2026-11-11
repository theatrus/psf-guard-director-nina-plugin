using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Sequencer;

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Director Flats")]
[ExportMetadata("Description", "Take panel flats for active PSF Guard plans, including plans whose light frames are complete. Sync replacement requests and keep the original files.")]
[ExportMetadata("Icon", "TelescopeSVG")]
[ExportMetadata("Category", "PSF Guard Director")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class DirectorFlatsItem : SequenceItem, IValidatable
{
    private readonly DirectorAcquisition acquisition;
    [ImportingConstructor]
    public DirectorFlatsItem(DirectorAcquisition acquisition)
    { this.acquisition = acquisition; Name = "Director Flats"; ErrorBehavior = InstructionErrorBehavior.AbortOnError; }
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public DirectorSessionOptions Options { get; private set; } = new() { ScheduledFlats = true, ClosePanelAtEnd = true };
    public IList<string> Issues { get; private set; } = new ObservableCollection<string>();
    internal DirectorSessionOptions CaptureOptions()
    {
        var options = Options.Clone();
        options.ScheduledFlats = true; options.DarksWhileIdle = false; options.DarksWhileWeatherHeld = false;
        return options;
    }
    public bool Validate()
    {
        var issues = CaptureOptions().ValidateSettings().ToList();
        for (var parent = Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is DirectorSessionContainer)
            { issues.Add("Place Director Flats before or after Director Session. Use the session's scheduled flats inside it."); break; }
            if (parent is NINA.Sequencer.Container.ParallelContainer)
            { issues.Add("Place Director Flats in a sequential container so other instructions cannot operate the camera at the same time."); break; }
        }
        Issues = new ObservableCollection<string>(issues); RaisePropertyChanged(nameof(Issues));
        return issues.Count == 0;
    }
    public override object Clone()
    {
        var clone = new DirectorFlatsItem(acquisition) { Options = Options.Clone() };
        clone.CopyMetaData(this); return clone;
    }
    public override TimeSpan GetEstimatedDuration() => TimeSpan.Zero;
    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        for (var parent = Parent; parent is not null; parent = parent.Parent)
            if (parent is DirectorSessionContainer or NINA.Sequencer.Container.ParallelContainer)
                throw new InvalidOperationException("Run Director Flats in a sequential container before or after Director Session.");
        var updates = new InlineProgress<ApplicationStatus>(p => { p.Source = Name; progress.Report(p); });
        try { await acquisition.ExecuteFlatsAsync(CaptureOptions(), updates, token); }
        finally { progress.Report(new ApplicationStatus { Source = Name, Status = "" }); }
    }
}
