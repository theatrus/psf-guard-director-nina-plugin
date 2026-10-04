using Moq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.SimulatorProbe;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NativeFlipProbeTests
{
    [Fact]
    public async Task ClonedSaveMarkersArmTimingOnlyAfterTwoSavesWithoutChangingNativeInfo()
    {
        var profiles = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
        var telescope = new Mock<ITelescopeMediator>();
        var info = new TelescopeInfo
        {
            Connected = true,
            TrackingEnabled = true,
            AtPark = false,
            AtHome = false,
            TimeToMeridianFlip = 7,
            SiderealTime = 5,
            Coordinates = new Coordinates(5, 10, Epoch.JNOW, Coordinates.RAType.Hours)
        };
        telescope.Setup(x => x.GetInfo()).Returns(info);
        var probe = new NativeFlipProbe(profiles.Object, Mock.Of<ICameraMediator>(), telescope.Object,
            Mock.Of<IFocuserMediator>(), Mock.Of<IGuiderMediator>(), Mock.Of<IFilterWheelMediator>(),
            Mock.Of<IDomeMediator>(), Mock.Of<IDomeFollower>(), Mock.Of<IImagingMediator>(), Mock.Of<IImageHistoryVM>(),
            Mock.Of<ISafetyMonitorMediator>(), Mock.Of<IAutoFocusVMFactory>(), Mock.Of<IWindowServiceFactory>(), false);
        var marker = probe.SaveMarker();
        Assert.False(probe.Trigger.ShouldTrigger(null!, null!));
        await ((SequenceItem)marker.Clone()).Execute(new Progress<ApplicationStatus>(), default);
        Assert.False(probe.Trigger.ShouldTrigger(null!, null!));
        await ((SequenceItem)marker.Clone()).Execute(new Progress<ApplicationStatus>(), default);
        Assert.True(probe.Trigger.ShouldTrigger(null!, null!));
        Assert.Equal(7, info.TimeToMeridianFlip);
        Assert.Equal(new[] { "Saved 1", "Saved 2" }, probe.Events);
        telescope.Verify(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
