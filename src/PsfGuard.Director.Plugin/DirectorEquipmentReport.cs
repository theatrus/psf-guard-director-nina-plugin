using System.Collections.Concurrent;
using System.Collections.Immutable;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

internal sealed record DirectorEquipmentReport(Uri Endpoint, CoordinatorBinding Binding, Guid ClientId, Guid Id,
    DirectorConfiguration Configuration, ImmutableDictionary<string, string> FilterNames, CoordinatorCameraDefaults CameraDefaults,
    ulong ObservedAtMs, ulong? AcceptedRevision = null)
{
    private static readonly ConcurrentDictionary<Guid, DirectorEquipmentReport> Latest = new();

    internal static DirectorEquipmentReport? Find(Uri endpoint, CoordinatorPairing pairing) =>
        Latest.TryGetValue(pairing.Binding.ProfileId, out var report) &&
        report.Endpoint == endpoint && report.Binding == pairing.Binding && report.ClientId == pairing.ClientId ? report : null;

    internal void Remember(ulong? acceptedRevision) => Latest[Binding.ProfileId] = this with { AcceptedRevision = acceptedRevision };

    internal async Task<ulong?> CheckAcceptanceAsync(Func<CancellationToken, ValueTask<string?>> credential,
        bool allowHttp, CancellationToken token)
    {
        if (AcceptedRevision is { } known) return known;
        // PSF Guard returns current acceptance for an exact replay of an acknowledged report.
        var acknowledgement = await SubmitAsync(credential, allowHttp, token);
        token.ThrowIfCancellationRequested();
        if (acknowledgement.AcceptedRevision is { } revision)
            Latest.TryUpdate(Binding.ProfileId, this with { AcceptedRevision = revision }, this);
        return acknowledgement.AcceptedRevision;
    }

    internal async Task<CoordinatorEquipmentAcknowledgement> SubmitAsync(Func<CancellationToken, ValueTask<string?>> credential,
        bool allowHttp, CancellationToken token)
    {
        using var client = new CoordinatorEquipmentClient(Endpoint, credential, allowHttp);
        return await client.ReportAsync(Binding, ClientId, Id, Configuration, FilterNames, CameraDefaults, ObservedAtMs, token);
    }
}
