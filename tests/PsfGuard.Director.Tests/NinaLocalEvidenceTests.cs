using System.IO;
using System.Data.SQLite;
using System.Security.Cryptography;
using Moq;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaLocalEvidenceTests
{
    private const long Day = 1790726400;

    [Fact]
    public void OrientationUsesDatedNativeValuesAndArcsecondsToRadians()
    {
        var evidence = NinaEarthOrientation.Bind(Samples(), (ulong)(Day - 3600) * 1000, (ulong)(Day + 3600) * 1000);
        Assert.Equal(Day, evidence.SampleUnixSeconds);
        Assert.Equal(0.1, evidence.Orientation.Ut1MinusUtcSeconds);
        Assert.Equal(0.2 * Math.PI / 648000, evidence.Orientation.PolarMotionXRadians, 15);
        Assert.Equal((ulong)(Day + 3600) * 1000 + 1, evidence.Orientation.ValidUntilMs);
        Assert.Equal(3, evidence.Samples.Count);
    }

    [Fact]
    public void FullDayCrossingMidnightIsBracketedWithoutExtendingSampleValidity()
    {
        var start = (ulong)(Day - 10 * 3600) * 1000;
        var evidence = NinaEarthOrientation.Bind(Samples(), start, start + 86400000);
        Assert.Equal(start, evidence.Orientation.ValidFromMs);
        Assert.Equal(start + 86400001, evidence.Orientation.ValidUntilMs);
        Assert.Throws<ArgumentException>(() => NinaEarthOrientation.Bind(Samples(), start, start + 86400001));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("mjd")]
    [InlineData("nan")]
    [InlineData("pole")]
    [InlineData("jump")]
    [InlineData("ut1")]
    public void MissingStaleMalformedOrDiscontinuousOrientationIsRefused(string change)
    {
        var rows = Samples();
        switch (change)
        {
            case "missing": rows.RemoveAt(0); break;
            case "stale": rows[0] = rows[0] with { UnixSeconds = Day - 2 * 86400 }; break;
            case "mjd": rows[0] = rows[0] with { ModifiedJulianDate = 0 }; break;
            case "nan": rows[0] = rows[0] with { XArcseconds = double.NaN }; break;
            case "pole": rows[0] = rows[0] with { YArcseconds = 10 }; break;
            case "jump": rows[0] = rows[0] with { Ut1MinusUtcSeconds = 0.103 }; break;
            case "ut1": rows[0] = rows[0] with { Ut1MinusUtcSeconds = 2 }; break;
        }
        Assert.Throws<InvalidDataException>(() => NinaEarthOrientation.Bind(rows, (ulong)(Day - 3600) * 1000, (ulong)(Day + 3600) * 1000));
    }

    [Fact]
    public void NativeCacheIsQueriedReadOnlyByColumnNameWithoutCreatingMissingDatabases()
    {
        var root = Path.Combine(Path.GetTempPath(), $"director-orientation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "NINA.sqlite");
        try
        {
            Assert.Throws<SQLiteException>(() => NinaEarthOrientation.Read(path, (ulong)Day * 1000, (ulong)(Day + 3600) * 1000, default));
            Assert.False(File.Exists(path));
            using (var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE earthrotationparameters (date INTEGER PRIMARY KEY, modifiedjuliandate REAL, x REAL, y REAL, ut1_utc REAL)";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO earthrotationparameters VALUES (@date,@mjd,@x,@y,@ut1)";
                foreach (var row in Samples())
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@date", row.UnixSeconds);
                    command.Parameters.AddWithValue("@mjd", row.ModifiedJulianDate);
                    command.Parameters.AddWithValue("@x", row.XArcseconds);
                    command.Parameters.AddWithValue("@y", row.YArcseconds);
                    command.Parameters.AddWithValue("@ut1", row.Ut1MinusUtcSeconds);
                    command.ExecuteNonQuery();
                }
            }
            var before = SHA256.HashData(File.ReadAllBytes(path));
            var evidence = NinaEarthOrientation.Read(path, (ulong)Day * 1000, (ulong)(Day + 3600) * 1000, default);
            Assert.Equal(0.1, evidence.Orientation.Ut1MinusUtcSeconds);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => NinaEarthOrientation.Read(path, (ulong)Day * 1000, (ulong)(Day + 3600) * 1000, cancellation.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SafetyNeedsFreshBroadcastAndCannotReviveAfterUnsafe()
    {
        using var f = new SafetyFixture();
        Assert.Equal(PlannerSafety.Unknown, f.Guard.Read().Safety);
        Assert.Throws<InvalidOperationException>(f.Guard.Arm);
        f.Broadcast();
        Assert.Equal(PlannerSafety.Safe, f.Guard.Read().Safety);
        f.Guard.Arm();
        f.Monitor.Raise(m => m.IsSafeChanged += null, new IsSafeEventArgs(false));
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
        f.Broadcast();
        Assert.NotEqual(PlannerSafety.Safe, f.Guard.Read().Safety);
        Assert.Throws<InvalidOperationException>(f.Guard.Arm);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("disconnected")]
    [InlineData("device")]
    [InlineData("profile")]
    [InlineData("clock-backwards")]
    [InlineData("read-failed")]
    [InlineData("profile-id")]
    [InlineData("bad-update")]
    public void LocalEvidenceLossInterruptsWithoutAServerOrAnotherDispatch(string change)
    {
        using var f = new SafetyFixture();
        f.Broadcast(); f.Guard.Arm();
        switch (change)
        {
            case "stale": f.Clock.Timestamp += 7000; f.Clock.Utc += TimeSpan.FromSeconds(7); break;
            case "disconnected": f.Info.Connected = false; break;
            case "device": f.Info.DeviceId = "different"; break;
            case "profile": f.Profiles.Raise(p => p.BeforeProfileChanging += null, EventArgs.Empty); break;
            case "clock-backwards": f.Clock.Utc -= TimeSpan.FromSeconds(1); break;
            case "read-failed": f.Monitor.Setup(m => m.GetInfo()).Throws(new IOException("driver")); break;
            case "profile-id": f.Profile.SetupGet(p => p.Id).Returns(Guid.NewGuid()); break;
            case "bad-update": f.Guard.UpdateDeviceInfo(null!); break;
        }
        f.Clock.Tick();
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
        Assert.NotEqual(PlannerSafety.Safe, f.Guard.Read().Safety);
    }

    [Fact]
    public void SafeBooleanEventCannotRenewAStaleSample()
    {
        using var f = new SafetyFixture();
        f.Broadcast(); f.Guard.Arm();
        f.Clock.Timestamp = 7000;
        f.Monitor.Raise(m => m.IsSafeChanged += null, new IsSafeEventArgs(true));
        Assert.Equal(PlannerSafety.Unknown, f.Guard.Read().Safety);
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
    }

    [Fact]
    public void ProfileChangingBeforeArmingPermanentlyInvalidatesTheOwner()
    {
        using var f = new SafetyFixture();
        f.Broadcast();
        f.Profiles.Raise(p => p.BeforeProfileChanging += null, EventArgs.Empty);
        f.Broadcast();
        Assert.Throws<InvalidOperationException>(f.Guard.Arm);
    }

    [Fact]
    public void ANewBroadcastCannotHideClockRegression()
    {
        using var f = new SafetyFixture();
        f.Broadcast(); f.Guard.Arm();
        f.Clock.Utc -= TimeSpan.FromSeconds(1);
        f.Broadcast();
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
    }

    [Fact]
    public void ResumingBroadcastsAfterSleepCannotHideStalenessFromTheWatchdog()
    {
        using var f = new SafetyFixture();
        f.Broadcast(); f.Guard.Arm();
        f.Clock.Timestamp += 7000;
        f.Clock.Utc += TimeSpan.FromSeconds(7);
        f.Broadcast();
        Assert.True(f.Guard.Interrupted.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationCallbacksCanReenterEvidenceWithoutBlockingTheMonitorThread()
    {
        using var f = new SafetyFixture();
        f.Broadcast(); f.Guard.Arm();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = f.Guard.Interrupted.Register(() =>
        {
            f.Guard.Read();
            observed.SetResult();
        });
        f.Monitor.Raise(m => m.IsSafeChanged += null, new IsSafeEventArgs(false));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static List<NinaOrientationSample> Samples() => Enumerable.Range(-1, 3).Select(i =>
        new NinaOrientationSample(Day + i * 86400, 40587 + Day / 86400.0 + i, 0.1 + i * 0.001, 0.2 + i * 0.001, 0.3 + i * 0.001)).ToList();

    private sealed class SafetyFixture : IDisposable
    {
        internal Mock<IProfileService> Profiles { get; } = new();
        internal Mock<IProfile> Profile { get; } = new() { DefaultValue = DefaultValue.Mock };
        internal Mock<ISafetyMonitorMediator> Monitor { get; } = new();
        internal SafetyMonitorInfo Info { get; } = new() { Connected = true, DeviceId = "test-safety", IsSafe = true };
        internal TestClock Clock { get; } = new();
        internal NinaSafetyInterlock Guard { get; }
        internal SafetyFixture()
        {
            var profile = Profile;
            profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
            profile.SetupGet(p => p.SafetyMonitorSettings.Id).Returns("test-safety");
            profile.SetupGet(p => p.ApplicationSettings.DevicePollingInterval).Returns(2);
            Profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
            Monitor.Setup(m => m.GetInfo()).Returns(Info);
            Guard = new(Profiles.Object, Monitor.Object, Clock);
        }
        internal void Broadcast() => Guard.UpdateDeviceInfo(Info);
        public void Dispose()
        {
            Guard.Dispose();
            Monitor.Verify(m => m.RemoveConsumer(Guard), Times.Once);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        internal long Timestamp;
        internal DateTimeOffset Utc = DateTimeOffset.FromUnixTimeSeconds(Day);
        private TimerCallback? callback;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            return new Timer();
        }
        internal void Tick() => callback?.Invoke(null);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
