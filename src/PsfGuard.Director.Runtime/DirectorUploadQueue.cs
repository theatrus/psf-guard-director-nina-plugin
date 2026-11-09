using System.Text.Json;

namespace PsfGuard.Director.Runtime;

/// <summary>One captured frame waiting to upload. It names the saved file; it holds no copy.</summary>
internal sealed record DirectorUploadJob
{
    public int Version { get; init; } = 2;
    public required Guid JobId { get; init; }
    public required string Endpoint { get; init; }
    public required Guid ProfileId { get; init; }
    public required Guid RigId { get; init; }
    public required Guid CoordinatorInstanceId { get; init; }
    public required Guid CatalogId { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public required string ImagePath { get; init; }
    public string? CaptureId { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? RefusalReason { get; set; }

    internal DirectorUploadDestination Destination => new(new Uri(Endpoint), new(CoordinatorInstanceId, CatalogId, RigId, ProfileId), AllowInsecureHttp);
}

/// <summary>How the queue reaches the server for a destination.</summary>
public interface IDirectorUploadConnector
{
    /// <summary>
    /// An identity for the pairing that covers <paramref name="destination"/>
    /// now, or null when none does: the job is for an earlier pairing. A new
    /// identity makes the queue build a new client.
    /// </summary>
    string? PairingKey(DirectorUploadDestination destination);

    /// <summary>A client for the destination's current pairing.</summary>
    IDirectorUploadServer Connect(DirectorUploadDestination destination);
}

public sealed class DirectorUploadConnector(
    Func<DirectorUploadDestination, string?> pairingKey,
    Func<DirectorUploadDestination, IDirectorUploadServer> connect) : IDirectorUploadConnector
{
    public string? PairingKey(DirectorUploadDestination destination) => pairingKey(destination);
    public IDirectorUploadServer Connect(DirectorUploadDestination destination) => connect(destination);
}

/// <summary>
/// The settings page's view of the queue. <see cref="EarlierPairing"/> counts
/// frames whose rig or server no current pairing covers; they are not queued.
/// </summary>
public sealed record DirectorUploadSummary(int Queued, int EarlierPairing, int Refused, DirectorUploadState? State);

/// <summary>
/// The Director plugin's durable upload queue. Each saved frame is one small
/// job file below <c>root</c> naming the file, its destination, attempts and
/// next try, so the queue survives restarts. The queue keeps an index of the
/// job files in memory, read from disk when it starts. One background worker
/// uploads one frame at a time and never runs on N.I.N.A.'s UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Every offered frame is written to disk, whatever the queue knows about
/// the server; only the worker decides when to send. The one exception is a
/// frame the server's known <c>max_bytes</c> cannot hold, which goes straight
/// to the <c>refused</c> folder.
/// </para>
/// <para>
/// Before uploading to a destination the worker reads its upload state, at
/// most once a minute, or once every 10 minutes after the server answered
/// 404, 405 or not with an upload state. A 401 or 403 on an upload
/// pauses that destination for a minute. A retry, after 503, 429, a server
/// error, a timeout or a lost connection, holds every job for the destination
/// until the server's Retry-After, or else 5 seconds doubling to 10 minutes,
/// has passed. A refused frame moves to <c>refused</c> with the reason; a
/// file gone from disk drops its job.
/// </para>
/// <para>
/// The queue can stop and start again; a frame offered while it is stopped
/// is still written to disk. Nothing here throws to a caller except
/// cancellation.
/// </para>
/// </remarks>
public sealed class DirectorUploadQueue : IAsyncDisposable
{
    public const string RefusedFolderName = "refused";
    public const string BlockedFolderName = "blocked";
    internal static readonly TimeSpan StateLifetime = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan UnsupportedLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan PauseLength = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleWait = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private sealed record Entry(string File, DirectorUploadJob Job);

    private sealed class Link
    {
        public DirectorUploadState? State;
        public DateTimeOffset CheckedAt;
        public DateTimeOffset HoldUntil;
        public string? Key;
        public IDirectorUploadServer? Server;
        public bool FailureLogged;
    }

    private readonly string root;
    private readonly string refusedFolder;
    private readonly IDirectorUploadConnector connector;
    private readonly Action<string> log;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim diskGate = new(1, 1);
    private readonly SemaphoreSlim serverGate = new(1, 1);
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly object indexLock = new();
    private readonly object stateLock = new();
    private readonly object lifeLock = new();
    private readonly Dictionary<Guid, Entry> jobs = [];
    private readonly Dictionary<Guid, Entry> refused = [];
    private readonly Dictionary<DirectorUploadDestination, Link> links = [];
    private readonly object routingGate = new();
    private DirectorUploadDestination? enabledDestination;
    private CancellationTokenSource transferLifetime = new();
    private bool loaded;
    private CancellationTokenSource? running;
    private Task? worker;

    /// <param name="root">The queue folder.</param>
    /// <param name="log">Writes one line to the N.I.N.A. log at info level.</param>
    public DirectorUploadQueue(string root, IDirectorUploadConnector connector, Action<string> log, TimeProvider? time = null)
    {
        this.root = Path.GetFullPath(root);
        refusedFolder = Path.Combine(this.root, RefusedFolderName);
        this.connector = connector;
        this.log = log;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a job is added, finished, refused or forgotten, or a server's state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Enable transfers for this exact active profile and pairing, or pause all
    /// transfers with null (the default). Pausing cancels a request in progress;
    /// its job stays queued until a valid server receipt confirms the upload.
    /// </summary>
    public void SetDestination(DirectorUploadDestination? destination)
    {
        CancellationTokenSource previous;
        lock (routingGate)
        {
            if (enabledDestination == destination) return;
            enabledDestination = destination;
            previous = transferLifetime;
            transferLifetime = new();
        }
        previous.Cancel();
        previous.Dispose();
        Wake();
        RaiseChanged();
    }

    private CancellationTokenSource? TransferPermit(DirectorUploadDestination destination, CancellationToken token)
    {
        lock (routingGate)
            return enabledDestination == destination
                ? CancellationTokenSource.CreateLinkedTokenSource(token, transferLifetime.Token) : null;
    }



    /// <summary>
    /// Read the job files from disk again and start the background worker.
    /// After <see cref="StopAsync"/> it starts again. Safe to call more than once.
    /// </summary>
    public void Start()
    {
        lock (lifeLock)
        {
            if (running is not null) return;
            running = new CancellationTokenSource();
            var token = running.Token;
            lock (indexLock) loaded = false;
            worker = Task.Run(() => RunAsync(token));
        }
        Wake();
    }

    /// <summary>Stop the worker and close its connections. Queued frames stay on disk.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? stopping;
        Task? stopped;
        lock (lifeLock)
        {
            stopping = running;
            stopped = worker;
            running = null;
            worker = null;
        }
        if (stopping is null) return;
        stopping.Cancel();
        Wake();
        try { if (stopped is not null) await stopped.ConfigureAwait(false); }
        catch (Exception) { }
        stopping.Dispose();
        await serverGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (stateLock)
                foreach (var link in links.Values)
                {
                    DisposeQuietly(link.Server);
                    link.Server = null;
                    link.Key = null;
                }
        }
        finally { serverGate.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Forget what the queue learned about every server and lift every pause,
    /// so the next pass asks again. Call it after pairing and after a server
    /// or profile change.
    /// </summary>
    public void RequestRecheck()
    {
        lock (stateLock)
            foreach (var link in links.Values)
            {
                // Keep the last answer to show and compare with; it is stale now.
                link.CheckedAt = DateTimeOffset.MinValue;
                link.HoldUntil = DateTimeOffset.MinValue;
            }
        Wake();
        RaiseChanged();
    }

    /// <summary>Queue a saved frame on a pool thread. Never blocks and never throws.</summary>
    public void Offer(DirectorUploadDestination destination, string imagePath, string? captureId) =>
        _ = Task.Run(async () =>
        {
            try { await EnqueueAsync(destination, imagePath, captureId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { Log($"Could not queue {SafeName(imagePath)} for upload ({error.GetType().Name})."); }
        });

    /// <summary>
    /// Write a job for a saved frame. Returns false when the frame went
    /// straight to <c>refused</c> because it is larger than the server takes.
    /// </summary>
    internal async Task<bool> EnqueueAsync(DirectorUploadDestination destination, string imagePath, string? captureId,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var permit = TransferPermit(destination, token);
        if (permit is null) return false;
        token = permit.Token;
        var path = Path.GetFullPath(imagePath);
        EnsureLoaded();
        var now = time.GetUtcNow();
        var job = new DirectorUploadJob
        {
            JobId = Guid.NewGuid(),
            Endpoint = destination.Endpoint.AbsoluteUri,
            ProfileId = destination.ProfileId,
            RigId = destination.RigId,
            CoordinatorInstanceId = destination.Binding.CoordinatorInstanceId,
            CatalogId = destination.Binding.CatalogId,
            AllowInsecureHttp = destination.AllowInsecureHttp,
            ImagePath = path,
            CaptureId = captureId,
            QueuedAt = now,
            NextAttemptAt = now,
        };
        long? maximum;
        lock (stateLock) maximum = links.TryGetValue(destination, out var link) ? link.State?.MaxBytes : null;
        if (maximum is { } max && TooLarge(path, max))
        {
            job.RefusalReason = TooLargeReason(max);
            var held = Path.Combine(refusedFolder, $"{job.JobId:N}.json");
            await diskGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(refusedFolder);
                await WriteAsync(job, held, token).ConfigureAwait(false);
                lock (indexLock) refused[job.JobId] = new(held, job);
            }
            finally { diskGate.Release(); }
            Log($"{SafeName(path)} is larger than PSF Guard takes; it is held under Refused uploads.");
            RaiseChanged();
            return false;
        }
        var file = Path.Combine(root, $"{job.JobId:N}.json");
        await diskGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(root);
            await WriteAsync(job, file, token).ConfigureAwait(false);
            lock (indexLock) jobs[job.JobId] = new(file, job);
        }
        finally { diskGate.Release(); }
        Wake();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Ask the server for its upload state now unless the last answer is
    /// still current. For startup and pairing; never throws.
    /// </summary>
    public async Task<DirectorUploadState?> RefreshStateAsync(DirectorUploadDestination destination, CancellationToken token)
    {
        using var permit = TransferPermit(destination, token);
        if (permit is null) return null;
        var callerToken = token;
        token = permit.Token;
        try
        {
            await serverGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var server = ServerFor(destination);
                return server is null ? null : await StateAsync(destination, server, token).ConfigureAwait(false);
            }
            finally { serverGate.Release(); }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The queue's counts and the last known state of
    /// <paramref name="destination"/>. It reads pairings, so call it off the
    /// UI thread.
    /// </summary>
    public DirectorUploadSummary Summarize(DirectorUploadDestination? destination)
    {
        EnsureLoaded();
        Entry[] waiting;
        int held;
        lock (indexLock) { waiting = [.. jobs.Values.Where(e => e.Job.Destination == destination)]; held = refused.Values.Count(e => e.Job.Destination == destination); }
        var queued = 0;
        var earlier = 0;
        foreach (var group in waiting.GroupBy(entry => entry.Job.Destination))
        {
            if (Covered(group.Key)) queued += group.Count();
            else earlier += group.Count();
        }
        DirectorUploadState? state = null;
        if (destination is not null)
            lock (stateLock) state = links.TryGetValue(destination, out var link) ? link.State : null;
        return new(queued, earlier, held, state);
    }

    /// <summary>Move every refused job back into the queue. Returns how many moved.</summary>
    public async Task<int> RetryRefusedAsync(DirectorUploadDestination? destination, CancellationToken token)
    {
        EnsureLoaded();
        var moved = 0;
        await diskGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Entry[] held;
            lock (indexLock) held = [.. refused.Values.Where(e => e.Job.Destination == destination)];
            foreach (var entry in held)
            {
                var job = entry.Job with { Attempts = 0, RefusalReason = null, LastError = null, NextAttemptAt = time.GetUtcNow() };
                var file = Path.Combine(root, Path.GetFileName(entry.File));
                try
                {
                    await WriteAsync(job, entry.File, token).ConfigureAwait(false);
                    Directory.CreateDirectory(root);
                    File.Move(entry.File, file, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                lock (indexLock)
                {
                    refused.Remove(job.JobId);
                    jobs[job.JobId] = new(file, job);
                }
                moved++;
            }
        }
        finally { diskGate.Release(); }
        if (moved > 0) { Wake(); RaiseChanged(); }
        return moved;
    }

    /// <summary>
    /// Delete every refused job and every job for an earlier pairing. The
    /// image files stay on disk. Returns how many jobs were removed.
    /// </summary>
    public async Task<int> ForgetHeldAsync(DirectorUploadDestination? destination, CancellationToken token)
    {
        EnsureLoaded();
        Entry[] held, waiting;
        lock (indexLock) { held = [.. refused.Values.Where(e => e.Job.Destination == destination)]; waiting = [.. jobs.Values.Where(e => e.Job.Destination == destination)]; }
        var orphans = waiting.GroupBy(entry => entry.Job.Destination).Where(group => !Covered(group.Key))
            .SelectMany(group => group).ToArray();
        var removed = 0;
        await diskGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var entry in held.Concat(orphans))
            {
                token.ThrowIfCancellationRequested();
                try { File.Delete(entry.File); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                lock (indexLock) { refused.Remove(entry.Job.JobId); jobs.Remove(entry.Job.JobId); }
                removed++;
            }
        }
        finally { diskGate.Release(); }
        if (removed > 0) RaiseChanged();
        return removed;
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
    /// One pass: for each destination that a pairing covers and whose server
    /// takes uploads, upload its due jobs one at a time until one does not
    /// succeed. Returns how long to wait before the next pass.
    /// </summary>
    internal async Task<TimeSpan> ProcessDueJobsAsync(CancellationToken token)
    {
        EnsureLoaded();
        Entry[] waiting;
        lock (indexLock) waiting = [.. jobs.Values];
        var soonest = IdleWait;
        await serverGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var group in waiting.GroupBy(entry => entry.Job.Destination))
            {
                using var permit = TransferPermit(group.Key, token);
                if (permit is null) continue;
                try
                {
                    var wait = await ProcessDestinationAsync(group.Key,
                        group.OrderBy(entry => entry.Job.QueuedAt).ThenBy(entry => entry.Job.JobId), permit.Token).ConfigureAwait(false);
                    if (wait < soonest) soonest = wait;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        finally { serverGate.Release(); }
        return soonest < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : soonest;
    }

    private async Task<TimeSpan> ProcessDestinationAsync(DirectorUploadDestination destination, IEnumerable<Entry> entries,
        CancellationToken token)
    {
        var link = LinkFor(destination);
        DateTimeOffset holdUntil;
        lock (stateLock) holdUntil = link.HoldUntil;
        var held = holdUntil - time.GetUtcNow();
        if (held > TimeSpan.Zero) return held;
        var server = ServerFor(destination);
        if (server is null) return IdleWait;
        var state = await StateAsync(destination, server, token).ConfigureAwait(false);
        if (state.Kind != DirectorUploadStateKind.Enabled)
        {
            DateTimeOffset checkedAt;
            lock (stateLock) checkedAt = link.CheckedAt;
            return checkedAt + Lifetime(state) - time.GetUtcNow();
        }
        var soonest = IdleWait;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var job = entry.Job;
            var due = job.NextAttemptAt - time.GetUtcNow();
            if (due > TimeSpan.Zero) { soonest = Min(soonest, due); continue; }
            if (!File.Exists(job.ImagePath))
            {
                await DropAsync(entry, token).ConfigureAwait(false);
                continue;
            }
            if (state.MaxBytes is { } maximum && TooLarge(job.ImagePath, maximum))
            {
                await RefuseAsync(entry, TooLargeReason(maximum), token).ConfigureAwait(false);
                continue;
            }
            DirectorUploadAnswer answer;
            try { answer = await server.UploadAsync(job.ImagePath, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                await DropAsync(entry, token).ConfigureAwait(false);
                continue;
            }
            catch (Exception error) { answer = new(DirectorUploadOutcome.Retry, error.GetType().Name); }

            switch (answer.Outcome)
            {
                case DirectorUploadOutcome.Stored:
                    await DeleteAsync(entry, token).ConfigureAwait(false);
                    RaiseChanged();
                    continue;
                case DirectorUploadOutcome.Refused:
                    await RefuseAsync(entry, answer.Detail, token).ConfigureAwait(false);
                    continue;
                case DirectorUploadOutcome.Paused:
                    // Frames still queue; this destination gets no upload for a minute.
                    Remember(destination, new(DirectorUploadStateKind.Disabled,
                        $"PSF Guard paused uploads from this rig ({answer.Detail}). Director checks again in a minute.", state.MaxBytes));
                    lock (stateLock) link.HoldUntil = time.GetUtcNow() + PauseLength;
                    return PauseLength;
                case DirectorUploadOutcome.Unsupported:
                    Remember(destination, new(DirectorUploadStateKind.Unsupported, answer.Detail));
                    return UnsupportedLifetime;
                default:
                    job.Attempts = job.Attempts == int.MaxValue ? job.Attempts : job.Attempts + 1;
                    job.LastError = answer.Detail;
                    var delay = DirectorUploadPolicy.RetryDelay(job.Attempts, answer.RetryAfter);
                    job.NextAttemptAt = time.GetUtcNow() + delay;
                    await diskGate.WaitAsync(token).ConfigureAwait(false);
                    try { await WriteAsync(job, entry.File, token).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    finally { diskGate.Release(); }
                    // The whole destination waits, not just this frame.
                    bool first;
                    lock (stateLock)
                    {
                        link.HoldUntil = job.NextAttemptAt;
                        first = !link.FailureLogged;
                        link.FailureLogged = true;
                    }
                    if (first) Log($"An upload to PSF Guard failed ({answer.Detail}). Director keeps the frames and tries again.");
                    return delay;
            }
        }
        return soonest;
    }

    private async Task<DirectorUploadState> StateAsync(DirectorUploadDestination destination, IDirectorUploadServer server,
        CancellationToken token)
    {
        var link = LinkFor(destination);
        lock (stateLock)
            if (link.State is { } known && time.GetUtcNow() - link.CheckedAt < Lifetime(known)) return known;
        DirectorUploadState state;
        try { state = await server.GetStateAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { state = new(DirectorUploadStateKind.Unavailable, error.GetType().Name); }
        Remember(destination, state);
        return state;
    }

    /// <summary>Store a server's state and log it once when it changes.</summary>
    private void Remember(DirectorUploadDestination destination, DirectorUploadState state)
    {
        var link = LinkFor(destination);
        DirectorUploadState? previous;
        lock (stateLock)
        {
            previous = link.State;
            link.State = state;
            link.CheckedAt = time.GetUtcNow();
        }
        if (previous?.Kind == state.Kind && previous.Reason == state.Reason) return;
        // A first answer that uploads are on needs no line.
        if (previous is not null || state.Kind != DirectorUploadStateKind.Enabled)
            Log(state.Kind switch
            {
                DirectorUploadStateKind.Enabled => "PSF Guard takes uploads from this rig again.",
                DirectorUploadStateKind.Disabled => $"PSF Guard does not take uploads now: {state.Reason}",
                DirectorUploadStateKind.Unsupported => $"PSF Guard does not take uploads from Director now ({state.Reason}). "
                    + "Frames stay queued; Director asks again every 10 minutes and after pairing.",
                _ => $"PSF Guard could not be reached ({state.Reason}). Frames stay queued.",
            });
        RaiseChanged();
    }

    private static TimeSpan Lifetime(DirectorUploadState state) =>
        state.Kind == DirectorUploadStateKind.Unsupported ? UnsupportedLifetime : StateLifetime;

    private Link LinkFor(DirectorUploadDestination destination)
    {
        lock (stateLock)
        {
            if (!links.TryGetValue(destination, out var link)) links[destination] = link = new Link();
            return link;
        }
    }

    /// <summary>
    /// The cached client for the destination's current pairing, rebuilt when
    /// the pairing changes; null when no pairing covers it. Call while
    /// holding <see cref="serverGate"/>.
    /// </summary>
    private IDirectorUploadServer? ServerFor(DirectorUploadDestination destination)
    {
        string? key;
        try { key = connector.PairingKey(destination); }
        catch (Exception) { return null; }
        var link = LinkFor(destination);
        IDirectorUploadServer? current;
        string? currentKey;
        lock (stateLock) { current = link.Server; currentKey = link.Key; }
        if (key is not null && current is not null && currentKey == key) return current;
        DisposeQuietly(current);
        IDirectorUploadServer? server = null;
        if (key is not null)
        {
            try { server = connector.Connect(destination); }
            catch (Exception) { server = null; }
        }
        lock (stateLock) { link.Server = server; link.Key = server is null ? null : key; }
        return server;
    }

    private bool Covered(DirectorUploadDestination destination)
    {
        try { return connector.PairingKey(destination) is not null; }
        // An unreadable credential is not proof of an earlier pairing.
        catch (Exception) { return true; }
    }

    /// <summary>Read the job files once after each start; later changes update the index directly.</summary>
    private void EnsureLoaded()
    {
        lock (indexLock) if (loaded) return;
        diskGate.Wait();
        try
        {
            lock (indexLock) if (loaded) return;
            var found = new List<Entry>();
            var held = new List<Entry>();
            foreach (var file in Files(root)) if (Read(file) is { } job) found.Add(new(file, job));
            foreach (var file in Files(refusedFolder)) if (Read(file) is { } job) held.Add(new(file, job));
            lock (indexLock)
            {
                jobs.Clear();
                refused.Clear();
                foreach (var entry in found) jobs[entry.Job.JobId] = entry;
                foreach (var entry in held) refused[entry.Job.JobId] = entry;
                loaded = true;
            }
        }
        finally { diskGate.Release(); }
    }

    private async Task RefuseAsync(Entry entry, string reason, CancellationToken token)
    {
        var job = entry.Job with { RefusalReason = reason, LastError = null };
        var held = Path.Combine(refusedFolder, Path.GetFileName(entry.File));
        await diskGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteAsync(job, entry.File, token).ConfigureAwait(false);
            Directory.CreateDirectory(refusedFolder);
            File.Move(entry.File, held, overwrite: true);
            lock (indexLock) { jobs.Remove(job.JobId); refused[job.JobId] = new(held, job); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        finally { diskGate.Release(); }
        Log($"PSF Guard refused {SafeName(job.ImagePath)}: {reason} It is held under Refused uploads.");
        RaiseChanged();
    }

    private async Task DropAsync(Entry entry, CancellationToken token)
    {
        await DeleteAsync(entry, token).ConfigureAwait(false);
        Log($"{SafeName(entry.Job.ImagePath)} is no longer on disk, so it will not be uploaded.");
        RaiseChanged();
    }

    private async Task DeleteAsync(Entry entry, CancellationToken token)
    {
        await diskGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            File.Delete(entry.File);
            lock (indexLock) jobs.Remove(entry.Job.JobId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { diskGate.Release(); }
    }

    private DirectorUploadJob? Read(string file)
    {
        try
        {
            var job = JsonSerializer.Deserialize<DirectorUploadJob>(File.ReadAllBytes(file), JsonOptions);
            if (job is null || job.Version != 2 || job.CoordinatorInstanceId == Guid.Empty || job.CatalogId == Guid.Empty || job.JobId == Guid.Empty || job.ProfileId == Guid.Empty || job.RigId == Guid.Empty
                || !Uri.TryCreate(job.Endpoint, UriKind.Absolute, out _) || !Path.IsPathFullyQualified(job.ImagePath)
                || !string.Equals(Path.GetFileNameWithoutExtension(file), job.JobId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid upload job.");
            return job;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NotSupportedException or ArgumentException)
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

    private static bool TooLarge(string path, long maximum)
    {
        // max_bytes is the largest image file the server takes; the request's own overhead is the server's concern.
        try { return File.Exists(path) && new FileInfo(path).Length > maximum; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string TooLargeReason(long maximum) =>
        $"The file is larger than PSF Guard takes ({maximum:N0} bytes).";

    private static string[] Files(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string SafeName(string path)
    {
        try { return Path.GetFileName(path); }
        catch (ArgumentException) { return "a frame"; }
    }

    private static void DisposeQuietly(IDisposable? value)
    {
        try { value?.Dispose(); }
        catch (Exception) { }
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
