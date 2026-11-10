using Newtonsoft.Json;
using System.ComponentModel;
using System.Runtime.Serialization;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace PsfGuard.Director.Plugin.Acquisition;

internal enum NinaInstructionSlot
{
    BeforeWait,
    AfterWait,
    BeforeNewTarget,
    AfterEachExposure,
    AfterNewTarget,
    AfterEachTarget,
    AfterTargetComplete
}

// Editable native instructions only. The session/core decides when a slot runs;
// these containers neither own an assignment nor authorize equipment dispatch.
[JsonObject(MemberSerialization.OptIn)]
internal sealed class NinaInstructionSlots
{
    [JsonProperty(Required = Required.Always)] public SequentialContainer BeforeWait { get; private set; } = New("Before Wait Instructions");
    [JsonProperty(Required = Required.Always)] public SequentialContainer AfterWait { get; private set; } = New("After Wait Instructions");
    [JsonProperty(Required = Required.Always)] public SequentialContainer BeforeNewTarget { get; private set; } = New("Before New Target Instructions");
    [JsonProperty(Required = Required.Always)] public SequentialContainer AfterEachExposure { get; private set; } = New("After Each Exposure Instructions");
    [JsonProperty(Required = Required.Always)] public SequentialContainer AfterNewTarget { get; private set; } = New("After New Target Instructions");
    [JsonProperty(Required = Required.Always)] public SequentialContainer AfterEachTarget { get; private set; } = New("After Each Target Instructions");
    [JsonProperty(Required = Required.Always)] public SequentialContainer AfterTargetComplete { get; private set; } = New("After Target Complete Instructions");

    private int running;

    [OnDeserialized]
    private void RestoreLabels(StreamingContext _)
    {
        var defaults = new NinaInstructionSlots();
        foreach (var slot in Enum.GetValues<NinaInstructionSlot>()) this[slot].Name = defaults[slot].Name;
    }

    internal SequentialContainer this[NinaInstructionSlot slot] => slot switch
    {
        NinaInstructionSlot.BeforeWait => BeforeWait,
        NinaInstructionSlot.AfterWait => AfterWait,
        NinaInstructionSlot.BeforeNewTarget => BeforeNewTarget,
        NinaInstructionSlot.AfterEachExposure => AfterEachExposure,
        NinaInstructionSlot.AfterNewTarget => AfterNewTarget,
        NinaInstructionSlot.AfterEachTarget => AfterEachTarget,
        NinaInstructionSlot.AfterTargetComplete => AfterTargetComplete,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };

    internal NinaInstructionSlots Clone() => new()
    {
        BeforeWait = CloneBlock(BeforeWait),
        AfterWait = CloneBlock(AfterWait),
        BeforeNewTarget = CloneBlock(BeforeNewTarget),
        AfterEachExposure = CloneBlock(AfterEachExposure),
        AfterNewTarget = CloneBlock(AfterNewTarget),
        AfterEachTarget = CloneBlock(AfterEachTarget),
        AfterTargetComplete = CloneBlock(AfterTargetComplete)
    };

    internal void AppendTargetDefaults(IEnumerable<ISequenceItem> defaults)
    {
        var items = defaults.ToArray();
        if (items.Length == 0) return;
        // Disabling a user's hook must not disable Director-owned preparation.
        var user = BeforeNewTarget;
        BeforeNewTarget = New("Director target setup");
        BeforeNewTarget.Add(user);
        foreach (var item in items) BeforeNewTarget.Add(item);
    }

