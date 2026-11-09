using System.Net;
using System.Net.Http.Headers;

namespace PsfGuard.Director.Runtime;

/// <summary>A response whose status the caller judges; <see cref="Json"/> is false for any other body or a cut one.</summary>
internal sealed record CoordinatorExchange(HttpStatusCode Status, byte[] Bytes, bool Json, TimeSpan? RetryAfter = null);

internal sealed class CoordinatorTransport : IDisposable
{
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly Func<CancellationToken, ValueTask<string?>> credential;
    internal Uri Endpoint { get; }

    internal static Uri Normalize(Uri endpoint, bool allowInsecureHttp = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.Scheme != Uri.UriSchemeHttps && (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback && !allowInsecureHttp))
            throw new ArgumentException("Use an absolute HTTPS coordinator URL; non-loopback HTTP needs explicit opt-in.", nameof(endpoint));
        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static HttpMessageHandler Handler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    };

    internal CoordinatorTransport(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, bool allowInsecureHttp, TimeProvider? clock = null)
    {
        Endpoint = Normalize(endpoint, allowInsecureHttp);
        ArgumentNullException.ThrowIfNull(credential);
        this.credential = credential;
        this.clock = clock ?? TimeProvider.System;
        http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal Task<CoordinatorResponse> SendAsync(string route, byte[]? body, CancellationToken token, Guid? profileId = null) =>
        SendCoreAsync(route, body, token, profileId, false);

    internal Task<CoordinatorResponse> ReadOptionalAsync(string route, CancellationToken token, Guid profileId) =>
        SendCoreAsync(route, null, token, profileId, true);

    private async Task<CoordinatorResponse> SendCoreAsync(string route, byte[]? body, CancellationToken token,
        Guid? profileId, bool allowMissing)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(Endpoint, route));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (profileId is { } profile) request.Headers.Add("X-PSF-Director-Profile", profile.ToString("D"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        await AuthorizeAsync(request, token).ConfigureAwait(false);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var responseClock = new CoordinatorClock(clock, response.Headers.Date, clock.GetTimestamp());
            if (response.StatusCode != HttpStatusCode.OK && !(allowMissing && response.StatusCode == HttpStatusCode.NotFound))
                throw new CoordinatorIntakeException(Classify(response.StatusCode), response.StatusCode);
            if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentEncoding.Count != 0)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse);
            if (response.Content.Headers.ContentLength > CoordinatorProgramContract.MaximumBytes)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ResponseTooLarge);
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                if (bytes.Length + read > CoordinatorProgramContract.MaximumBytes)
                    throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ResponseTooLarge);
                bytes.Write(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            return new(bytes.ToArray(), response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null, response.StatusCode, responseClock);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Transport); }
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken token)
    {
        string? secret;
        try { secret = await credential(token).AsTask().WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.CredentialUnavailable); }
        if (secret is not null)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length > 8192 || secret.Any(c => c < '!' || c > '~'))
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.AuthenticationRequired);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    /// <summary>
    /// One request whose caller handles every status itself, such as an image
    /// upload. It applies the same credential, profile header and redirect
    /// policy as <see cref="SendAsync"/>, returns the status with at most
    /// <paramref name="maximumBytes"/> of the body, and throws only for a
    /// transport failure, an unusable credential or cancellation.
    /// </summary>
    internal async Task<CoordinatorExchange> ExchangeAsync(HttpMethod method, string route, HttpContent? content,
        IReadOnlyDictionary<string, string>? headers, Guid profileId, CancellationToken token, int maximumBytes = 65536)
    {
        using var request = new HttpRequestMessage(method, new Uri(Endpoint, route)) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Add("X-PSF-Director-Profile", profileId.ToString("D"));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.Add(name, value);
        await AuthorizeAsync(request, token).ConfigureAwait(false);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var json = response.Content.Headers.ContentType?.MediaType == "application/json"
                && response.Content.Headers.ContentEncoding.Count == 0;
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            var truncated = false;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                var keep = (int)Math.Min(read, maximumBytes - bytes.Length);
                if (keep < read) truncated = true;
                if (keep > 0) bytes.Write(buffer, 0, keep);
                if (truncated) break;
            }
            token.ThrowIfCancellationRequested();
            TimeSpan? retryAfter = response.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - clock.GetUtcNow(),
                _ => null
            };
            return new(response.StatusCode, bytes.ToArray(), json && !truncated, retryAfter);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Transport); }
    }

    private static CoordinatorIntakeFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => CoordinatorIntakeFailure.AuthenticationRequired,
        HttpStatusCode.Forbidden => CoordinatorIntakeFailure.Forbidden,
        HttpStatusCode.NotFound => CoordinatorIntakeFailure.NotFound,
        HttpStatusCode.UnprocessableEntity => CoordinatorIntakeFailure.NotReady,
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => CoordinatorIntakeFailure.Busy,
        HttpStatusCode.NotModified => CoordinatorIntakeFailure.UnexpectedNotModified,
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => CoordinatorIntakeFailure.RedirectRefused,
        >= HttpStatusCode.InternalServerError => CoordinatorIntakeFailure.ServerUnavailable,
        _ => CoordinatorIntakeFailure.UnexpectedStatus
    };

    public void Dispose() => http.Dispose();
}
