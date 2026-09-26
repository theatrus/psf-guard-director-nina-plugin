using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

public sealed record PreparationContext(string GoalId, string TargetId, string RecipeId,
    string? PreviousTargetId, string FilterId, short ReadoutMode, bool MountParked,
    bool RotatorConnected, bool EnableSlewCenter, uint DitherEvery, uint? DitherOverride,
    uint FilterExposuresSinceDither);
public sealed record PreparationEstimates(ulong UnparkMs, ulong CenterMs, ulong BeforeTargetMs,
    ulong DitherMs, ulong FilterMs, ulong ReadoutMs, ulong CaptureOverheadMs);
public abstract record PreparationOperation
{
    public sealed record Unpark : PreparationOperation;
    public sealed record Center(bool Rotate) : PreparationOperation;
    public sealed record BeforeTarget : PreparationOperation;
    public sealed record Dither : PreparationOperation;
    public sealed record SwitchFilter(string FilterId) : PreparationOperation;
    public sealed record SetReadoutMode(short Mode) : PreparationOperation;
}
public sealed record PreparationCommand(string PreparationId, uint Ordinal, string GoalId,
    string TargetId, string RecipeId, PreparationOperation Operation);
public abstract record PreparationOutcome
{
    public sealed record Succeeded : PreparationOutcome;
    public sealed record Failed(string Reason) : PreparationOutcome;
    public sealed record Uncertain(string Reason) : PreparationOutcome;
}
public sealed record PreparationCompletion(string PreparationId, uint Ordinal, ulong EndedAtMs,
    ulong ElapsedMs, PreparationOutcome Outcome);
public sealed record PreparationObservation(PreparationCommand Command, ulong IssuedAtMs, PreparationCompletion Completion);
public enum PreparationLifecycle { Active, Closed, Captured }
/// <summary>Recovery evidence only. Pending commands must not be dispatched again.</summary>
public sealed record PreparationRecord(string PreparationId, PreparationLifecycle Lifecycle, string GoalId,
    PreparationCommand? Pending, PlannerDecision? Halted, ImmutableArray<PreparationObservation> Observations, string? CaptureId);
public sealed record PreparationStarted(bool Created, PreparationRecord Record);
public sealed record PreparationLookup(PreparationRecord? Record);
/// <summary>Only Run is a newly issued operation; all cases still require local dispatch validation.</summary>
public abstract record PreparationNext
{
    public sealed record Run(PreparationCommand Command) : PreparationNext;
    public sealed record InFlight(uint Ordinal) : PreparationNext;
    public sealed record ReadyToReserve(string GoalId) : PreparationNext;
    public sealed record Decision(PlannerDecision Value) : PreparationNext;
}
public abstract record PreparationEventData
{
    public sealed record Started(PreparationContext Context, PreparationEstimates Estimates) : PreparationEventData;
    public sealed record Issued(PreparationCommand Command, ulong IssuedAtMs) : PreparationEventData;
    public sealed record Completed(PreparationObservation Observation) : PreparationEventData;
    public sealed record Halted(PlannerDecision Decision) : PreparationEventData;
    public sealed record Closed : PreparationEventData;
    public sealed record Captured(string CaptureId) : PreparationEventData;
}
public sealed record PreparationEvent(ulong Sequence, string PreparationId, PreparationEventData Data);
/// <summary>This cursor is independent of the capture-event cursor.</summary>
public sealed record PreparationEventPage(LedgerIdentity Identity, ImmutableArray<PreparationEvent> Events, ulong NextCursor);

internal static class PreparationContract
{
    internal const int MaxPage = 32;

    internal static JsonObject EncodeCompletion(PreparationCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        LedgerContract.CheckId(completion.PreparationId);
        var outcome = completion.Outcome switch
        {
            PreparationOutcome.Succeeded => new JsonObject { ["outcome"] = "succeeded" },
            PreparationOutcome.Failed failed when LedgerContract.ValidId(failed.Reason) => new JsonObject { ["outcome"] = "failed", ["reason"] = failed.Reason },
            PreparationOutcome.Uncertain uncertain when LedgerContract.ValidId(uncertain.Reason) => new JsonObject { ["outcome"] = "uncertain", ["reason"] = uncertain.Reason },
            _ => throw new ArgumentException("Invalid native operation outcome.", nameof(completion))
        };
        return new JsonObject
        {
            ["preparation_id"] = completion.PreparationId,
            ["ordinal"] = completion.Ordinal,
            ["ended_at_ms"] = completion.EndedAtMs,
            ["elapsed_ms"] = completion.ElapsedMs,
            ["outcome"] = outcome
        };
    }

