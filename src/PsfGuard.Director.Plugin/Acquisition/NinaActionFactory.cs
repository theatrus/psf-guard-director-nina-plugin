using System.ComponentModel.Composition;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace PsfGuard.Director.Plugin.Acquisition;

public interface INinaActionFactory
{
    T GetItem<T>() where T : ISequenceItem;
    T GetTrigger<T>() where T : ISequenceTrigger;
}

// NINA exports device/algorithm services to plugins, not ISequencerFactory.
// Each call returns a fresh native entity with no previous sequence state.
[Export(typeof(INinaActionFactory))]
public sealed class NinaActionFactory : INinaActionFactory
{
    private readonly Dictionary<Type, Func<object>> create;

    [ImportingConstructor]
    public NinaActionFactory(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFilterWheelMediator wheel, IGuiderMediator guider, IFocuserMediator focuser, IRotatorMediator rotator,
        IDomeMediator dome, IDomeFollower follower, IImagingMediator imaging, IImageHistoryVM history,
        ISafetyMonitorMediator safety, IPlateSolverFactory plates, IWindowServiceFactory windows,
        IAutoFocusVMFactory autofocus, IMeridianFlipVMFactory meridian, IApplicationStatusMediator status)
    {
        create = new()
        {
            [typeof(Center)] = () => new Center(profiles, telescope, imaging, wheel, guider, dome, follower, plates, windows),
            [typeof(CenterAndRotate)] = () => new CenterAndRotate(profiles, telescope, imaging, rotator, wheel, guider, dome, follower, plates, windows),
            [typeof(RunAutofocus)] = () => new RunAutofocus(profiles, history, camera, wheel, focuser, autofocus),
            [typeof(StartGuiding)] = () => new StartGuiding(guider),
            [typeof(StopGuiding)] = () => new StopGuiding(guider),
            [typeof(Dither)] = () => new Dither(guider, profiles),
            [typeof(AutofocusAfterFilterChange)] = () => new AutofocusAfterFilterChange(profiles, history, camera, wheel, focuser, autofocus, safety),
            [typeof(AutofocusAfterTimeTrigger)] = () => new AutofocusAfterTimeTrigger(profiles, history, camera, wheel, focuser, autofocus, safety),
            [typeof(AutofocusAfterTemperatureChangeTrigger)] = () => new AutofocusAfterTemperatureChangeTrigger(profiles, history, camera, wheel, focuser, autofocus, safety),
            [typeof(MeridianFlipTrigger)] = () => new NinaMeridianFlipTrigger(profiles, camera, telescope, focuser, status, meridian, safety),
            [typeof(RestoreGuiding)] = () => new RestoreGuiding(guider, safety)
        };
    }

    public T GetItem<T>() where T : ISequenceItem => (T)create[typeof(T)]();
    public T GetTrigger<T>() where T : ISequenceTrigger => (T)create[typeof(T)]();
}
