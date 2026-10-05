using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

public sealed partial class CoordinatorCheckpointClient
{
    public Task<CoordinatorCheckpointResult> DeliverPreparationAsync(
        Func<ulong, int, CancellationToken, Task<PreparationEventPage>> readEvents,
        string? programRevision = null, int maxPages = 16, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(readEvents);
        if (feed != CoordinatorEventFeed.Preparation) throw new InvalidOperationException("This cursor belongs to the capture feed.");
        return DeliverPagesAsync(async (after, limit, ct) =>
        {
            var page = await readEvents(after, limit, ct).ConfigureAwait(false);
            if (page.Identity != ledger || page.Events.IsDefault || page.Events.Length > PreparationContract.MaxPage)
                throw new InvalidDataException("Preparation source ledger mismatch.");
            var events = new JsonArray();
            foreach (var item in page.Events)
            {
                if (item.Sequence != checked(after + 1) || item.Sequence > long.MaxValue || !LedgerContract.ValidId(item.PreparationId))
                    throw new InvalidDataException("Invalid preparation event identity or sequence.");
                after = item.Sequence;
                JsonNode data = item.Data switch
                {
                    PreparationEventData.Started started => new JsonObject
                    {
                        ["kind"] = "started",
                        ["context"] = JsonSerializer.SerializeToNode(started.Context, PlannerContract.Options),
                        ["estimates"] = JsonSerializer.SerializeToNode(started.Estimates, PlannerContract.Options)
                    },
                    PreparationEventData.Issued issued when issued.Command.PreparationId == item.PreparationId => new JsonObject
                    {
                        ["kind"] = "issued",
                        ["command"] = PreparationContract.EncodeCommand(issued.Command),
                        ["issued_at_ms"] = issued.IssuedAtMs
                    },
                    PreparationEventData.Completed completed when completed.Observation.Command.PreparationId == item.PreparationId
                        && completed.Observation.Completion.PreparationId == item.PreparationId => new JsonObject
                        {
                            ["kind"] = "completed",
                            ["observation"] = new JsonObject
                            {
                                ["command"] = PreparationContract.EncodeCommand(completed.Observation.Command),
                                ["issued_at_ms"] = completed.Observation.IssuedAtMs,
                                ["completion"] = PreparationContract.EncodeCompletion(completed.Observation.Completion)
                            }
                        },
                    PreparationEventData.Halted halted when halted.Decision.Action != PlannerAction.Acquire && LedgerContract.ValidId(halted.Decision.Reason) => new JsonObject
                    {
                        ["kind"] = "halted",
                        ["decision"] = new JsonObject
                        {
                            ["action"] = JsonSerializer.SerializeToNode(halted.Decision.Action, PlannerContract.Options),
                            ["reason"] = halted.Decision.Reason
                        }
                    },
                    PreparationEventData.Closed => new JsonObject { ["kind"] = "closed" },
                    PreparationEventData.Captured captured when LedgerContract.ValidId(captured.CaptureId) => new JsonObject
                    {
                        ["kind"] = "captured",
                        ["capture_id"] = captured.CaptureId
                    },
                    _ => throw new InvalidDataException("Invalid preparation event.")
                };
                events.Add(new JsonObject
                {
                    ["schema_version"] = 1,
                    ["ledger_id"] = ledger.LedgerId,
                    ["sequence"] = item.Sequence,
                    ["contract_version"] = RuntimeContract.ContractVersion,
                    ["engine_version"] = RuntimeContract.EngineVersion,
                    ["assignment_id"] = ledger.AssignmentId,
                    ["assignment_revision"] = ledger.AssignmentRevision,
                    ["rig_id"] = ledger.RigId,
                    ["configuration_id"] = ledger.ConfigurationId,
                    ["preparation_id"] = item.PreparationId,
                    ["event"] = data
                });
            }
            if (page.NextCursor != after) throw new InvalidDataException("Preparation source cursor mismatch.");
            return new EncodedPage(events, page.NextCursor);
        }, programRevision, maxPages, token);
    }
}
