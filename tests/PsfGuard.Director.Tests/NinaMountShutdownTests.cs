using Moq;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaMountShutdownTests
{
    [Fact]
    public async Task WeatherStopWaitsForNativeConfirmationWithoutRepeatingCommands()
    {
        var f = new Fixture();
        f.Telescope.Setup(t => t.SetTrackingEnabled(false)).Returns(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stopped = NinaMountShutdown.StopAndConfirmAsync(f.Profiles.Object, f.Id, f.Telescope.Object, "mount", deadline.Token);
        Assert.False(stopped.IsCompleted);
        f.Info.TrackingEnabled = false;
        await stopped;
        f.VerifyStopped();
    }

    [Fact]
    public async Task WeatherStopCannotAssumeQuiescenceFromAnUnchangedNativeState()
    {
        var f = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NinaMountShutdown.StopAndConfirmAsync(f.Profiles.Object, f.Id, f.Telescope.Object, "mount", deadline.Token));
        f.VerifyStopped();
    }
    [Fact]
    public void LegacySequencesKeepParkPolicyAndUnknownPolicyIsRejected()
    {
        var options = Newtonsoft.Json.JsonConvert.DeserializeObject<DirectorSessionOptions>("{}")!;
        Assert.Equal(DirectorAbortPolicy.ParkMount, options.OnAbort);
        options.OnAbort = (DirectorAbortPolicy)99;
        Assert.Contains("Unknown abort policy.", options.ValidateSettings());
    }

    [Fact]
    public void StopOnlyNeverParks()
    {
        var f = new Fixture();
        f.Stop();
        f.VerifyStopped();
        f.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void AlreadyParkedMountNeedsNoNewStopCommands()
    {
        var f = new Fixture();
        f.Info.AtPark = true;
        f.Stop();
        f.Telescope.Verify(t => t.StopSlew(), Times.Never);
        f.Telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void ReplacementDuringSlewStopDoesNotReceiveTrackingCommand()
    {
        var f = new Fixture();
        f.Telescope.Setup(t => t.StopSlew()).Callback(() => f.Info.DeviceId = "replacement");
        Assert.Throws<AggregateException>(f.Stop);
        f.Telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopFailuresRemainVisibleAndBothCommandsAreAttempted(bool slewFails)
    {
        var f = new Fixture();
        if (slewFails) f.Telescope.Setup(t => t.StopSlew()).Throws(new IOException("stop failed"));
        else f.Telescope.Setup(t => t.SetTrackingEnabled(false)).Returns(true);
        Assert.Throws<AggregateException>(f.Stop);
        f.VerifyStopped();
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("exception")]
    [InlineData("cancelled")]
    public async Task FailedParkFallsBackToStopEvenWithClearEnclosure(string failure)
    {
        var f = new Fixture();
        var setup = f.Telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()));
        if (failure == "refused") setup.ReturnsAsync(false);
        else setup.ThrowsAsync(failure == "cancelled" ? new OperationCanceledException() : new IOException("park failed"));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Park());
        Assert.False(f.CanResumeWeather);
        f.VerifyStopped();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Park());
        f.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

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
        Assert.False(f.CanResumeWeather);
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
        Assert.Throws<InvalidOperationException>(f.Stop);
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
        private readonly NinaMountShutdown shutdown = new();
        internal bool CanResumeWeather => shutdown.CanResumeWeather;
        internal Fixture()
        {
            Profile.SetupGet(p => p.Id).Returns(Id);
            Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile.Object);
            Telescope.Setup(t => t.GetInfo()).Returns(Info);
            Telescope.Setup(t => t.SetTrackingEnabled(false)).Returns(false);
            Telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }
        internal Task Park(CancellationToken interrupted = default) => shutdown.ParkAsync(Profiles.Object, Id, Telescope.Object,
            "mount", () => new(Motion, "test clearance", 1000), interrupted, new Progress<ApplicationStatus>(), default);
        internal void Stop() => NinaMountShutdown.Stop(Profiles.Object, Id, Telescope.Object, "mount");
        internal void VerifyStopped()
        {
            Telescope.Verify(t => t.StopSlew(), Times.Once);
            Telescope.Verify(t => t.SetTrackingEnabled(false), Times.Once);
        }
    }
}
