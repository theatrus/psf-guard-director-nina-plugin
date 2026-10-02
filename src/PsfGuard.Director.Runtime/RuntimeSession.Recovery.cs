using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

internal sealed partial class RuntimeSession
{
    private RecoveryRecord? recoveryRecord;

    internal Task<RecoveryResult<RecoveryOpened>> OpenRecoveryAsync(RecoveryIdentity identity, RecoveryPolicy policy, ulong nowMs, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(policy);
        if (identity.RigId != rigId) throw new ArgumentException("Recovery belongs to another rig.");
        return SendRecoveryAsync(new JsonObject
        {
            ["action"] = "open",
            ["identity"] = JsonSerializer.SerializeToNode(identity, RecoveryContract.Options),
            ["policy"] = JsonSerializer.SerializeToNode(policy, RecoveryContract.Options),
            ["now_ms"] = nowMs
        }, response => RecoveryContract.Decode(response, "opened", value =>
        {
            PipeProtocol.RequireFields(value, "status", "created", "record");
            var record = RecoveryContract.Record(value.GetProperty("record"), rigId, identity.NightId, identity.ConfigurationId);
            var created = value.GetProperty("created").GetBoolean();
            if (record.Snapshot.Identity != identity || record.Snapshot.Policy != policy || created && record.Revision != 0)
                throw new InvalidDataException("Recovery admission changed its identity or policy.");
            if (recoveryRecord is { } previous && previous.Snapshot.Identity != identity)
            {
                if (!created || previous.Snapshot.Phase is not RecoveryPhase.Stopped
                    || identity.StartsAtMs < previous.Snapshot.Identity.EndsAtMs)
                    throw new InvalidDataException("Recovery replaced an active or overlapping night.");
                recoveryRecord = null;
            }
            TrackRecovery(record);
            return new RecoveryOpened(created, record);
        }), token);
    }

    internal Task<RecoveryResult<RecoveryCurrent>> ReadRecoveryAsync(CancellationToken token) =>
        SendRecoveryAsync(new JsonObject { ["action"] = "current" }, response => RecoveryContract.Decode(response, "current", value =>
        {
            PipeProtocol.RequireFields(value, "status", "record");
            var record = value.GetProperty("record").ValueKind == JsonValueKind.Null ? null
                : RecoveryContract.Record(value.GetProperty("record"), rigId);
            if (record is not null) TrackRecovery(record);
            else if (recoveryRecord is not null) throw new InvalidDataException("Recovery state disappeared.");
            return new RecoveryCurrent(record);
        }), token);

    internal Task<RecoveryResult<RecoveryApplied>> ApplyRecoveryAsync(RecoveryRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRecoveryAsync(new JsonObject { ["action"] = "apply", ["request"] = JsonSerializer.SerializeToNode(request, RecoveryContract.Options) },
            response => RecoveryContract.Decode(response, "applied", value =>
            {
                PipeProtocol.RequireFields(value, "status", "newly_applied", "record", "issued");
                var record = RecoveryContract.Record(value.GetProperty("record"), rigId, request.NightId, request.ConfigurationId);
                var newly = value.GetProperty("newly_applied").GetBoolean();
                var issued = value.GetProperty("issued").ValueKind == JsonValueKind.Null ? null : RecoveryContract.Read<RecoveryIssued>(value.GetProperty("issued"));
                if (record.Revision < checked(request.ExpectedRevision + 1) || newly && record.Revision != request.ExpectedRevision + 1
                    || record.Snapshot.LastEventMs < request.NowMs || newly && record.Snapshot.LastEventMs != request.NowMs)
                    throw new InvalidDataException("Recovery result does not match its event revision or time.");
                if (issued is not null)
                {
                    var matches = (request.Event, record.Snapshot.Phase, issued.Operation) switch
                    {
                        (RecoveryEvent.BeginRecovery begin, RecoveryPhase.Recovering phase, "probe") =>
                            begin.AttemptId == issued.AttemptId && phase.AttemptId == issued.AttemptId && phase.DeadlineMs == issued.DeadlineMs
                            && phase.StartedAtMs == request.NowMs && request.Conditions.Safety == PlannerSafety.Safe,
                        (RecoveryEvent.BeginPark begin, RecoveryPhase.Stopping phase, "park") =>
                            begin.AttemptId == issued.AttemptId && phase.ParkAttemptId == issued.AttemptId && phase.DeadlineMs == issued.DeadlineMs,
                        _ => false
                    };
                    if (!newly || !matches || request.Conditions.Motion != RecoveryMotion.Permitted || issued.DeadlineMs <= request.NowMs
                        || issued.DeadlineMs - request.NowMs > record.Snapshot.Policy.OperationTimeoutMs)
                        throw new InvalidDataException("Unexpected, replayed or expired recovery issuance.");
                }
                TrackRecovery(record);
                return new RecoveryApplied(newly, record, issued);
            }), token);
    }

