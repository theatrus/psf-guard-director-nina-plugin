using Moq;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaMountShutdownTests
{
    [Theory]
    [InlineData(RecoveryMotion.Unknown)]
    [InlineData(RecoveryMotion.Prohibited)]
    public async Task BlockedClearanceStopsMotionWithoutParking(RecoveryMotion motion)
    {
        var f = new Fixture { Motion = motion };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Park());
        f.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        f.VerifyStopped();
    }

    [Fact]
    public async Task WeatherCancellationStillAllowsParkWithIndependentClearance()
    {
        var f = new Fixture();
        await f.Park();
        f.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        f.Telescope.Verify(t => t.StopSlew(), Times.Never);
    }

    [Fact]
    public async Task ClearanceLossDuringParkCancelsAndStopsWithoutRetry()
    {
        var f = new Fixture();
        using var interrupted = new CancellationTokenSource();
        f.Telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .Returns<IProgress<ApplicationStatus>, CancellationToken>(async (_, token) =>
            {
                f.Motion = RecoveryMotion.Prohibited; interrupted.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Park(interrupted.Token));
        f.VerifyStopped();
        f.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedSlewStopDoesNotSkipTrackingOffOrPretendShutdownSucceeded()
    {
        var f = new Fixture { Motion = RecoveryMotion.Unknown };
        f.Telescope.Setup(t => t.StopSlew()).Throws(new IOException("stop failed"));
        await Assert.ThrowsAsync<AggregateException>(() => f.Park());
        f.VerifyStopped();
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("device")]
    [InlineData("disconnected")]
    public async Task CleanupNeverCommandsAReplacementMount(string changed)
    {
        var f = new Fixture { Motion = RecoveryMotion.Unknown };
        if (changed == "profile") f.Profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
        if (changed == "device") f.Info.DeviceId = "other";
        if (changed == "disconnected") f.Info.Connected = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Park());
        f.Telescope.Verify(t => t.StopSlew(), Times.Never);
        f.Telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
        f.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Fixture
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal Mock<IProfileService> Profiles { get; } = new();
        internal Mock<IProfile> Profile { get; } = new();
        internal Mock<ITelescopeMediator> Telescope { get; } = new();
        internal TelescopeInfo Info { get; } = new() { Connected = true, DeviceId = "mount", TrackingEnabled = true };
        internal RecoveryMotion Motion = RecoveryMotion.Permitted;
        internal Fixture()
        {
            Profile.SetupGet(p => p.Id).Returns(Id);
            Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile.Object);
            Telescope.Setup(t => t.GetInfo()).Returns(Info);
            Telescope.Setup(t => t.SetTrackingEnabled(false)).Returns(true);
            Telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }
        internal Task Park(CancellationToken interrupted = default) => NinaMountShutdown.ParkAsync(Profiles.Object, Id, Telescope.Object,
            "mount", () => new(Motion, "test clearance", 1000), interrupted, new Progress<ApplicationStatus>(), default);
        internal void VerifyStopped()
        {
            Telescope.Verify(t => t.StopSlew(), Times.Once);
            Telescope.Verify(t => t.SetTrackingEnabled(false), Times.Once);
        }
    }
}
