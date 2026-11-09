using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

/// <summary>One captured frame waiting to upload. It names the saved file; it holds no copy.</summary>
internal sealed record DirectorUploadJob
{
    public int Version { get; init; } = 1;
    public required Guid JobId { get; init; }
    public required string Endpoint { get; init; }
    public required Guid ProfileId { get; init; }
    public required Guid RigId { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public required string ImagePath { get; init; }
    public string? CaptureId { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? RefusalReason { get; set; }

    internal DirectorUploadDestination Destination => new(new Uri(Endpoint), ProfileId, RigId, AllowInsecureHttp);
}

/// <summary>Counts and the server's state for the settings page.</summary>
public sealed record DirectorUploadSummary(int Queued, int Refused, DirectorUploadState? State);

/// <summary>
/// The Director plugin's durable upload queue. Each frame is one small job
/// file below <c>root</c>, naming the saved image, its destination, attempts
/// and next try, so the queue survives restarts. One background worker
/// uploads one frame at a time and never runs on N.I.N.A.'s UI thread.
/// </summary>
/// <remarks>
/// <para>
/// The worker asks the server whether it takes uploads before an upload, at
/// most once a minute for each destination. An older server (403, 404, 405,
/// or no <c>data.enabled</c>) gets no upload and no new jobs until
/// <see cref="RequestRecheck"/> or a restart; its queued jobs stay on disk.
/// A refused frame moves to the <c>refused</c> folder with the reason. A file
/// gone from disk drops its job. Every other failure retries after 5 seconds,
/// doubling to 10 minutes.
/// </para>
/// <para>Nothing here throws to a caller except cancellation and disposal.</para>
/// </remarks>
public sealed class DirectorUploadQueue : IAsyncDisposable
{
    public const string RefusedFolderName = "refused";
    public const string BlockedFolderName = "blocked";
    internal static readonly TimeSpan StateLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleWait = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string root;
    private readonly string refusedFolder;
    private readonly Func<DirectorUploadDestination, IDirectorUploadServer?> connect;
    private readonly Action<string> log;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly object stateGate = new();
    private readonly Dictionary<DirectorUploadDestination, (DirectorUploadState State, DateTimeOffset At)> states = [];
    private readonly HashSet<string> logged = [];
    private Task? worker;
    private int disposed;

    /// <param name="root">The queue folder.</param>
    /// <param name="connect">
    /// A server client for a destination, or null when the current pairing
    /// does not cover it; its jobs then wait.
    /// </param>
    /// <param name="log">Writes one line to the N.I.N.A. log at info level.</param>
    public DirectorUploadQueue(string root, Func<DirectorUploadDestination, IDirectorUploadServer?> connect,
        Action<string> log, TimeProvider? time = null)
    {
        this.root = Path.GetFullPath(root);
        refusedFolder = Path.Combine(this.root, RefusedFolderName);
        this.connect = connect;
        this.log = log;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a job is added, finished, refused or forgotten, or a server state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts the background worker. Safe to call more than once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        worker ??= Task.Run(() => RunAsync(stopping.Token));
        Wake();
    }

    /// <summary>
    /// Forget what the queue learned about every server, so the next upload
    /// asks again. Call it after pairing and after a server or profile change.
    /// </summary>
    public void RequestRecheck()
    {
        lock (stateGate) { states.Clear(); logged.Clear(); }
        Wake();
        RaiseChanged();
    }

    /// <summary>
    /// Queue a saved frame without blocking the caller. It skips the frame
    /// when the server is known to take no uploads, or the file is larger
    /// than the server takes. Never throws.
    /// </summary>
    public void Offer(DirectorUploadDestination destination, string imagePath, string? captureId)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await EnqueueAsync(destination, imagePath, captureId, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log($"Could not queue {SafeName(imagePath)} for upload ({error.GetType().Name})."); }
        });
    }

    /// <summary>Queue a saved frame. Returns false when the frame is not queued.</summary>
    internal async Task<bool> EnqueueAsync(DirectorUploadDestination destination, string imagePath, string? captureId,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var path = Path.GetFullPath(imagePath);
        var known = Cached(destination, fresh: true);
        if (known?.Kind is DirectorUploadStateKind.Unsupported or DirectorUploadStateKind.Disabled
            || Sticky(destination) is { Kind: DirectorUploadStateKind.Unsupported })
            return false;
        if (known?.MaxBytes is { } maximum && File.Exists(path) && new FileInfo(path).Length > maximum)
        {
            Log($"{SafeName(path)} is larger than PSF Guard takes ({maximum:N0} bytes); it was not queued.");
            return false;
        }
        var now = time.GetUtcNow();
        var job = new DirectorUploadJob
        {
            JobId = Guid.NewGuid(),
            Endpoint = destination.Endpoint.AbsoluteUri,
            ProfileId = destination.ProfileId,
            RigId = destination.RigId,
            AllowInsecureHttp = destination.AllowInsecureHttp,
            ImagePath = path,
            CaptureId = captureId,
            QueuedAt = now,
            NextAttemptAt = now,
        };
        Directory.CreateDirectory(root);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await WriteAsync(job, JobPath(job.JobId), token).ConfigureAwait(false); }
        finally { gate.Release(); }
        Wake();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Ask the server for its upload state now, if the cached answer is older
    /// than a minute. Used at startup and after pairing; never throws.
    /// </summary>
    public async Task<DirectorUploadState?> RefreshStateAsync(DirectorUploadDestination destination, CancellationToken token)
    {
        try { return await StateAsync(destination, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>The queue's counts and the last known state of <paramref name="destination"/>.</summary>
    public DirectorUploadSummary Summarize(DirectorUploadDestination? destination)
    {
        int Count(string folder)
        {
            try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Count() : 0; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
        }
        return new(Count(root), Count(refusedFolder), destination is null ? null : Cached(destination, fresh: false));
    }

    /// <summary>Move every refused job back into the queue. Returns how many moved.</summary>
    public async Task<int> RetryRefusedAsync(CancellationToken token)
    {
        var moved = 0;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var file in Files(refusedFolder))
            {
                var job = await ReadAsync(file, token).ConfigureAwait(false);
                if (job is null) continue;
                job.Attempts = 0;
                job.RefusalReason = null;
                job.LastError = null;
                job.NextAttemptAt = time.GetUtcNow();
                await WriteAsync(job, file, token).ConfigureAwait(false);
                File.Move(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
                moved++;
            }
        }
        finally { gate.Release(); }
        if (moved > 0) { Wake(); RaiseChanged(); }
        return moved;
    }

    /// <summary>Delete every refused job. The image files stay on disk. Returns how many were removed.</summary>
    public async Task<int> ForgetRefusedAsync(CancellationToken token)
    {
        var removed = 0;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var file in Files(refusedFolder))
            {
                token.ThrowIfCancellationRequested();
                File.Delete(file);
                removed++;
            }
        }
        finally { gate.Release(); }
        if (removed > 0) RaiseChanged();
        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        Wake();
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); }
            catch (Exception) { }
        }
        stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var wait = IdleWait;
            try { wait = await ProcessDueJobsAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { Log($"The upload worker met an error and carries on ({error.GetType().Name})."); }
            try { await signal.WaitAsync(wait, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass over the queue: uploads every due job whose server takes
    /// uploads, one at a time. Returns how long to wait before the next pass.
    /// </summary>
    internal async Task<TimeSpan> ProcessDueJobsAsync(CancellationToken token)
    {
        if (!Directory.Exists(root)) return IdleWait;
        var soonest = IdleWait;
        foreach (var file in Files(root))
        {
            token.ThrowIfCancellationRequested();
            DirectorUploadJob? job;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { job = await ReadAsync(file, token).ConfigureAwait(false); }
            finally { gate.Release(); }
            if (job is null) continue;
            var wait = job.NextAttemptAt - time.GetUtcNow();
            if (wait > TimeSpan.Zero) { soonest = Min(soonest, wait); continue; }
            var destination = job.Destination;
            var state = await StateAsync(destination, token).ConfigureAwait(false);
            if (state is null || state.Kind != DirectorUploadStateKind.Enabled)
            {
                // Off, unsupported, unpaired or unreachable: keep the job and
                // look again once the cached answer expires.
                soonest = Min(soonest, StateLifetime);
                continue;
            }
            if (!File.Exists(job.ImagePath))
            {
                await DeleteAsync(file, token).ConfigureAwait(false);
                Log($"{SafeName(job.ImagePath)} is no longer on disk, so it will not be uploaded.");
                RaiseChanged();
                continue;
            }
            if (state.MaxBytes is { } maximum && new FileInfo(job.ImagePath).Length > maximum)
            {
                await RefuseAsync(job, file, $"The file is larger than PSF Guard takes ({maximum:N0} bytes).", token).ConfigureAwait(false);
                continue;
            }
            DirectorUploadAnswer answer;
            try
            {
                using var server = connect(destination);
                if (server is null) { soonest = Min(soonest, StateLifetime); continue; }
                answer = await server.UploadAsync(job.ImagePath, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                await DeleteAsync(file, token).ConfigureAwait(false);
                Log($"{SafeName(job.ImagePath)} is no longer on disk, so it will not be uploaded.");
                RaiseChanged();
                continue;
            }
            catch (Exception error) { answer = new(DirectorUploadOutcome.Retry, error.GetType().Name); }

            switch (answer.Outcome)
            {
                case DirectorUploadOutcome.Stored:
                    await DeleteAsync(file, token).ConfigureAwait(false);
                    RaiseChanged();
                    break;
                case DirectorUploadOutcome.Refused:
                    await RefuseAsync(job, file, answer.Detail, token).ConfigureAwait(false);
                    break;
                case DirectorUploadOutcome.Paused:
                    // Keep the job; stop sending to this server until its state is read again.
                    Remember(destination, new(DirectorUploadStateKind.Disabled, "PSF Guard stopped taking uploads from this rig. The plugin checks again in a minute.", state.MaxBytes));
                    soonest = Min(soonest, StateLifetime);
                    break;
                case DirectorUploadOutcome.Unsupported:
                    Remember(destination, new(DirectorUploadStateKind.Unsupported));
                    break;
                default:
                    job.Attempts = job.Attempts == int.MaxValue ? job.Attempts : job.Attempts + 1;
                    job.LastError = answer.Detail;
                    var delay = DirectorUploadPolicy.RetryDelay(job.Attempts);
                    job.NextAttemptAt = time.GetUtcNow() + delay;
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try { await WriteAsync(job, file, token).ConfigureAwait(false); }
                    finally { gate.Release(); }
                    LogOnce($"retry:{destination}", $"An upload to PSF Guard failed ({answer.Detail}). The plugin keeps the frame and tries again.");
                    soonest = Min(soonest, delay);
                    break;
            }
        }
        return soonest < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : soonest;
    }

    private async Task<DirectorUploadState?> StateAsync(DirectorUploadDestination destination, CancellationToken token)
    {
        if (Sticky(destination) is { Kind: DirectorUploadStateKind.Unsupported } unsupported) return unsupported;
        if (Cached(destination, fresh: true) is { } cached) return cached;
        IDirectorUploadServer? server;
        try { server = connect(destination); }
        catch (Exception) { return null; }
        if (server is null) return null;
        using var owned = server;
        DirectorUploadState state;
        try { state = await server.GetStateAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { state = new(DirectorUploadStateKind.Unavailable, error.GetType().Name); }
        Remember(destination, state);
        return state;
    }

    private void Remember(DirectorUploadDestination destination, DirectorUploadState state)
    {
        DirectorUploadState? previous;
        lock (stateGate)
        {
            previous = states.TryGetValue(destination, out var entry) ? entry.State : null;
            states[destination] = (state, time.GetUtcNow());
        }
        if (state.Kind == DirectorUploadStateKind.Unsupported)
            LogOnce($"unsupported:{destination}", "This PSF Guard server does not take uploads from Director. Frames stay on disk and are not queued; the plugin asks again after pairing, a server or profile change, or a N.I.N.A. restart.");
        else if (state.Kind == DirectorUploadStateKind.Disabled && state.Reason is { } reason)
            LogOnce($"disabled:{destination}:{reason}", $"PSF Guard does not take uploads now: {reason}");
        if (previous != state) RaiseChanged();
    }

    private DirectorUploadState? Cached(DirectorUploadDestination destination, bool fresh)
    {
        lock (stateGate)
        {
            if (!states.TryGetValue(destination, out var entry)) return null;
            if (entry.State.Kind == DirectorUploadStateKind.Unsupported) return entry.State;
            return !fresh || time.GetUtcNow() - entry.At < StateLifetime ? entry.State : null;
        }
    }

    private DirectorUploadState? Sticky(DirectorUploadDestination destination)
    {
        lock (stateGate)
            return states.TryGetValue(destination, out var entry) && entry.State.Kind == DirectorUploadStateKind.Unsupported ? entry.State : null;
    }

    private async Task RefuseAsync(DirectorUploadJob job, string file, string reason, CancellationToken token)
    {
        job.RefusalReason = reason;
        job.LastError = null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteAsync(job, file, token).ConfigureAwait(false);
            Directory.CreateDirectory(refusedFolder);
            File.Move(file, Path.Combine(refusedFolder, Path.GetFileName(file)), overwrite: true);
        }
        finally { gate.Release(); }
        Log($"PSF Guard refused {SafeName(job.ImagePath)}: {reason} It is held under Refused uploads.");
        RaiseChanged();
    }

    private async Task DeleteAsync(string file, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { File.Delete(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { gate.Release(); }
    }

    private async Task<DirectorUploadJob?> ReadAsync(string file, CancellationToken token)
    {
        try
        {
            var job = JsonSerializer.Deserialize<DirectorUploadJob>(await File.ReadAllBytesAsync(file, token).ConfigureAwait(false), JsonOptions);
            if (job is null || job.Version != 1 || job.JobId == Guid.Empty || job.ProfileId == Guid.Empty || job.RigId == Guid.Empty
                || !Uri.TryCreate(job.Endpoint, UriKind.Absolute, out _) || !Path.IsPathFullyQualified(job.ImagePath))
                throw new InvalidDataException("Invalid upload job.");
            return job;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (FileNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            // An unreadable job never blocks the others.
            try
            {
                var blocked = Path.Combine(root, BlockedFolderName);
                Directory.CreateDirectory(blocked);
                File.Move(file, Path.Combine(blocked, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { }
            Log($"Set aside an unreadable upload job ({error.GetType().Name}).");
            return null;
        }
    }

    private static async Task WriteAsync(DirectorUploadJob job, string path, CancellationToken token)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions), token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string[] Files(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).ToArray()
        : [];

    private string JobPath(Guid id) => Path.Combine(root, $"{id:N}.json");

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string SafeName(string path)
    {
        try { return Path.GetFileName(path); }
        catch (ArgumentException) { return "a frame"; }
    }

    private void LogOnce(string key, string message)
    {
        lock (stateGate) { if (!logged.Add(key)) return; }
        Log(message);
    }

    private void Log(string message)
    {
        try { log(message); }
        catch (Exception) { }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { }
    }

    private void Wake()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
}
