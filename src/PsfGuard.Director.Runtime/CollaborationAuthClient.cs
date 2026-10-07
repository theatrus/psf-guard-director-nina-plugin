using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed class CollaborationCredential(string agentId, string token)
{
    public string AgentId { get; } = agentId;
    public string Token { get; } = token;
    public override string ToString() => "Collaboration credential (redacted)";
}
public sealed class CollaborationLogin(string code, Uri url, TimeSpan lifetime)
{
    public string Code { get; } = code;
    public Uri Url { get; } = url;
    public TimeSpan Lifetime { get; } = lifetime;
    public override string ToString() => "Collaboration browser sign-in (redacted)";
}
public sealed record CollaborationCapabilities(bool Signin, bool Pairing);
public sealed class CollaborationPoll(string state, string? token)
{
    public string State { get; } = state;
    public string? Token { get; } = token;
    public override string ToString() => "Collaboration sign-in state: " + State;
}

/// Host-owned authentication. Credentials never enter the planner or sidecar.
public sealed class CollaborationAuthClient : IDisposable
{
    private readonly Func<HttpMessageHandler> handler;
    private readonly TimeSpan timeout;
    public Uri Endpoint { get; }
    public CollaborationAuthClient(Uri endpoint, bool allowLoopbackHttp = false)
        : this(endpoint, allowLoopbackHttp, CoordinatorTransport.Handler, TimeSpan.FromSeconds(20)) { }
    internal CollaborationAuthClient(Uri endpoint, bool allowLoopbackHttp, Func<HttpMessageHandler> handler, TimeSpan timeout)
    {
        Endpoint = Normalize(endpoint, allowLoopbackHttp);
        this.handler = handler;
        this.timeout = timeout;
    }
    public static Uri Normalize(Uri endpoint, bool allowLoopbackHttp)
    {
        var result = CoordinatorTransport.Normalize(endpoint);
        if (result.AbsoluteUri.Length > 2048 || result.Scheme != Uri.UriSchemeHttps && !(result.IsLoopback && allowLoopbackHttp))
            throw new ArgumentException("Use HTTPS; loopback HTTP needs explicit testing consent.");
        return result;
    }
    public static bool ValidAgent(string id) => id.Length == 12 && id.All(char.IsAsciiHexDigitLower);
    public static bool ValidSecret(string value) => value.Length is > 0 and <= 8192 && value.All(c => c is >= '!' and <= '~');
    public async Task<CollaborationCapabilities> DiscoverAsync(CancellationToken token)
    {
        var health = await SendAsync("health", null, null, token);
        if (Integer(health, "protocol") != 1
            || Property(health, "ok").ValueKind != JsonValueKind.True
            || Number(health, "time") < 0 || Text(health, "version", 120).Length == 0)
            throw Malformed();
        if (health.TryGetProperty("features", out var features))
        {
            if (features.ValueKind != JsonValueKind.Array || features.GetArrayLength() > 32) throw Malformed();
            var values = features.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString()! : throw Malformed()).ToArray();
            if (values.Any(v => v.Length > 80 || v.Any(char.IsControl))) throw Malformed();
            return new(values.Contains("signin"), values.Contains("pairing"));
        }
        var auth = await SendAsync("auth", null, null, token);
        var discord = Property(auth, "discord");
        if (discord.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Malformed();
        return new(discord.ValueKind == JsonValueKind.True, false);
    }
    public async Task<CollaborationCredential> PairAsync(string code, string name, CancellationToken token) =>
        Enrollment(await SendAsync("pair", new { code = Input(code, 512), name = Input(name, 128) }, null, token));
    public async Task<CollaborationLogin> LoginAsync(CancellationToken token)
    {
        var reply = await SendAsync("auth/login", new { }, null, token);
        var code = Text(reply, "code", 512);
        var seconds = Number(reply, "expiresIn");
        if (!double.IsFinite(seconds) || seconds is < 1 or > 3600) throw Malformed();
        if (!Uri.TryCreate(Text(reply, "url", 4096), UriKind.Absolute, out var url) || url.UserInfo.Length != 0
            || url.Fragment.Length != 0 || url.Scheme != Endpoint.Scheme || url.Host != Endpoint.Host || url.Port != Endpoint.Port)
            throw Malformed();
        return new(code, url, TimeSpan.FromSeconds(Math.Floor(seconds)));
    }
    public async Task<CollaborationPoll> PollAsync(string code, CancellationToken token)
    {
        var reply = await SendAsync("auth/poll?code=" + Uri.EscapeDataString(Input(code, 512)), null, null, token);
        var state = Text(reply, "state", 16);
        if (state is not ("pending" or "done" or "claimed" or "expired")) throw Malformed();
        var person = state == "done" ? Text(reply, "token", 8192) : null;
        if (person is not null && !ValidSecret(person)) throw Malformed();
        return new(state, person);
    }
    public async Task<CollaborationCredential> EnrollAsync(string person, string name, CancellationToken token) =>
        Enrollment(await SendAsync("agents", new { name = Input(name, 128) }, person, token));
    public async Task ValidateAsync(CollaborationCredential credential, CancellationToken token)
    {
        if (!ValidAgent(credential.AgentId) || !ValidSecret(credential.Token)) throw Malformed();
        var reply = await SendAsync("agent/projects", null, credential.Token, token);
        if (Integer(reply, "protocol") != 1 || Property(reply, "projects").ValueKind != JsonValueKind.Array
            || Property(reply, "projects").GetArrayLength() > 64) throw Malformed();
    }
    private static CollaborationCredential Enrollment(JsonElement reply)
    {
        var id = Text(Property(reply, "agent"), "id", 12);
        var secret = Text(reply, "token", 8192);
        if (!ValidAgent(id) || !ValidSecret(secret)) throw Malformed();
        return new(id, secret);
    }
    private async Task<JsonElement> SendAsync(string route, object? body, string? secret, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var transport = new CoordinatorTransport(Endpoint, _ => ValueTask.FromResult(secret), handler(), false);
        try
        {
            var reply = await transport.SendAsync("api/v1/" + route, body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body), deadline.Token);
            using var document = JsonDocument.Parse(reply.Bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var count = 0;
            Check(document.RootElement, ref count);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Malformed(); }
    }
    private static void Check(JsonElement value, ref int count)
    {
        if (++count > 4096) throw Malformed();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw Malformed(); Check(property.Value, ref count); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Check(item, ref count);
    }
    private static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : throw Malformed();
    private static int Integer(JsonElement value, string key)
    {
        var result = Property(value, key);
        return result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var number) ? number : throw Malformed();
    }
    private static double Number(JsonElement value, string key)
    {
        var result = Property(value, key);
        return result.ValueKind == JsonValueKind.Number && result.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw Malformed();
    }
    private static string Text(JsonElement value, string key, int maximum)
    {
        var result = Property(value, key);
        return result.ValueKind == JsonValueKind.String ? Input(result.GetString()!, maximum) : throw Malformed();
    }
    private static string Input(string text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) throw Malformed();
        return text;
    }
    private static CoordinatorIntakeException Malformed() => new(CoordinatorIntakeFailure.MalformedResponse);
    public void Dispose() { }
}
