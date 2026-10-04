using Moq;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Utility.AutoFocus;
using PsfGuard.Director.Plugin.Acquisition;

namespace PsfGuard.Director.SimulatorProbe;

// Test-only optical evidence. Native actions, NINA sequencing and ASCOM devices
// remain real; synthetic focus/solve results do not validate optical algorithms.
internal sealed class NativeImagingProbe
{
    internal List<string> Operations { get; } = [];
    internal bool FailureInjected { get; private set; }
    internal INinaActionFactory Factory { get; }

    internal NativeImagingProbe(INinaActionFactory original, IProfileService profiles, ICameraMediator camera,
        ITelescopeMediator telescope, IFilterWheelMediator wheel, IGuiderMediator guider, IFocuserMediator focuser,
        IDomeMediator dome, IDomeFollower follower, IImagingMediator imaging, IImageHistoryVM history,
        ISafetyMonitorMediator safety, string? failure)
    {
        var windows = new Mock<IWindowServiceFactory> { DefaultValue = DefaultValue.Mock };
        var focus = new Mock<IAutoFocusVM>();
        focus.Setup(f => f.StartAutoFocus(It.IsAny<FilterInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns(async (FilterInfo filter, CancellationToken ct, IProgress<ApplicationStatus> p) =>
            {
                Operations.Add("Autofocus");
                var position = await focuser.MoveFocuserRelative(10, ct);
                await Task.Delay(200, ct);
                if (failure == "autofocus") FailureInjected = true;
                return failure == "autofocus" ? null! : new AutoFocusReport
                {
                    Filter = filter?.Name ?? "",
                    Timestamp = DateTime.Now,
                    Temperature = focuser.GetInfo().Temperature,
                    CalculatedFocusPoint = new() { Position = position, Value = 2 },
                    Duration = TimeSpan.FromMilliseconds(200)
                };
            });
        var focusFactory = new Mock<IAutoFocusVMFactory>();
        focusFactory.Setup(f => f.Create()).Returns(focus.Object);
        var solver = new Mock<ICenteringSolver>();
        solver.Setup(s => s.Center(It.IsAny<CaptureSequence>(), It.IsAny<CenterSolveParameter>(), It.IsAny<IProgress<PlateSolveProgress>>(),
            It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Operations.Add("Center");
                if (failure == "center") FailureInjected = true;
                return new PlateSolveResult { Success = failure != "center", Coordinates = telescope.GetCurrentPosition() };
            });
        var plates = new Mock<IPlateSolverFactory>();
        plates.Setup(f => f.GetCenteringSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(), imaging, telescope, wheel, dome, follower)).Returns(solver.Object);
        var factory = new Mock<INinaActionFactory>();
        factory.Setup(f => f.GetItem<Center>()).Returns(() => new Center(profiles, telescope, imaging, wheel, guider, dome, follower, plates.Object, windows.Object));
        factory.Setup(f => f.GetItem<RunAutofocus>()).Returns(() => new RunAutofocus(profiles, history, camera, wheel, focuser, focusFactory.Object) { WindowServiceFactory = windows.Object });
        factory.Setup(f => f.GetItem<NINA.Sequencer.SequenceItem.Guider.StartGuiding>()).Returns(() => original.GetItem<NINA.Sequencer.SequenceItem.Guider.StartGuiding>());
        factory.Setup(f => f.GetItem<NINA.Sequencer.SequenceItem.Guider.StopGuiding>()).Returns(() => original.GetItem<NINA.Sequencer.SequenceItem.Guider.StopGuiding>());
        factory.Setup(f => f.GetItem<NINA.Sequencer.SequenceItem.Guider.Dither>()).Returns(() => original.GetItem<NINA.Sequencer.SequenceItem.Guider.Dither>());
        factory.Setup(f => f.GetTrigger<NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger>()).Returns(() => original.GetTrigger<NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger>());
        factory.Setup(f => f.GetTrigger<NINA.Sequencer.Trigger.Guider.RestoreGuiding>()).Returns(() => original.GetTrigger<NINA.Sequencer.Trigger.Guider.RestoreGuiding>());
        factory.Setup(f => f.GetTrigger<AutofocusAfterFilterChange>()).Returns(() => Configure(new AutofocusAfterFilterChange(profiles, history, camera, wheel, focuser, focusFactory.Object, safety)));
        factory.Setup(f => f.GetTrigger<AutofocusAfterTimeTrigger>()).Returns(() => Configure(new AutofocusAfterTimeTrigger(profiles, history, camera, wheel, focuser, focusFactory.Object, safety)));
        factory.Setup(f => f.GetTrigger<AutofocusAfterTemperatureChangeTrigger>()).Returns(() => Configure(new AutofocusAfterTemperatureChangeTrigger(profiles, history, camera, wheel, focuser, focusFactory.Object, safety)));
        Factory = factory.Object;

        T Configure<T>(T trigger) where T : NINA.Sequencer.Trigger.SequenceTrigger
        {
            foreach (var item in trigger.TriggerRunner.GetItemsSnapshot().OfType<RunAutofocus>()) item.WindowServiceFactory = windows.Object;
            return trigger;
        }
    }
}
