using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.SimulatorProbe;

// Test-only operator access to a disposable loopback server. Never shipped in the plugin ZIP.
internal sealed class CoordinatorProbe : IAsyncDisposable
{
    private sealed record Fixture(string Endpoint, Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId);
    private readonly HttpClient operatorClient;
    private readonly Uri endpoint;
    private readonly CoordinatorPairing pairing;
    internal string RigId => pairing.Binding.RigId.ToString("D");
    private CoordinatorProbe(Uri endpoint, HttpClient operatorClient, CoordinatorPairing pairing) =>
        (this.endpoint, this.operatorClient, this.pairing) = (endpoint, operatorClient, pairing);

    internal static async Task<CoordinatorProbe?> PairAsync(string root, Guid profile, CancellationToken token)
    {
        var path = Path.Combine(root, "coordinator-fixture.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Invalid coordinator fixture.");
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException();
        var endpoint = new Uri(fixture.Endpoint);
        if (!endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0 || fixture.CoordinatorInstanceId == Guid.Empty || fixture.CatalogId == Guid.Empty || fixture.RigId == Guid.Empty)
            throw new InvalidDataException("Coordinator probe requires an explicit disposable loopback server and binding.");
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = endpoint };
        CoordinatorPairing? pairing = null;
        try
        {
            using var issued = await http.PostAsJsonAsync($"api/director/v1/rigs/{fixture.RigId:D}/pairing-token",
                new { coordinator_instance_id = fixture.CoordinatorInstanceId, catalog_id = fixture.CatalogId }, token);
            issued.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await issued.Content.ReadAsByteArrayAsync(token));
            var code = body.RootElement.GetProperty("data").GetProperty("pairing_token").GetString()!;
            using var client = new CoordinatorPairingClient(endpoint);
            pairing = await client.PairAsync(code, profile, token);
            if (pairing.Binding != new CoordinatorBinding(fixture.CoordinatorInstanceId, fixture.CatalogId, fixture.RigId, profile))
                throw new InvalidDataException("Coordinator fixture pairing changed identity.");
            DirectorCredentialStore.Store(endpoint, pairing);
            if (DirectorCredentialStore.Read(endpoint, profile)?.Binding != pairing.Binding) throw new InvalidDataException("Pairing vault readback failed.");
            return new(endpoint, http, pairing);
        }
        catch
        {
            if (pairing is not null)
            {
                // Best-effort cleanup must not replace the primary validation/vault failure.
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await http.DeleteAsync($"api/director/v1/rigs/{pairing.Binding.RigId:D}/clients/{pairing.ClientId:D}", cleanup.Token);
                }
                catch (Exception) { }
                try { DirectorCredentialStore.Forget(endpoint, profile); }
                catch (Exception) { }
            }
            http.Dispose();
            throw;
        }
    }

    internal async Task<CoordinatorCheckpointResult> DeliverAsync(string root, RuntimeController runtime, LedgerIdentity ledger, CancellationToken token)
    {
        ValueTask<string?> Credential(CancellationToken _) => ValueTask.FromResult(DirectorCredentialStore.Read(endpoint, pairing.Binding.ProfileId)?.Token
            ?? throw new InvalidOperationException("Simulator credential disappeared."))!;
        async Task<LedgerEventPage> Events(ulong after, int limit, CancellationToken cancellation)
        {
            var result = await runtime.ReadEventsAsync(after, limit, cancellation);
            return result.Value ?? throw new InvalidDataException("Simulator ledger read failed.");
        }
        using (var client = new CoordinatorCheckpointClient(root, endpoint, pairing.Binding, ledger, Credential))
        {
            var result = await client.DeliverAsync(Events, maxPages: 1, token: token);
            if (result.DeliveredEvents != 6) throw new InvalidDataException("Expected three reservation/save receipt pairs.");
        }
        using (var restarted = new CoordinatorCheckpointClient(root, endpoint, pairing.Binding, ledger, Credential))
        {
            var result = await restarted.DeliverAsync(Events, token: token);
            if (!result.CaughtUp || result.AcknowledgedThrough != 6 || result.DeliveredEvents != 0)
                throw new InvalidDataException("Checkpoint restart did not retain its cursor.");
        }
        using var replay = new CoordinatorCheckpointClient(Path.Combine(root, "replay"), endpoint, pairing.Binding, ledger, Credential);
        var duplicate = await replay.DeliverAsync(Events, token: token);
        if (!duplicate.CaughtUp || duplicate.AcknowledgedThrough != 6 || duplicate.DeliveredEvents != 6)
            throw new InvalidDataException("Coordinator did not acknowledge idempotent replay.");
        return duplicate;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await operatorClient.DeleteAsync($"api/director/v1/rigs/{pairing.Binding.RigId:D}/clients/{pairing.ClientId:D}", cleanup.Token);
            response.EnsureSuccessStatusCode();
        }
        finally { DirectorCredentialStore.Forget(endpoint, pairing.Binding.ProfileId); operatorClient.Dispose(); }
    }
}
