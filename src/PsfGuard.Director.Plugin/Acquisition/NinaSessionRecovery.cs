using NINA.Core.Model;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// The shared runtime owns persisted budgets and decisions. This adapter only
// dispatches a fresh, one-shot retry of a known-completed native operation.
internal sealed class NinaSessionRecovery(RuntimeController runtime, DirectorSessionOptions options,
    Func<RecoveryConditions> conditions, Action check, Action<string> report,
    TimeProvider clock)
{
    private RecoveryRecord? record;
    internal RecoveryRecord? Record => record;
    private ulong Now() => checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());

    internal async Task AdmitAsync(string rig, string configuration, string night, ulong start, ulong end, CancellationToken token)
    {
        var current = Require(await runtime.ReadRecoveryAsync(token)).Record;
        if (current is not null && current.Snapshot.Identity.NightId != night)
        {
            // A new Run or allocation must not reset the previous night's latch
            // or spent budget. Expired state is closed without authorizing motion.
            if (Now() < current.Snapshot.Identity.EndsAtMs)
                throw new InvalidOperationException("This observing session is already recorded. Recovery cannot restart it; wait until its maximum-duration window ends.");
            record = current;
            if (current.Snapshot.Phase is not RecoveryPhase.Stopped)
                await ApplyAsync(new RecoveryEvent.StopNight(), token, new(PlannerSafety.Unknown, RecoveryMotion.Unknown));
        }
        var identity = new RecoveryIdentity(rig, configuration, night, start, end);
        var policy = new RecoveryPolicy(1, RecoveryQualityMode.Disabled, 3, 1,
            checked((ulong)options.RetryCooldownSeconds * 1000), checked((ulong)options.MaximumRecoveryMinutes * 60000),
            checked((uint)options.MaximumRecoveryAttempts), checked((ulong)options.HookTimeoutSeconds * 1000),
            60000, end, 100, 10000, options.OnAbort == DirectorAbortPolicy.ParkMount);
        record = Require(await runtime.OpenRecoveryAsync(identity, policy, Now(), token)).Record;
        if (record.Snapshot.Phase is not RecoveryPhase.Acquiring)
            throw new InvalidOperationException("The observing session is stopped or needs recovery reconciliation.");
        await RefreshAsync(token);
    }

    internal async Task RefreshAsync(CancellationToken token)
    {
        await ApplyAsync(new RecoveryEvent.Tick(), token);
        if (record!.Snapshot.Phase is not RecoveryPhase.Acquiring)
            throw new InvalidOperationException("The observing session no longer permits acquisition.");
    }

    internal async Task ExecuteAsync(RecoveryOperation operation, string device, string target,
        Func<CancellationToken, Task> execute, Func<CancellationToken, Task> settle, CancellationToken token)
    {
        if (operation is not (RecoveryOperation.Focus or RecoveryOperation.Guide))
            throw new InvalidOperationException("This native operation cannot be retried.");
        check();
        await RefreshAsync(token);
        try { await execute(token); token.ThrowIfCancellationRequested(); return; }
        catch (SequenceEntityFailedException) when (!token.IsCancellationRequested) { }
        // Confirm quiescence before classifying the failure as known-completed.
        await settle(token);
        check();
        await ApplyAsync(new RecoveryEvent.OperationFailure(new(Guid.NewGuid().ToString("D"), operation, device, target, false)), token);
        while (record!.Snapshot.Phase is RecoveryPhase.Holding hold)
        {
            while (Now() < hold.Hold.RetryAtMs)
            {
                check();
                report($"{operation} retry in {Math.Ceiling(Math.Max(0, (double)hold.Hold.RetryAtMs - Now()) / 1000)} s; {record.Snapshot.ProbesSpent}/{options.MaximumRecoveryAttempts} used");
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, token);
                await ApplyAsync(new RecoveryEvent.Tick(), token);
                if (record.Snapshot.Phase is not RecoveryPhase.Holding) break;
            }
            if (record.Snapshot.Phase is not RecoveryPhase.Holding) break;
            check();
            var attempt = Guid.NewGuid().ToString("D");
            var granted = await ApplyAsync(new RecoveryEvent.BeginRecovery(attempt), token);
            if (!granted.NewlyApplied || granted.Issued is not { Operation: "probe" } issued || issued.AttemptId != attempt)
                break;
            check();
            var now = Now();
            if (now >= issued.DeadlineMs) throw new TimeoutException("Recovery dispatch expired.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(issued.DeadlineMs - now));
            report($"Retrying {operation}; {record.Snapshot.ProbesSpent}/{options.MaximumRecoveryAttempts}");
            RecoveryOutcome outcome;
            try
            {
                await execute(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                check();
                outcome = new RecoveryOutcome.EquipmentVerified();
            }
            catch (SequenceEntityFailedException) when (!deadline.IsCancellationRequested)
            {
                await settle(deadline.Token);
                check();
                outcome = new RecoveryOutcome.Failed();
            }
            // Cancellation/timeout/unknown exceptions intentionally leave the
            // issued attempt unresolved. They can never enter another retry.
            await ApplyAsync(new RecoveryEvent.RecoveryCompleted(attempt, outcome), token);
            if (record.Snapshot.Phase is RecoveryPhase.Acquiring) { report($"{operation} recovered"); return; }
        }
        throw new InvalidOperationException($"{operation} recovery stopped: {record!.Snapshot.Phase.GetType().Name}. Review the session before another night.");
    }

    internal async Task StopAsync(CancellationToken token)
    {
        if (record is not null && record.Snapshot.Phase is not (RecoveryPhase.Stopping or RecoveryPhase.Stopped))
            await ApplyAsync(new RecoveryEvent.StopNight(), token);
    }

    internal async Task<RecoveryIssued?> BeginParkAsync(CancellationToken token)
    {
        if (record?.Snapshot.Phase is not RecoveryPhase.Stopping) return null;
        var result = await ApplyAsync(new RecoveryEvent.BeginPark(Guid.NewGuid().ToString("D")), token);
        return result.NewlyApplied ? result.Issued : null;
    }

    internal Task FinishParkAsync(RecoveryIssued issued, RecoveryParkResult result, CancellationToken token) =>
        ApplyAsync(new RecoveryEvent.ParkCompleted(issued.AttemptId, result), token);

    private async Task<RecoveryApplied> ApplyAsync(RecoveryEvent input, CancellationToken token, RecoveryConditions? observed = null)
    {
        var state = record ?? throw new InvalidOperationException("Recovery session is not admitted.");
        var result = Require(await runtime.ApplyRecoveryAsync(new(state.Snapshot.Identity.NightId,
            state.Snapshot.Identity.ConfigurationId, Guid.NewGuid().ToString("D"), state.Revision, Now(), observed ?? conditions(), input), token));
        record = result.Record;
        return result;
    }

    private static T Require<T>(RecoveryResult<T> result) where T : class =>
        result.Value ?? throw new InvalidOperationException($"Director recovery refused: {result.Error}");
}