    internal NinaInstructionInvocation CreateInvocation(NinaInstructionSlot slot) =>
        new(CloneBlock(this[slot]), Enter, Leave);

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Director instruction slots cannot execute concurrently.");
    }

    private void Leave() => Volatile.Write(ref running, 0);

    private static SequentialContainer New(string name) => new() { Name = name };

    private static SequentialContainer CloneBlock(SequentialContainer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Clone() is not SequentialContainer clone || ReferenceEquals(source, clone))
            throw new InvalidOperationException("The native instruction container did not create a separate clone.");
        CopyExecutionSettings(source, clone);
        clone.AttachNewParent(null);
        return clone;
    }

    internal static void CopyExecutionSettings(ISequenceItem source, ISequenceItem clone)
    {
        if (ReferenceEquals(source, clone))
            throw new InvalidOperationException("Native instructions must clone without sharing execution state.");
        // NINA's SequentialContainer.Clone omits these settings. Preserve them
        // recursively instead of changing a user's retry/error/disabled choices.
        clone.Attempts = source.Attempts;
        clone.ErrorBehavior = source.ErrorBehavior;
        clone.Status = source.Status == SequenceEntityStatus.DISABLED ? SequenceEntityStatus.DISABLED : SequenceEntityStatus.CREATED;
        if (source is not ISequenceContainer original) return;
        if (clone is not ISequenceContainer copied)
            throw new InvalidOperationException("Cloning changed the native container type.");
        copied.IsExpanded = original.IsExpanded;
        var originals = original.GetItemsSnapshot().ToArray();
        var copies = copied.GetItemsSnapshot().ToArray();
        if (originals.Length != copies.Length)
            throw new InvalidOperationException("Cloning changed the native instruction list.");
        for (var i = 0; i < originals.Length; i++)
        {
            CopyExecutionSettings(originals[i], copies[i]);
            copies[i].AttachNewParent(copied);
        }
        if (original is ITriggerable sourceTriggers && copied is ITriggerable cloneTriggers)
            CopyStates(sourceTriggers.GetTriggersSnapshot(), cloneTriggers.GetTriggersSnapshot());
        if (original is IConditionable sourceConditions && copied is IConditionable cloneConditions)
            CopyStates(sourceConditions.GetConditionsSnapshot(), cloneConditions.GetConditionsSnapshot());
    }

    private static void CopyStates<T>(IEnumerable<T> source, IEnumerable<T> clone) where T : ISequenceEntity
    {
        var originals = source.ToArray();
        var copies = clone.ToArray();
        if (originals.Length != copies.Length)
            throw new InvalidOperationException("Cloning changed the native trigger or condition list.");
        for (var i = 0; i < originals.Length; i++)
        {
            if (ReferenceEquals(originals[i], copies[i]))
                throw new InvalidOperationException("Native triggers and conditions must not share execution state.");
            copies[i].Status = originals[i].Status == SequenceEntityStatus.DISABLED ? SequenceEntityStatus.DISABLED : SequenceEntityStatus.CREATED;
            if (originals[i] is SequenceTrigger originalTrigger && copies[i] is SequenceTrigger copiedTrigger)
                CopyExecutionSettings(originalTrigger.TriggerRunner, copiedTrigger.TriggerRunner);
        }
    }
}

internal sealed record NinaInstructionIssue(string Name, SequenceEntityStatus Status)
{
    internal string Description => $"{(string.IsNullOrWhiteSpace(Name) ? "An instruction" : Name)} {Status switch
    {
        SequenceEntityStatus.FAILED => "failed",
        SequenceEntityStatus.RUNNING => "is still running",
        SequenceEntityStatus.SKIPPED => "was skipped",
        SequenceEntityStatus.CREATED => "did not run",
        _ => "did not finish"
    }}";
}
internal sealed record NinaInstructionResult(IReadOnlyList<NinaInstructionIssue> Issues)
{
    internal bool Completed => Issues.Count == 0;
}

// One invocation uses an isolated copy; reset/clone of editable configuration
// never retries an invocation that may already have operated equipment.
internal sealed class NinaInstructionInvocation(SequentialContainer block, Action enter, Action leave)
{
    private int entered;

    internal async Task<NinaInstructionResult> RunAsync(ISequenceContainer parent,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (Interlocked.Exchange(ref entered, 1) != 0)
            throw new InvalidOperationException("This Director instruction invocation has already run.");
        enter();
        var initialized = new List<ISequenceEntity>();
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            block.AttachNewParent(parent);
            using var inherited = new InheritedTriggerRuns(parent);
            foreach (var entity in LifecycleEntities(block))
            {
                initialized.Add(entity);
                entity.Initialize();
            }
            await block.Run(progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var issues = new List<NinaInstructionIssue>();
            CollectItems(block, issues);
            // A trigger's task can return after NINA swallowed its failure.
            // Do not turn that into successful hook evidence for the session.
            // Ancestor triggers retain old outcomes across Director retries;
            // only a trigger involved in this invocation can fail this hook.
            for (ISequenceContainer? current = parent; current is not null; current = current.Parent)
                CollectTriggers(current, issues, inherited.Includes);
            return new(issues.AsReadOnly());
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            foreach (var entity in initialized)
                try { entity.Teardown(); }
                catch (Exception error) { cleanupErrors.Add(error); }
            try { block.AttachNewParent(null); }
            catch (Exception error) { cleanupErrors.Add(error); }
            leave();
            if (cleanupErrors.Count > 0)
            {
                if (failure is not null) cleanupErrors.Insert(0, failure);
                throw new AggregateException("Director instruction cleanup failed.", cleanupErrors);
            }
        }
    }

