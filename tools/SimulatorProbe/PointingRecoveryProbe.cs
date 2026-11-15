using System.IO;
using System.Windows;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.SimulatorProbe;

// Moves only the isolated ASCOM simulator. Exercise a real pointing refusal,
// including one after reservation when an inherited trigger changes pointing.
internal sealed class PointingRecoveryProbe : SequenceTrigger, IAsyncDisposable
{
    private readonly SequenceContainer parent;
    private readonly ITelescopeMediator telescope;
    private readonly string scenario;
    private int saves;
    internal int Offsets { get; private set; }

    private PointingRecoveryProbe(SequenceContainer parent, ITelescopeMediator telescope, string scenario)
    {
        this.parent = parent; this.telescope = telescope; this.scenario = scenario;
        Name = "Simulator pointing offset";
    }

    internal static async Task<PointingRecoveryProbe> InstallAsync(ISequenceContainer parent, DirectorSessionContainer session,
        ITelescopeMediator telescope, string scenario)
    {
        var probe = new PointingRecoveryProbe((SequenceContainer)parent, telescope, scenario);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // The first target needs one image, the second needs two. Offset
            // after the second save so the next preparation is for the same
            // target, reproducing drift before its dither/filter operations.
            if (scenario == "pointing-preparation") session.AfterEachExposure.Add(new OffsetItem(probe));
            else probe.parent.Add(probe);
        });
        return probe;
    }

    public override object Clone() => throw new NotSupportedException("Test-only trigger.");
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
        nextItem is NinaExposureItem && (Offsets == 0 || scenario == "pointing-always");
    public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => OffsetAsync(token);

    private async Task OffsetAsync(CancellationToken token)
    {
        if (scenario != "pointing-always" && Offsets != 0) return;
        if (telescope.GetInfo() is not { Connected: true, DeviceId: "ASCOM.OmniSim.Telescope" })
            throw new InvalidOperationException("Pointing fault requires the isolated ASCOM simulator.");
        var current = telescope.GetCurrentPosition().Transform(Epoch.J2000);
        var displaced = new Coordinates(current.RADegrees, current.Dec + 0.25, Epoch.J2000, Coordinates.RAType.Degrees);
        if (!await telescope.SlewToCoordinatesAsync(displaced, token)) throw new InvalidDataException("Simulator offset failed.");
        while ((telescope.GetCurrentPosition().Transform(Epoch.J2000) - displaced).Distance.Degree > 0.01)
            await Task.Delay(100, token);
        Offsets++;
    }

    internal void Verify(DirectorSessionContainer session, int centers, bool failed)
    {
        if (Offsets != (scenario == "pointing-always" ? 2 : 1) || centers != (failed ? 2 : 3)
            || session.ActionHistory.Count(a => a.Action == "Pointing") != 1
            || session.ActionHistory.Count(a => a.Action == "Unpark mount" && a.Outcome == "Started") != 1
            || session.ActionHistory.Any(a => a.Outcome.Contains("Director will try again", StringComparison.Ordinal)))
            throw new InvalidDataException("Pointing recovery restarted the session or did not use exactly one recenter.");
    }

    public async ValueTask DisposeAsync() => await Application.Current.Dispatcher.InvokeAsync(() => parent.Remove(this));

    private sealed class OffsetItem(PointingRecoveryProbe owner) : SequenceItem
    {
        public override object Clone() => new OffsetItem(owner);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            ++owner.saves == 2 ? owner.OffsetAsync(token) : Task.CompletedTask;
    }
}