    internal static PlannerDecision ReadDecision(JsonElement value, PlannerAssignment assignment, bool allowAcquire, bool allowContinue = true)
    {
        var action = PlannerContract.ParseEnum<PlannerAction>(value.GetProperty("action").GetString());
        if ((!allowAcquire && action == PlannerAction.Acquire) || (!allowContinue && action == PlannerAction.Continue))
            throw new InvalidDataException("Unexpected preparation decision.");
        if (action == PlannerAction.Acquire) PipeProtocol.RequireFields(value, "action", "reason", "goal_id");
        else PipeProtocol.RequireFields(value, "action", "reason");
        return new(action, LedgerContract.ReadId(value, "reason"), action == PlannerAction.Acquire ? ReadGoal(value, assignment) : null);
    }

    internal static string ReadGoal(JsonElement value, PlannerAssignment assignment)
    {
        var goal = LedgerContract.ReadId(value, "goal_id");
        if (assignment.Goals.IsDefault || assignment.Goals.Count(g => g?.Id == goal) != 1)
            throw new InvalidDataException("Preparation belongs to an unknown or ambiguous goal.");
        return goal;
    }

    internal static PreparationCommand ReadCommand(JsonElement value, PlannerAssignment assignment, string id)
    {
        PipeProtocol.RequireFields(value, "preparation_id", "ordinal", "goal_id", "target_id", "recipe_id", "operation");
        if (LedgerContract.ReadId(value, "preparation_id") != id) throw new InvalidDataException("Preparation command identity mismatch.");
        var operation = value.GetProperty("operation");
        PreparationOperation decoded;
        switch (operation.GetProperty("operation").GetString())
        {
            case "unpark": PipeProtocol.RequireFields(operation, "operation"); decoded = new PreparationOperation.Unpark(); break;
            case "center": PipeProtocol.RequireFields(operation, "operation", "rotate"); decoded = new PreparationOperation.Center(operation.GetProperty("rotate").GetBoolean()); break;
            case "before_target": PipeProtocol.RequireFields(operation, "operation"); decoded = new PreparationOperation.BeforeTarget(); break;
            case "dither": PipeProtocol.RequireFields(operation, "operation"); decoded = new PreparationOperation.Dither(); break;
            case "switch_filter": PipeProtocol.RequireFields(operation, "operation", "filter_id"); decoded = new PreparationOperation.SwitchFilter(LedgerContract.ReadId(operation, "filter_id")); break;
            case "set_readout_mode":
                PipeProtocol.RequireFields(operation, "operation", "mode");
                var mode = operation.GetProperty("mode").GetInt16();
                if (mode < 0) throw new InvalidDataException("Invalid readout mode.");
                decoded = new PreparationOperation.SetReadoutMode(mode); break;
            default: throw new InvalidDataException("Unknown native operation.");
        }
        var ordinal = ReadOrdinal(value);
        return new(id, ordinal, ReadGoal(value, assignment),
            LedgerContract.ReadId(value, "target_id"), LedgerContract.ReadId(value, "recipe_id"), decoded);
    }

    internal static PreparationObservation ReadObservation(JsonElement value, PlannerAssignment assignment, string id)
    {
        PipeProtocol.RequireFields(value, "command", "issued_at_ms", "completion");
        var command = ReadCommand(value.GetProperty("command"), assignment, id);
        var completion = value.GetProperty("completion");
        PipeProtocol.RequireFields(completion, "preparation_id", "ordinal", "ended_at_ms", "elapsed_ms", "outcome");
        if (completion.GetProperty("preparation_id").GetString() != id || completion.GetProperty("ordinal").GetUInt32() != command.Ordinal)
            throw new InvalidDataException("Native receipt identity mismatch.");
        var outcome = completion.GetProperty("outcome");
        PreparationOutcome decoded;
        switch (outcome.GetProperty("outcome").GetString())
        {
            case "succeeded": PipeProtocol.RequireFields(outcome, "outcome"); decoded = new PreparationOutcome.Succeeded(); break;
            case "failed": PipeProtocol.RequireFields(outcome, "outcome", "reason"); decoded = new PreparationOutcome.Failed(LedgerContract.ReadId(outcome, "reason")); break;
            case "uncertain": PipeProtocol.RequireFields(outcome, "outcome", "reason"); decoded = new PreparationOutcome.Uncertain(LedgerContract.ReadId(outcome, "reason")); break;
            default: throw new InvalidDataException("Unknown native operation outcome.");
        }
        var issued = value.GetProperty("issued_at_ms").GetUInt64();
        var ended = completion.GetProperty("ended_at_ms").GetUInt64();
        if (ended < issued) throw new InvalidDataException("Native receipt predates the operation.");
        return new(command, issued, new(id, command.Ordinal, ended, completion.GetProperty("elapsed_ms").GetUInt64(), decoded));
    }

