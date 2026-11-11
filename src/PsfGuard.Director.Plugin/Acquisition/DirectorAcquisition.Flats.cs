using System.Collections.Immutable;
using System.IO;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Profile;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

public sealed partial class DirectorAcquisition
{
    internal static async Task<CalibrationPlan> RefreshCalibrationPlanAsync(CoordinatorCalibrationClient client,
        DirectorConfiguration configuration, CancellationToken token)
    {
        try { return await client.RefreshAsync(configuration, token); }
        catch (CoordinatorIntakeException error) when (error.Failure == CoordinatorIntakeFailure.NotFound)
        {
            throw PolicyStop("This PSF Guard server does not support calibration sync. Update PSF Guard before using Director Flats or automatic calibration.");
        }
    }

    private double? CalibrationRotation()
    {
        var selected = profiles.ActiveProfile.RotatorSettings.Id;
        if (selected == "No_Device") return null;
        var info = rotator?.GetInfo();
        if (info?.Connected != true || info.DeviceId != selected || info.IsMoving || !double.IsFinite(info.MechanicalPosition))
            throw new InvalidOperationException("Connect the selected rotator and stop its movement before taking flats.");
        return info.MechanicalPosition;
    }

    internal async Task ExecuteFlatsAsync(DirectorSessionOptions options, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var problems = options.ValidateSettings();
        if (problems.Count != 0) throw new InvalidOperationException(string.Join(" ", problems));
        using var owner = new AcquisitionLease(LocalStateRoot);
        var profile = profiles.ActiveProfile;
        var settings = new PluginOptionsAccessor(profiles, PluginId);
        using var connection = new DirectorConnection(() => profiles.ActiveProfile.Id, () => false,
            () => settings.GetValueString("CoordinatorUrl", ""), _ => { },
            readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""));
        var endpoint = connection.ResolvedEndpoint ?? throw new InvalidOperationException("Pair Director with PSF Guard in the plugin settings first.");
        var pairing = connection.ReadPairing() ?? throw new InvalidOperationException("Pair Director with PSF Guard in the plugin settings first.");
        ValueTask<string?> Credential(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var current = DirectorCredentialStore.Read(endpoint, profile.Id);
            if (!ReferenceEquals(profiles.ActiveProfile, profile) || current?.ClientId != pairing.ClientId || current.Binding != pairing.Binding)
                throw new InvalidOperationException("The Director pairing or profile changed during flats.");
            return ValueTask.FromResult<string?>(current.Token);
        }
        var wheel = profile.FilterWheelSettings.Id;
        var binding = new NinaEquipmentBinding(profile.Id, pairing.Binding.RigId.ToString("D"), "calibration",
            profile.CameraSettings.Id, wheel == "No_Device" ? null : wheel,
            wheel == "No_Device" ? [new("fixed-filter", null, null)] : profile.FilterWheelSettings.FilterWheelFilters
                .Select(f => new NinaFilterBinding($"filter-{f.Position}", f.Position, f.Name)).ToImmutableArray(), false, 0);
        var reader = new NinaEquipmentSnapshot(profiles, camera, filters);
        var configuration = reader.Read(binding);
        if (camera.GetInfo().IsExposing)
            throw new InvalidOperationException("Wait for the current camera exposure to finish before starting Director Flats.");
        var container = NinaSequencerDispatcher.Invoke(() => DirectorSessionContainer.ForCalibration(options));
        var calibration = new NinaCalibrationSession(profiles, camera, telescope, filters, imaging, saves, history,
            flatDevice, nighttime, container, progress, immediateFlats: true);
        var coverage = new DirectorFlatCoverage(LocalStateRoot, endpoint, pairing.Binding, connection.AllowInsecureHttp);
        calibration.AttachCoverage(coverage, CalibrationRotation);
        using var client = new CoordinatorCalibrationClient(endpoint, pairing.Binding, coverage, Credential, connection.AllowInsecureHttp);
        void Check()
        {
            owner.CheckClock();
            if (!ReferenceEquals(profiles.ActiveProfile, profile) || reader.Read(binding).Id != configuration.Id)
                throw new InvalidOperationException("The calibration camera, filter wheel or profile changed.");
        }
        progress.Report(new ApplicationStatus { Status = "Checking active plans and flat coverage with PSF Guard" });
        var plan = await RefreshCalibrationPlanAsync(client, configuration, token);
        calibration.SetRecipes(plan.Recipes, configuration);
        if (!calibration.FlatsDue)
        {
            progress.Report(new ApplicationStatus { Status = plan.Recipes.Length == 0 ? "No active-plan filters need flats" : "Flats are already complete for this setup and night" });
            return;
        }
        if (!calibration.Panel.CanClose || flatDevice?.GetInfo().SupportsOnOff != true)
            throw new InvalidOperationException("Director Flats requires a connected motorized cover with a controllable panel light.");
        Check();
        if (telescope.GetInfo().Connected)
            await NinaMountShutdown.StopAndConfirmAsync(profiles, profile.Id, telescope, profile.TelescopeSettings.Id, token);
        if (guider?.GetInfo().Connected == true)
        {
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token);
            stopped.CancelAfter(TimeSpan.FromSeconds(15));
            await ConfirmGuiderStoppedAsync(stopped.Token);
        }
        Exception? failure = null;
        try
        {
            var nextSync = DateTimeOffset.UtcNow.AddSeconds(30);
            while (calibration.FlatsDue)
            {
                token.ThrowIfCancellationRequested(); Check();
                if (DateTimeOffset.UtcNow >= nextSync)
                {
                    plan = await RefreshCalibrationPlanAsync(client, configuration, token);
                    calibration.SetRecipes(plan.Recipes, configuration);
                    nextSync = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                if (!await calibration.RunOneAsync(false, _ => Task.FromResult(true), Check, token)) break;
            }
            // Send the last completed batch now. Any failed upload remains durable
            // and is replayed by the next action or calibration-enabled session.
            await RefreshCalibrationPlanAsync(client, configuration, token);
            progress.Report(new ApplicationStatus { Status = "Flat frames saved; coverage sent to PSF Guard" });
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(calibration.CleanupTimeout);
            try
            {
                await calibration.RestoreCameraAsync(Check, cleanup.Token);
                if (options.ClosePanelAtEnd || failure is not null) await calibration.Panel.CloseAsync(Check, progress, cleanup.Token);
                else await calibration.Panel.OpenForLightsAsync(Check, progress, cleanup.Token);
            }
            catch (Exception error) when (failure is not null) { Logger.Error(error); }
        }
    }
}
