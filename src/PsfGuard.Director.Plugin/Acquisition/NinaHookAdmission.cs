using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaHookAdmission
{
    internal static void Validate(IEnumerable<ISequenceContainer> blocks, ISequenceContainer parent)
    {
        var visited = new HashSet<ISequenceItem>(ReferenceEqualityComparer.Instance);
        foreach (var block in blocks) Check(block);
        for (ISequenceContainer? current = parent; current is not null; current = current.Parent)
            Triggers(current);

        void Check(ISequenceItem item)
        {
            if (!visited.Add(item) || item.Status == SequenceEntityStatus.DISABLED) return;
            if (item is IExposureItem)
                throw new InvalidOperationException("Science exposures in Director hooks need a reserved-capture adapter. Move acquisition out of the hook.");
            if (item is ISequenceContainer container)
            {
                foreach (var child in container.GetItemsSnapshot()) Check(child);
                Triggers(container);
            }
        }

        void Triggers(ISequenceContainer container)
        {
            if (container is not ITriggerable triggerable) return;
            foreach (var trigger in triggerable.GetTriggersSnapshot())
                if (trigger.Status != SequenceEntityStatus.DISABLED && trigger is SequenceTrigger native) Check(native.TriggerRunner);
        }
    }
}