    internal static PreparationRecord ReadRecord(JsonElement value, PlannerAssignment assignment, string? expectedId = null)
    {
        PipeProtocol.RequireFields(value, "preparation_id", "lifecycle", "goal_id", "pending", "halted", "observations", "capture_id");
        var id = LedgerContract.ReadId(value, "preparation_id");
        if (expectedId is not null && id != expectedId) throw new InvalidDataException("Preparation record identity mismatch.");
        var lifecycle = PlannerContract.ParseEnum<PreparationLifecycle>(value.GetProperty("lifecycle").GetString());
        var goal = ReadGoal(value, assignment);
        var pending = value.GetProperty("pending").ValueKind == JsonValueKind.Null ? null : ReadCommand(value.GetProperty("pending"), assignment, id);
        var halted = value.GetProperty("halted").ValueKind == JsonValueKind.Null ? null : ReadDecision(value.GetProperty("halted"), assignment, false, false);
        var capture = value.GetProperty("capture_id").ValueKind == JsonValueKind.Null ? null : LedgerContract.ReadId(value, "capture_id");
        var observations = ImmutableArray.CreateBuilder<PreparationObservation>();
        foreach (var item in value.GetProperty("observations").EnumerateArray())
        {
            var observation = ReadObservation(item, assignment, id);
            if (observation.Command.GoalId != goal || observation.Command.Ordinal != (uint)observations.Count + 1)
                throw new InvalidDataException("Preparation receipt order or goal mismatch.");
            if (observations.Count > 0 && (observations[^1].Completion.Outcome is not PreparationOutcome.Succeeded
                || observation.IssuedAtMs < observations[^1].Completion.EndedAtMs))
                throw new InvalidDataException("Preparation continued after failure or reversed its clock.");
            observations.Add(observation);
        }
        var commands = observations.Select(o => o.Command).Concat(pending is null ? [] : new[] { pending }).ToArray();
        if (commands.Select(c => (c.TargetId, c.RecipeId)).Distinct().Count() > 1)
            throw new InvalidDataException("Preparation changed its resolved target or recipe.");
        var unsuccessful = observations.LastOrDefault()?.Completion.Outcome is PreparationOutcome.Failed or PreparationOutcome.Uncertain;
        if ((lifecycle == PreparationLifecycle.Captured) != (capture is not null)
            || (pending is not null && (pending.GoalId != goal || pending.Ordinal != (uint)observations.Count + 1 || lifecycle != PreparationLifecycle.Active))
            || (lifecycle == PreparationLifecycle.Captured && halted is not null)
            || (unsuccessful && (halted is null || pending is not null || lifecycle == PreparationLifecycle.Captured))
            || (lifecycle == PreparationLifecycle.Closed && observations.Any(o => o.Completion.Outcome is PreparationOutcome.Uncertain)))
            throw new InvalidDataException("Inconsistent preparation lifecycle.");
        return new(id, lifecycle, goal, pending, halted, observations.ToImmutable(), capture);
    }

    internal static PreparationNext ReadNext(JsonElement value, PlannerAssignment assignment, string id)
    {
        PipeProtocol.RequireFields(value, "status", "value");
        var body = value.GetProperty("value");
        switch (value.GetProperty("status").GetString())
        {
            case "run": return new PreparationNext.Run(ReadCommand(body, assignment, id));
            case "in_flight": PipeProtocol.RequireFields(body, "ordinal"); return new PreparationNext.InFlight(ReadOrdinal(body));
            case "ready_to_reserve": PipeProtocol.RequireFields(body, "goal_id"); return new PreparationNext.ReadyToReserve(ReadGoal(body, assignment));
            case "decision": return new PreparationNext.Decision(ReadDecision(body, assignment, false));
            default: throw new InvalidDataException("Unknown preparation advancement.");
        }
    }