    private static IEnumerable<ISequenceEntity> LifecycleEntities(ISequenceContainer container)
    {
        if (container is IConditionable conditions)
            foreach (var condition in conditions.GetConditionsSnapshot()) yield return condition;
        if (container is ITriggerable triggers)
            foreach (var trigger in triggers.GetTriggersSnapshot()) yield return trigger;
        foreach (var item in container.GetItemsSnapshot())
        {
            yield return item;
            if (item is ISequenceContainer child)
                foreach (var nested in LifecycleEntities(child)) yield return nested;
        }
    }

    private static void CollectItems(ISequenceItem item, List<NinaInstructionIssue> issues)
    {
        if (item.Status == SequenceEntityStatus.DISABLED) return;
        if (item.Status != SequenceEntityStatus.FINISHED) issues.Add(new(item.Name, item.Status));
        if (item is not ISequenceContainer container) return;
        foreach (var child in container.GetItemsSnapshot()) CollectItems(child, issues);
        CollectTriggers(container, issues);
    }

    private static void CollectTriggers(ISequenceContainer container, List<NinaInstructionIssue> issues,
        Func<ISequenceTrigger, bool>? include = null)
    {
        if (container is not NINA.Sequencer.Trigger.ITriggerable triggerable) return;
        foreach (var trigger in triggerable.GetTriggersSnapshot())
        {
            if (include is not null && !include(trigger)) continue;
            if (trigger.Status is SequenceEntityStatus.FAILED or SequenceEntityStatus.RUNNING or SequenceEntityStatus.SKIPPED)
                issues.Add(new(trigger.Name, trigger.Status));
            if (trigger is SequenceTrigger native && trigger.Status == SequenceEntityStatus.FINISHED)
                foreach (var item in native.TriggerRunner.GetItemsSnapshot()) CollectItems(item, issues);
        }
    }

    private sealed class InheritedTriggerRuns : IDisposable
    {
        private readonly Dictionary<ISequenceTrigger, Observation> observations = new(ReferenceEqualityComparer.Instance);

        internal InheritedTriggerRuns(ISequenceContainer parent)
        {
            for (ISequenceContainer? current = parent; current is not null; current = current.Parent)
                if (current is ITriggerable triggers)
                    foreach (var trigger in triggers.GetTriggersSnapshot())
                        if (!observations.ContainsKey(trigger)) observations.Add(trigger, new(trigger));
        }

        // A trigger added mid-invocation has no known historical outcome.
        internal bool Includes(ISequenceTrigger trigger) => !observations.TryGetValue(trigger, out var observed) || observed.Involved;
        public void Dispose() { foreach (var observation in observations.Values) observation.Dispose(); }

        private sealed class Observation : IDisposable
        {
            private readonly ISequenceTrigger trigger;
            private readonly INotifyPropertyChanged? notifications;
            private readonly SequenceEntityStatus initialStatus;
            private int lastStatus, changed;

            internal Observation(ISequenceTrigger trigger)
            {
                this.trigger = trigger;
                initialStatus = trigger.Status;
                lastStatus = (int)initialStatus;
                notifications = trigger as INotifyPropertyChanged;
                // Direct notification subscription is thread-neutral. WPF weak
                // event managers must not be created on the acquisition thread.
                if (notifications is not null) notifications.PropertyChanged += StatusChanged;
            }

            // FAILED -> RUNNING -> FAILED is a new failure even though the final
            // status matches the old one. Unobservable custom triggers retain
            // the conservative check rather than hiding a possible new failure.
            internal bool Involved => notifications is null || initialStatus == SequenceEntityStatus.RUNNING
                || Volatile.Read(ref changed) != 0 || trigger.Status != initialStatus;

            private void StatusChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (!string.IsNullOrEmpty(args.PropertyName) && args.PropertyName != nameof(ISequenceTrigger.Status)) return;
                var status = (int)trigger.Status;
                if (Interlocked.Exchange(ref lastStatus, status) != status) Volatile.Write(ref changed, 1);
            }

            public void Dispose() { if (notifications is not null) notifications.PropertyChanged -= StatusChanged; }
        }
    }
}
