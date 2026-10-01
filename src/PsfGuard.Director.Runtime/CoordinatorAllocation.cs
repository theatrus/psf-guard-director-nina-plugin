using System.Security.Cryptography;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorAllocationEnvelope(int SchemaVersion, Guid AllocationId, Guid CoordinatorInstanceId,
    Guid CatalogId, Guid RigId, Guid ClientId, Guid ProfileId, string PreviewRevision, ulong AdmittedAtMs,
    CoordinatorProgramEnvelope Snapshot);

/// <summary>An operator-issued immutable allocation. Local safety, ownership,
/// ledger recovery and fresh dispatch checks are still required before equipment work.</summary>
public sealed class CoordinatorAllocation
{
    public CoordinatorAllocationEnvelope Envelope { get; }
    internal CoordinatorProgramPreview ValidatedSnapshot { get; }
    public string Fingerprint { get; }
    internal CoordinatorAllocation(CoordinatorAllocationEnvelope envelope, CoordinatorProgramPreview snapshot, string fingerprint)
        => (Envelope, ValidatedSnapshot, Fingerprint) = (envelope, snapshot, fingerprint);

    internal static CoordinatorAllocation Read(byte[] bytes, string origin, CoordinatorBinding binding, Guid clientId,
        DirectorConfiguration configuration, ulong now)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            CoordinatorProgramContract.CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var envelope = root.GetProperty("data").Deserialize<CoordinatorAllocationEnvelope>(CoordinatorProgramContract.Options)
                ?? throw new InvalidDataException();
            if (envelope.SchemaVersion != 1 || envelope.AllocationId == Guid.Empty || clientId == Guid.Empty
                || envelope.ClientId != clientId || envelope.ProfileId != binding.ProfileId
                || envelope.CoordinatorInstanceId != binding.CoordinatorInstanceId || envelope.CatalogId != binding.CatalogId
                || envelope.RigId != binding.RigId) throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
            var assignment = envelope.Snapshot.Program.Assignment;
            if (assignment.Id != $"allocation-{envelope.AllocationId:D}" || envelope.PreviewRevision.Length != 64
                || envelope.PreviewRevision.Any(c => !char.IsAsciiHexDigitLower(c))
                || envelope.AdmittedAtMs < envelope.Snapshot.IssuedAtMs || envelope.AdmittedAtMs > now
                || envelope.AdmittedAtMs >= assignment.ExpiresAtMs || assignment.ExpiresAtMs - assignment.ValidFromMs > 86400000)
                throw new InvalidDataException();
            var snapshot = CoordinatorProgramContract.Read(Wrap(envelope.Snapshot), $"\"{envelope.Snapshot.Revision}\"",
                origin, binding, configuration, now, null);
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope, CoordinatorProgramContract.Options)));
            return new(envelope, snapshot, fingerprint);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or NotSupportedException or OverflowException or FormatException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse); }
    }

    internal static byte[] Wrap<T>(T data) => JsonSerializer.SerializeToUtf8Bytes(new { success = true, data, error = (string?)null, status = "ready" }, CoordinatorProgramContract.Options);
}

/// <summary>Explicit immutable allocation intake; never silently falls back to a preview or cached grant on HTTP errors.</summary>
public sealed class CoordinatorAllocationClient : IDisposable
{
    private readonly CoordinatorTransport transport;
    private readonly TimeProvider clock;
    public CoordinatorAllocationClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false)
        : this(endpoint, credential, CoordinatorTransport.Handler(), TimeProvider.System, allowInsecureHttp) { }
    internal CoordinatorAllocationClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, TimeProvider clock, bool allowInsecureHttp = false)
    { transport = new(endpoint, credential, handler, allowInsecureHttp); this.clock = clock; }

    public async Task<CoordinatorAllocation> ReadAsync(CoordinatorBinding binding, Guid clientId, DirectorConfiguration configuration,
        CancellationToken token = default)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        if (clientId == Guid.Empty || configuration.RigId != binding.RigId.ToString("D")) throw new ArgumentException("Exact client and rig configuration required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/allocation?coordinator_instance_id={binding.CoordinatorInstanceId:D}&catalog_id={binding.CatalogId:D}",
                null, deadline.Token, binding.ProfileId).ConfigureAwait(false);
            var allocation = CoordinatorAllocation.Read(response.Bytes, transport.Endpoint.AbsoluteUri, binding, clientId, configuration,
                checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds()));
            deadline.Token.ThrowIfCancellationRequested();
            return allocation;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
    }
    public void Dispose() => transport.Dispose();
}

/// <summary>Persisted intake, not a hardware permit. One pairing scope retains one
/// allocation; neither expiry nor configuration changes erase its outstanding budget.</summary>
public sealed class CoordinatorAllocationCache
{
    private sealed record Entry(int SchemaVersion, string Origin, CoordinatorBinding Binding, Guid ClientId,
        string Fingerprint, CoordinatorAllocationEnvelope Envelope);
    private readonly CoordinatorStateFile file;
    private readonly string origin;
    private readonly CoordinatorBinding binding;
    private readonly Guid clientId;
    private readonly DirectorConfiguration configuration;
    public CoordinatorAllocationCache(string root, Uri endpoint, CoordinatorBinding binding, Guid clientId,
        DirectorConfiguration configuration, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        if (clientId == Guid.Empty) throw new ArgumentException("A paired client is required.");
        origin = CoordinatorTransport.Normalize(endpoint, allowInsecureHttp).AbsoluteUri;
        this.binding = binding; this.clientId = clientId; this.configuration = configuration;
        // Do not include configuration/allocation IDs: changes must not choose a
        // fresh empty cache and conceal an outstanding allocation.
        file = new(root, new { origin, binding, clientId }, "allocation");
    }
    public CoordinatorAllocation? Read(ulong now)
    {
        var entry = file.Read<Entry>();
        if (entry is null) return null;
        if (entry.SchemaVersion != 1 || entry.Origin != origin || entry.Binding != binding || entry.ClientId != clientId)
            throw new InvalidDataException("Director allocation cache identity mismatch.");
        var result = CoordinatorAllocation.Read(CoordinatorAllocation.Wrap(entry.Envelope), origin, binding, clientId, configuration, now);
        if (result.Fingerprint != entry.Fingerprint) throw new InvalidDataException("Director allocation cache integrity mismatch.");
        return result;
    }
    public void Store(CoordinatorAllocation allocation, ulong now)
    {
        if (allocation.ValidatedSnapshot.Origin != origin || allocation.ValidatedSnapshot.Binding != binding)
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
        var value = CoordinatorAllocation.Read(CoordinatorAllocation.Wrap(allocation.Envelope), origin, binding, clientId, configuration, now);
        using var lease = file.Lock();
        var old = file.Read<Entry>();
        if (old is not null && (old.SchemaVersion != 1 || old.Origin != origin || old.Binding != binding || old.ClientId != clientId
            || old.Fingerprint != value.Fingerprint || JsonSerializer.Serialize(old.Envelope, CoordinatorProgramContract.Options)
                != JsonSerializer.Serialize(value.Envelope, CoordinatorProgramContract.Options)))
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ChangedImmutableProgram);
        file.Write(new Entry(1, origin, binding, clientId, value.Fingerprint, value.Envelope));
    }
}
