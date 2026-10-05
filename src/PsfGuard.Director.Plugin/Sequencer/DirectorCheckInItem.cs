using System.ComponentModel.Composition;
using System.Collections.ObjectModel;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Sequencer;

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Director Check In")]
[ExportMetadata("Description", "Deliver saved Director runs and finish clean deferred workload releases. Does not operate equipment.")]
[ExportMetadata("Icon", "TelescopeSVG")]
[ExportMetadata("Category", "PSF Guard Director")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class DirectorCheckInItem : SequenceItem, IValidatable
{
    private readonly DirectorCheckInService service;
    [ImportingConstructor]
    public DirectorCheckInItem(DirectorCheckInService service) { this.service = service; Name = "Director Check In"; }
    public override object Clone()
    {
        var clone = new DirectorCheckInItem(service);
        clone.CopyMetaData(this);
        return clone;
    }
    public IList<string> Issues { get; private set; } = new ObservableCollection<string>();
    public override TimeSpan GetEstimatedDuration() => TimeSpan.Zero;
    public bool Validate()
    {
        var issues = new List<string>();
        for (var parent = Parent; parent is not null; parent = parent.Parent)
            if (parent is DirectorSessionContainer)
            { issues.Add("Place Director Check In before or after the Session. Use its live check-ins during acquisition."); break; }
        Issues = new ObservableCollection<string>(issues);
        RaisePropertyChanged(nameof(Issues));
        return issues.Count == 0;
    }
    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var updates = new InlineProgress<CoordinatorRunCheckInProgress>(p =>
        {
            progress.Report(new ApplicationStatus
            { Source = Name, Status = $"{p.Runs} runs; {p.DeliveredEvents} events; capture {p.AcknowledgedThrough}; operations {p.OperationsAcknowledgedThrough}" });
        });
        try { await service.RunAsync(updates, token); }
        finally { progress.Report(new ApplicationStatus { Source = Name, Status = "" }); }
    }
}
