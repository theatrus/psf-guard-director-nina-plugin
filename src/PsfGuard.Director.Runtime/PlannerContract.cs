using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

// Wire DTOs, not a second planner. All selection and window policy stays in Rust.
public sealed record PlannerRequest(PlannerAssignment Assignment, PlannerState State)
{
    public int ContractVersion => RuntimeContract.ContractVersion;
}
public sealed record PlannerAssignment(string Id, ulong Revision, string RigId, string ConfigurationId,
    ulong ValidFromMs, ulong ExpiresAtMs, ImmutableArray<PlannerGoal> Goals);
public sealed record PlannerGoal(string Id, uint Priority, uint Requested, uint Accepted, uint Pending,
    uint AttemptsRemaining, ulong ExposureMs, ulong OverheadMs,
    ImmutableArray<PlannerInterval> EligibleWindows, PlannerTransits? Transits = null);
public readonly record struct PlannerInterval(ulong StartMs, ulong EndMs);
public readonly record struct PlannerMeridianExclusion(ulong BeforeMs, ulong AfterMs);
public sealed record PlannerTransits(PlannerInterval Searched, ImmutableArray<ulong> TransitsMs);
public enum PlannerSafety { Safe, Unsafe, Unknown }
public sealed record PlannerState(string RigId, string ConfigurationId, ulong NowMs,
    ulong ConditionsValidUntilMs, PlannerSafety Safety, bool AtBoundary, bool OperatorStop,
    PlannerMeridianExclusion MeridianExclusion);
public enum PlannerAction { Acquire, Continue, Stop, CheckIn, Wait, Complete }
public enum PlannerError
{
    RequestTooLarge, InvalidJson, UnsupportedContract, InvalidAssignment, InvalidState,
    DuplicateGoal, InvalidGoal, InvalidWindows, InvalidTransits, IncompleteTransitCoverage, MeridianTimeOverflow
}
public sealed record PlannerDecision(PlannerAction Action, string Reason, string? GoalId = null);

/// <summary>
/// A recommendation for one immutable snapshot, not a durable dispatch permit.
/// Re-evaluate current assignment/state at the hardware boundary; reserve attempts
/// durably and enforce local safety independently. Errors never contain a decision.
/// </summary>
public sealed record PlannerEvaluation(PlannerRequest Snapshot, PlannerDecision? Decision, PlannerError? Error);

internal static class PlannerContract
{
    internal const int MaxRequestBytes = 262144;
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    internal static JsonObject Encode(PlannerRequest request, string rigId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Assignment);
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.Assignment.RigId != rigId)
            throw new ArgumentException("Planning assignment belongs to another runtime rig.", nameof(request));
        // Validate transport shape/size before admission so caller mistakes cannot
        // abandon an unrelated in-flight exchange. Domain validation stays in Rust.
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(request, Options); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new ArgumentException("Planning request cannot be serialized.", nameof(request), error); }
        if (bytes.Length > MaxRequestBytes) throw new ArgumentException("Planning request is too large.", nameof(request));
        return JsonNode.Parse(bytes)!.AsObject();
    }

    internal static PlannerEvaluation Decode(JsonElement response, PlannerRequest request)
    {
        try
        {
            var status = response.GetProperty("status").GetString();
            if (status is not ("ok" or "error")) throw new InvalidDataException("Unknown planner outcome.");
            PipeProtocol.RequireFields(response, "contract_version", "engine_version", "assignment_id", "assignment_revision",
                "status", status == "ok" ? "decision" : "code");
            if (response.GetProperty("contract_version").GetInt32() != RuntimeContract.ContractVersion
                || response.GetProperty("engine_version").GetString() != RuntimeContract.EngineVersion)
                throw new InvalidDataException("Planner response version mismatch.");
            var id = response.GetProperty("assignment_id");
            var revision = response.GetProperty("assignment_revision");
            var error = status == "error" ? ParseEnum<PlannerError>(response.GetProperty("code").GetString()) : (PlannerError?)null;
            // A syntactically invalid core request has no assignment identity.
            // It can report an error, but it can never authorize an operation.
            if (!(error == PlannerError.InvalidJson && id.ValueKind == JsonValueKind.Null && revision.ValueKind == JsonValueKind.Null)
                && (id.GetString() != request.Assignment.Id || revision.GetUInt64() != request.Assignment.Revision))
                throw new InvalidDataException("Planner response belongs to another assignment revision.");
            if (error is not null) return new(request, null, error);
            var decision = response.GetProperty("decision");
            var action = ParseEnum<PlannerAction>(decision.GetProperty("action").GetString());
            if (action == PlannerAction.Acquire) PipeProtocol.RequireFields(decision, "action", "reason", "goal_id");
            else PipeProtocol.RequireFields(decision, "action", "reason");
            var reason = decision.GetProperty("reason").GetString();
            if (string.IsNullOrEmpty(reason) || reason.Length > 128 || reason.Any(c => c < '!' || c > '~'))
                throw new InvalidDataException("Invalid planner decision reason.");
            var goal = action == PlannerAction.Acquire ? decision.GetProperty("goal_id").GetString() : null;
            if (action == PlannerAction.Acquire && (string.IsNullOrEmpty(goal)
                || request.Assignment.Goals.IsDefault || request.Assignment.Goals.Count(g => g?.Id == goal) != 1))
                throw new InvalidDataException("Planner selected an unknown or ambiguous goal.");
            return new(request, new(action, reason, goal), null);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Malformed planner response.", error); }
    }

    internal static T ParseEnum<T>(string? value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
            if (JsonNamingPolicy.SnakeCaseLower.ConvertName(candidate.ToString()) == value) return candidate;
        throw new InvalidDataException("Unknown planner enum value.");
    }
}
