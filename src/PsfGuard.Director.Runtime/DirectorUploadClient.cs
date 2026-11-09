using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

/// <summary>Where a captured frame goes: the paired server, profile and rig.</summary>
public sealed record DirectorUploadDestination(Uri Endpoint, Guid ProfileId, Guid RigId, bool AllowInsecureHttp = false);

public enum DirectorUploadStateKind
{
    /// <summary>The database takes uploads and its receive folder works.</summary>
    Enabled,

    /// <summary>The server takes Director uploads, but this database has them off.</summary>
    Disabled,

    /// <summary>
    /// The server takes no Director uploads now: 403, 404 or 405, or a 200 that
    /// is not an upload state. That can be an older PSF Guard, the Director
    /// service turned off, or a proxy's page, so the queue asks again later.
    /// </summary>
    Unsupported,

    /// <summary>No answer this time: a timeout, a lost connection or a server error.</summary>
    Unavailable,
}

/// <summary>The server's answer to <c>GET …/uploads</c>.</summary>
public sealed record DirectorUploadState(DirectorUploadStateKind Kind, string? Reason = null, long? MaxBytes = null);

public enum DirectorUploadOutcome
{
    /// <summary>Stored, or already present. The job is done.</summary>
    Stored,

    /// <summary>Refused for this file. Hold the job; do not retry it on its own.</summary>
    Refused,

    /// <summary>Uploads are off or the pairing no longer covers them. Keep the job; check the state again later.</summary>
    Paused,

    /// <summary>An older server. Keep the queue and stop uploading.</summary>
    Unsupported,

    /// <summary>A failure that may pass. Retry with backoff.</summary>
    Retry,
}

/// <param name="RetryAfter">The server's Retry-After, for an answer to retry.</param>
public sealed record DirectorUploadAnswer(DirectorUploadOutcome Outcome, string Detail, bool AlreadyPresent = false,
    TimeSpan? RetryAfter = null);

/// <summary>The PSF Guard calls the upload queue makes for one destination.</summary>
public interface IDirectorUploadServer : IDisposable
{
    Task<DirectorUploadState> GetStateAsync(CancellationToken token);

    Task<DirectorUploadAnswer> UploadAsync(string path, CancellationToken token);
}

/// <summary>How the upload contract maps each answer to an action, in one place.</summary>
public static class DirectorUploadPolicy
{
    public const int MaximumFileNameLength = 240;

    public static DirectorUploadOutcome ForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => DirectorUploadOutcome.Stored,
        // A busy database answers 503 with Retry-After, so 409 is always about this file.
        HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Gone
            or HttpStatusCode.RequestEntityTooLarge => DirectorUploadOutcome.Refused,
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => DirectorUploadOutcome.Paused,
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => DirectorUploadOutcome.Unsupported,
        _ => DirectorUploadOutcome.Retry,
    };

    /// <summary>
    /// Why the server would refuse this file name, or null when it takes it:
    /// at most 240 characters, none of <c>&lt;&gt;:"/\|?*</c> or control
    /// characters, and not ending in a dot or a space.
    /// </summary>
    public static string? FileNameProblem(string fileName)
    {
        if (fileName.Length == 0) return "The file has no name.";
        if (fileName.Length > MaximumFileNameLength) return $"The file name is longer than {MaximumFileNameLength} characters.";
        if (fileName.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            return "The file name contains a character PSF Guard does not accept.";
        if (fileName.EndsWith('.') || fileName.EndsWith(' ')) return "The file name ends with a dot or a space.";
        return null;
    }

    /// <summary>Room left for the multipart boundary and part headers, with a safety margin.</summary>
    public const long RequestOverheadBytes = 64 * 1024;

    /// <summary>
    /// The size of the upload request for a file: its bytes, the part headers
    /// that carry its name, and <see cref="RequestOverheadBytes"/>. Compare
    /// this, not the file length, with the server's <c>max_bytes</c>.
    /// </summary>
    public static long RequestBytes(long fileLength, string fileName) =>
        fileLength + RequestOverheadBytes + 2L * System.Text.Encoding.UTF8.GetByteCount(fileName);

    /// <summary>
    /// The wait before retry number <paramref name="attempts"/>: the server's
    /// Retry-After when it sent one, kept between 1 second and 10 minutes;
    /// otherwise 5 seconds, doubling to 10 minutes.
    /// </summary>
    public static TimeSpan RetryDelay(int attempts, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } requested)
            return TimeSpan.FromSeconds(Math.Clamp(requested.TotalSeconds, 1, 600));
        var seconds = 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, 600));
    }
}

