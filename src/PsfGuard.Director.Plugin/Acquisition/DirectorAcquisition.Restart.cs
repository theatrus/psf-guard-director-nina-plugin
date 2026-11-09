using System.IO;
using System.Security.Cryptography;
using System.Text;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using NINA.Profile;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

public sealed partial class DirectorAcquisition
{
    internal static NightCheckpointScope RestartScope(CoordinatorBinding binding, Guid client, Uri endpoint,
        string configuration, DirectorSessionOptions options) => new(binding, client, endpoint.AbsoluteUri, configuration,
            HashEncoding.Lower(SHA256.HashData(Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(options)))));

    private async Task<RecoveryIdentity> ReviewRestartAsync(DirectorSessionContainer container, CancellationToken token)
    {
        var options = container.Options.Clone();
        if (!options.AllowSettledRestart || !options.AutomaticWorkloads)
            throw new InvalidOperationException("Restart requires an already recorded settled night and automatic workloads.");
        NinaRestartAdmission.ValidateLifecycle(container);
        var profile = profiles.ActiveProfile.Id;
        var settings = new PluginOptionsAccessor(profiles, PluginId);
        using var connection = new DirectorConnection(() => profiles.ActiveProfile.Id, () => false,
            () => settings.GetValueString("CoordinatorUrl", ""), _ => { },
            readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""));
        var endpoint = connection.ResolvedEndpoint ?? throw new InvalidOperationException("Pair a Director coordinator first.");
        var pairing = connection.ReadPairing() ?? throw new InvalidOperationException("Pairing credentials are unavailable.");
        var rig = pairing.Binding.RigId.ToString("D");
        using var reader = new NinaConstraintSnapshot(profiles);
        var (_, equipment, configuration) = ReadNativeEquipment(options, profile, rig, reader);
        var recoveryRoot = Path.Combine(LocalStateRoot, "recovery", rig);
        if (!Directory.Exists(recoveryRoot)) throw new InvalidOperationException("No recorded night exists.");
        var storage = Directory.CreateDirectory(Path.Combine(LocalStateRoot, "restart-review", Guid.NewGuid().ToString("N"))).FullName;
        await using var runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!, storage, recoveryRoot);
        await runtime.StartAsync(rig, token);
        var record = (await runtime.ReadRecoveryAsync(token)).Value?.Record ?? throw new InvalidOperationException("No recorded night exists.");
        if (record.Snapshot.Phase is not RecoveryPhase.Acquiring)
            throw new InvalidOperationException("The recorded night is stopped, holding or has uncertain recovery. Restart cannot clear it.");
        var checkpoint = new CoordinatorNightCheckpoint(recoveryRoot, record.Snapshot.Identity,
            RestartScope(pairing.Binding, pairing.ClientId, endpoint, configuration.Id, options));
        var boundary = checkpoint.RequireIdle();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (now >= record.Snapshot.Identity.EndsAtMs) throw new InvalidOperationException("The recorded observing night has ended.");
        var stableMs = options.Weather == DirectorWeatherPolicy.HoldAndResume ? checked((ulong)options.StableSafeSeconds * 1000) : 0;
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(record.Snapshot.Identity.EndsAtMs - now, stableMs + 15000)));
        using var interlock = new NinaSafetyInterlock(profiles, safety, TimeProvider.System);
        using var enclosure = new NinaEnclosureInterlock(profiles, dome, options.Enclosure, TimeProvider.System);
        System.Diagnostics.Stopwatch? stable = null;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (interlock.Read().Safety == PlannerSafety.Safe && enclosure.Read().Motion == RecoveryMotion.Permitted)
            {
                stable ??= System.Diagnostics.Stopwatch.StartNew();
                if (stable.Elapsed.TotalMilliseconds >= stableMs) break;
            }
            else stable = null;
            await Task.Delay(250, deadline.Token);
        }
        interlock.Arm(); enclosure.Arm();
        NinaCaptureAdapter.RequireQuiescent(profiles, camera, profile, equipment.CameraDeviceId);
        var mount = telescope.GetInfo();
        if (!mount.Connected || mount.DeviceId != equipment.TelescopeDeviceId || !mount.AtPark || mount.Slewing || mount.TrackingEnabled)
            throw new InvalidOperationException("Restart requires the bound mount parked with tracking stopped.");
        await ConfirmGuiderStoppedAsync(deadline.Token);
        NinaCaptureAdapter.RequireQuiescent(profiles, camera, profile, equipment.CameraDeviceId);
        mount = telescope.GetInfo();
        if (profiles.ActiveProfile.Id != profile || !mount.Connected || mount.DeviceId != equipment.TelescopeDeviceId
            || !mount.AtPark || mount.Slewing || mount.TrackingEnabled || interlock.Interrupted.IsCancellationRequested || enclosure.Interrupted.IsCancellationRequested)
            throw new InvalidOperationException("Native context changed during restart admission.");
        var reviewed = (await runtime.ReviewRestartAsync(new(rig, configuration.Id, record.Snapshot.Identity.NightId,
            checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), true, boundary, true, true, true,
            interlock.ReadCurrent().Safety, enclosure.ReadCurrent().Motion), deadline.Token)).Value
            ?? throw new InvalidOperationException("The shared runtime refused restart review.");
        if (reviewed.Advice != RestartAdvice.RequestFreshAuthority)
            throw new InvalidOperationException($"Restart refused: {reviewed.Advice}.");
        return reviewed.Record.Snapshot.Identity;
    }

    private Task ConfirmGuiderStoppedAsync(CancellationToken token) => NinaGuiderStop.ConfirmAsync(profiles, guider, token);
}
