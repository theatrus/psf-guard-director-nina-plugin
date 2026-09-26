namespace PsfGuard.Director.Runtime;

public enum RuntimeState { Stopped, Starting, Ready, Stopping, Faulted }
public sealed record RuntimeStatus(RuntimeState State, string Message, string? RigId = null, Exception? Error = null);

public sealed class RuntimeController : IAsyncDisposable
{
    private readonly string pluginDirectory;
    private readonly string? storageDirectory;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object cancellationLock = new();
    private readonly TimeSpan heartbeatInterval;
    private CancellationTokenSource? runCancellation;
    private Task? run;
    private RuntimeSession? activeSession;
    private RuntimeStatus status = new(RuntimeState.Stopped, "Stopped");
    private bool disposed;
    public RuntimeStatus Status => Volatile.Read(ref status);
    public event EventHandler? StateChanged;

    public RuntimeController(string pluginDirectory) : this(pluginDirectory, TimeSpan.FromSeconds(5)) { }
    public RuntimeController(string pluginDirectory, string storageDirectory) : this(pluginDirectory, TimeSpan.FromSeconds(5), storageDirectory) { }
    internal RuntimeController(string pluginDirectory, TimeSpan heartbeatInterval, string? storageDirectory = null)
    {
        this.pluginDirectory = pluginDirectory;
        this.heartbeatInterval = heartbeatInterval;
        this.storageDirectory = RuntimeSession.NormalizeStorageDirectory(storageDirectory);
    }

    public async Task StartAsync(string rigId, CancellationToken token = default)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Status.State == RuntimeState.Ready && Status.RigId == rigId) return;
            await EndRunAsync().ConfigureAwait(false);
            CancellationTokenSource cancellation;
            lock (cancellationLock)
            {
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                runCancellation = cancellation;
            }
            SetStatus(new(RuntimeState.Starting, "Starting runtime", rigId));
            try
            {
                var session = await RuntimeSession.StartAsync(pluginDirectory, rigId, cancellation.Token, storageDirectory).ConfigureAwait(false);
                activeSession = session;
                SetStatus(new(RuntimeState.Ready, "Ready", rigId));
                run = SuperviseAsync(session, rigId, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                SetStatus(new(RuntimeState.Stopped, "Stopped"));
                await EndRunAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                SetStatus(new(RuntimeState.Faulted, Describe(error), rigId, error));
                await EndRunAsync().ConfigureAwait(false);
            }
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        // Cancellation precedes the lifecycle gate so a starting child cannot
        // hold up a profile change for the entire handshake timeout.
        CancelRun();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            SetStatus(new(RuntimeState.Stopping, "Stopping runtime", Status.RigId));
            await EndRunAsync().ConfigureAwait(false);
            SetStatus(new(RuntimeState.Stopped, "Stopped"));
        }
        finally { lifecycle.Release(); }
    }

    public Task<PlannerEvaluation> EvaluateAsync(PlannerRequest request, CancellationToken token = default) =>
        RunRequestAsync((session, linked) => session.EvaluateAsync(request, linked), token);
    public Task<LedgerResult<LedgerIdentity>> OpenLedgerAsync(PlannerRequest request, CancellationToken token = default) =>
        RunRequestAsync((session, linked) => session.OpenLedgerAsync(request, linked), token);
    public Task<LedgerResult<LedgerReservation>> ReserveAsync(string captureId, PlannerState state, CancellationToken token = default) =>
        RunRequestAsync((session, linked) => session.ReserveAsync(captureId, state, linked), token);
    public Task<LedgerResult<LedgerAttempt>> RecordAsync(string captureId, LedgerEvidence evidence, CancellationToken token = default) =>
        RunRequestAsync((session, linked) => session.RecordAsync(captureId, evidence, linked), token);
    public Task<LedgerResult<LedgerLookup>> FindAttemptAsync(string captureId, CancellationToken token = default) =>
        RunRequestAsync((session, linked) => session.FindAttemptAsync(captureId, linked), token);
    public Task<LedgerResult<LedgerEventPage>> ReadEventsAsync(ulong after, int limit = 64, CancellationToken token = default) =>
        RunRequestAsync((session, linked) => session.ReadEventsAsync(after, limit, linked), token);

    private async Task<T> RunRequestAsync<T>(Func<RuntimeSession, CancellationToken, Task<T>> request, CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var session = activeSession;
            if (Status.State != RuntimeState.Ready || session is null || !session.IsReady)
                throw new IOException("Director runtime is not ready for requests.");
            CancellationToken runToken;
            lock (cancellationLock) runToken = runCancellation!.Token;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, runToken);
            try
            {
                var result = await request(session, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (!session.IsReady || Status.State != RuntimeState.Ready)
                    throw new IOException("Director runtime ended during the request.");
                return result;
            }
            catch (Exception error)
            {
                if (!session.IsReady)
                {
                    SetStatus(new(RuntimeState.Faulted, Describe(error), Status.RigId, error));
                    CancelRun();
                }
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    private async Task SuperviseAsync(RuntimeSession session, string rigId, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(heartbeatInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await session.PingAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { SetStatus(new(RuntimeState.Faulted, Describe(error), rigId, error)); }
        finally
        {
            try
            {
                if (session.IsReady)
                    await session.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) { SetStatus(new(RuntimeState.Faulted, Describe(error), rigId, error)); }
            finally
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    SetStatus(new(RuntimeState.Faulted, Describe(error), rigId, error));
                    // Cleanup failure must remain observable to Stop/Start;
                    // it must not be reported as a successfully stopped child.
                    throw;
                }
            }
            if (Status.State == RuntimeState.Ready)
                SetStatus(new(RuntimeState.Stopped, "Stopped"));
        }
    }

    private void CancelRun()
    {
        lock (cancellationLock) runCancellation?.Cancel();
    }

    private async Task EndRunAsync()
    {
        CancelRun();
        if (run is not null) await run.ConfigureAwait(false);
        run = null;
        activeSession = null;
        lock (cancellationLock)
        {
            runCancellation?.Dispose();
            runCancellation = null;
        }
    }

    private void SetStatus(RuntimeStatus value)
    {
        Volatile.Write(ref status, value);
        // A UI observer must not interrupt ownership/cleanup of the child.
        if (StateChanged is not { } observers) return;
        foreach (EventHandler observer in observers.GetInvocationList())
        {
            try { observer(this, EventArgs.Empty); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Director state observer failed: {0}", error.GetType().Name);
            }
        }
    }

    private static string Describe(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "Runtime missing. Reinstall the Director bundle.",
        InvalidDataException => "Runtime verification or protocol failed. Reinstall the matching Director bundle.",
        OperationCanceledException => "Runtime timed out. Stop and start it to retry.",
        _ => "Runtime connection failed. Stop and start it to retry."
    };

    public async ValueTask DisposeAsync()
    {
        CancelRun();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await EndRunAsync().ConfigureAwait(false);
            SetStatus(new(RuntimeState.Stopped, "Stopped"));
        }
        finally { lifecycle.Release(); }
    }
}
