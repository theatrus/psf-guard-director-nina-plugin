using Moq;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaEnclosureTests
{
    [Fact]
    public void ReopenNeedsExplicitRearmAndCannotReviveOldTokensOrHideAFlap()
    {
        using var f = new Fixture();
        f.Broadcast(); f.Guard.Arm();
        var old = f.Guard.Interrupted;
        f.Info.ShutterStatus = ShutterState.ShutterClosing; f.Broadcast();
        var interrupted = f.Guard.RefusalRevision;
        f.Info.ShutterStatus = ShutterState.ShutterOpen; f.Broadcast();
        Assert.Equal(RecoveryMotion.Permitted, f.Guard.ReadCurrent().Motion);
        Assert.NotEqual(RecoveryMotion.Permitted, f.Guard.Read().Motion);
        f.Info.ShutterStatus = ShutterState.ShutterClosing; f.Broadcast();
        f.Info.ShutterStatus = ShutterState.ShutterOpen; f.Broadcast();
        Assert.Throws<InvalidOperationException>(() => f.Guard.Rearm(interrupted));
        f.Guard.Rearm(f.Guard.RefusalRevision);
        Assert.Equal(RecoveryMotion.Permitted, f.Guard.Read().Motion);
        Assert.True(old.IsCancellationRequested);
        Assert.False(f.Guard.Interrupted.IsCancellationRequested);
    }

    [Fact]
    public void OldSequencesCannotSilentlyCommissionClearance()
    {
        var options = Newtonsoft.Json.JsonConvert.DeserializeObject<DirectorSessionOptions>("{}")!;
        Assert.Equal(DirectorEnclosurePolicy.Unconfigured, options.Enclosure);
        Assert.Contains(DirectorAcquisition.PolicyIssues(options), message => message.Contains("enclosure"));
        options.Enclosure = (DirectorEnclosurePolicy)99;
        Assert.Contains(options.ValidateSettings(), message => message.Contains("enclosure"));
    }

    [Fact]
    public void FullyOpenGetInfoAloneDoesNotProveFreshClearance()
    {
        using var f = new Fixture();
        Assert.Equal(RecoveryMotion.Unknown, f.Guard.Read().Motion);
        Assert.Throws<InvalidOperationException>(f.Guard.Arm);
        f.Broadcast(); f.Guard.Arm();
        Assert.Equal(RecoveryMotion.Permitted, f.Guard.Read().Motion);
        Assert.True(f.Guard.Read().ValidUntilMs > (ulong)f.Clock.Utc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void CachedRegistrationCallbackIsNotAFreshDevicePoll()
    {
        using var f = new Fixture(cachedRegistration: true);
        Assert.Equal(RecoveryMotion.Unknown, f.Guard.Read().Motion);
        f.Broadcast(); f.Guard.Arm();
        Assert.Equal(RecoveryMotion.Permitted, f.Guard.Read().Motion);
    }

    [Theory]
    [InlineData(ShutterState.ShutterClosed)]
    [InlineData(ShutterState.ShutterClosing)]
    [InlineData(ShutterState.ShutterOpening)]
    [InlineData(ShutterState.ShutterError)]
    [InlineData(ShutterState.ShutterNone)]
    [InlineData((ShutterState)999)]
    public void EveryNonOpenShutterStopsAndCannotResumeOrPark(ShutterState state)
    {
        using var f = new Fixture();
        f.Broadcast(); f.Guard.Arm();
        f.Info.ShutterStatus = state; f.Broadcast();
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
        f.Info.ShutterStatus = ShutterState.ShutterOpen; f.Broadcast();
        Assert.NotEqual(RecoveryMotion.Permitted, f.Guard.Read().Motion);
        Assert.Throws<InvalidOperationException>(f.Guard.RequireClear);
        Assert.Throws<InvalidOperationException>(f.Guard.Arm);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("sleep")]
    [InlineData("disconnected")]
    [InlineData("wrong-device")]
    [InlineData("profile")]
    [InlineData("device")]
    [InlineData("clock")]
    [InlineData("read")]
    [InlineData("null")]
    public void LostEvidenceStopsWithoutWaitingForAnotherDispatch(string fault)
    {
        using var f = new Fixture();
        f.Broadcast(); f.Guard.Arm();
        switch (fault)
        {
            case "stale": f.Clock.Advance(7000); break;
            case "sleep": f.Clock.Advance(7000); f.Broadcast(); break;
            case "disconnected": f.Info.Connected = false; break;
            case "wrong-device": f.Info.DeviceId = "other"; break;
            case "profile": f.Profiles.Raise(p => p.BeforeProfileChanging += null, EventArgs.Empty); break;
            case "device": f.Profile.SetupGet(p => p.DomeSettings.Id).Returns("other"); break;
            case "clock": f.Clock.Advance(-1000); f.Broadcast(); break;
            case "read": f.Dome.Setup(d => d.GetInfo()).Throws(new IOException()); break;
            case "null": f.Guard.UpdateDeviceInfo(null!); break;
        }
        f.Clock.Tick();
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
        Assert.NotEqual(RecoveryMotion.Permitted, f.Guard.Read().Motion);
    }

    [Fact]
    public void OpenAirRequiresNoConfiguredOrConnectedEnclosure()
    {
        using var f = new Fixture(DirectorEnclosurePolicy.OpenAir);
        f.Guard.Arm();
        Assert.Equal(RecoveryMotion.Permitted, f.Guard.Read().Motion);
        f.Info.Connected = true; f.Broadcast();
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
        f.Info.Connected = false;
        Assert.Throws<InvalidOperationException>(f.Guard.RequireClear);
    }

    [Fact]
    public void ProfileSwitchBeforeArmingCannotBeHiddenByBroadcast()
    {
        using var f = new Fixture();
        f.Profiles.Raise(p => p.BeforeProfileChanging += null, EventArgs.Empty);
        f.Broadcast();
        Assert.Throws<InvalidOperationException>(f.Guard.Arm);
    }

    [Fact]
    public async Task ReentrantCancellationDoesNotBlockDomeBroadcasts()
    {
        using var f = new Fixture();
        f.Broadcast(); f.Guard.Arm();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = f.Guard.Interrupted.Register(() => { f.Guard.Read(); done.SetResult(); });
        f.Info.ShutterStatus = ShutterState.ShutterClosing; f.Broadcast();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ClosedEventCannotBeOverriddenByOldOpenInfo()
    {
        using var f = new Fixture();
        f.Broadcast(); f.Guard.Arm();
        f.Dome.Raise(d => d.Closed += null, f.Dome.Object, EventArgs.Empty);
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(f.Guard.RequireClear);
    }

    private sealed class Fixture : IDisposable
    {
        internal Mock<IProfileService> Profiles { get; } = new();
        internal Mock<IProfile> Profile { get; } = new() { DefaultValue = DefaultValue.Mock };
        internal Mock<IDomeMediator> Dome { get; } = new();
        internal DomeInfo Info { get; } = new() { Connected = true, DeviceId = "test-dome", ShutterStatus = ShutterState.ShutterOpen };
        internal Clock Clock { get; } = new();
        internal NinaEnclosureInterlock Guard { get; }
        internal Fixture(DirectorEnclosurePolicy policy = DirectorEnclosurePolicy.RequireOpenShutter, bool cachedRegistration = false)
        {
            Profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
            Profile.SetupGet(p => p.DomeSettings.Id).Returns(policy == DirectorEnclosurePolicy.OpenAir ? "No_Device" : "test-dome");
            Profile.SetupGet(p => p.ApplicationSettings.DevicePollingInterval).Returns(2);
            Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile.Object);
            Info.Connected = policy != DirectorEnclosurePolicy.OpenAir;
            Dome.Setup(d => d.GetInfo()).Returns(Info);
            if (cachedRegistration) Dome.Setup(d => d.RegisterConsumer(It.IsAny<IDomeConsumer>())).Callback<IDomeConsumer>(c => c.UpdateDeviceInfo(Info));
            Guard = new(Profiles.Object, Dome.Object, policy, Clock);
        }
        internal void Broadcast() => Guard.UpdateDeviceInfo(Info);
        public void Dispose()
        {
            Guard.Dispose();
            Dome.Verify(d => d.RemoveConsumer(Guard), Times.Once);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        internal DateTimeOffset Utc = DateTimeOffset.FromUnixTimeSeconds(1790726400);
        private TimerCallback? callback;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        internal void Advance(long ms) { timestamp += ms; Utc = Utc.AddMilliseconds(ms); }
        internal void Tick() => callback?.Invoke(null);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { this.callback = callback; return new Timer(); }
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
