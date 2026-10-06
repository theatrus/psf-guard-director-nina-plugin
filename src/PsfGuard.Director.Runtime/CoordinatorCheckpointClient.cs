using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorCheckpointResult(ulong AcknowledgedThrough, int DeliveredEvents, bool CaughtUp, bool ProgramChanged);
public enum CoordinatorEventFeed { Capture, Preparation }

/// <summary>Delivers one independently acknowledged ledger feed. Never dispatches equipment or changes allocations.</summary>
public sealed partial class CoordinatorCheckpointClient : IDisposable
{
    private sealed record EncodedPage(JsonArray Events, ulong NextCursor);
    private readonly CoordinatorEventFeed feed;
    private sealed record Cursor(int SchemaVersion, string Origin, CoordinatorBinding Binding, LedgerIdentity Ledger, ulong Through,
        string? ProgramRevision, string Checksum);
    private sealed record Acknowledgement(Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId, string LedgerId,
        ulong AcknowledgedThrough, ulong HighestSeen, ImmutableArray<string> Outcomes, int Applied, int Duplicates,
        ImmutableArray<ulong> Conflicts, string? ProgramRevision, bool ProgramChanged, ulong ReceivedAtMs);
    private readonly CoordinatorTransport transport;
    private readonly CoordinatorBinding binding;
    private readonly LedgerIdentity ledger;
    private readonly CoordinatorStateFile cursorFile;
    private readonly TimeSpan timeout;

    public CoordinatorCheckpointClient(string stateRoot, Uri endpoint, CoordinatorBinding binding, LedgerIdentity ledger,
        Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false, CoordinatorEventFeed feed = CoordinatorEventFeed.Capture)
        : this(stateRoot, endpoint, binding, ledger, credential, CoordinatorTransport.Handler(), TimeSpan.FromSeconds(30), allowInsecureHttp, feed) { }

    internal CoordinatorCheckpointClient(string stateRoot, Uri endpoint, CoordinatorBinding binding, LedgerIdentity ledger,
        Func<CancellationToken, ValueTask<string?>> credential, HttpMessageHandler handler, TimeSpan timeout, bool allowInsecureHttp = false,
        CoordinatorEventFeed feed = CoordinatorEventFeed.Capture)
    {
        ValidateBinding(binding);
        if (!Guid.TryParseExact(ledger.LedgerId, "D", out var id) || id == Guid.Empty || ledger.RigId != binding.RigId.ToString("D")
            || !LedgerContract.ValidId(ledger.AssignmentId) || !LedgerContract.ValidId(ledger.ConfigurationId) || ledger.AssignmentRevision == 0)
            throw new ArgumentException("Invalid checkpoint ledger identity.", nameof(ledger));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.binding = binding;
        this.ledger = ledger;
        this.timeout = timeout;
        if (!Enum.IsDefined(feed)) throw new ArgumentOutOfRangeException(nameof(feed));
        this.feed = feed;
        transport = new(endpoint, credential, handler, allowInsecureHttp);
        cursorFile = new(stateRoot, new { origin = transport.Endpoint.AbsoluteUri, binding, ledger },
            feed == CoordinatorEventFeed.Capture ? "capture-cursor" : "preparation-cursor");
    }

