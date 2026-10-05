using System.ComponentModel.Composition;
using System.IO;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Core.Utility;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

[Export(typeof(DirectorCheckInService))]
public sealed class DirectorCheckInService
{
    private readonly IProfileService profiles;
    internal string LocalStateRoot { get; init; } = DirectorAcquisition.StateRoot;
    [ImportingConstructor]
    public DirectorCheckInService(IProfileService profiles) => this.profiles = profiles;

    public Task<CoordinatorRunCheckInProgress> RunAsync(IProgress<CoordinatorRunCheckInProgress>? progress, CancellationToken token) =>
        RunCoreAsync(progress, token, null);

    internal Task<CoordinatorRunCheckInProgress> RunBeforeAcquisitionAsync(AcquisitionLease owner,
        IProgress<CoordinatorRunCheckInProgress>? progress, CancellationToken token) => RunCoreAsync(progress, token, owner);

    private async Task<CoordinatorRunCheckInProgress> RunCoreAsync(IProgress<CoordinatorRunCheckInProgress>? progress, CancellationToken token, AcquisitionLease? owner)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Changed(object? sender, EventArgs e) => lifetime.Cancel();
        profiles.BeforeProfileChanging += Changed;
        profiles.ProfileChanged += Changed;
        try
        {
            var profile = profiles.ActiveProfile.Id;
            var settings = new PluginOptionsAccessor(profiles, new Guid("03a1d13e-67eb-4e24-a407-82bce7e576a5"));
            using var connection = new DirectorConnection(() => profiles.ActiveProfile.Id, () => false,
                () => settings.GetValueString("CoordinatorUrl", ""), _ => { },
                readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""));
            var endpoint = connection.ResolvedEndpoint ?? throw new InvalidOperationException("Configure a Director coordinator first.");
            var pairing = connection.ReadPairing() ?? throw new InvalidOperationException("Pairing credentials are unavailable. Re-pair first.");
            ValueTask<string?> Credential(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var current = DirectorCredentialStore.Read(endpoint, profile);
                if (profiles.ActiveProfile.Id != profile || current?.ClientId != pairing.ClientId || current.Binding != pairing.Binding)
                    throw new InvalidOperationException("Pairing or profile changed during check-in.");
                return ValueTask.FromResult<string?>(current.Token);
            }
            await Credential(lifetime.Token);
            Logger.Info("Director saved-run check-in started.");
            var pluginDirectory = Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!;
            var result = owner is null ? await CoordinatorRunCheckIn.DeliverAsync(LocalStateRoot,
                pluginDirectory, endpoint, pairing.Binding, pairing.ClientId,
                Credential, progress, connection.AllowInsecureHttp, lifetime.Token)
                : await CoordinatorRunCheckIn.DeliverBeforeAcquisitionAsync(owner, LocalStateRoot, pluginDirectory,
                    endpoint, pairing.Binding, pairing.ClientId, Credential, progress, connection.AllowInsecureHttp, lifetime.Token);
            await Credential(lifetime.Token);
            Logger.Info($"Director saved-run check-in: {result.Runs} runs, {result.DeliveredEvents} events, {result.ReleasedWorkloads} workload releases.");
            return result;
        }
        finally
        {
            profiles.BeforeProfileChanging -= Changed;
            profiles.ProfileChanged -= Changed;
        }
    }
}
