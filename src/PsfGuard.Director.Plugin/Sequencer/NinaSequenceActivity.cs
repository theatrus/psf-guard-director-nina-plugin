using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Director.Plugin.Sequencer;

internal sealed record NinaSequenceActivity(bool Running, string? Operation)
{
    internal static NinaSequenceActivity? Read(ISequenceMediator sequence)
    {
        if (!sequence.Initialized) return null;
        if (!sequence.IsAdvancedSequenceRunning()) return new(false, null);
        const int limit = 4;
        var paths = (sequence.GetAdvancedSequencerCurrentRunningItems() ?? [])
            .Where(item => item.Status == SequenceEntityStatus.RUNNING)
            .Select(PathOf).OfType<string>().Distinct().Order(StringComparer.Ordinal).Take(limit + 1).ToArray();
        var operation = paths.Length == 0 ? null : string.Join(" · ", paths.Take(limit));
        return new(true, paths.Length > limit ? operation + " · More instructions running" : operation);
    }

    private static string? PathOf(ISequenceItem item)
    {
        var parts = new List<string>(4);
        for (ISequenceItem? current = item; current is not null && current is not ISequenceRootContainer; current = current.Parent)
        {
            if (current is DirectorSessionContainer) return null;
            if (parts.Count == 4) continue;
            var label = new string((current.Name ?? "").Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
            if (label.Length != 0) parts.Add(label);
        }
        parts.Reverse();
        return parts.Count == 0 ? item.GetType().Name : string.Join(" › ", parts);
    }
}
