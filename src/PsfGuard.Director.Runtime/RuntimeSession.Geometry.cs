using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

internal sealed partial class RuntimeSession
{
    private bool geometryLedger;

    internal Task<LedgerResult<LedgerIdentity>> OpenGeometryAsync(DirectorProgram program, DirectorConstraints constraints,
        PlannerState state, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(program.Assignment);
        if (program.Assignment.RigId != rigId) throw new ArgumentException("Program assignment belongs to another rig.");
        ProgramContract.CheckRig(program.Configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "open_geometry",
            ["program"] = ProgramContract.Encode(program),
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, true, response => LedgerContract.Decode(response, "geometry_opened", "info", value =>
        {
            if (value.GetProperty("program_version").GetUInt32() != ProgramContract.Version || program.SchemaVersion != ProgramContract.Version
                || value.GetProperty("constraints_version").GetUInt32() != GeometryContract.Version || constraints.SchemaVersion != GeometryContract.Version)
                throw new InvalidDataException("Geometry program or constraints version mismatch.");
            var identity = LedgerContract.ReadIdentity(value.GetProperty("info"), program.Assignment);
            ledgerIdentity = identity;
            ledgerAssignment = program.Assignment;
            ledgerProgram = program;
            geometryLedger = true;
            return identity;
        }), token);
    }

    internal Task<LedgerResult<PlannerDecision>> EvaluateGeometryAsync(DirectorConstraints constraints, PlannerState state, CancellationToken token) =>
        SendLedgerAsync(new JsonObject
        {
            ["action"] = "evaluate_geometry",
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, false, response => LedgerContract.Decode(response, "evaluated", "decision",
            value => PreparationContract.ReadDecision(value.GetProperty("decision"), ledgerAssignment!, true)), token, programBound: true, geometryBound: true);

    internal Task<LedgerResult<PreparationStarted>> BeginGeometryPreparationAsync(string id, string goalId, ProgramLocalState local,
        PreparationEstimates estimates, DirectorConstraints constraints, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        LedgerContract.CheckId(goalId);
        ArgumentNullException.ThrowIfNull(local);
        ProgramContract.CheckRig(local.Configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "begin_geometry_preparation",
            ["preparation_id"] = id,
            ["goal_id"] = goalId,
            ["local"] = ProgramContract.Encode(local),
            ["estimates"] = ProgramContract.Encode(estimates),
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, false, response => LedgerContract.Decode(response, "preparation_started", "record", value =>
        {
            var record = ReadPreparationRecord(value.GetProperty("record"), id);
            var created = value.GetProperty("created").GetBoolean();
            if (record.GoalId != goalId || (created && (record.Lifecycle != PreparationLifecycle.Active
                || record.Pending is not null || record.Halted is not null || !record.Observations.IsEmpty)))
                throw new InvalidDataException("New geometry preparation contains inconsistent evidence.");
            return new PreparationStarted(created, record);
        }), token, programBound: true, geometryBound: true);
    }

    internal Task<LedgerResult<PreparationNext>> AdvanceGeometryPreparationAsync(string id, DirectorConfiguration configuration,
        DirectorConstraints constraints, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        ProgramContract.CheckRig(configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "advance_geometry_preparation",
            ["preparation_id"] = id,
            ["configuration"] = ProgramContract.Encode(configuration),
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, false, response => LedgerContract.Decode(response, "preparation_advanced", "next", value =>
        {
            var next = PreparationContract.ReadNext(value.GetProperty("next"), ledgerAssignment!, id);
            ProgramContract.CheckNext(next, ledgerProgram!);
            return next;
        }), token, programBound: true, geometryBound: true);
    }

    internal Task<LedgerResult<LedgerReservation>> ReserveGeometryPreparedAsync(string id, string captureId, DirectorConfiguration configuration,
        DirectorConstraints constraints, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        LedgerContract.CheckId(captureId);
        ProgramContract.CheckRig(configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "reserve_geometry_prepared",
            ["preparation_id"] = id,
            ["capture_id"] = captureId,
            ["configuration"] = ProgramContract.Encode(configuration),
            ["constraints"] = GeometryContract.Encode(constraints, rigId),
            ["state"] = EncodeState(state)
        }, false, response => LedgerContract.Decode(response, "reserved", "outcome",
            value => LedgerContract.ReadReservation(value.GetProperty("outcome"), ledgerAssignment!, captureId, state)), token, programBound: true, geometryBound: true);
    }
}
