using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

public enum LedgerError
{
    Disabled, NotOpen, AlreadyOpen, InvalidInput, InvalidSnapshot, InvalidDirectory,
    Unavailable, Busy, ForeignDatabase, UnsupportedSchema, UnsupportedEngine, UnsupportedStorage,
    AssignmentMismatch, UnknownCapture, ConflictingEvidence, CorruptLedger,
    PreparationNotSelected, InvalidCompletion, ClockRegression
}
public sealed record LedgerResult<T>(T? Value, LedgerError? Error) where T : class;
public sealed record LedgerIdentity(string LedgerId, string AssignmentId, ulong AssignmentRevision, string RigId, string ConfigurationId);
public abstract record LedgerEvidence
{
    public sealed record Reserved : LedgerEvidence;
    public sealed record Saved(string ImageId, ulong ElapsedMs) : LedgerEvidence;
    public sealed record Failed(string Reason) : LedgerEvidence;
    public sealed record Uncertain(string Reason) : LedgerEvidence;
}
public sealed record LedgerAttempt(string CaptureId, string GoalId, ulong ReservedAtMs, LedgerEvidence Evidence);
public enum ReservationKind { Created, Existing, RecoveryRequired, Decision }
/// <summary>No result is a hardware permit. Existing/recovery results must never dispatch a retry.</summary>
public sealed record LedgerReservation(ReservationKind Kind, LedgerAttempt? Attempt, PlannerDecision? Decision);
public sealed record LedgerEvent(ulong Sequence, LedgerAttempt Attempt);
public sealed record LedgerEventPage(LedgerIdentity Identity, ImmutableArray<LedgerEvent> Events, ulong NextCursor);
public sealed record LedgerLookup(LedgerAttempt? Attempt);

internal static class LedgerContract
{
    internal const int MaxPage = 64;

    internal static bool ValidId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128
        && value.All(c => c >= '!' && c <= '~');

    internal static void CheckId(string value)
    {
        if (!ValidId(value)) throw new ArgumentException("Invalid ledger identity or reason code.");
    }

