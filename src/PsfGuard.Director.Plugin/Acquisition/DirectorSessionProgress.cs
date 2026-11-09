using NINA.Core.Model;
using NINA.Core.Utility;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.Plugin.Acquisition;

// One status-bar entry for the lifetime of a Director run. Only immutable
// snapshots cross threads; NINA's supplied progress sink owns UI dispatch.
internal sealed class DirectorSessionProgress : IProgress<ApplicationStatus>, IDisposable
{
    internal const string Source = "PSF Guard Director";
    private readonly object gate = new();
    private readonly DirectorSessionContainer container;
    private readonly IProgress<ApplicationStatus> sink;
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private DirectorSessionDisplay display = DirectorSessionDisplay.Empty with { Phase = "Starting Director Session", Operation = "" };
    private ApplicationStatus? native;
    private long started;
    private bool disposed;

    internal DirectorSessionProgress(DirectorSessionContainer container, IProgress<ApplicationStatus> sink, TimeProvider? clock = null)
    {
        this.container = container;
        this.sink = sink;
        this.clock = clock ?? TimeProvider.System;
        started = this.clock.GetTimestamp();
        container.DisplayChanged += Update;
        Publish();
        timer = this.clock.CreateTimer(_ => { lock (gate) { if (!disposed) Publish(); } }, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Update(DirectorSessionDisplay next)
    {
        lock (gate)
        {
            if (disposed) return;
            if ((display.Phase, display.Operation, display.Target) == (next.Phase, next.Operation, next.Target)) return;
            display = next;
            native = null;
            started = clock.GetTimestamp();
            Publish();
        }
    }

    public void Report(ApplicationStatus value)
    {
        lock (gate)
        {
            if (disposed) return;
            // Native NINA actions can reuse and clear their status object.
            // Drop the adapter's duplicate camera message, but keep native
            // triggers (such as a flip or autofocus before the next exposure).
            native = value.Status == "Exposing and downloading" || string.IsNullOrWhiteSpace(value.Status) ? null : Copy(value);
            Publish();
        }
    }

    private void Publish()
    {
        var status = native is null ? new ApplicationStatus() : Copy(native);
        var action = native?.Status ?? (display.Operation is not "" and not "-" ? display.Operation : display.Phase);
        var target = display.Target is not "" and not "-" ? $" — {display.Target}" : "";
        var elapsed = clock.GetElapsedTime(started);
        status.Source = Source;
        status.Status = $"{action}{target} ({(int)elapsed.TotalMinutes}:{elapsed.Seconds:00})";
        Send(status);
    }

    private void Send(ApplicationStatus status)
    {
        try { sink.Report(status); }
        catch (Exception error) { Logger.Error(error); } // Display failure cannot interrupt equipment cleanup.
    }

    private static ApplicationStatus Copy(ApplicationStatus value) => new()
    {
        Status = value.Status,
        Progress = value.Progress,
        MaxProgress = value.MaxProgress,
        ProgressType = value.ProgressType,
        Status2 = value.Status2,
        Progress2 = value.Progress2,
        MaxProgress2 = value.MaxProgress2,
        ProgressType2 = value.ProgressType2,
        Status3 = value.Status3,
        Progress3 = value.Progress3,
        MaxProgress3 = value.MaxProgress3,
        ProgressType3 = value.ProgressType3
    };

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            container.DisplayChanged -= Update;
            timer.Dispose();
            Send(new ApplicationStatus { Source = Source, Status = "" });
        }
    }
}
