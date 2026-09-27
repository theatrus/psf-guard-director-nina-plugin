namespace PsfGuard.Director.Runtime;

public enum CoordinatorIntakeFailure
{
    AuthenticationRequired, Forbidden, NotFound, NotReady, Busy, ServerUnavailable, UnexpectedStatus,
    RedirectRefused, UnexpectedNotModified, ResponseTooLarge, MalformedResponse, IdentityMismatch,
    ConfigurationMismatch, UnsupportedProgram, ExpiredOrFutureProgram, ChangedImmutableProgram, Timeout, Transport, CredentialUnavailable,
    ReceiptConflict, InvalidAcknowledgement
}

public sealed class CoordinatorIntakeException(CoordinatorIntakeFailure failure)
    : Exception($"Director program intake: {failure}.")
{
    public CoordinatorIntakeFailure Failure { get; } = failure;
}

/// <summary>Read-only coordinator inspection. Does not persist credentials, cache authority, or open a runtime ledger.</summary>
public sealed class CoordinatorProgramClient : IDisposable
{
    private readonly CoordinatorTransport transport;
    private readonly Uri endpoint;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;

    public CoordinatorProgramClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential,
        bool allowInsecureHttp = false) : this(endpoint, credential, CoordinatorTransport.Handler(),
            TimeProvider.System, TimeSpan.FromSeconds(30), allowInsecureHttp)
    { }

    internal CoordinatorProgramClient(Uri endpoint, Func<CancellationToken, ValueTask<string?>> credential,
        HttpMessageHandler handler, TimeProvider clock, TimeSpan timeout, bool allowInsecureHttp = false)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        transport = new(endpoint, credential, handler, allowInsecureHttp);
        this.endpoint = transport.Endpoint;
        this.clock = clock;
        this.timeout = timeout;
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
            var response = await transport.SendAsync(
                $"api/director/v1/rigs/{binding.RigId:D}/program?coordinator_instance_id={binding.CoordinatorInstanceId:D}&catalog_id={binding.CatalogId:D}",
                null, deadline.Token, binding.ProfileId).ConfigureAwait(false);
            if (response.ETag is null) throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse);
            deadline.Token.ThrowIfCancellationRequested();
            var result = CoordinatorProgramContract.Read(response.Bytes, response.ETag, endpoint.AbsoluteUri, binding, expected,
                checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds()), previous);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
    }

    public async Task<CoordinatorProgramPreview> ReadAndCachePreviewAsync(CoordinatorBinding binding, DirectorConfiguration expected,
        CoordinatorPreviewCache cache, CancellationToken token = default)
    {
        var previous = cache.ReadPrevious();
        var preview = await ReadPreviewAsync(binding, expected, previous, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        cache.Store(preview);
        return preview;
    }

    public void Dispose() => transport.Dispose();
}
