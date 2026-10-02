using System.IO;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaMountShutdown
{
    internal static async Task ParkAsync(IProfileService profiles, Guid profileId, ITelescopeMediator telescope, string deviceId,
        Func<NinaMotionEvidence> clearance, CancellationToken interrupted, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        TelescopeInfo ReadMount()
        {
            var info = telescope.GetInfo();
            if (profiles.ActiveProfile.Id != profileId || !info.Connected || info.DeviceId != deviceId)
                throw new InvalidOperationException("Cannot shut down mount: original telescope/profile is unavailable.");
            return info;
        }
        void StopMotion()
        {
            // Stop requests are allowed when a new slew/park is not. Attempt both
            // independently, but never send either to a replacement device.
            var errors = new List<Exception>();
            try { ReadMount(); telescope.StopSlew(); } catch (Exception e) { errors.Add(e); }
            try
            {
                ReadMount();
                if (!telescope.SetTrackingEnabled(false)) throw new IOException("Mount refused tracking-off during blocked shutdown.");
            }
            catch (Exception e) { errors.Add(e); }
            if (errors.Count != 0) throw new AggregateException("Mount stop could not be confirmed; enclosure blocks parking.", errors);
        }

        if (ReadMount().AtPark) return;
        using var motion = CancellationTokenSource.CreateLinkedTokenSource(token, interrupted);
        try
        {
            var evidence = clearance();
            if (evidence.Motion != RecoveryMotion.Permitted) throw new InvalidOperationException(evidence.Reason);
            ReadMount();
            motion.Token.ThrowIfCancellationRequested();
            if (!await telescope.ParkTelescope(progress, motion.Token)) throw new IOException("Director shutdown park failed.");
            if (clearance().Motion != RecoveryMotion.Permitted || interrupted.IsCancellationRequested)
                throw new InvalidOperationException("Enclosure clearance was lost during park.");
        }
        finally
        {
            if (interrupted.IsCancellationRequested || clearance().Motion != RecoveryMotion.Permitted) StopMotion();
        }
    }
}
