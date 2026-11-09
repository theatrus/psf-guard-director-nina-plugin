using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NINA.Core.Utility;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

public sealed partial class DirectorPlugin
{
    private AsyncCommand? retryRefusedUploads;
    private AsyncCommand? forgetRefusedUploads;
    private bool uploadsStarted;
    private string uploadStatus = "No paired server.";
    private string uploadQueueStatus = "No frames are queued.";
    private int refusedUploads;

    /// <summary>Per profile, off by default. Independent of Live image record delivery.</summary>
    public bool UploadCapturedFrames
    {
        get => settings.GetValueBoolean(DirectorImageUploads.SettingKey, DirectorImageUploads.DefaultEnabled);
        set
        {
            if (value == UploadCapturedFrames) return;
            settings.SetValueBoolean(DirectorImageUploads.SettingKey, value);
            CheckUploadState();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadCapturedFrames)));
        }
    }

    /// <summary>Whether the paired server takes uploads, or why not.</summary>
    public string UploadStatus => uploadStatus;

    /// <summary>How many frames wait and how many PSF Guard refused.</summary>
    public string UploadQueueStatus => uploadQueueStatus;

    public ICommand RetryRefusedUploadsCommand => retryRefusedUploads ??= new(
        async () => { await DirectorImageUploads.Queue.RetryRefusedAsync(CurrentUploadDestination(), CancellationToken.None); RefreshUploads(); },
        () => initialized && refusedUploads > 0, ReportError);

    public ICommand ForgetRefusedUploadsCommand => forgetRefusedUploads ??= new(
        async () => { await DirectorImageUploads.Queue.ForgetRefusedAsync(CurrentUploadDestination(), CancellationToken.None); RefreshUploads(); },
        () => initialized && refusedUploads > 0, ReportError);

    private DirectorUploadDestination? CurrentUploadDestination()
    {
        try
        {
            if (ConnectionModel.ResolvedEndpoint is not { } endpoint) return null;
            var pairing = ConnectionModel.ReadPairing();
            return pairing is null ? null
                : new(endpoint, pairing.Binding, ConnectionModel.AllowInsecureHttp);
        }
        catch (Exception) { return null; }
    }

    private void StartUploads()
    {
        try
        {
            var queue = DirectorImageUploads.Queue;
            queue.Changed += UploadsChanged;
            queue.Start();
            uploadsStarted = true;
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
        // The queue lives for the N.I.N.A. process; queued frames stay on disk.
        try { await DirectorImageUploads.Queue.DisposeAsync(); }
        catch (Exception error) { Logger.Error(error); }
    }

    /// <summary>Forget older answers and ask the paired server again, off the UI thread.</summary>
    private void CheckUploadState()
    {
        if (!uploadsStarted) return;
        var destination = CurrentUploadDestination();
        DirectorImageUploads.Queue.SetDestination(UploadCapturedFrames ? destination : null);
        DirectorImageUploads.Queue.RequestRecheck();
        if (UploadCapturedFrames && destination is not null)
            _ = Task.Run(() => DirectorImageUploads.Queue.RefreshStateAsync(destination, CancellationToken.None));
        RefreshUploads();
    }

    private void UploadsChanged(object? sender, EventArgs args) => RefreshUploads();

    private void RefreshUploads()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(RefreshUploads);
            return;
        }
        var destination = initialized ? CurrentUploadDestination() : null;
        var summary = DirectorImageUploads.Queue.Summarize(destination);
        uploadStatus = !UploadCapturedFrames ? "File uploads are off. Queued files wait; image record delivery is unchanged."
            : destination is null ? "Pair with PSF Guard to upload frames." : summary.State switch
            {
                null => "Checking whether PSF Guard takes uploads.",
                { Kind: DirectorUploadStateKind.Enabled } => "PSF Guard takes uploads from this rig.",
                { Kind: DirectorUploadStateKind.Disabled, Reason: { } reason } => reason,
                { Kind: DirectorUploadStateKind.Disabled } => "Image uploads are off for this database.",
                { Kind: DirectorUploadStateKind.Unsupported } => "This PSF Guard server does not take uploads from Director. Frames stay on disk.",
                _ => "PSF Guard could not be reached. Queued frames wait and upload later.",
            };
        refusedUploads = summary.Refused;
        uploadQueueStatus = (summary.Queued, summary.Refused) switch
        {
            (0, 0) => "No frames are queued.",
            (var queued, 0) => queued == 1 ? "1 frame is queued." : $"{queued} frames are queued.",
            (var queued, var refused) => (queued == 1 ? "1 frame is queued; " : $"{queued} frames are queued; ")
                + (refused == 1 ? "PSF Guard refused 1 frame." : $"PSF Guard refused {refused} frames."),
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadQueueStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadCapturedFrames)));
        retryRefusedUploads?.Refresh();
        forgetRefusedUploads?.Refresh();
    }
}
