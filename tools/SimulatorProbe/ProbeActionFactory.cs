using System.ComponentModel.Composition;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin.Acquisition;

namespace PsfGuard.Director.SimulatorProbe;

// Each NINA plugin has a separate MEF catalog. Exercise the production factory
// with the probe's native imports rather than importing another plugin's part.
[Export(typeof(INinaActionFactory))]
public sealed class ProbeActionFactory : INinaActionFactory
{
    private readonly NinaActionFactory factory;
    [ImportingConstructor]
    public ProbeActionFactory(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFilterWheelMediator wheel, IGuiderMediator guider, IFocuserMediator focuser, IRotatorMediator rotator,
        IDomeMediator dome, IDomeFollower follower, IImagingMediator imaging, IImageHistoryVM history,
        ISafetyMonitorMediator safety, IPlateSolverFactory plates, IWindowServiceFactory windows,
        IAutoFocusVMFactory autofocus, IMeridianFlipVMFactory meridian, IApplicationStatusMediator status) =>
        factory = new(profiles, camera, telescope, wheel, guider, focuser, rotator, dome, follower, imaging, history,
            safety, plates, windows, autofocus, meridian, status);
    public T GetItem<T>() where T : ISequenceItem => factory.GetItem<T>();
    public ISequenceTrigger GetTrigger<T>() where T : ISequenceTrigger => factory.GetTrigger<T>();
}
