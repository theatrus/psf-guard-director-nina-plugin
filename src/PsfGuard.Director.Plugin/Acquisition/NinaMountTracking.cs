using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaMountTracking
{
    internal static async Task StartSiderealAsync(ITelescopeMediator telescope, Action checkContext,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        checkContext();
        progress.Report(new ApplicationStatus { Status = "Starting sidereal tracking" });
        // NINA sets both the sidereal rate and tracking enabled, and resets
        // custom rate offsets. A slew alone does not guarantee any of these.
        // The immediate return can still reflect ASCOM's pre-command cache.
        // Confirm through NINA's subsequent equipment updates instead.
        await new NINA.Sequencer.SequenceItem.Telescope.SetTracking(telescope) { TrackingMode = TrackingMode.Sidereal }.Execute(progress, token);
        using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(token);
        confirmation.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                confirmation.Token.ThrowIfCancellationRequested();
                checkContext();
                var info = telescope.GetInfo();
                if (info.TrackingEnabled && info.TrackingRate.TrackingMode == TrackingMode.Sidereal) return;
                await Task.Delay(100, confirmation.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("The mount did not confirm sidereal tracking within 5 seconds. Target setup stopped before imaging.");
        }
    }
}
