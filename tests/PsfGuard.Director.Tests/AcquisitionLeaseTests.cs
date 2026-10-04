using PsfGuard.Director.Runtime;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class AcquisitionLeaseTests
{
    private sealed class BrokenClock : TimeProvider { public override long GetTimestamp() => throw new InvalidOperationException("Clock unavailable"); }

    [Fact]
    public void FailedClockInitializationDoesNotLeaveAFileOrProcessOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<InvalidOperationException>(() => new AcquisitionLease(root, new BrokenClock()));
            Assert.False(AcquisitionLease.IsActive);
            using var lease = new AcquisitionLease(root);
            lease.CheckClock();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = DateTimeOffset.UtcNow;
        internal long Ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void OnlyOneOwnerAndClockDiscontinuityRefusesDispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        try
        {
            using (var lease = new AcquisitionLease(root, clock))
            {
                Assert.True(AcquisitionLease.IsActive);
                Assert.Throws<InvalidOperationException>(() => new AcquisitionLease(root));
                clock.Utc = clock.Utc.AddSeconds(5);
                clock.Ticks += TimeSpan.FromSeconds(5).Ticks;
                lease.CheckClock();
                clock.Utc = clock.Utc.AddSeconds(-4);
                Assert.Throws<InvalidOperationException>(lease.CheckClock);
            }
            Assert.False(AcquisitionLease.IsActive);
            using var reopened = new AcquisitionLease(root);
            reopened.CheckClock();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PublicModeNeverSilentlyIgnoresUnsupportedAutomationOrSafety()
    {
        var options = new DirectorSessionOptions();
        Assert.NotEmpty(DirectorAcquisition.PolicyIssues(options));
        options.EnableAcquisition = true;
        options.Enclosure = DirectorEnclosurePolicy.OpenAir;
        options.SlewCenter = options.Focus = options.Guiding = options.Dither = options.MeridianFlip = DirectorOperationOwner.Sequence;
        Assert.Empty(DirectorAcquisition.PolicyIssues(options));
        options.Safety = DirectorSafetyPolicy.Attended;
        Assert.Contains(DirectorAcquisition.PolicyIssues(options), x => x.Contains("safety monitor"));
        options.Safety = DirectorSafetyPolicy.RequireMonitor;
        options.Focus = DirectorOperationOwner.Director;
        Assert.Empty(DirectorAcquisition.PolicyIssues(options));
        Assert.True(NinaNativeImaging.Required(options));
    }
}
