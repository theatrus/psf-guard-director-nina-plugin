using System.Windows;
using System.Windows.Threading;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Sequencer.Container;
using NINA.Sequencer;
using NINA.Sequencer.Trigger;

namespace PsfGuard.Director.Plugin.Sequencer;

// Observe public NINA trigger state, including triggers owned by parent containers.
// Some plugins perform autofocus directly and never report progress to our sink.
// Collection reads stay on the dispatcher; only the resulting text crosses threads.
internal sealed class NinaTriggerActivity : IDisposable
{
    private readonly DirectorSessionContainer session;
    private readonly DispatcherTimer? timer;

    internal NinaTriggerActivity(DirectorSessionContainer session)
    {
        this.session = session;
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        timer = dispatcher.Invoke(() =>
        {
            var result = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            result.Tick += Tick;
            result.Start();
            return result;
        });
    }

    private void Tick(object? sender, EventArgs e)
    {
        try { session.SetTriggerActivity(Read(session)); }
        catch (Exception error) { Logger.Error(error); session.SetTriggerActivity(null); }
    }

    internal static string? Read(ISequenceContainer session)
    {
        var names = new List<string>();
        var visited = new HashSet<ISequenceContainer>();
        ISequenceContainer root = session;
        // Include every ancestor: global triggers can run while Director is
        // waiting inside its next native exposure instruction.
        for (ISequenceContainer? block = session; block is not null && !visited.Contains(block); block = block.Parent)
        { root = block; Visit(block); }
        // Issued target and cloned hook containers have a parent but are not
        // members of the editor's Items collection. NINA tracks their running
        // instructions through the root's public snapshot instead.
        if (root is ISequenceRootContainer sequence)
            foreach (var item in sequence.GetCurrentRunningItems().Where(i => i.Status == SequenceEntityStatus.RUNNING))
                for (var block = item as ISequenceContainer ?? item.Parent; block is not null && !visited.Contains(block); block = block.Parent) Visit(block);
        return names.Count == 0 ? null : string.Join(" · ", names.Distinct().Take(4));

        void Visit(ISequenceContainer block)
        {
            if (!visited.Add(block)) return;
            foreach (var trigger in (block is ITriggerable triggerable ? triggerable.GetTriggersSnapshot() : []).Where(t => t.Status == SequenceEntityStatus.RUNNING))
            {
                var name = new string((trigger.Name ?? trigger.GetType().Name).Where(c => !char.IsControl(c)).Take(160).ToArray()).Trim();
                if (name.Length == 0) name = trigger.GetType().Name;
                names.Add(name);
                if (trigger is SequenceTrigger { TriggerRunner: { } runner }) Visit(runner);
            }
            foreach (var child in block.GetItemsSnapshot().OfType<ISequenceContainer>().Where(c => c.Status == SequenceEntityStatus.RUNNING)) Visit(child);
        }
    }

    public void Dispose()
    {
        if (timer is not null) timer.Dispatcher.Invoke(() => { timer.Stop(); timer.Tick -= Tick; });
        session.SetTriggerActivity(null);
    }
}
