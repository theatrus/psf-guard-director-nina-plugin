using System.IO;
using System.Windows;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.SimulatorProbe;

// A global autofocus-like trigger that exceeds the recovery evidence age.
// It performs no hardware operation; the following exposure uses the real adapter.
internal sealed class DelayedTriggerProbe : SequenceTrigger, IAsyncDisposable
{
    private readonly SequenceContainer parent;
    private readonly HistoricalFailure history = new();
    private int entered;
    private NinaExposureItem? exposure;
    internal bool Completed { get; private set; }

    private DelayedTriggerProbe(SequenceContainer parent)
    {
        this.parent = parent;
        Name = "Delayed global autofocus fixture";
    }

    internal static async Task<DelayedTriggerProbe> InstallAsync(ISequenceContainer parent)
    {
        var trigger = new DelayedTriggerProbe((SequenceContainer)parent);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            trigger.parent.Add(trigger.history);
            trigger.history.Status = SequenceEntityStatus.FAILED;
            trigger.parent.Add(trigger);
        });
        return trigger;
    }

    public override object Clone() => throw new NotSupportedException("Test-only transient trigger.");
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem)
    {
        if (entered != 0 || nextItem is not NinaExposureItem next) return false;
        exposure = next;
        return true;
    }
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
        if (exposure is not { ExecutionError: null, Evidence.Phase: CapturePhase.Saved })
            throw new InvalidDataException("The original exposure after the delayed trigger did not save successfully.");
        if (history.Status != SequenceEntityStatus.FAILED)
            throw new InvalidDataException("Historical trigger evidence was changed by a later hook.");
    }

    public async ValueTask DisposeAsync() => await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        parent.Remove(this);
        parent.Remove(history);
    });

    // Reproduce the stale failed global-autofocus status in Ultracat's log.
    // It did not run during this session and must not fail target setup/waits.
    private sealed class HistoricalFailure : SequenceTrigger
    {
        internal HistoricalFailure() => Name = "Earlier global autofocus failure fixture";
        public override object Clone() => throw new NotSupportedException("Test-only transient trigger.");
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => false;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
            throw new InvalidOperationException("The historical trigger must not execute.");
    }
}