    private static uint ReadOrdinal(JsonElement value)
    {
        var ordinal = value.GetProperty("ordinal").GetUInt32();
        if (ordinal == 0) throw new InvalidDataException("Native operation ordinals start at one.");
        return ordinal;
    }

    internal static PreparationEventPage ReadPage(JsonElement response, LedgerIdentity identity, PlannerAssignment assignment, ulong after, int limit)
    {
        var array = response.GetProperty("events");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > limit)
            throw new InvalidDataException("Invalid preparation event page size.");
        var events = ImmutableArray.CreateBuilder<PreparationEvent>();
        var cursor = after;
        foreach (var item in array.EnumerateArray())
        {
            PipeProtocol.RequireFields(item, "schema_version", "ledger_id", "sequence", "contract_version", "engine_version",
                "assignment_id", "assignment_revision", "rig_id", "configuration_id", "preparation_id", "event");
            if (item.GetProperty("schema_version").GetInt32() != 1
                || item.GetProperty("contract_version").GetInt32() != RuntimeContract.ContractVersion
                || item.GetProperty("engine_version").GetString() != RuntimeContract.EngineVersion
                || item.GetProperty("ledger_id").GetString() != identity.LedgerId
                || item.GetProperty("assignment_id").GetString() != identity.AssignmentId
                || item.GetProperty("assignment_revision").GetUInt64() != identity.AssignmentRevision
                || item.GetProperty("rig_id").GetString() != identity.RigId
                || item.GetProperty("configuration_id").GetString() != identity.ConfigurationId
                || item.GetProperty("sequence").GetUInt64() != checked(cursor + 1))
                throw new InvalidDataException("Preparation event identity, version, or sequence mismatch.");
            var id = LedgerContract.ReadId(item, "preparation_id");
            var value = item.GetProperty("event");
            PreparationEventData data;
            switch (value.GetProperty("kind").GetString())
            {
                case "started":
                    PipeProtocol.RequireFields(value, "kind", "context", "estimates");
                    var context = value.GetProperty("context");
                    PipeProtocol.RequireFields(context, "goal_id", "target_id", "recipe_id", "previous_target_id", "filter_id", "readout_mode",
                        "mount_parked", "rotator_connected", "enable_slew_center", "dither_every", "dither_override", "filter_exposures_since_dither");
                    ReadGoal(context, assignment);
                    foreach (var field in new[] { "target_id", "recipe_id", "filter_id" }) LedgerContract.ReadId(context, field);
                    if (context.GetProperty("previous_target_id").ValueKind != JsonValueKind.Null) LedgerContract.ReadId(context, "previous_target_id");
                    if (context.GetProperty("readout_mode").GetInt16() < 0) throw new InvalidDataException("Invalid readout mode.");
                    var estimates = value.GetProperty("estimates");
                    PipeProtocol.RequireFields(estimates, "unpark_ms", "center_ms", "before_target_ms", "dither_ms", "filter_ms", "readout_ms", "capture_overhead_ms");
                    data = new PreparationEventData.Started(context.Deserialize<PreparationContext>(PlannerContract.Options)!, estimates.Deserialize<PreparationEstimates>(PlannerContract.Options)!);
                    break;
                case "issued":
                    PipeProtocol.RequireFields(value, "kind", "command", "issued_at_ms");
                    data = new PreparationEventData.Issued(ReadCommand(value.GetProperty("command"), assignment, id), value.GetProperty("issued_at_ms").GetUInt64());
                    break;
                case "completed":
                    PipeProtocol.RequireFields(value, "kind", "observation");
                    data = new PreparationEventData.Completed(ReadObservation(value.GetProperty("observation"), assignment, id));
                    break;
                case "halted":
                    PipeProtocol.RequireFields(value, "kind", "decision");
                    data = new PreparationEventData.Halted(ReadDecision(value.GetProperty("decision"), assignment, false, false));
                    break;
                case "closed": PipeProtocol.RequireFields(value, "kind"); data = new PreparationEventData.Closed(); break;
                case "captured": PipeProtocol.RequireFields(value, "kind", "capture_id"); data = new PreparationEventData.Captured(LedgerContract.ReadId(value, "capture_id")); break;
                default: throw new InvalidDataException("Unknown preparation event.");
            }
            events.Add(new(++cursor, id, data));
        }
        if (response.GetProperty("next_cursor").GetUInt64() != cursor) throw new InvalidDataException("Preparation cursor does not match its page.");
        return new(identity, events.ToImmutable(), cursor);
    }
}
