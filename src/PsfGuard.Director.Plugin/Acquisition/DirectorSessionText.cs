using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// Keep planner reason codes in the protocol. The sequencer and Live page
// show what Director is waiting for or doing next.
internal static class DirectorSessionText
{
    internal static (string Phase, string Detail) StartupWait(NinaSafetyEvidence monitor, NinaMotionEvidence roof)
    {
        if (monitor.Safety != PlannerSafety.Safe)
            return ("Waiting for the safety monitor to report Safe", $"{monitor.Reason}.");
        return ("Waiting for the enclosure to open", $"{roof.Reason}.");
    }

    internal static string OperationName(PreparationOperation operation) => operation switch
    {
        PreparationOperation.Unpark => "Unpark mount",
        PreparationOperation.Center { Rotate: true } => "Center and rotate",
        PreparationOperation.Center => "Center target",
        PreparationOperation.BeforeTarget => "Set up target",
        PreparationOperation.Dither => "Dither",
        PreparationOperation.SwitchFilter => "Change filter",
        PreparationOperation.SetReadoutMode => "Set camera readout mode",
        _ => "Prepare exposure",
    };

    internal static (string Phase, string Detail) ForDecision(PlannerDecision decision) => decision.Reason switch
    {
        "future_window" => ("Waiting for a target to become observable",
            "An assigned exposure can fit later. Director will check again automatically."),
        "assignment_not_started" => ("Waiting for the assigned observing time",
            "Director will check again when the observing window opens."),
        "moon_avoidance" => ("Waiting for the Moon to clear",
            "The assigned exposures need the Moon lower or farther from the target."),
        "observing_night_window_too_short" => ("Waiting for the session to end",
            "The remaining session time is too short for an assigned exposure."),
        "pending_assessment" => ("Waiting for image grades",
            "PSF Guard is still grading the saved images."),
        "no_authorized_feasible_work" => ("No assigned exposure fits the observing limits",
            "Check the assigned targets and the rig’s horizon, altitude, meridian and Moon settings."),
        "configuration_mismatch" => ("Equipment settings changed",
            "Start a new Director Session with the current equipment settings."),
        "assignment_expired" => ("The assigned observing time ended", "-"),
        "conditions_stale" => ("Director needs to check the observing conditions again", "-"),
        "safety_not_confirmed" => ("Director stopped because safety could not be confirmed", "-"),
        "operator_stop" => ("Session stopped", "-"),
        "await_operation_boundary" => ("Finishing the current operation", "-"),
        "accepted_goal_met" => ("The assigned image counts are complete", "-"),
        "observing_night_ended" => ("Session reached its maximum duration", "-"),
        _ => decision.Action switch
        {
            PlannerAction.Acquire => ("Selecting the next exposure", "-"),
            PlannerAction.Wait => ("Waiting for an exposure that can run", "-"),
            PlannerAction.Complete => ("Assigned work is complete", "-"),
            _ => ("Director could not select another exposure", "See the N.I.N.A. log for the planner reason."),
        },
    };
}
