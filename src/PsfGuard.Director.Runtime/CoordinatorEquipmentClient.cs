using System.Collections.Immutable;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorEquipmentAcknowledgement(Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId,
    Guid ProfileId, Guid ClientId, Guid ReportId, ulong ReceivedAtMs, ulong? AcceptedRevision);

// Equipment evidence is pending operator review, never an acquisition permit.
public sealed class CoordinatorEquipmentClient : IDisposable
{
    private readonly CoordinatorTransport transport;
    public CoordinatorEquipmentClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false)
        : this(endpoint, credential, CoordinatorTransport.Handler(), allowInsecureHttp) { }
    internal CoordinatorEquipmentClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, bool allowInsecureHttp = false) => transport = new(endpoint, credential, handler, allowInsecureHttp);

    public async Task<CoordinatorEquipmentAcknowledgement> ReportAsync(CoordinatorBinding binding, Guid clientId, Guid reportId,
        DirectorConfiguration configuration, ImmutableDictionary<string, string> filterNames, ulong observedAtMs, CancellationToken token = default)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        if (clientId == Guid.Empty || reportId == Guid.Empty || configuration.RigId != binding.RigId.ToString("D")
            || observedAtMs == 0 || observedAtMs > 4102444800000UL || filterNames.Count == 0
            || filterNames.Count != configuration.Filters.Length || configuration.Filters.Any(f => !filterNames.ContainsKey(f.Id)))
            throw new ArgumentException("Equipment evidence requires the exact pairing, rig, filter mapping and observation time.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            coordinator_instance_id = binding.CoordinatorInstanceId,
            catalog_id = binding.CatalogId,
            report_id = reportId,
            observed_at_ms = observedAtMs,
            configuration,
            filter_names = filterNames
        }, CoordinatorProgramContract.Options);
        if (bytes.Length > PlannerContract.MaxRequestBytes) throw new ArgumentException("Equipment report is too large.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/equipment-reports", bytes, deadline.Token, binding.ProfileId).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(response.Bytes);
            var root = body.RootElement;
            CoordinatorProgramContract.CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var ack = root.GetProperty("data").Deserialize<CoordinatorEquipmentAcknowledgement>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException();
            if (ack.CoordinatorInstanceId != binding.CoordinatorInstanceId || ack.CatalogId != binding.CatalogId
                || ack.RigId != binding.RigId || ack.ProfileId != binding.ProfileId || ack.ClientId != clientId || ack.ReportId != reportId
                || ack.ReceivedAtMs == 0 || ack.ReceivedAtMs > 4102444800000UL || ack.AcceptedRevision == 0)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement);
            return ack;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or FormatException or OverflowException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement); }
    }
    public void Dispose() => transport.Dispose();
}
