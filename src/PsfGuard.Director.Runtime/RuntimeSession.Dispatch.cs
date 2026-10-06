using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

internal sealed partial class RuntimeSession
{
    internal Task<LedgerResult<PlannerDispatchCheck>> CheckQualityProbeAsync(string goal, string attempt,
        ExposureRecipe recipe, DirectorConstraints constraints, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(goal);
        LedgerContract.CheckId(attempt);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "check_quality_probe",
            ["goal_id"] = goal,
            ["attempt_id"] = attempt,
            ["recipe"] = ProgramContract.Encode(recipe),
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, false, response => ReadDispatch(response, goal, state), token, programBound: true, geometryBound: true);
    }
    internal Task<LedgerResult<PlannerDispatchCheck>> CheckGeometryPendingDispatchAsync(PreparationCommand command,
        DirectorConfiguration configuration, DirectorConstraints constraints, PlannerState state, CancellationToken token)
    {
        var encoded = PreparationContract.EncodeCommand(command);
        ProgramContract.CheckRig(configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "check_geometry_pending_dispatch",
            ["command"] = encoded,
            ["configuration"] = ProgramContract.Encode(configuration),
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, false, response => ReadDispatch(response, command.GoalId, state), token, programBound: true, geometryBound: true);
    }

    internal Task<LedgerResult<PlannerDispatchCheck>> CheckGeometryCaptureDispatchAsync(string id, LedgerAttempt attempt,
        DirectorConfiguration configuration, DirectorConstraints constraints, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        ArgumentNullException.ThrowIfNull(attempt);
        LedgerContract.CheckId(attempt.CaptureId);
        LedgerContract.CheckId(attempt.GoalId);
        if (attempt.Evidence is not LedgerEvidence.Reserved) throw new ArgumentException("Expected the original reserved attempt.");
        ProgramContract.CheckRig(configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "check_geometry_capture_dispatch",
            ["preparation_id"] = id,
            ["capture_id"] = attempt.CaptureId,
            ["configuration"] = ProgramContract.Encode(configuration),
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, false, response => ReadDispatch(response, attempt.GoalId, state), token, programBound: true, geometryBound: true);
    }

    private LedgerResult<PlannerDispatchCheck> ReadDispatch(JsonElement response, string expectedGoal, PlannerState state) =>
        LedgerContract.Decode(response, "dispatch_checked", "decision", value =>
        {
            var decision = PreparationContract.ReadDecision(value.GetProperty("decision"), ledgerAssignment!, true);
            if (decision.Action == PlannerAction.Acquire && decision.GoalId != expectedGoal)
                throw new InvalidDataException("Dispatch feasibility changed the authorized goal.");
            var evaluated = value.GetProperty("evaluated_at_ms").GetUInt64();
            var deadline = value.GetProperty("latest_start_ms");
            ulong? latest = deadline.ValueKind == JsonValueKind.Null ? null : deadline.GetUInt64();
            if (evaluated != state.NowMs || (decision.Action == PlannerAction.Acquire
                    ? latest is null || latest < evaluated || latest >= state.ConditionsValidUntilMs || latest >= ledgerAssignment!.ExpiresAtMs
                    : latest is not null))
                throw new InvalidDataException("Dispatch deadline does not match the evaluated allocation and conditions.");
            return new PlannerDispatchCheck(decision, evaluated, latest);
        });
}
