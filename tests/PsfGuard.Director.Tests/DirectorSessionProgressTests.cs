using NINA.Core.Model;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DirectorSessionProgressTests
{
    [Fact]
    public async Task WorkerStagesShowElapsedTimeAndClearOnceEvenAfterLateReports()
    {
        var session = new DirectorSessionContainer();
        var reports = new List<ApplicationStatus>();
        var clock = new Clock();
        var progress = new DirectorSessionProgress(session, new InlineProgress<ApplicationStatus>(reports.Add), clock);
        await Task.Run(() => session.UpdateDisplay(d => d with { Phase = "Stopping guiding", Operation = "" }));
        clock.Advance(125000);
        Assert.Equal("Stopping guiding (2:05)", reports[^1].Status);
        Assert.All(reports, p => Assert.Equal(DirectorSessionProgress.Source, p.Source));
        progress.Dispose();
        Assert.Equal("", reports[^1].Status);
        var count = reports.Count;
        session.UpdateDisplay(d => d with { Phase = "Late update" });
        clock.Advance(1000);
        progress.Report(new ApplicationStatus { Status = "Late native update" });
        progress.Dispose();
        Assert.Equal(count, reports.Count);
    }

    [Fact]
    public void NativeDetailsAreCopiedButCameraProgressDoesNotReplaceTargetAndFilter()
    {
        var session = new DirectorSessionContainer();
        var reports = new List<ApplicationStatus>();
        var clock = new Clock();
        using var progress = new DirectorSessionProgress(session, new InlineProgress<ApplicationStatus>(reports.Add), clock);
        session.UpdateDisplay(d => d with { Phase = "Preparing target", Target = "M31", Operation = "Start guiding" });
        var native = new ApplicationStatus
        {
            Status = "Calibrating",
            Progress = 3,
            MaxProgress = 10,
            Status2 = "West",
            Progress2 = 4,
            MaxProgress2 = 12,
            Status3 = "Guide star",
            Progress3 = 5,
            MaxProgress3 = 20
        };
        progress.Report(native);
        native.Status = "";
        clock.Advance(2000);
        Assert.Equal("Calibrating — M31 (0:02)", reports[^1].Status);
        Assert.Equal(3, reports[^1].Progress);
        Assert.Equal(12, reports[^1].MaxProgress2);
        Assert.Equal("Guide star", reports[^1].Status3);
        progress.Report(new ApplicationStatus { Status = "" });
        Assert.StartsWith("Start guiding — M31", reports[^1].Status);
        session.UpdateDisplay(d => d with { Phase = "Acquiring", Operation = "HA · 120 s exposure" });
        progress.Report(new ApplicationStatus { Status = "Exposing and downloading" });
        Assert.StartsWith("HA · 120 s exposure — M31", reports[^1].Status);
        progress.Report(new ApplicationStatus { Status = "Meridian flip" });
        Assert.StartsWith("Meridian flip — M31", reports[^1].Status);
        progress.Report(new ApplicationStatus { Status = "" });
        Assert.StartsWith("HA · 120 s exposure — M31", reports[^1].Status);
        session.UpdateDisplay(d => d with { Phase = "Waiting for the safety monitor to report Safe", Operation = "" });
        Assert.StartsWith("Waiting for the safety monitor", reports[^1].Status);
    }

    [Fact]
    public void BrokenStatusObserverDoesNotInterruptEquipmentWorkOrCleanup()
    {
        var session = new DirectorSessionContainer();
        using var progress = new DirectorSessionProgress(session,
            new InlineProgress<ApplicationStatus>(_ => throw new InvalidOperationException("UI unavailable")), new Clock());
        session.UpdateDisplay(d => d with { Phase = "Stopping mount" });
        progress.Report(new ApplicationStatus { Status = "Parking" });
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        private TimerCallback? callback;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(long ms) { timestamp += ms; callback?.Invoke(null); }
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
