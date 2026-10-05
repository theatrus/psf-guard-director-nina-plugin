using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorPointing(double RaDegrees, double DecDegrees);
public sealed record CoordinatorLiveStatus(string Phase, string TargetName, string Safety,
    string? Operation = null, long? OperationStartedMs = null, ulong? OperationElapsedMs = null,
    string? GoalId = null, string? WaitReason = null, string? QueueState = null, int? QueueDepth = null,
    CoordinatorPointing? Pointing = null, int FreshForMs = 45000);

public sealed class CoordinatorSessionReporter : IDisposable
{
    private sealed record Acknowledgement(bool Accepted, string? ProgramRevision, bool ProgramChanged, ulong ReceivedAtMs);
    private readonly CoordinatorTransport transport;
    private readonly CoordinatorBinding binding;
    public CoordinatorSessionReporter(Uri endpoint, CoordinatorBinding binding, Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false)
        : this(endpoint, binding, credential, CoordinatorTransport.Handler(), allowInsecureHttp) { }
    internal CoordinatorSessionReporter(Uri endpoint, CoordinatorBinding binding, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        this.binding = binding;
        transport = new(endpoint, credential, handler, allowInsecureHttp);
    }
    public Task ReportAsync(CoordinatorAllocation allocation, string sessionId, string phase, string target, string safety, CancellationToken token) =>
        ReportAsync(allocation, sessionId, new CoordinatorLiveStatus(phase, target, safety), token);

    public async Task ReportAsync(CoordinatorAllocation allocation, string sessionId, CoordinatorLiveStatus status, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.FreshForMs is < 15000 or > 600000 || status.QueueDepth is < 0
            || status.Pointing is { } p && (!double.IsFinite(p.RaDegrees) || p.RaDegrees is < 0 or >= 360
                || !double.IsFinite(p.DecDegrees) || p.DecDegrees is < -90 or > 90))
            throw new ArgumentException("Invalid live status observation.");
        if (allocation.Envelope.CoordinatorInstanceId != binding.CoordinatorInstanceId || allocation.Envelope.CatalogId != binding.CatalogId
            || allocation.Envelope.RigId != binding.RigId || allocation.Envelope.ProfileId != binding.ProfileId
            || !Guid.TryParseExact(sessionId, "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Status must match the allocation and session binding.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/status", JsonSerializer.SerializeToUtf8Bytes(new
            {
                coordinator_instance_id = binding.CoordinatorInstanceId,
                catalog_id = binding.CatalogId,
                session_id = sessionId,
                reported_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                program_revision = allocation.Envelope.PreviewRevision,
                status = new
                {
                    phase = status.Phase,
                    target_name = status.TargetName,
                    allocation_id = allocation.Envelope.AllocationId,
                    safety = status.Safety,
                    operation = status.Operation,
                    operation_started_ms = status.OperationStartedMs,
                    operation_elapsed_ms = status.OperationElapsedMs,
                    goal_id = status.GoalId,
                    wait_reason = status.WaitReason,
                    queue_state = status.QueueState,
                    queue_depth = status.QueueDepth,
                    pointing = status.Pointing,
                    fresh_for_ms = status.FreshForMs
                }
            }, CoordinatorProgramContract.Options), deadline.Token, binding.ProfileId);
            using var body = JsonDocument.Parse(response.Bytes);
            var root = body.RootElement;
            CoordinatorProgramContract.CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var ack = root.GetProperty("data").Deserialize<Acknowledgement>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException();
            if (!ack.Accepted || ack.ReceivedAtMs == 0
                || ack.ProgramRevision is not null && (ack.ProgramRevision.Length != 64 || !ack.ProgramRevision.All(char.IsAsciiHexDigitLower))
                || ack.ProgramChanged != (ack.ProgramRevision is not null && ack.ProgramRevision != allocation.Envelope.PreviewRevision))
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or FormatException or OverflowException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement); }
    }
    public void Dispose() => transport.Dispose();
}
