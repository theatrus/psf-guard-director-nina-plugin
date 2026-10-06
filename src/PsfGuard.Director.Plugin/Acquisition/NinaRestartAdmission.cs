using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Conditions;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaRestartAdmission
{
    internal static void ValidateLifecycle(ISequenceContainer session)
    {
        // Their initialization/teardown can have side effects outside allocation
        // ownership. Until those lifecycles are journaled, do not infer settlement.
        for (ISequenceContainer? current = session; current is not null; current = current.Parent)
            if (current is ITriggerable triggers && triggers.GetTriggersSnapshot().Count != 0
                || current is IConditionable conditions && conditions.GetConditionsSnapshot().Count != 0)
                throw new InvalidOperationException("Settled-night restart cannot journal session or inherited trigger/condition lifecycles. Disable restart recording for this sequence.");
    }
}
