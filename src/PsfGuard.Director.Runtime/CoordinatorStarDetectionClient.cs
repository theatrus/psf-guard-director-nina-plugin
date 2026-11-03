using System.Net;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

/// <summary>PSF Guard answered 400: the snapshot failed validation and cannot pass on a retry.</summary>
public sealed class StarDetectionSettingsRefusedException()
    : Exception("PSF Guard refused these star-detection settings (HTTP 400).");

// Sends the profile's star-detection settings so PSF Guard can reproduce the
// star count and HFR N.I.N.A. recorded. The body is the Sync plugin's report;
// it is evidence for quality scans, never an acquisition permit.
public sealed class CoordinatorStarDetectionClient : IDisposable
{
    // The shared transport reports 400 with other unexpected statuses. This
    // route needs 400 alone, so it records the status of its own response
    // rather than changing the shared classification. One send at a time.
    private sealed class StatusRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal HttpStatusCode? Status;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token).ConfigureAwait(false);
            Status = response.StatusCode;
            return response;
        }
    }
    private readonly CoordinatorTransport transport;
    private readonly StatusRecorder status;
    private readonly CoordinatorBinding binding;
    public CoordinatorStarDetectionClient(Uri endpoint, CoordinatorBinding binding, Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false)
        : this(endpoint, binding, credential, CoordinatorTransport.Handler(), allowInsecureHttp) { }
    internal CoordinatorStarDetectionClient(Uri endpoint, CoordinatorBinding binding, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        this.binding = binding;
        status = new(handler);
        transport = new(endpoint, credential, status, allowInsecureHttp);
    }

    public async Task SendAsync(StarDetectionSettingsReport report, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.ProfileId != binding.ProfileId) throw new ArgumentException("Star-detection settings must come from the paired profile.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, StarDetectionJson.Options);
        if (bytes.Length > PlannerContract.MaxRequestBytes) throw new ArgumentException("Star-detection settings are too large.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        status.Status = null;
        try
        {
            (byte[] Bytes, string? ETag) response;
            try { response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/star-detection-settings", bytes, deadline.Token, binding.ProfileId).ConfigureAwait(false); }
            catch (CoordinatorIntakeException) when (status.Status == HttpStatusCode.BadRequest)
            { throw new StarDetectionSettingsRefusedException(); }
            using var body = JsonDocument.Parse(response.Bytes);
            var root = body.RootElement;
            CoordinatorProgramContract.CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or FormatException or OverflowException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement); }
    }
    /// <summary>True when the server has no route for these settings: HTTP 404, an older PSF Guard.</summary>
    public static bool IsMissingRoute(Exception error) =>
        error is CoordinatorIntakeException { Failure: CoordinatorIntakeFailure.NotFound };

    /// <summary>
    /// True only for HTTP 400, which PSF Guard answers for a snapshot that
    /// fails validation. Server errors, 429, timeouts and every other failure
    /// retry with backoff.
    /// </summary>
    public static bool IsRefused(Exception error) => error is StarDetectionSettingsRefusedException;

    public void Dispose() => transport.Dispose();
}
