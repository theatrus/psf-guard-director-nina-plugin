using System.Net;
using System.Net.Http.Headers;

namespace PsfGuard.Director.Runtime;

public enum CoordinatorIntakeFailure
{
    AuthenticationRequired, Forbidden, NotFound, NotReady, Busy, ServerUnavailable, UnexpectedStatus,
    RedirectRefused, UnexpectedNotModified, ResponseTooLarge, MalformedResponse, IdentityMismatch,
    ConfigurationMismatch, UnsupportedProgram, ExpiredOrFutureProgram, ChangedImmutableProgram, Timeout, Transport, CredentialUnavailable
}

public sealed class CoordinatorIntakeException(CoordinatorIntakeFailure failure)
    : Exception($"Director program intake: {failure}.")
{
    public CoordinatorIntakeFailure Failure { get; } = failure;
}

/// <summary>Read-only coordinator inspection. Does not persist credentials, cache authority, or open a runtime ledger.</summary>
public sealed class CoordinatorProgramClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly Func<CancellationToken, ValueTask<string?>> credential;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;

    public CoordinatorProgramClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential,
        bool allowInsecureHttp = false) : this(endpoint, credential, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        }, TimeProvider.System, TimeSpan.FromSeconds(30), allowInsecureHttp)
    { }

    internal CoordinatorProgramClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, TimeProvider clock, TimeSpan timeout, bool allowInsecureHttp = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.Scheme != Uri.UriSchemeHttps && (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback && !allowInsecureHttp))
            throw new ArgumentException("Use an absolute HTTPS coordinator URL; non-loopback HTTP needs explicit opt-in.", nameof(endpoint));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
        this.credential = credential;
        this.clock = clock;
        this.timeout = timeout;
        http = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task<CoordinatorProgramPreview> ReadPreviewAsync(CoordinatorBinding binding, DirectorConfiguration expected,
        CoordinatorProgramPreview? previous = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expected);
        if (binding.CoordinatorInstanceId == Guid.Empty || binding.CatalogId == Guid.Empty
            || binding.RigId == Guid.Empty || binding.ProfileId == Guid.Empty || expected.RigId != binding.RigId.ToString("D"))
            throw new ArgumentException("An exact coordinator/catalog/rig/profile binding and native configuration are required.");
        if (previous is not null && (previous.Binding != binding || previous.Origin != endpoint.AbsoluteUri))
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint,
                $"api/director/v1/rigs/{binding.RigId:D}/program?coordinator_instance_id={binding.CoordinatorInstanceId:D}&catalog_id={binding.CatalogId:D}"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            string? secret;
            try { secret = await credential(deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.CredentialUnavailable); }
            if (secret is not null)
            {
                if (string.IsNullOrEmpty(secret) || secret.Length > 8192 || secret.Any(c => c < '!' || c > '~'))
                    throw new CoordinatorIntakeException(CoordinatorIntakeFailure.AuthenticationRequired);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new CoordinatorIntakeException(Classify(response.StatusCode));
            if (response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentEncoding.Count != 0 || response.Headers.ETag is not { IsWeak: false } etag)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse);
            if (response.Content.Headers.ContentLength > CoordinatorProgramContract.MaximumBytes)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ResponseTooLarge);
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (bytes.Length + read > CoordinatorProgramContract.MaximumBytes)
                    throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ResponseTooLarge);
                bytes.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            var result = CoordinatorProgramContract.Read(bytes.ToArray(), etag.ToString(), endpoint.AbsoluteUri, binding, expected,
                checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds()), previous);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
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
