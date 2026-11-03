using System.Text.Json;

namespace PsfGuard.Director.Runtime;

// Sends the profile's star-detection settings so PSF Guard can reproduce the
// star count and HFR N.I.N.A. recorded. The body is the Sync plugin's report;
// it is evidence for quality scans, never an acquisition permit.
public sealed class CoordinatorStarDetectionClient : IDisposable
{
    private readonly CoordinatorTransport transport;
    private readonly CoordinatorBinding binding;
    public CoordinatorStarDetectionClient(Uri endpoint, CoordinatorBinding binding, Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false)
        : this(endpoint, binding, credential, CoordinatorTransport.Handler(), allowInsecureHttp) { }
    internal CoordinatorStarDetectionClient(Uri endpoint, CoordinatorBinding binding, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        this.binding = binding;
        transport = new(endpoint, credential, handler, allowInsecureHttp);
    }

    public async Task SendAsync(StarDetectionSettingsReport report, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.ProfileId != binding.ProfileId) throw new ArgumentException("Star-detection settings must come from the paired profile.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, StarDetectionJson.Options);
        if (bytes.Length > PlannerContract.MaxRequestBytes) throw new ArgumentException("Star-detection settings are too large.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/star-detection-settings", bytes, deadline.Token, binding.ProfileId).ConfigureAwait(false);
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
    public void Dispose() => transport.Dispose();
}