/// <summary>
/// Calls the Director upload routes with the rig pairing's credentials and
/// turns each HTTP answer into the contract's action. It never throws for an
/// answer from the server, only for cancellation and a missing local file.
/// </summary>
public sealed class DirectorUploadClient : IDirectorUploadServer
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);
    private readonly CoordinatorTransport transport;
    private readonly DirectorUploadDestination destination;

    public DirectorUploadClient(DirectorUploadDestination destination, Func<CancellationToken, ValueTask<string?>> credential)
        : this(destination, credential, CoordinatorTransport.Handler()) { }

    internal DirectorUploadClient(DirectorUploadDestination destination, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.ProfileId == Guid.Empty || destination.RigId == Guid.Empty)
            throw new ArgumentException("An upload destination needs a profile and a rig.", nameof(destination));
        this.destination = destination;
        transport = new(destination.Endpoint, credential, handler, destination.AllowInsecureHttp);
    }

    private string Rig => destination.RigId.ToString("D");

    public async Task<DirectorUploadState> GetStateAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(StateTimeout);
        CoordinatorExchange answer;
        try
        {
            answer = await transport.ExchangeAsync(HttpMethod.Get, $"api/director/v1/rigs/{Rig}/uploads", null, null,
                destination.ProfileId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(DirectorUploadStateKind.Unavailable, "PSF Guard did not answer in time."); }
        catch (CoordinatorIntakeException error)
        { return new(DirectorUploadStateKind.Unavailable, $"PSF Guard could not be reached ({error.Failure})."); }
        return ReadState(answer);
    }

    internal static DirectorUploadState ReadState(CoordinatorExchange answer)
    {
        switch (answer.Status)
        {
            case HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                return new(DirectorUploadStateKind.Unsupported, $"PSF Guard answered HTTP {(int)answer.Status}.");
            case not HttpStatusCode.OK:
                return new(DirectorUploadStateKind.Unavailable, $"PSF Guard answered HTTP {(int)answer.Status}.");
        }
        // A 200 without data.enabled also comes from a server that knows nothing of this route.
        var notState = new DirectorUploadState(DirectorUploadStateKind.Unsupported, "PSF Guard's answer was not an upload state.");
        if (!answer.Json) return notState;
        try
        {
            using var body = JsonDocument.Parse(answer.Bytes);
            if (body.RootElement.ValueKind != JsonValueKind.Object
                || !body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return notState;
            var reason = data.TryGetProperty("reason", out var text) && text.ValueKind == JsonValueKind.String
                ? Clean(text.GetString()) : null;
            long? maximum = data.TryGetProperty("max_bytes", out var bytes) && bytes.TryGetInt64(out var value) && value > 0 ? value : null;
            return enabled.GetBoolean()
                ? new(DirectorUploadStateKind.Enabled, null, maximum)
                : new(DirectorUploadStateKind.Disabled, reason ?? "Image uploads are off for this database.", maximum);
        }
        catch (JsonException) { return notState; }
    }

    public async Task<DirectorUploadAnswer> UploadAsync(string path, CancellationToken token)
    {
        var fileName = Path.GetFileName(path);
        if (DirectorUploadPolicy.FileNameProblem(fileName) is { } problem)
            return new(DirectorUploadOutcome.Refused, problem);
        string digest;
        long length;
        await using (var hashing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        {
            length = hashing.Length;
            digest = HashEncoding.Lower(await SHA256.HashDataAsync(hashing, token).ConfigureAwait(false));
        }
        // Long enough for a large frame on a slow link: five minutes plus one
        // second for each 256 KiB.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(length / (256.0 * 1024)));
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var image = new StreamContent(file);
        image.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent { { image, "image", fileName } };
        CoordinatorExchange answer;
        try
        {
            answer = await transport.ExchangeAsync(HttpMethod.Post, $"api/director/v1/rigs/{Rig}/images", form,
                new Dictionary<string, string> { ["X-Content-SHA256"] = digest }, destination.ProfileId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(DirectorUploadOutcome.Retry, "The upload timed out."); }
        catch (CoordinatorIntakeException error)
        { return new(DirectorUploadOutcome.Retry, $"PSF Guard could not be reached ({error.Failure})."); }
        var outcome = DirectorUploadPolicy.ForStatus(answer.Status);
        var message = ErrorText(answer);
        if (outcome != DirectorUploadOutcome.Stored)
            return new(outcome, message is null ? $"PSF Guard answered HTTP {(int)answer.Status}." : $"PSF Guard answered HTTP {(int)answer.Status}: {message}",
                RetryAfter: outcome == DirectorUploadOutcome.Retry ? answer.RetryAfter : null);
        return new(DirectorUploadOutcome.Stored, "Stored", AlreadyPresent(answer));
    }

    private static string? ErrorText(CoordinatorExchange answer)
    {
        if (!answer.Json) return null;
        try
        {
            using var body = JsonDocument.Parse(answer.Bytes);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? Clean(error.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool AlreadyPresent(CoordinatorExchange answer)
    {
        if (!answer.Json) return false;
        try
        {
            using var body = JsonDocument.Parse(answer.Bytes);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("already_present", out var present) && present.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string? Clean(string? value)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).Take(500).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    public void Dispose() => transport.Dispose();
}
