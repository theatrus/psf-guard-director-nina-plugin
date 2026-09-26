using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

internal sealed partial class RuntimeSession
{
    private JsonNode EncodeState(PlannerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.RigId != rigId) throw new ArgumentException("Ledger state belongs to another rig.", nameof(state));
        return JsonSerializer.SerializeToNode(state, PlannerContract.Options)!;
    }

    internal Task<LedgerResult<PlannerDecision>> EvaluateLedgerAsync(PlannerState state, CancellationToken token) =>
        SendLedgerAsync(new JsonObject { ["action"] = "evaluate", ["state"] = EncodeState(state) }, false,
            response => LedgerContract.Decode(response, "evaluated", "decision",
                value => PreparationContract.ReadDecision(value.GetProperty("decision"), ledgerAssignment!, true)), token);

    internal Task<LedgerResult<LedgerLookup>> FindUnresolvedAttemptAsync(CancellationToken token) =>
        SendLedgerAsync(new JsonObject { ["action"] = "unresolved_attempt" }, false,
            response => LedgerContract.Decode(response, "found", "attempt", value =>
            {
                var attempt = value.GetProperty("attempt").ValueKind == JsonValueKind.Null ? null
                    : LedgerContract.ReadAttempt(value.GetProperty("attempt"), ledgerAssignment!);
                if (attempt is not null && attempt.Evidence is not (LedgerEvidence.Reserved or LedgerEvidence.Uncertain))
                    throw new InvalidDataException("Recovery returned a resolved attempt.");
                return new LedgerLookup(attempt);
            }), token);

    internal Task<LedgerResult<PreparationStarted>> BeginPreparationAsync(string id, PreparationContext context,
        PreparationEstimates estimates, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(estimates);
        foreach (var value in new[] { context.GoalId, context.TargetId, context.RecipeId, context.FilterId }) LedgerContract.CheckId(value);
        if (context.PreviousTargetId is not null) LedgerContract.CheckId(context.PreviousTargetId);
        var operation = new JsonObject
        {
            ["action"] = "begin_preparation",
            ["preparation_id"] = id,
            ["context"] = JsonSerializer.SerializeToNode(context, PlannerContract.Options),
            ["estimates"] = JsonSerializer.SerializeToNode(estimates, PlannerContract.Options),
            ["state"] = EncodeState(state)
        };
        return SendLedgerAsync(operation, false, response => LedgerContract.Decode(response, "preparation_started", "record", value =>
        {
            var record = ReadPreparationRecord(value.GetProperty("record"), id);
            var created = value.GetProperty("created").GetBoolean();
            if (record.GoalId != context.GoalId || (created && (record.Lifecycle != PreparationLifecycle.Active
                || record.Pending is not null || record.Halted is not null || !record.Observations.IsEmpty)))
                throw new InvalidDataException("New preparation contains inconsistent recovery evidence.");
            return new PreparationStarted(created, record);
        }), token);
    }

    internal Task<LedgerResult<PreparationNext>> AdvancePreparationAsync(string id, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        return SendLedgerAsync(new JsonObject { ["action"] = "advance_preparation", ["preparation_id"] = id, ["state"] = EncodeState(state) }, false,
            response => LedgerContract.Decode(response, "preparation_advanced", "next",
                value => PreparationContract.ReadNext(value.GetProperty("next"), ledgerAssignment!, id)), token);
    }

    internal Task<LedgerResult<PreparationRecord>> CompletePreparationAsync(PreparationCompletion completion, CancellationToken token) =>
        SendLedgerAsync(new JsonObject { ["action"] = "complete_preparation", ["completion"] = PreparationContract.EncodeCompletion(completion) }, false,
            response => LedgerContract.Decode(response, "preparation_recorded", "record", value =>
            {
                var record = ReadPreparationRecord(value.GetProperty("record"), completion.PreparationId);
                if (!record.Observations.Any(o => o.Completion == completion))
                    throw new InvalidDataException("Native receipt was not echoed by the ledger.");
                return record;
            }), token);

    internal Task<LedgerResult<PreparationLookup>> FindPreparationAsync(string id, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        return ReadPreparationAsync(new JsonObject { ["action"] = "preparation", ["preparation_id"] = id }, id, false, token);
    }

    internal Task<LedgerResult<PreparationLookup>> FindActivePreparationAsync(CancellationToken token) =>
        ReadPreparationAsync(new JsonObject { ["action"] = "active_preparation" }, null, true, token);

    private Task<LedgerResult<PreparationLookup>> ReadPreparationAsync(JsonObject operation, string? id, bool activeOnly, CancellationToken token) =>
        SendLedgerAsync(operation, false, response => LedgerContract.Decode(response, "preparation_found", "record", value =>
        {
            var record = value.GetProperty("record").ValueKind == JsonValueKind.Null ? null
                : ReadPreparationRecord(value.GetProperty("record"), id);
            if (activeOnly && record is not null && record.Lifecycle != PreparationLifecycle.Active)
                throw new InvalidDataException("Active preparation lookup returned terminal evidence.");
            return new PreparationLookup(record);
        }), token);

    internal Task<LedgerResult<PreparationRecord>> ClosePreparationAsync(string id, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        return SendLedgerAsync(new JsonObject { ["action"] = "close_preparation", ["preparation_id"] = id }, false,
            response => LedgerContract.Decode(response, "preparation_closed", "record", value =>
            {
                var record = ReadPreparationRecord(value.GetProperty("record"), id);
                // Closing terminal work is idempotent; a captured record stays captured.
                if (record.Lifecycle == PreparationLifecycle.Active) throw new InvalidDataException("Preparation is still active.");
                return record;
            }), token);
    }

    internal Task<LedgerResult<LedgerReservation>> ReservePreparedAsync(string id, string captureId, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        LedgerContract.CheckId(captureId);
        return SendLedgerAsync(new JsonObject { ["action"] = "reserve_prepared", ["preparation_id"] = id, ["capture_id"] = captureId, ["state"] = EncodeState(state) }, false,
            response => LedgerContract.Decode(response, "reserved", "outcome",
                value => LedgerContract.ReadReservation(value.GetProperty("outcome"), ledgerAssignment!, captureId, state)), token);
    }

    internal Task<LedgerResult<PreparationEventPage>> ReadPreparationEventsAsync(ulong after, int limit, CancellationToken token)
    {
        if (after > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(after));
        if (limit is < 1 or > PreparationContract.MaxPage) throw new ArgumentOutOfRangeException(nameof(limit));
        return SendLedgerAsync(new JsonObject { ["action"] = "preparation_events", ["after"] = after, ["limit"] = limit }, false,
            response => LedgerContract.Decode(response, "preparation_events", "events",
                value =>
                {
                    var page = PreparationContract.ReadPage(value, ledgerIdentity!, ledgerAssignment!, after, limit);
                    if (ledgerProgram is not null) ProgramContract.CheckPage(page, ledgerProgram);
                    return page;
                }), token);
    }
}
