using System.IO;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaMountShutdown
{
    private static TelescopeInfo ReadMount(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId)
    {
        var info = telescope.GetInfo();
        if (profiles.ActiveProfile.Id != profileId || !info.Connected || info.DeviceId != deviceId)
            throw new InvalidOperationException("Cannot shut down mount: original telescope/profile is unavailable.");
        return info;
    }

    internal static void Stop(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId)
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
            if (telescope.SetTrackingEnabled(false)) throw new IOException("Mount refused tracking-off during shutdown.");
            ReadMount(profiles, profileId, telescope, deviceId);
        }
        catch (Exception e) { errors.Add(e); }
        if (errors.Count != 0) throw new AggregateException("Mount stop could not be confirmed.", errors);
    }

    internal static async Task ParkAsync(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId,
        Func<NinaMotionEvidence> clearance, CancellationToken interrupted, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
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
            // A failed or timed-out park must not leave tracking/slewing running.
            try { Stop(profiles, profileId, telescope, deviceId); }
            catch (Exception stopError) { throw new AggregateException("Mount park and fallback stop failed.", parkError, stopError); }
            throw;
        }
    }
}
