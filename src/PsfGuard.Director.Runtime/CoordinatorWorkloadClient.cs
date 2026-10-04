using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorWorkloadResult(CoordinatorAllocation? Allocation, int RetryAfterSeconds);

/// <summary>Durable automatic intake under server commissioning. A request is
/// retryable; the separate allocation launch is still strictly one-shot.</summary>
public sealed class CoordinatorWorkloadClient : IDisposable
{
    private sealed record Pending(int SchemaVersion, string Origin, CoordinatorBinding Binding, Guid ClientId, Guid RequestId, string ConfigurationId, bool Submitted, string? AllocationFingerprint, string ExecutionMode = "prepared_target_v1");
    private sealed record Workload(CoordinatorAllocationEnvelope Allocation, bool Released, Guid? LedgerId, ulong? TerminalSequence);
    private sealed record Reply(Guid RequestId, string State, Workload? Workload, int RetryAfterSeconds);
    private readonly CoordinatorTransport transport;
    private readonly CoordinatorStateFile file;
    private readonly CoordinatorBinding binding;
    private readonly Guid clientId;
    private readonly DirectorConfiguration configuration;
    private readonly TimeProvider clock;
    private readonly string executionMode;

    public CoordinatorWorkloadClient(string root, Uri endpoint, CoordinatorBinding binding, Guid clientId,
        DirectorConfiguration configuration, Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false, bool localTargetScheduling = false, bool nativeImaging = false)
        : this(root, endpoint, binding, clientId, configuration, credential, CoordinatorTransport.Handler(), TimeProvider.System, allowInsecureHttp, localTargetScheduling, nativeImaging) { }

    internal CoordinatorWorkloadClient(string root, Uri endpoint, CoordinatorBinding binding, Guid clientId,
        DirectorConfiguration configuration, Func<CancellationToken, ValueTask<string?>> credential, HttpMessageHandler handler,
        TimeProvider clock, bool allowInsecureHttp = false, bool localTargetScheduling = false, bool nativeImaging = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        if (clientId == Guid.Empty || configuration.RigId != binding.RigId.ToString("D")) throw new ArgumentException("Exact client and configuration required.");
        transport = new(endpoint, credential, handler, allowInsecureHttp);
        this.binding = binding; this.clientId = clientId; this.configuration = configuration; this.clock = clock;
        executionMode = nativeImaging ? localTargetScheduling ? "native_imaging_v1" : "native_single_target_v1"
            : localTargetScheduling ? "local_sequence_v3" : "prepared_target_v3";
        file = new(root, new { origin = transport.Endpoint.AbsoluteUri, binding, clientId }, "workload-request");
    }

