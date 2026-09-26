using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

internal sealed partial class RuntimeSession
{
    internal Task<LedgerResult<PlannerDecision>> CheckGeometryPendingDispatchAsync(PreparationCommand command,
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
        }, false, response => ReadDispatch(response, command.GoalId), token, programBound: true, geometryBound: true);
    }

    internal Task<LedgerResult<PlannerDecision>> CheckGeometryCaptureDispatchAsync(string id, LedgerAttempt attempt,
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
        }, false, response => ReadDispatch(response, attempt.GoalId), token, programBound: true, geometryBound: true);
    }

    private LedgerResult<PlannerDecision> ReadDispatch(JsonElement response, string expectedGoal) =>
        LedgerContract.Decode(response, "dispatch_checked", "decision", value =>
        {
            var decision = PreparationContract.ReadDecision(value.GetProperty("decision"), ledgerAssignment!, true);
            if (decision.Action == PlannerAction.Acquire && decision.GoalId != expectedGoal)
                throw new InvalidDataException("Dispatch feasibility changed the authorized goal.");
            return decision;
        });
}
