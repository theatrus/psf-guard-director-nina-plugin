using System.IO;
using System.Text.Json;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record NinaDispatchSnapshot(DirectorConfiguration Configuration, DirectorConstraints Constraints, PlannerState State);

// Owns one-use callbacks, not scheduling policy. Construct within the session
// that issues the work; recovered evidence cannot create a callback.
internal sealed class NinaGeometryDispatch
{
    private readonly RuntimeController runtime;
    private readonly RuntimeStatus session;
    private readonly Func<NinaDispatchSnapshot> read;
    private readonly Action validateNative;
    private readonly TimeProvider clock;
    private readonly object claimsLock = new();
    private readonly HashSet<(string PreparationId, uint Ordinal)> commands = [];
    private readonly HashSet<string> captures = [];

    internal NinaGeometryDispatch(RuntimeController runtime, Func<NinaDispatchSnapshot> read, Action validateNative, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(validateNative);
        this.runtime = runtime;
        this.read = read;
        this.validateNative = validateNative;
        this.clock = clock ?? TimeProvider.System;
        session = runtime.Status;
        CheckSession();
    }

    internal Func<CancellationToken, Task<Action>> Pending(PreparationNext next, Action? validateContext = null)
    {
        CheckSession();
        if (next is not PreparationNext.Run issued)
            throw new InvalidOperationException("Only newly issued preparation can enter native dispatch.");
        var command = issued.Command;
        lock (claimsLock)
            if (!commands.Add((command.PreparationId, command.Ordinal)))
                throw new InvalidOperationException("This issued command already has a native dispatch callback.");
        return Once(command.GoalId, (snapshot, token) => runtime.CheckGeometryPendingDispatchAsync(command,
            snapshot.Configuration, snapshot.Constraints, snapshot.State, token), validateContext);
    }

    internal Func<CancellationToken, Task<Action>> Capture(string preparationId, LedgerReservation reservation, Action? validateContext = null)
    {
        CheckSession();
        if (reservation is not { Kind: ReservationKind.Created, Decision: null, Attempt.Evidence: LedgerEvidence.Reserved })
            throw new InvalidOperationException("Only a new reservation can enter native dispatch.");
        var attempt = reservation.Attempt;
        lock (claimsLock)
            if (!captures.Add(attempt.CaptureId))
                throw new InvalidOperationException("This reserved capture already has a native dispatch callback.");
        return Once(attempt.GoalId, (snapshot, token) => runtime.CheckGeometryCaptureDispatchAsync(preparationId, attempt,
            snapshot.Configuration, snapshot.Constraints, snapshot.State, token), validateContext);
    }

    private Func<CancellationToken, Task<Action>> Once(string goal,
        Func<NinaDispatchSnapshot, CancellationToken, Task<LedgerResult<PlannerDispatchCheck>>> check, Action? validateContext)
    {
        var entered = 0;
        return async token =>
        {
            if (Interlocked.Exchange(ref entered, 1) != 0)
                throw new InvalidOperationException("This native dispatch check has already been consumed.");
            token.ThrowIfCancellationRequested();
            CheckSession();
            validateNative();
            validateContext?.Invoke();
            var started = clock.GetTimestamp();
            var before = read();
            CheckSession();
            var result = await check(before, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            CheckSession();
            if (result.Error is not null || result.Value is not { Decision.Action: PlannerAction.Acquire } checkedDispatch || checkedDispatch.Decision.GoalId != goal)
                throw new InvalidOperationException($"Director refused native dispatch: {result.Error?.ToString() ?? result.Value?.Decision.Reason ?? "missing_decision"}.");

            Validate();
            var dispatched = 0;
            return () =>
            {
                if (Interlocked.Exchange(ref dispatched, 1) != 0)
                    throw new InvalidOperationException("This native dispatch guard has already been consumed.");
                // The adapter calls this after its own validation, progress and
                // durable journal writes, immediately before entering NINA.
                Validate();
            };

            void Validate()
            {
                validateNative();
                validateContext?.Invoke();
                var after = read();
                CheckSession();
                token.ThrowIfCancellationRequested();
                if (JsonSerializer.Serialize(before.Configuration) != JsonSerializer.Serialize(after.Configuration)
                    || JsonSerializer.Serialize(before.Constraints) != JsonSerializer.Serialize(after.Constraints)
                    || before.State != (after.State with { NowMs = before.State.NowMs })
                    || after.State.NowMs < before.State.NowMs || after.State.NowMs >= after.State.ConditionsValidUntilMs)
                    throw new InvalidOperationException("Native constraints or conditions changed during the dispatch check.");
                checkedDispatch.EnsureWithinDeadline(after.State.NowMs, clock.GetElapsedTime(started));
            }
        };
    }

    private void CheckSession()
    {
        // Ready status is immutable and replaced on every run transition. Equal
        // values after a restart must not revive the original callbacks.
        if (!runtime.IsCurrentReadySession(session))
            throw new IOException("The Director session that issued this work is no longer ready.");
    }
}
