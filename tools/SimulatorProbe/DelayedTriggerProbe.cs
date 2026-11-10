using System.IO;
using System.Windows;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using PsfGuard.Director.Plugin.Acquisition;

namespace PsfGuard.Director.SimulatorProbe;

// A global autofocus-like trigger that exceeds the recovery evidence age.
// It performs no hardware operation; the following exposure uses the real adapter.
internal sealed class DelayedTriggerProbe : SequenceTrigger, IAsyncDisposable
{
    private readonly SequenceContainer parent;
    private int entered;
    internal bool Completed { get; private set; }

    private DelayedTriggerProbe(SequenceContainer parent)
    {
        this.parent = parent;
        Name = "Delayed global autofocus fixture";
    }

    internal static async Task<DelayedTriggerProbe> InstallAsync(ISequenceContainer parent)
    {
        var trigger = new DelayedTriggerProbe((SequenceContainer)parent);
        await Application.Current.Dispatcher.InvokeAsync(() => trigger.parent.Add(trigger));
        return trigger;
    }

    public override object Clone() => throw new NotSupportedException("Test-only transient trigger.");
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
        entered == 0 && nextItem is NinaExposureItem;
    public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref entered, 1) != 0) throw new InvalidDataException("Delayed trigger was replayed.");
        progress.Report(new() { Status = "Testing a long global autofocus trigger" });
        await Task.Delay(TimeSpan.FromSeconds(35), token);
        Completed = true;
    }

    internal void Verify()
    {
        if (!Completed) throw new InvalidDataException("The delayed global trigger did not finish before capture.");
    }

    public async ValueTask DisposeAsync() => await Application.Current.Dispatcher.InvokeAsync(() => parent.Remove(this));
}
