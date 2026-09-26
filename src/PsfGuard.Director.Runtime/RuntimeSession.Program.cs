using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

internal sealed partial class RuntimeSession
{
    private DirectorProgram? ledgerProgram;

    internal Task<LedgerResult<LedgerIdentity>> OpenProgramAsync(DirectorProgram program, PlannerState state, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(program.Assignment);
        if (program.Assignment.RigId != rigId) throw new ArgumentException("Program assignment belongs to another rig.");
        ProgramContract.CheckRig(program.Configuration, rigId);
        return SendLedgerAsync(new JsonObject { ["action"] = "open_program", ["program"] = ProgramContract.Encode(program), ["state"] = EncodeState(state) }, true,
            response => LedgerContract.Decode(response, "program_opened", "info", value =>
            {
                if (value.GetProperty("program_version").GetUInt32() != ProgramContract.Version || program.SchemaVersion != ProgramContract.Version)
                    throw new InvalidDataException("Execution program version mismatch.");
                var identity = LedgerContract.ReadIdentity(value.GetProperty("info"), program.Assignment);
                ledgerIdentity = identity;
                ledgerAssignment = program.Assignment;
                ledgerProgram = program;
                return identity;
            }), token);
    }

    internal Task<LedgerResult<PreparationStarted>> BeginProgramPreparationAsync(string id, string goalId, ProgramLocalState local,
        PreparationEstimates estimates, PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        LedgerContract.CheckId(goalId);
        ArgumentNullException.ThrowIfNull(local);
        ProgramContract.CheckRig(local.Configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "begin_program_preparation",
            ["preparation_id"] = id,
            ["goal_id"] = goalId,
            ["local"] = ProgramContract.Encode(local),
            ["estimates"] = ProgramContract.Encode(estimates),
            ["state"] = EncodeState(state)
        }, false, response => LedgerContract.Decode(response, "preparation_started", "record", value =>
        {
            var record = ReadPreparationRecord(value.GetProperty("record"), id);
            var created = value.GetProperty("created").GetBoolean();
            if (record.GoalId != goalId || (created && (record.Lifecycle != PreparationLifecycle.Active
                || record.Pending is not null || record.Halted is not null || !record.Observations.IsEmpty)))
                throw new InvalidDataException("New bound preparation contains inconsistent evidence.");
            return new PreparationStarted(created, record);
        }), token, programBound: true, geometryBound: false);
    }

    internal Task<LedgerResult<PreparationNext>> AdvanceProgramPreparationAsync(string id, DirectorConfiguration configuration,
        PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        ProgramContract.CheckRig(configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "advance_program_preparation",
            ["preparation_id"] = id,
            ["configuration"] = ProgramContract.Encode(configuration),
            ["state"] = EncodeState(state)
        }, false, response => LedgerContract.Decode(response, "preparation_advanced", "next", value =>
        {
            var next = PreparationContract.ReadNext(value.GetProperty("next"), ledgerAssignment!, id);
            ProgramContract.CheckNext(next, ledgerProgram!);
            return next;
        }), token, programBound: true, geometryBound: false);
    }

    internal Task<LedgerResult<LedgerReservation>> ReserveProgramPreparedAsync(string id, string captureId, DirectorConfiguration configuration,
        PlannerState state, CancellationToken token)
    {
        LedgerContract.CheckId(id);
        LedgerContract.CheckId(captureId);
        ProgramContract.CheckRig(configuration, rigId);
        return SendLedgerAsync(new JsonObject
        {
            ["action"] = "reserve_program_prepared",
            ["preparation_id"] = id,
            ["capture_id"] = captureId,
            ["configuration"] = ProgramContract.Encode(configuration),
            ["state"] = EncodeState(state)
        }, false, response => LedgerContract.Decode(response, "reserved", "outcome",
            value => LedgerContract.ReadReservation(value.GetProperty("outcome"), ledgerAssignment!, captureId, state)), token, programBound: true, geometryBound: false);
    }

    internal Task<LedgerResult<CaptureBindingLookup>> FindCaptureBindingAsync(string captureId, CancellationToken token)
    {
        LedgerContract.CheckId(captureId);
        return SendLedgerAsync(new JsonObject { ["action"] = "capture_binding", ["capture_id"] = captureId }, false,
            response => LedgerContract.Decode(response, "capture_binding_found", "binding", value => new CaptureBindingLookup(
                value.GetProperty("binding").ValueKind == JsonValueKind.Null ? null
                    : ProgramContract.ReadBinding(value.GetProperty("binding"), ledgerProgram!, ledgerIdentity!, captureId))), token, programBound: true);
    }

    private PreparationRecord ReadPreparationRecord(JsonElement value, string? id)
    {
        var record = PreparationContract.ReadRecord(value, ledgerAssignment!, id);
        if (ledgerProgram is not null) ProgramContract.CheckRecord(record, ledgerProgram);
        return record;
    }
}
