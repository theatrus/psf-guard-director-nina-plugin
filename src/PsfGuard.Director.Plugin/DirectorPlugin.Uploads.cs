using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NINA.Core.Utility;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

public sealed partial class DirectorPlugin
{
    private static readonly TimeSpan UploadViewInterval = TimeSpan.FromSeconds(1);
    private AsyncCommand? retryRefusedUploads;
    private AsyncCommand? forgetHeldUploads;
    private bool uploadsStarted;
    private int uploadViewPending;
    private string uploadStatus = "Pair with PSF Guard to upload frames.";
    private string uploadQueueStatus = "No frames are queued.";
    private int refusedUploads;
    private int heldUploads;

    /// <summary>Per profile, on by default. Off, Director Sessions queue no new frames.</summary>
    public bool UploadCapturedFrames
    {
        get => settings.GetValueBoolean(DirectorImageUploads.SettingKey, true);
        set
        {
            if (value == UploadCapturedFrames) return;
            settings.SetValueBoolean(DirectorImageUploads.SettingKey, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadCapturedFrames)));
        }
    }

    /// <summary>Whether the paired server takes uploads, or why not.</summary>
    public string UploadStatus => uploadStatus;

    /// <summary>How many frames wait, are for an earlier pairing, or were refused.</summary>
    public string UploadQueueStatus => uploadQueueStatus;

    public ICommand RetryRefusedUploadsCommand => retryRefusedUploads ??= new(
        () => Task.Run(() => DirectorImageUploads.Queue.RetryRefusedAsync(CancellationToken.None)),
        () => initialized && refusedUploads > 0, ReportError);

    public ICommand ForgetHeldUploadsCommand => forgetHeldUploads ??= new(
        () => Task.Run(() => DirectorImageUploads.Queue.ForgetHeldAsync(CancellationToken.None)),
        () => initialized && heldUploads > 0, ReportError);

    private DirectorUploadDestination? CurrentUploadDestination()
    {
        try
        {
            if (ConnectionModel.ResolvedEndpoint is not { } endpoint) return null;
            var pairing = ConnectionModel.ReadPairing();
            return pairing is null ? null
                : new(endpoint, profiles.ActiveProfile.Id, pairing.Binding.RigId, ConnectionModel.AllowInsecureHttp);
        }
        catch (Exception) { return null; }
    }

    private void StartUploads()
    {
        try
        {
            var queue = DirectorImageUploads.Queue;
            if (!uploadsStarted) queue.Changed += UploadsChanged;
            uploadsStarted = true;
            queue.Start();
            CheckUploadState();
        }
        catch (Exception error)
        {
            // Uploads are optional; they never stop the plugin.
            Logger.Info($"PSF Guard Director uploads are off ({error.GetType().Name}).");
        }
    }

    private async Task StopUploadsAsync()
    {
        if (!uploadsStarted) return;
        uploadsStarted = false;
        DirectorImageUploads.Queue.Changed -= UploadsChanged;
        // The queue stays usable: frames offered from now on are still written to disk.
        try { await DirectorImageUploads.Queue.StopAsync(); }
        catch (Exception error) { Logger.Error(error); }
    }

    /// <summary>Forget older answers and ask the paired server again, off the UI thread.</summary>
    private void CheckUploadState()
    {
        if (!uploadsStarted) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var queue = DirectorImageUploads.Queue;
                queue.RequestRecheck();
                if (CurrentUploadDestination() is { } destination)
                    await queue.RefreshStateAsync(destination, CancellationToken.None);
            }
            catch (Exception error) { Logger.Debug($"PSF Guard Director could not check uploads: {error.GetType().Name}"); }
            ScheduleUploadView();
        });
    }

    private void UploadsChanged(object? sender, EventArgs args) => ScheduleUploadView();

    /// <summary>
    /// Recompute the settings text on a pool thread at most once a second,
    /// then post only the finished values to the UI thread.
    /// </summary>
    private void ScheduleUploadView()
    {
        if (Interlocked.Exchange(ref uploadViewPending, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(UploadViewInterval);
                Volatile.Write(ref uploadViewPending, 0);
                var view = ComputeUploadView();
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) ApplyUploadView(view);
                else if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(() => ApplyUploadView(view));
            }
            catch (Exception error)
            {
                Volatile.Write(ref uploadViewPending, 0);
                Logger.Debug($"PSF Guard Director could not read the upload queue: {error.GetType().Name}");
            }
        });
    }

    private sealed record UploadView(string Status, string Queue, int Refused, int Held);

    internal static string DescribeUploads(DirectorUploadDestination? destination, DirectorUploadSummary summary)
    {
        if (destination is null) return "Pair with PSF Guard to upload frames.";
        return summary.State switch
        {
            null => "Checking whether PSF Guard takes uploads.",
            { Kind: DirectorUploadStateKind.Enabled } => "PSF Guard takes uploads from this rig.",
            { Kind: DirectorUploadStateKind.Disabled, Reason: { } reason } => reason,
            { Kind: DirectorUploadStateKind.Disabled } => "Image uploads are off for this database.",
            { Kind: DirectorUploadStateKind.Unsupported } =>
                "PSF Guard does not take uploads from Director now. Frames stay queued; Director asks again every 10 minutes.",
            _ => "PSF Guard could not be reached. Queued frames upload later.",
        };
    }

    internal static string DescribeQueue(DirectorUploadSummary summary)
    {
        static string Frames(int count) => count == 1 ? "1 frame" : $"{count} frames";
        var parts = new List<string> { summary.Queued == 1 ? "1 frame is queued" : $"{summary.Queued} frames are queued" };
        if (summary.EarlierPairing > 0) parts.Add($"{Frames(summary.EarlierPairing)} for an earlier pairing");
        if (summary.Refused > 0) parts.Add($"{Frames(summary.Refused)} refused by PSF Guard");
        return summary is { Queued: 0, EarlierPairing: 0, Refused: 0 } ? "No frames are queued." : string.Join("; ", parts) + ".";
    }

    private UploadView ComputeUploadView()
    {
        var destination = initialized ? CurrentUploadDestination() : null;
        var summary = DirectorImageUploads.Queue.Summarize(destination);
        return new(DescribeUploads(destination, summary), DescribeQueue(summary), summary.Refused, summary.Refused + summary.EarlierPairing);
    }

    private void ApplyUploadView(UploadView view)
    {
        uploadStatus = view.Status;
        uploadQueueStatus = view.Queue;
        refusedUploads = view.Refused;
        heldUploads = view.Held;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadQueueStatus)));
        retryRefusedUploads?.Refresh();
        forgetHeldUploads?.Refresh();
    }
}