    internal static void ValidateBinding(CoordinatorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.CoordinatorInstanceId == Guid.Empty || binding.CatalogId == Guid.Empty || binding.RigId == Guid.Empty || binding.ProfileId == Guid.Empty)
            throw new ArgumentException("An exact coordinator/catalog/rig/profile binding is required.", nameof(binding));
    }

    public Task<CoordinatorCheckpointResult> DeliverAsync(
        Func<ulong, int, CancellationToken, Task<LedgerEventPage>> readCaptureEvents,
        string? programRevision = null, int maxPages = 16, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(readCaptureEvents);
        if (feed != CoordinatorEventFeed.Capture) throw new InvalidOperationException("This cursor belongs to the preparation feed.");
        return DeliverPagesAsync(async (after, limit, ct) =>
        {
            var page = await readCaptureEvents(after, limit, ct).ConfigureAwait(false);
            return new EncodedPage(Encode(page, after), page.NextCursor);
        }, programRevision, maxPages, token);
    }

    private async Task<CoordinatorCheckpointResult> DeliverPagesAsync(
        Func<ulong, int, CancellationToken, Task<EncodedPage>> readEvents,
        string? programRevision, int maxPages, CancellationToken token)
    {
        if (maxPages is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxPages));
        if (programRevision is not null && !ValidRevision(programRevision)) throw new ArgumentException("Invalid program revision.", nameof(programRevision));
        // Serialize delivery across instances/processes; never overwrite a newer cursor with a stale one.
        using var lease = cursorFile.Lock();
        var persisted = cursorFile.Read<Cursor>();
        if (persisted is not null && (persisted.SchemaVersion != 1 || persisted.Origin != transport.Endpoint.AbsoluteUri
            || persisted.Binding != binding || persisted.Ledger != ledger || persisted.Through > long.MaxValue
            || persisted.ProgramRevision is not null && !ValidRevision(persisted.ProgramRevision)
            || persisted.Checksum != CursorChecksum(persisted.Through, persisted.ProgramRevision)))
            throw new InvalidDataException("Director checkpoint cursor identity mismatch.");
        var through = persisted?.Through ?? 0;
        var delivered = 0;
        var observedRevision = persisted?.ProgramRevision;
        bool Changed() => observedRevision is not null && observedRevision != programRevision;
        for (var pageNumber = 0; pageNumber < maxPages; pageNumber++)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            try
            {
                var page = await readEvents(through, feed == CoordinatorEventFeed.Capture ? LedgerContract.MaxPage : PreparationContract.MaxPage,
                    deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                var events = page.Events;
                deadline.Token.ThrowIfCancellationRequested();
                if (events.Count == 0) return new(through, delivered, true, Changed());
                var request = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    coordinator_instance_id = binding.CoordinatorInstanceId,
                    catalog_id = binding.CatalogId,
                    ledger_id = ledger.LedgerId,
                    program_revision = programRevision,
                    events
                }, CoordinatorProgramContract.Options);
                if (request.Length > PlannerContract.MaxRequestBytes) throw new InvalidDataException("Director checkpoint page is too large.");
                var route = feed == CoordinatorEventFeed.Capture ? "checkin" : "operations";
                var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/{route}", request,
                    deadline.Token, binding.ProfileId).ConfigureAwait(false);
                var ack = ReadAcknowledgement(response.Bytes, page, programRevision);
                deadline.Token.ThrowIfCancellationRequested();
                // Only acknowledge this page, even if the server already knows later events.
                observedRevision = ack.ProgramRevision ?? observedRevision;
                cursorFile.Write(new Cursor(1, transport.Endpoint.AbsoluteUri, binding, ledger, page.NextCursor,
                    observedRevision, CursorChecksum(page.NextCursor, observedRevision)));
                through = page.NextCursor;
                delivered += page.Events.Count;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        }
        return new(through, delivered, false, Changed());
    }

    private JsonArray Encode(LedgerEventPage page, ulong after)
    {
        if (page.Identity != ledger || page.Events.IsDefault || page.Events.Length > LedgerContract.MaxPage)
            throw new InvalidDataException("Checkpoint source ledger mismatch.");
        var result = new JsonArray();
        foreach (var item in page.Events)
        {
            if (item.Sequence != checked(after + 1) || item.Sequence > long.MaxValue || item.Attempt is null
                || !LedgerContract.ValidId(item.Attempt.CaptureId) || !LedgerContract.ValidId(item.Attempt.GoalId))
                throw new InvalidDataException("Invalid checkpoint event sequence or identity.");
            after = item.Sequence;
            var evidence = item.Attempt.Evidence is LedgerEvidence.Reserved
                ? new JsonObject { ["state"] = "reserved" } : LedgerContract.EncodeEvidence(item.Attempt.Evidence);
            result.Add(new JsonObject
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
                ["attempt"] = new JsonObject
                {
                    ["capture_id"] = item.Attempt.CaptureId,
                    ["goal_id"] = item.Attempt.GoalId,
                    ["reserved_at_ms"] = item.Attempt.ReservedAtMs,
                    ["evidence"] = evidence
                }
            });
        }
        if (page.NextCursor != after) throw new InvalidDataException("Checkpoint source cursor mismatch.");
        return result;
    }

    private Acknowledgement ReadAcknowledgement(byte[] bytes, EncodedPage page, string? revision)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            CoordinatorProgramContract.CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var ack = root.GetProperty("data").Deserialize<Acknowledgement>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException();
            if (ack.CoordinatorInstanceId != binding.CoordinatorInstanceId || ack.CatalogId != binding.CatalogId
                || ack.RigId != binding.RigId || ack.LedgerId != ledger.LedgerId || ack.Outcomes.Length != page.Events.Count
                || ack.Outcomes.Any(o => o is not ("applied" or "duplicate" or "conflict"))
                || ack.Applied != ack.Outcomes.Count(o => o == "applied") || ack.Duplicates != ack.Outcomes.Count(o => o == "duplicate")
                || !ack.Conflicts.SequenceEqual(ack.Outcomes.Select((o, i) => (o, i)).Where(x => x.o == "conflict").Select(x => page.Events[x.i]!["sequence"]!.GetValue<ulong>()))
                || ack.HighestSeen < ack.AcknowledgedThrough || ack.HighestSeen > long.MaxValue
                || ack.ProgramRevision is not null && !ValidRevision(ack.ProgramRevision)
                || ack.ProgramChanged != (ack.ProgramRevision is not null && ack.ProgramRevision != revision)) throw new InvalidDataException();
            if (ack.Conflicts.Length != 0) throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ReceiptConflict);
            if (ack.AcknowledgedThrough < page.NextCursor) throw new InvalidDataException();
            return ack;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement); }
    }

    private static bool ValidRevision(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    private string CursorChecksum(ulong through, string? programRevision) => HashEncoding.Lower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        feed == CoordinatorEventFeed.Capture
            ? (object)new { origin = transport.Endpoint.AbsoluteUri, binding, ledger, through, programRevision }
            : new { origin = transport.Endpoint.AbsoluteUri, binding, ledger, through, programRevision, feed = "preparation" }, CoordinatorProgramContract.Options)));
    public void Dispose() => transport.Dispose();
}
