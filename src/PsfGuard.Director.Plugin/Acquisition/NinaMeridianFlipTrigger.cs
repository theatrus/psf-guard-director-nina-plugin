using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.Plugin.Acquisition;

// Compose the host's native trigger so its version-specific constructor and full
// workflow stay in NINA. Retain positive result and workflow-completion checks.
internal sealed class NinaMeridianFlipTrigger : SequenceTrigger, IMeridianFlipTrigger, IValidatable
{
    private readonly MeridianFlipTrigger native;
    private readonly ObservedFactory observed;
    private readonly Func<NinaMeridianFlipTrigger> clone;

    internal NinaMeridianFlipTrigger(IProfileService profiles, ICameraMediator camera,
        ITelescopeMediator telescope, IFocuserMediator focuser, IApplicationStatusMediator status,
        IMeridianFlipVMFactory factory, ISafetyMonitorMediator safety)
    {
        observed = new(factory);
        native = NinaCompatibility.Create<MeridianFlipTrigger>(safety, profiles, camera, telescope, focuser, status, observed);
        clone = () => new(profiles, camera, telescope, focuser, status, factory, safety);
        TriggerRunner = native.TriggerRunner;
        native.PropertyChanged += (_, args) => RaisePropertyChanged(args.PropertyName);
    }

    public DateTime LatestFlipTime => native.LatestFlipTime;
    public DateTime EarliestFlipTime => native.EarliestFlipTime;
    public IList<string> Issues { get => native.Issues; set => native.Issues = value; }
    public bool Validate() => native.Validate();
    public override bool ShouldTrigger(ISequenceItem previous, ISequenceItem next) => native.ShouldTrigger(previous, next);
    public override bool ShouldTriggerAfter(ISequenceItem previous, ISequenceItem next) => native.ShouldTriggerAfter(previous, next);
    public override void AfterParentChanged() => native.AttachNewParent(Parent);
    public override void Initialize() => native.Initialize();
    public override void SequenceBlockInitialize() => native.SequenceBlockInitialize();
    public override void SequenceBlockStarted() => native.SequenceBlockStarted();
    public override void SequenceBlockFinished() => native.SequenceBlockFinished();
    public override void SequenceBlockTeardown() => native.SequenceBlockTeardown();
    public override void Teardown() => native.Teardown();
    public override object Clone() { var copy = clone(); copy.CopyMetaData(this); return copy; }

    public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DirectorSessionContainer? session = null;
        for (var parent = context; parent is not null; parent = parent.Parent)
            if (parent is DirectorSessionContainer director) { session = director; break; }
        var target = session?.Display.Target ?? "";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        session?.RecordAction(target, "Meridian flip", "Started");
        try
        {
            var execution = native.Execute(context, progress, token);
            await execution;
            token.ThrowIfCancellationRequested();
            if (execution is not Task<bool> result || !result.Result)
                throw new SequenceEntityFailedException("NINA meridian flip failed or returned no completion result.");
            var steps = observed.Current?.Steps;
            if (steps is not null && steps.Any(step => !step.Finished))
                throw new SequenceEntityFailedException("NINA meridian flip has failed or unfinished workflow steps.");
            session?.RecordAction(target, "Meridian flip", "Succeeded", checked((ulong)elapsed.ElapsedMilliseconds));
        }
        catch
        {
            session?.RecordAction(target, "Meridian flip", "Failed or interrupted", checked((ulong)elapsed.ElapsedMilliseconds));
            throw;
        }
    }

    private sealed class ObservedFactory(IMeridianFlipVMFactory inner) : IMeridianFlipVMFactory
    {
        internal IMeridianFlipVM? Current { get; private set; }
        public IMeridianFlipVM Create() => Current = inner.Create();
    }
}