    internal static JsonObject Encode(JsonObject operation)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(operation).Length > PlannerContract.MaxRequestBytes)
            throw new ArgumentException("Ledger operation is too large.");
        return operation;
    }

    internal static JsonObject EncodeEvidence(LedgerEvidence evidence) => evidence switch
    {
        LedgerEvidence.Saved value when ValidId(value.ImageId) => new()
        { ["state"] = "saved", ["image_id"] = value.ImageId, ["elapsed_ms"] = value.ElapsedMs },
        LedgerEvidence.Failed value when ValidId(value.Reason) => new()
        { ["state"] = "failed", ["reason"] = value.Reason },
        LedgerEvidence.Uncertain value when ValidId(value.Reason) => new()
        { ["state"] = "uncertain", ["reason"] = value.Reason },
        _ => throw new ArgumentException("Expected verified saved, failed, or uncertain evidence.", nameof(evidence))
    };

    internal static LedgerResult<T> Decode<T>(JsonElement response, string expected, string field,
        Func<JsonElement, T> read) where T : class
    {
        try
        {
            var status = response.GetProperty("status").GetString();
            if (status == "error")
            {
                PipeProtocol.RequireFields(response, "status", "code");
                return new(null, PlannerContract.ParseEnum<LedgerError>(response.GetProperty("code").GetString()));
            }
            if (status != expected) throw new InvalidDataException("Unexpected ledger result.");
            if (expected is "events" or "preparation_events") PipeProtocol.RequireFields(response, "status", "events", "next_cursor");
            else if (expected == "preparation_started") PipeProtocol.RequireFields(response, "status", "created", "record");
            else PipeProtocol.RequireFields(response, "status", field);
            return new(read(response), null);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or JsonException)
        { throw new InvalidDataException("Malformed ledger response.", error); }
    }

    internal static LedgerIdentity ReadIdentity(JsonElement value, PlannerAssignment assignment)
    {
        PipeProtocol.RequireFields(value, "ledger_id", "assignment_id", "assignment_revision", "rig_id", "configuration_id");
        var id = value.GetProperty("ledger_id").GetString();
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty
            || value.GetProperty("assignment_id").GetString() != assignment.Id
            || value.GetProperty("assignment_revision").GetUInt64() != assignment.Revision
            || value.GetProperty("rig_id").GetString() != assignment.RigId
            || value.GetProperty("configuration_id").GetString() != assignment.ConfigurationId)
            throw new InvalidDataException("Ledger identity does not match the opened allocation.");
        return new(id!, assignment.Id, assignment.Revision, assignment.RigId, assignment.ConfigurationId);
    }

    internal static LedgerAttempt ReadAttempt(JsonElement value, PlannerAssignment assignment, string? captureId = null)
    {
        PipeProtocol.RequireFields(value, "capture_id", "goal_id", "reserved_at_ms", "evidence");
        var capture = ReadId(value, "capture_id");
        var goal = ReadId(value, "goal_id");
        if ((captureId is not null && capture != captureId) || assignment.Goals.IsDefault
            || assignment.Goals.Count(g => g?.Id == goal) != 1)
            throw new InvalidDataException("Ledger attempt belongs to another capture or goal.");
        var reservedAt = value.GetProperty("reserved_at_ms").GetUInt64();
        if (reservedAt < assignment.ValidFromMs || reservedAt >= assignment.ExpiresAtMs)
            throw new InvalidDataException("Ledger reservation time is outside its allocation.");
        var evidence = value.GetProperty("evidence");
        LedgerEvidence decoded;
        switch (evidence.GetProperty("state").GetString())
        {
            case "reserved":
                PipeProtocol.RequireFields(evidence, "state");
                decoded = new LedgerEvidence.Reserved();
                break;
            case "saved":
                PipeProtocol.RequireFields(evidence, "state", "image_id", "elapsed_ms");
                decoded = new LedgerEvidence.Saved(ReadId(evidence, "image_id"), evidence.GetProperty("elapsed_ms").GetUInt64());
                break;
            case "failed":
            case "uncertain":
                PipeProtocol.RequireFields(evidence, "state", "reason");
                var reason = ReadId(evidence, "reason");
                decoded = evidence.GetProperty("state").GetString() == "failed"
                    ? new LedgerEvidence.Failed(reason) : new LedgerEvidence.Uncertain(reason);
                break;
            default: throw new InvalidDataException("Unknown capture evidence.");
        }
        return new(capture, goal, reservedAt, decoded);
    }

    internal static LedgerReservation ReadReservation(JsonElement value, PlannerAssignment assignment, string captureId, PlannerState state)
    {
        PipeProtocol.RequireFields(value, "status", "value");
        var kind = PlannerContract.ParseEnum<ReservationKind>(value.GetProperty("status").GetString());
        var body = value.GetProperty("value");
        if (kind == ReservationKind.Decision)
        {
            PipeProtocol.RequireFields(body, "action", "reason");
            var action = PlannerContract.ParseEnum<PlannerAction>(body.GetProperty("action").GetString());
            if (action == PlannerAction.Acquire) throw new InvalidDataException("Unreserved acquire decision.");
            return new(kind, null, new(action, ReadId(body, "reason")));
        }
        var attempt = ReadAttempt(body, assignment, kind == ReservationKind.RecoveryRequired ? null : captureId);
        if (kind == ReservationKind.Created && (attempt.Evidence is not LedgerEvidence.Reserved || attempt.ReservedAtMs != state.NowMs))
            throw new InvalidDataException("New reservation contains stale or terminal evidence.");
        if (kind == ReservationKind.RecoveryRequired && (attempt.CaptureId == captureId
            || attempt.Evidence is not (LedgerEvidence.Reserved or LedgerEvidence.Uncertain)))
            throw new InvalidDataException("Recovery response contains inconsistent evidence.");
        return new(kind, attempt, null);
    }

    internal static LedgerEventPage ReadPage(JsonElement response, LedgerIdentity identity, PlannerAssignment assignment, ulong after, int limit)
    {
        var array = response.GetProperty("events");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > limit)
            throw new InvalidDataException("Invalid ledger event page size.");
        var events = ImmutableArray.CreateBuilder<LedgerEvent>();
        var cursor = after;
        foreach (var item in array.EnumerateArray())
        {
            PipeProtocol.RequireFields(item, "schema_version", "ledger_id", "sequence", "contract_version", "engine_version",
                "assignment_id", "assignment_revision", "rig_id", "configuration_id", "attempt");
            if (item.GetProperty("schema_version").GetInt32() != 1
                || item.GetProperty("contract_version").GetInt32() != RuntimeContract.ContractVersion
                || item.GetProperty("engine_version").GetString() != RuntimeContract.EngineVersion
                || item.GetProperty("ledger_id").GetString() != identity.LedgerId
                || item.GetProperty("assignment_id").GetString() != identity.AssignmentId
                || item.GetProperty("assignment_revision").GetUInt64() != identity.AssignmentRevision
                || item.GetProperty("rig_id").GetString() != identity.RigId
                || item.GetProperty("configuration_id").GetString() != identity.ConfigurationId
                || item.GetProperty("sequence").GetUInt64() != checked(cursor + 1))
                throw new InvalidDataException("Ledger event identity, version, or sequence mismatch.");
            cursor++;
            events.Add(new(cursor, ReadAttempt(item.GetProperty("attempt"), assignment)));
        }
        if (response.GetProperty("next_cursor").GetUInt64() != cursor)
            throw new InvalidDataException("Ledger event cursor does not match its page.");
        return new(identity, events.ToImmutable(), cursor);
    }

    internal static string ReadId(JsonElement value, string field)
    {
        var result = value.GetProperty(field).GetString();
        if (!ValidId(result)) throw new InvalidDataException("Invalid ledger identity or reason code.");
        return result!;
    }
}
