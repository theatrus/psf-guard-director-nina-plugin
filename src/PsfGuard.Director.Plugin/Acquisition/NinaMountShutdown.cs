using System.IO;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed class NinaMountShutdown
{
    private bool parkFailed;
    internal bool CanResumeWeather => !parkFailed;
    private static TelescopeInfo ReadMount(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId)
    {
        var info = telescope.GetInfo();
        if (profiles.ActiveProfile.Id != profileId || !info.Connected || info.DeviceId != deviceId)
            throw new InvalidOperationException("Cannot shut down mount: original telescope/profile is unavailable.");
        return info;
    }

    internal static void Stop(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId)
        => RequestStop(profiles, profileId, telescope, deviceId, requireImmediateConfirmation: true);

    internal static async Task StopAndConfirmAsync(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId, CancellationToken token)
    {
        // ASCOM DeviceState can retain the pre-command tracking value until
        // NINA's next poll. Send once, then verify native state without retrying.
        RequestStop(profiles, profileId, telescope, deviceId, requireImmediateConfirmation: false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var info = ReadMount(profiles, profileId, telescope, deviceId);
            if (!info.Slewing && !info.TrackingEnabled) return;
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }

    private static void RequestStop(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId, bool requireImmediateConfirmation)
    {
        if (ReadMount(profiles, profileId, telescope, deviceId).AtPark) return;
        // Attempt both stop commands independently, but never command a replacement device.
        var errors = new List<Exception>();
        try { ReadMount(profiles, profileId, telescope, deviceId); telescope.StopSlew(); }
        catch (Exception e) { errors.Add(e); }
        try
        {
            ReadMount(profiles, profileId, telescope, deviceId);
            // NINA returns the resulting tracking state, not a success flag.
            var tracking = telescope.SetTrackingEnabled(false);
            if (tracking && requireImmediateConfirmation) throw new IOException("Mount refused tracking-off during shutdown.");
            ReadMount(profiles, profileId, telescope, deviceId);
        }
        catch (Exception e) { errors.Add(e); }
        if (errors.Count != 0) throw new AggregateException("Mount stop could not be confirmed.", errors);
    }

    internal async Task ParkAsync(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId,
        Func<NinaMotionEvidence> clearance, CancellationToken interrupted, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (parkFailed) throw new InvalidOperationException("A prior park failed; automatic retry is blocked for this session.");
        if (ReadMount(profiles, profileId, telescope, deviceId).AtPark) return;
        using var motion = CancellationTokenSource.CreateLinkedTokenSource(token, interrupted);
        try
        {
            var evidence = clearance();
            if (evidence.Motion != RecoveryMotion.Permitted) throw new InvalidOperationException(evidence.Reason);
            ReadMount(profiles, profileId, telescope, deviceId);
            motion.Token.ThrowIfCancellationRequested();
            if (!await telescope.ParkTelescope(progress, motion.Token)) throw new IOException("Director shutdown park failed.");
            if (clearance().Motion != RecoveryMotion.Permitted || interrupted.IsCancellationRequested)
                throw new InvalidOperationException("Enclosure clearance was lost during park.");
        }
        catch (Exception parkError)
        {
            parkFailed = true;
            // A failed or timed-out park must not leave tracking/slewing running.
            try { Stop(profiles, profileId, telescope, deviceId); }
            catch (Exception stopError) { throw new AggregateException("Mount park and fallback stop failed.", parkError, stopError); }
            throw;
        }
    }
}