    internal Task<RecoveryResult<RecoveryPage>> ReadRecoveryEventsAsync(string nightId, ulong after, int limit, CancellationToken token)
    {
        LedgerContract.CheckId(nightId);
        if (limit is < 1 or > 16 || after > 100000) throw new ArgumentOutOfRangeException(nameof(limit));
        return SendRecoveryAsync(new JsonObject { ["action"] = "events", ["night_id"] = nightId, ["after"] = after, ["limit"] = limit },
            response => RecoveryContract.Decode(response, "events", value =>
            {
                PipeProtocol.RequireFields(value, "status", "events", "next_cursor");
                var events = RecoveryContract.Read<System.Collections.Immutable.ImmutableArray<RecoveryJournalEvent>>(value.GetProperty("events"));
                if (events.IsDefault || events.Length > limit) throw new InvalidDataException("Recovery page exceeds its limit.");
                var cursor = after;
                foreach (var item in events)
                {
                    if (item.Revision != checked(cursor + 1) || item.Request.ExpectedRevision != cursor || item.Request.NightId != nightId)
                        throw new InvalidDataException("Recovery page skipped or changed event identity.");
                    cursor = item.Revision;
                }
                if (value.GetProperty("next_cursor").GetUInt64() != cursor) throw new InvalidDataException("Recovery page cursor mismatch.");
                return new RecoveryPage(events, cursor);
            }), token);
    }

    private void TrackRecovery(RecoveryRecord record)
    {
        if (recoveryRecord is { } previous && (record.Snapshot.Identity != previous.Snapshot.Identity
            || record.Snapshot.Policy != previous.Snapshot.Policy || record.Revision < previous.Revision
            || record.Snapshot.LastEventMs < previous.Snapshot.LastEventMs
            || record.Revision == previous.Revision && JsonSerializer.Serialize(record, RecoveryContract.Options)
                != JsonSerializer.Serialize(previous, RecoveryContract.Options)))
            throw new InvalidDataException("Recovery identity, policy or progress changed unexpectedly.");
        recoveryRecord = record;
    }

    private async Task<T> SendRecoveryAsync<T>(JsonObject operation, Func<JsonElement, T> decode, CancellationToken token)
    {
        if (!recoveryEnabled) throw new InvalidOperationException("Recovery storage was not enabled for this runtime.");
        RecoveryContract.CheckJson(JsonSerializer.SerializeToElement(operation));
        LedgerContract.Encode(operation);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsReady) throw new IOException("Director runtime session is unavailable.");
            if (operation["action"]!.GetValue<string>() == "apply" && recoveryRecord is null)
                throw new InvalidOperationException("Read or open the recovery night before applying events.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var payload = await ExchangeAsync(checked(nextId++), new JsonObject
            {
                ["type"] = "recovery",
                ["operation"] = new JsonObject { ["recovery_version"] = RecoveryContract.Version, ["operation"] = operation }
            }, "recovery", deadline.Token).ConfigureAwait(false);
            var result = decode(payload.GetProperty("response"));
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch { Abort(); throw; }
        finally { gate.Release(); }
    }
}
