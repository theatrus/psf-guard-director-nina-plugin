using System.Collections.Immutable;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

/// <summary>Store only in the platform credential vault. Do not log or serialize into plugin settings.</summary>
public sealed class CoordinatorPairing
{
    public CoordinatorBinding Binding { get; }
    public Guid ClientId { get; }
    public string Token { get; }
    internal CoordinatorPairing(CoordinatorBinding binding, Guid clientId, string token) => (Binding, ClientId, Token) = (binding, clientId, token);
    public override string ToString() => "Director pairing (redacted)";
}

public sealed class CoordinatorPairingClient : IDisposable
{
    private sealed record Answer(int ProtocolVersion, Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId,
        Guid ProfileId, Guid ClientId, string Token, ImmutableArray<string> Scopes)
    {
        public override string ToString() => "Director pairing response (redacted)";
    }
    private readonly CoordinatorTransport transport;
    private readonly TimeSpan timeout;
    public CoordinatorPairingClient(Uri endpoint, bool allowInsecureHttp = false)
        : this(endpoint, CoordinatorTransport.Handler(), TimeSpan.FromSeconds(30), allowInsecureHttp) { }
    internal CoordinatorPairingClient(Uri endpoint, HttpMessageHandler handler, TimeSpan timeout, bool allowInsecureHttp = false)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout;
        transport = new(endpoint, _ => ValueTask.FromResult<string?>(null), handler, allowInsecureHttp);
    }

    public static bool ValidCode(string? code) => ValidSecret(code, "psfdpt_");
    internal static bool ValidToken(string? token) => ValidSecret(token, "psfdrc_");
    private static bool ValidSecret(string? value, string prefix) => value is not null && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length == prefix.Length + 64 && value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef".AsSpan()) == false;

    public async Task<CoordinatorPairing> PairAsync(string code, Guid profileId, CancellationToken token = default)
    {
        if (!ValidCode(code) || profileId == Guid.Empty) throw new ArgumentException("A Director pairing code and NINA profile are required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol_version = 1,
                pairing_token = code,
                profile_id = profileId,
                client_name = "PSF Guard Director for NINA"
            });
            var response = await transport.SendAsync("api/director/v1/pair", request, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(response.Bytes);
            var root = document.RootElement;
            CoordinatorProgramContract.CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var answer = root.GetProperty("data").Deserialize<Answer>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException();
            if (answer.ProtocolVersion != 1 || answer.ProfileId != profileId || answer.ClientId == Guid.Empty || !ValidToken(answer.Token)
                || answer.Scopes.Length != 3 || !answer.Scopes.ToHashSet(StringComparer.Ordinal).SetEquals(["program:read", "checkin:write", "status:write"]))
                throw new InvalidDataException();
            var binding = new CoordinatorBinding(answer.CoordinatorInstanceId, answer.CatalogId, answer.RigId, profileId);
            CoordinatorCheckpointClient.ValidateBinding(binding);
            deadline.Token.ThrowIfCancellationRequested();
            return new(binding, answer.ClientId, answer.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or FormatException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse); }
    }

    public void Dispose() => transport.Dispose();
}
