using Moq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.SimulatorProbe;

// Test-only timing and fault injection. The flip VM, sequence trigger, mount
// dispatch, native before/after events and autofocus workflow remain NINA's.
internal sealed class NativeFlipProbe
{
    private int saved;
    internal int Attempts { get; private set; }
    internal bool FailureInjected { get; private set; }
    internal bool MountResult { get; private set; }
    internal bool? NativeAfterSuccess { get; private set; }
    internal string? PierBefore { get; private set; }
    internal string? PierAfter { get; private set; }
    internal List<string> Events { get; } = [];
    internal MeridianFlipTrigger Trigger { get; }
    internal MeridianFlipVM? Workflow { get; private set; }

    internal NativeFlipProbe(IProfileService profiles, ICameraMediator camera, ITelescopeMediator telescope,
        IFocuserMediator focuser, IGuiderMediator guider, IFilterWheelMediator wheel,
        IDomeMediator dome, IDomeFollower follower, IImagingMediator imaging, IImageHistoryVM history,
        ISafetyMonitorMediator safety, IAutoFocusVMFactory focus, IWindowServiceFactory windows, bool fail)
    {
        var mount = new Mock<ITelescopeMediator>(MockBehavior.Strict);
        mount.Setup(x => x.GetInfo()).Returns(() =>
        {
            var info = telescope.GetInfo();
            return new TelescopeInfo
            {
                Connected = info.Connected,
                DeviceId = info.DeviceId,
                AtPark = info.AtPark,
                AtHome = info.AtHome,
                TrackingEnabled = info.TrackingEnabled,
                Coordinates = info.Coordinates,
                SideOfPier = info.SideOfPier,
                // OmniSim's explicit pier-side setter re-slews to its normal
                // side. Let the real VM wait until the target has crossed.
                SiderealTime = info.SiderealTime,
                TimeToMeridianFlip = saved >= 2 && Attempts == 0
                    ? Math.Max(0, Math.IEEERemainder(info.Coordinates.Transform(Epoch.JNOW).RA - info.SiderealTime, 24) + 10.0 / 3600) : 2
            };
        });
        mount.Setup(x => x.GetCurrentPosition()).Returns(telescope.GetCurrentPosition);
        mount.Setup(x => x.SetTrackingEnabled(It.IsAny<bool>())).Returns((bool value) => telescope.SetTrackingEnabled(value));
        mount.Setup(x => x.RaiseBeforeMeridianFlip(It.IsAny<BeforeMeridianFlipEventArgs>()))
            .Returns(async (BeforeMeridianFlipEventArgs e) => { Events.Add("Before flip"); await telescope.RaiseBeforeMeridianFlip(e); });
        mount.Setup(x => x.RaiseAfterMeridianFlip(It.IsAny<AfterMeridianFlipEventArgs>()))
            .Returns(async (AfterMeridianFlipEventArgs e) => { NativeAfterSuccess = e.Success; Events.Add("After flip"); await telescope.RaiseAfterMeridianFlip(e); });
        mount.Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
            .Returns(async (Coordinates target, CancellationToken token) =>
            {
                if (telescope.GetInfo() is not { Connected: true, DeviceId: "ASCOM.OmniSim.Telescope" })
                    throw new InvalidOperationException("Forced flip requires the isolated ASCOM simulator.");
                if (++Attempts != 1 || saved != 2) throw new InvalidOperationException("Flip did not occur at the requested saved-frame boundary.");
                PierBefore = ((ITelescope)telescope.GetDevice()).SideOfPier.ToString();
                Events.Add("Mount flip");
                if (fail) { FailureInjected = true; MountResult = false; }
                else MountResult = await telescope.MeridianFlip(target, token);
                PierAfter = ((ITelescope)telescope.GetDevice()).SideOfPier.ToString();
                return MountResult;
            });
        var status = Mock.Of<IApplicationStatusMediator>();
        var vmFactory = new Mock<IMeridianFlipVMFactory>();
        vmFactory.Setup(x => x.Create()).Returns(() => Workflow = new MeridianFlipVM(profiles, mount.Object, guider,
            imaging, dome, follower, status, wheel, history, focus)
        { WindowServiceFactory = windows });
        Trigger = new NinaMeridianFlipTrigger(profiles, camera, mount.Object, focuser, status, vmFactory.Object, safety);
    }

    internal SequenceItem SaveMarker() => new Marker(this);

    private sealed class Marker(NativeFlipProbe owner) : SequenceItem
    {
        public override object Clone() => new Marker(owner);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            owner.Events.Add($"Saved {++owner.saved}");
            return Task.CompletedTask;
        }
    }

    internal void Verify(DirectorSessionContainer session, bool failed)
    {
        var expected = new[] { "Saved 1", "Saved 2", "Before flip", "Mount flip", "After flip" };
        if (!failed) expected = [.. expected, "Saved 3"];
        if (Attempts != 1 || saved != (failed ? 2 : 3) || FailureInjected != failed || MountResult == failed
            || !Events.SequenceEqual(expected)
            || !session.ActionHistory.Any(x => x.Action == "Meridian flip" && x.Outcome == (failed ? "Failed or interrupted" : "Succeeded")))
            throw new InvalidOperationException("Native flip outcome, boundary events or acquisition continuation was not verified.");
        if (!failed && (NativeAfterSuccess != true || PierBefore is null or "pierUnknown"
            || PierBefore == PierAfter || PierAfter is null or "pierUnknown"
            || Workflow is null || Workflow.Steps.Any(x => !x.Finished)))
            throw new InvalidOperationException("Native flip did not change pier side or finish all workflow steps.");
        if (failed && Workflow?.Steps.Single(x => x.Id == "Flip").Finished != false)
            throw new InvalidOperationException("Injected mount failure did not reach the native workflow.");
    }
}