    public async Task<CoordinatorWorkloadResult> RequestAsync(CancellationToken token = default)
    {
        using var lease = file.Lock();
        var pending = ReadPending();
        file.Write(pending with { Submitted = true });
        var bytes = await Send("request", new
        {
            coordinator_instance_id = binding.CoordinatorInstanceId,
            catalog_id = binding.CatalogId,
            request_id = pending.RequestId,
            configuration_id = configuration.Id,
            execution_mode = pending.ExecutionMode
        }, token).ConfigureAwait(false);
        var reply = Read<Reply>(bytes);
        if (reply.RequestId != pending.RequestId) throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
        if (reply.State == "waiting" && reply.Workload is null && reply.RetryAfterSeconds is >= 5 and <= 300 && pending.AllocationFingerprint is null)
        {
            file.Write(pending with { Submitted = false });
            return new(null, reply.RetryAfterSeconds);
        }
        if (reply.State is not ("issued" or "released") || reply.Workload is null || reply.RetryAfterSeconds != 0
            || reply.Workload.Allocation is null || reply.Workload.Allocation.AllocationId != pending.RequestId || reply.Workload.Released != (reply.State == "released"))
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse);
        var w = reply.Workload;
        // Released history can be expired. Validate its immutable scope at issuance;
        // it is never returned as an equipment permit.
        var allocation = Validate(w.Allocation, w.Released ? w.Allocation.AdmittedAtMs : Now());
        if (pending.AllocationFingerprint is not null && pending.AllocationFingerprint != allocation.Fingerprint)
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
        if (w.Released)
        {
            if (w.LedgerId is null || w.LedgerId == Guid.Empty || w.TerminalSequence is null)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse);
            Advance(pending);
            return new(null, 5);
        }
        if (w.LedgerId is not null || w.TerminalSequence is not null) throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse);
        file.Write(pending with { Submitted = true, AllocationFingerprint = allocation.Fingerprint });
        return new(allocation, 0);
    }

    /// <summary>Call only after parking, stopping hooks/dispatch, verifying no
    /// unresolved capture/preparation, and delivering the complete capture feed.</summary>
    public async Task ReleaseAsync(CoordinatorAllocation allocation, LedgerIdentity ledger, ulong through, CancellationToken token = default)
    {
        using var lease = file.Lock();
        var pending = ReadPending();
        if (pending.RequestId != allocation.Envelope.AllocationId || ledger.AssignmentId != allocation.Envelope.Snapshot.Program.Assignment.Id
            || ledger.AssignmentRevision != allocation.Envelope.Snapshot.Program.Assignment.Revision || ledger.ConfigurationId != configuration.Id
            || ledger.RigId != binding.RigId.ToString("D") || !Guid.TryParseExact(ledger.LedgerId, "D", out var ledgerId) || ledgerId == Guid.Empty)
            throw new InvalidDataException("Terminal workload scope mismatch.");
        await ConfirmArchivedReleaseAsync(allocation, ledger, through, token).ConfigureAwait(false);
        Advance(pending);
    }

    /// <summary>Confirm a durably completed/parked historical run. Does not advance
    /// or overwrite a newer intake request; RequestAsync reconciles released history.</summary>
    public async Task ConfirmArchivedReleaseAsync(CoordinatorAllocation allocation, LedgerIdentity ledger, ulong through, CancellationToken token = default)
    {
        var a = Validate(allocation.Envelope, allocation.Envelope.AdmittedAtMs);
        if (a.Fingerprint != allocation.Fingerprint || ledger.AssignmentId != a.Envelope.Snapshot.Program.Assignment.Id
            || ledger.AssignmentRevision != a.Envelope.Snapshot.Program.Assignment.Revision || ledger.ConfigurationId != configuration.Id
            || ledger.RigId != binding.RigId.ToString("D") || !Guid.TryParseExact(ledger.LedgerId, "D", out var ledgerId) || ledgerId == Guid.Empty)
            throw new InvalidDataException("Archived terminal workload scope mismatch.");
        var bytes = await Send("release", new
        {
            coordinator_instance_id = binding.CoordinatorInstanceId,
            catalog_id = binding.CatalogId,
            allocation_id = allocation.Envelope.AllocationId,
            ledger_id = ledgerId,
            terminal_sequence = through,
            operations_quiescent = true,
            parked = true
        }, token).ConfigureAwait(false);
        var w = Read<Workload>(bytes);
        if (!w.Released || w.LedgerId != ledgerId || w.TerminalSequence != through || w.Allocation is null
            || Validate(w.Allocation, w.Allocation.AdmittedAtMs).Fingerprint != allocation.Fingerprint)
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement);
    }

    private Pending ReadPending()
    {
        var p = file.Read<Pending>();
        if (p is null)
        {
            p = new(1, transport.Endpoint.AbsoluteUri, binding, clientId, Guid.NewGuid(), configuration.Id, false, null, executionMode);
            file.Write(p);
        }
        if (p.SchemaVersion != 1 || p.Origin != transport.Endpoint.AbsoluteUri || p.Binding != binding || p.ClientId != clientId
            || p.RequestId == Guid.Empty || p.ExecutionMode is not ("prepared_target_v1" or "local_sequence_v1" or "prepared_target_v2" or "local_sequence_v2" or "prepared_target_v3" or "local_sequence_v3" or "native_imaging_v1" or "native_single_target_v1")
            || p.Submitted && (p.ConfigurationId != configuration.Id || !CompatibleMode(p.ExecutionMode))
            || p.AllocationFingerprint is not null && (p.AllocationFingerprint.Length != 64 || !p.AllocationFingerprint.All(char.IsAsciiHexDigitLower)))
            throw new InvalidDataException("Outstanding workload request does not match this equipment. Reconciliation is required.");
        if (!p.Submitted && (p.ConfigurationId != configuration.Id || p.ExecutionMode != executionMode))
        { p = p with { ConfigurationId = configuration.Id, ExecutionMode = executionMode }; file.Write(p); }
        return p;
    }
    private bool CompatibleMode(string pendingMode) => pendingMode == executionMode
        || pendingMode is "prepared_target_v1" or "prepared_target_v2" && executionMode == "prepared_target_v3"
        || pendingMode is "local_sequence_v1" or "local_sequence_v2" && executionMode == "local_sequence_v3";
    private void Advance(Pending p) => file.Write(p with { RequestId = Guid.NewGuid(), Submitted = false, AllocationFingerprint = null });
    private ulong Now() => checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());
    private CoordinatorAllocation Validate(CoordinatorAllocationEnvelope a, ulong now) => CoordinatorAllocation.Read(
        CoordinatorAllocation.Wrap(a), transport.Endpoint.AbsoluteUri, binding, clientId, configuration, now);
    private async Task<byte[]> Send(string operation, object body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/workloads/{operation}",
                JsonSerializer.SerializeToUtf8Bytes(body, CoordinatorProgramContract.Options), deadline.Token, binding.ProfileId).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return response.Bytes;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
    }
    private static T Read<T>(byte[] bytes)
    {
        try
        {
            using var d = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var r = d.RootElement;
            CoordinatorProgramContract.CheckTree(r);
            PipeProtocol.RequireFields(r, "success", "data", "error", "status");
            if (r.GetProperty("success").ValueKind != JsonValueKind.True || r.GetProperty("error").ValueKind != JsonValueKind.Null
                || r.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var data = r.GetProperty("data");
            if (typeof(T) == typeof(Reply))
            {
                PipeProtocol.RequireFields(data, "request_id", "state", "workload", "retry_after_seconds");
                if (data.GetProperty("workload").ValueKind != JsonValueKind.Null)
                    PipeProtocol.RequireFields(data.GetProperty("workload"), "allocation", "released", "ledger_id", "terminal_sequence");
            }
            else PipeProtocol.RequireFields(data, "allocation", "released", "ledger_id", "terminal_sequence");
            return data.Deserialize<T>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException();
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse); }
    }
    public void Dispose() => transport.Dispose();
}
