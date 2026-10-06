using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// NINA owns device algorithms and native triggers. Rust still selects targets,
// issues preparation, reserves captures and enforces fresh geometry at dispatch.
internal sealed class NinaNativeImaging(INinaActionFactory factory, IProfileService profiles,
    IGuiderMediator guider, IFocuserMediator focuser, IRotatorMediator rotator,
    DirectorSessionOptions options) : IDisposable
{
    private readonly Guid profileId = profiles.ActiveProfile.Id;
    private readonly string guiderId = profiles.ActiveProfile.GuiderSettings.GuiderName;
    private readonly string focuserId = profiles.ActiveProfile.FocuserSettings.Id;
    private readonly string rotatorId = profiles.ActiveProfile.RotatorSettings.Id;
    private readonly List<ISequenceTrigger> installed = [];
    private DirectorSessionContainer? session;
    internal NinaSessionRecovery? Recovery { get; set; }
    internal Func<string>? SelectedTargetId { get; set; }
    internal bool RotatorConnected => options.SlewCenter == DirectorOperationOwner.Director && rotatorId != "No_Device";
    internal static string ScopeFor(IProfileService profiles, DirectorSessionOptions options) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            profiles.ActiveProfile.FocuserSettings.Id,
            Guider = profiles.ActiveProfile.GuiderSettings.GuiderName,
            Rotator = profiles.ActiveProfile.RotatorSettings.Id,
            options.SlewCenter,
            options.Focus,
            options.Guiding,
            options.Dither,
            options.MeridianFlip
        });

    internal static bool Required(DirectorSessionOptions o) =>
        new[] { o.SlewCenter, o.Focus, o.Guiding, o.Dither, o.MeridianFlip }.Contains(DirectorOperationOwner.Director);

    internal void CheckEquipment()
    {
        if (profiles.ActiveProfile.Id != profileId) throw new InvalidOperationException("Native imaging profile changed.");
        if (options.Guiding == DirectorOperationOwner.Director || options.Dither == DirectorOperationOwner.Director)
        {
            if (guiderId == "No_Guider" || profiles.ActiveProfile.GuiderSettings.GuiderName != guiderId || !guider.GetInfo().Connected
                || guider.GetInfo().DeviceId != guiderId)
                throw new InvalidOperationException("Director guiding/dither requires the configured guider connected. Use Sequence ownership for an unguided setup.");
        }
        if ((options.Focus == DirectorOperationOwner.Director
            || options.MeridianFlip == DirectorOperationOwner.Director && profiles.ActiveProfile.MeridianFlipSettings.AutoFocusAfterFlip)
            && (focuserId == "No_Device" || profiles.ActiveProfile.FocuserSettings.Id != focuserId
                || !focuser.GetInfo().Connected || focuser.GetInfo().DeviceId != focuserId))
            throw new InvalidOperationException("Director autofocus requires the configured focuser connected. Use Sequence ownership for manual focus.");
        if (RotatorConnected && (profiles.ActiveProfile.RotatorSettings.Id != rotatorId
            || !rotator.GetInfo().Connected || rotator.GetInfo().DeviceId != rotatorId))
            throw new InvalidOperationException("The configured rotator is unavailable.");
    }

    internal void ConfigureTargetSetup(NinaInstructionSlots slots)
    {
        var items = new List<ISequenceItem>();
        if (options.Focus == DirectorOperationOwner.Director) items.Add(Recoverable(Item<RunAutofocus>(), RecoveryOperation.Focus, focuserId));
        if (options.Guiding == DirectorOperationOwner.Director) items.Add(Recoverable(Item<StartGuiding>(), RecoveryOperation.Guide, guiderId));
        slots.AppendTargetDefaults(items);
    }

    private SequenceItem Recoverable(SequenceItem item, RecoveryOperation operation, string device) => Recovery is null ? item
        : new RetryItem(item.Name, async (parent, progress, token) =>
        {
            await Recovery.ExecuteAsync(operation, device, SelectedTargetId?.Invoke()
                ?? throw new InvalidOperationException("No recovery target is selected."), async ct =>
            {
                CheckEquipment();
                var fresh = operation == RecoveryOperation.Focus ? (SequenceItem)Item<RunAutofocus>() : Item<StartGuiding>();
                fresh.AttachNewParent(parent);
                try
                {
                    if (fresh is IValidatable valid && !valid.Validate())
                        throw new InvalidOperationException(string.Join("; ", valid.Issues));
                    await fresh.Execute(progress, ct);
                    ct.ThrowIfCancellationRequested();
                    CheckEquipment();
                }
                finally { fresh.AttachNewParent(null); }
            }, async ct =>
            {
                CheckEquipment();
                if (operation == RecoveryOperation.Guide)
                {
                    // NINA's StopGuiding instruction returns Task and drops the
                    // mediator's boolean. Recovery needs confirmed quiescence.
                    if (!await guider.StopGuiding(ct))
                        throw new InvalidOperationException("Guider stop was not confirmed; recovery is uncertain.");
                    ct.ThrowIfCancellationRequested();
                }
                if (operation == RecoveryOperation.Focus && focuser.GetInfo().IsMoving)
                    throw new InvalidOperationException("Focuser is still moving; recovery is uncertain.");
            }, token);
        });

    private sealed class RetryItem : SequenceItem
    {
        private readonly Func<NINA.Sequencer.Container.ISequenceContainer, IProgress<ApplicationStatus>, CancellationToken, Task> execute;
        internal RetryItem(string name, Func<NINA.Sequencer.Container.ISequenceContainer, IProgress<ApplicationStatus>, CancellationToken, Task> execute)
        {
            Name = name; this.execute = execute; Attempts = 1; ErrorBehavior = InstructionErrorBehavior.AbortOnError;
        }
        public override object Clone() => new RetryItem(Name, execute);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => execute(Parent, progress, token);
    }

    internal void Install(DirectorSessionContainer target)
    {
        if (session is not null) throw new InvalidOperationException("Native defaults already installed.");
        ValidateOwnership(target, options);
        CheckEquipment();
        session = target;
        if (options.Focus == DirectorOperationOwner.Director)
        {
            Add<AutofocusAfterFilterChange>();
            Add<AutofocusAfterTimeTrigger>();
            Add<AutofocusAfterTemperatureChangeTrigger>();
        }
        if (options.MeridianFlip == DirectorOperationOwner.Director) Add<MeridianFlipTrigger>();
        if (options.Guiding == DirectorOperationOwner.Director) Add<NINA.Sequencer.Trigger.Guider.RestoreGuiding>();
    }

    private void Add<T>() where T : ISequenceTrigger
    {
        var trigger = factory.GetTrigger<T>() ?? throw new InvalidOperationException($"NINA action {typeof(T).Name} is unavailable.");
        if (string.IsNullOrWhiteSpace(trigger.Name))
            trigger.Name = typeof(T) == typeof(MeridianFlipTrigger) ? "Meridian flip" : typeof(T).Name;
        session!.AddRuntimeTrigger(trigger);
        installed.Add(trigger);
        trigger.Initialize();
        trigger.SequenceBlockInitialize();
        trigger.SequenceBlockStarted();
        if (trigger is IValidatable valid && (!valid.Validate() || valid.Issues.Count > 0))
            throw new InvalidOperationException($"NINA {typeof(T).Name}: {string.Join("; ", valid.Issues)}");
    }

    internal void CheckTriggers()
    {
        foreach (var trigger in installed)
        {
            if (trigger.Status is SequenceEntityStatus.FAILED or SequenceEntityStatus.SKIPPED or SequenceEntityStatus.RUNNING)
                throw new InvalidOperationException($"Native {trigger.Name} did not complete.");
            if (trigger.Status == SequenceEntityStatus.FINISHED && trigger is SequenceTrigger native)
                foreach (var item in native.TriggerRunner.GetItemsSnapshot())
                    if (item.Status != SequenceEntityStatus.FINISHED && item.Status != SequenceEntityStatus.DISABLED)
                        throw new InvalidOperationException($"Native {item.Name} did not complete.");
        }
    }

    internal async Task PrepareAsync(PreparationOperation operation, ISequenceContainer context,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        CheckEquipment();
        if (operation is PreparationOperation.Center center)
        {
            if (options.SlewCenter != DirectorOperationOwner.Director || center.Rotate && !RotatorConnected)
                throw new InvalidOperationException("Centering is not owned by Director or the rotator is unavailable.");
            if (options.Guiding == DirectorOperationOwner.Director) await Execute(Item<StopGuiding>());
            Center item = center.Rotate ? Item<CenterAndRotate>() : Item<Center>();
            item.Inherited = true;
            await Execute(item);
        }
        else if (operation is PreparationOperation.Dither)
        {
            if (options.Dither != DirectorOperationOwner.Director) throw new InvalidOperationException("Dithering is sequence-owned.");
            await Execute(new CheckedDither(guider, profiles));
        }
        else throw new InvalidOperationException("Unsupported native imaging operation.");
        CheckEquipment();

        async Task Execute(SequenceItem item)
        {
            item.AttachNewParent(context);
            try
            {
                if (item is IValidatable valid && !valid.Validate()) throw new InvalidOperationException(string.Join("; ", valid.Issues));
                await item.Execute(progress, token);
                token.ThrowIfCancellationRequested();
            }
            finally { item.AttachNewParent(null); }
        }
    }

    internal async Task StopGuidingAsync(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (options.Guiding != DirectorOperationOwner.Director) return;
        if (profiles.ActiveProfile.Id != profileId || profiles.ActiveProfile.GuiderSettings.GuiderName != guiderId)
            throw new InvalidOperationException("Guider binding changed before shutdown.");
        var info = guider.GetInfo();
        if (!info.Connected) return;
        if (info.DeviceId != guiderId) throw new InvalidOperationException("The connected guider changed before shutdown.");
        await Item<StopGuiding>().Execute(progress, token);
    }

    private T Item<T>() where T : SequenceItem
    {
        var item = factory.GetItem<T>() ?? throw new InvalidOperationException($"NINA action {typeof(T).Name} is unavailable.");
        item.Attempts = 1;
        item.ErrorBehavior = InstructionErrorBehavior.AbortOnError;
        return item;
    }

    // The pinned NINA action drops the mediator's false result. Keep native
    // validation and guider algorithms, but do not credit a failed dither.
    private sealed class CheckedDither(IGuiderMediator guider, IProfileService profiles) : Dither(guider, profiles)
    {
        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var succeeded = await guiderMediator.Dither(token);
            token.ThrowIfCancellationRequested();
            if (!succeeded) throw new SequenceEntityFailedException("Failed to dither.");
        }
    }

    internal static void ValidateOwnership(DirectorSessionContainer session, DirectorSessionOptions options)
    {
        var visited = new HashSet<ISequenceItem>(ReferenceEqualityComparer.Instance);
        foreach (var block in session.InstructionBlocks) Visit(block);
        for (ISequenceContainer? parent = session; parent is not null; parent = parent.Parent) Triggers(parent);

        void Check(Type type)
        {
            var name = type.Namespace ?? "";
            var conflict = options.SlewCenter == DirectorOperationOwner.Director && (typeof(Center).IsAssignableFrom(type)
                    || type.Name == "SlewScopeToRaDec" || type.Name == "CenterAfterDriftTrigger")
                || options.Focus == DirectorOperationOwner.Director && (typeof(RunAutofocus).IsAssignableFrom(type) || name.Contains(".Autofocus", StringComparison.Ordinal))
                || options.Guiding == DirectorOperationOwner.Director && (typeof(StartGuiding).IsAssignableFrom(type) || typeof(StopGuiding).IsAssignableFrom(type) || type.Name == "RestoreGuiding")
                || options.Dither == DirectorOperationOwner.Director && (typeof(Dither).IsAssignableFrom(type) || type.Name == "DitherAfterExposures")
                || options.MeridianFlip == DirectorOperationOwner.Director && (typeof(MeridianFlipTrigger).IsAssignableFrom(type)
                    || typeof(NinaMeridianFlipTrigger).IsAssignableFrom(type));
            if (conflict) throw new InvalidOperationException($"{type.Name} conflicts with Director defaults. Select Sequence ownership for that operation.");
        }
        void Visit(ISequenceItem item)
        {
            if (!visited.Add(item) || item.Status == SequenceEntityStatus.DISABLED) return;
            Check(item.GetType());
            if (item is ISequenceContainer block) { foreach (var child in block.GetItemsSnapshot()) Visit(child); Triggers(block); }
        }
        void Triggers(ISequenceContainer block)
        {
            if (block is not ITriggerable triggerable) return;
            foreach (var trigger in triggerable.GetTriggersSnapshot())
            {
                if (trigger.Status == SequenceEntityStatus.DISABLED) continue;
                Check(trigger.GetType());
                if (trigger is SequenceTrigger native) Visit(native.TriggerRunner);
            }
        }
    }

    public void Dispose()
    {
        var errors = new List<Exception>();
        foreach (var trigger in installed)
        {
            try { trigger.SequenceBlockTeardown(); }
            catch (Exception error) { errors.Add(error); }
            try { trigger.Teardown(); }
            catch (Exception error) { errors.Add(error); }
            try { session!.RemoveRuntimeTrigger(trigger); }
            catch (Exception error) { errors.Add(error); }
        }
        installed.Clear();
        if (errors.Count != 0) throw new AggregateException("Native imaging trigger cleanup failed.", errors);
    }
}
