using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.Plugin.Acquisition;

// Keep NINA's timing, target context and complete flip workflow. Its trigger
// returns Task<bool> as Task, so a failed VM result otherwise looks successful.
internal sealed class NinaMeridianFlipTrigger : MeridianFlipTrigger
{
    internal NinaMeridianFlipTrigger(IProfileService profiles, ICameraMediator camera,
        ITelescopeMediator telescope, IFocuserMediator focuser, IApplicationStatusMediator status,
        IMeridianFlipVMFactory factory, ISafetyMonitorMediator safety)
        : base(profiles, camera, telescope, focuser, status, new ObservedFactory(factory), safety) { }

    private NinaMeridianFlipTrigger(NinaMeridianFlipTrigger source)
        : this(source.profileService, source.cameraMediator, source.telescopeMediator, source.focuserMediator,
            source.applicationStatusMediator, ((ObservedFactory)source.meridianFlipVMFactory).Inner, source.safetyMonitorMediator)
    { CopyMetaData(source); }

    public override object Clone() => new NinaMeridianFlipTrigger(this);

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
            var execution = base.Execute(context, progress, token);
            await execution;
            token.ThrowIfCancellationRequested();
            if (execution is not Task<bool> result || !result.Result)
                throw new SequenceEntityFailedException("NINA meridian flip failed or returned no completion result.");
            var steps = ((ObservedFactory)meridianFlipVMFactory).Current?.Steps;
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
        internal IMeridianFlipVMFactory Inner => inner;
        internal IMeridianFlipVM? Current { get; private set; }
        public IMeridianFlipVM Create() => Current = inner.Create();
    }
}
