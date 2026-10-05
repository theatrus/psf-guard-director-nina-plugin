namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorRunCheckInProgress(int Runs, int DeliveredEvents, int ReleasedWorkloads,
    string RunId, ulong AcknowledgedThrough, bool CaughtUp, ulong OperationsAcknowledgedThrough = 0);

/// <summary>Reopens original ledgers to deliver evidence. No equipment or allocation-start operations.</summary>
public static class CoordinatorRunCheckIn
{
    public static async Task<CoordinatorRunCheckInProgress> DeliverAsync(string stateRoot, string pluginDirectory,
        Uri endpoint, CoordinatorBinding binding, Guid clientId, Func<CancellationToken, ValueTask<string?>> credential,
        IProgress<CoordinatorRunCheckInProgress>? progress = null, bool allowInsecureHttp = false, CancellationToken token = default)
    {
        using var lease = new AcquisitionLease(stateRoot);
        return await DeliverBeforeAcquisitionAsync(lease, stateRoot, pluginDirectory, endpoint, binding, clientId,
            credential, progress, allowInsecureHttp, token).ConfigureAwait(false);
    }

    internal static async Task<CoordinatorRunCheckInProgress> DeliverBeforeAcquisitionAsync(AcquisitionLease lease,
        string stateRoot, string pluginDirectory, Uri endpoint, CoordinatorBinding binding, Guid clientId,
        Func<CancellationToken, ValueTask<string?>> credential, IProgress<CoordinatorRunCheckInProgress>? progress = null,
        bool allowInsecureHttp = false, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        using var replay = lease.EnterReplay(stateRoot);
        CoordinatorCheckpointClient.ValidateBinding(binding);
        if (clientId == Guid.Empty) throw new ArgumentException("A paired client is required.");
        ArgumentNullException.ThrowIfNull(credential);
        // The caller retains ownership throughout replay. Once native execution
        // starts this path is closed; active feeds use their existing sidecar.
        var profileRoot = Path.Combine(stateRoot, binding.ProfileId.ToString("N"));
        var result = new CoordinatorRunCheckInProgress(0, 0, 0, "", 0, true);
        if (!Directory.Exists(profileRoot)) return result;
        RefuseLink(profileRoot);
        foreach (var directory in Directory.EnumerateDirectories(profileRoot).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            lease.RequireIdle(stateRoot);
            var runId = Path.GetFileName(directory);
            if (!Guid.TryParseExact(runId, "N", out _)) continue;
            RefuseLink(directory);
            var archive = new CoordinatorRunArchive(directory, endpoint, binding, clientId, allowInsecureHttp);
            var run = archive.Read();
            if (run is null) continue; // Other pairings and pre-archive runs are never guessed.
            var ledgerRoot = Path.Combine(directory, "ledger");
            var ledgerFile = Path.Combine(ledgerRoot, "execution.sqlite");
            if (!File.Exists(ledgerFile)) throw new InvalidDataException("Saved Director ledger is missing; check-in cannot recreate it.");
            RefuseLink(ledgerRoot); RefuseLink(ledgerFile);
            if (new FileInfo(ledgerFile).Length < 100) throw new InvalidDataException("Saved Director ledger is empty or truncated.");
            result = result with { RunId = runId, AcknowledgedThrough = 0, OperationsAcknowledgedThrough = 0, CaughtUp = false };
            progress?.Report(result);
            await using var runtime = new RuntimeController(pluginDirectory, ledgerRoot);
            await runtime.StartAsync(binding.RigId.ToString("D"), token).ConfigureAwait(false);
            var identity = Require(await runtime.OpenGeometryAsync(run.Allocation.Snapshot.Program, run.Constraints, run.InitialState, token).ConfigureAwait(false));
            if (identity != run.Ledger) throw new InvalidDataException("Saved Director ledger identity changed.");
            using var checkpoint = new CoordinatorCheckpointClient(directory, endpoint, binding, identity, credential, allowInsecureHttp);
            CoordinatorCheckpointResult batch;
            do
            {
                batch = await checkpoint.DeliverAsync(async (after, limit, ct) => Require(await runtime.ReadEventsAsync(after, limit, ct).ConfigureAwait(false)),
                    run.Allocation.PreviewRevision, token: token).ConfigureAwait(false);
                result = result with
                {
                    DeliveredEvents = checked(result.DeliveredEvents + batch.DeliveredEvents),
                    AcknowledgedThrough = batch.AcknowledgedThrough,
                    CaughtUp = false
                };
                progress?.Report(result);
            } while (!batch.CaughtUp);
            using var operations = new CoordinatorCheckpointClient(directory, endpoint, binding, identity, credential,
                allowInsecureHttp, CoordinatorEventFeed.Preparation);
            CoordinatorCheckpointResult operationBatch;
            do
            {
                operationBatch = await operations.DeliverPreparationAsync(async (after, limit, ct) =>
                    Require(await runtime.ReadPreparationEventsAsync(after, limit, ct).ConfigureAwait(false)),
                    run.Allocation.PreviewRevision, token: token).ConfigureAwait(false);
                result = result with
                {
                    DeliveredEvents = checked(result.DeliveredEvents + operationBatch.DeliveredEvents),
                    OperationsAcknowledgedThrough = operationBatch.AcknowledgedThrough,
                    CaughtUp = operationBatch.CaughtUp
                };
                progress?.Report(result);
            } while (!operationBatch.CaughtUp);
            if (run.Completed && run.AutomaticWorkload && !run.Released)
            {
                if (Require(await runtime.FindUnresolvedAttemptAsync(token).ConfigureAwait(false)).Attempt is not null
                    || Require(await runtime.FindActivePreparationAsync(token).ConfigureAwait(false)).Record is not null)
                    throw new InvalidDataException("Unresolved operations prevent deferred workload release.");
                using var workloads = new CoordinatorWorkloadClient(stateRoot, endpoint, binding, clientId,
                    run.Allocation.Snapshot.Program.Configuration, credential, allowInsecureHttp, run.LocalTargetScheduling);
                await workloads.ConfirmArchivedReleaseAsync(archive.Allocation(run), identity, batch.AcknowledgedThrough, token).ConfigureAwait(false);
                archive.MarkReleased();
                result = result with { ReleasedWorkloads = result.ReleasedWorkloads + 1 };
            }
            result = result with { Runs = result.Runs + 1 };
            progress?.Report(result);
        }
        return result;
    }

    private static T Require<T>(LedgerResult<T> result) where T : class => result.Value
        ?? throw new InvalidDataException($"Saved Director ledger refused check-in: {result.Error}.");
    private static void RefuseLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Director check-in does not follow linked run storage.");
    }
}
