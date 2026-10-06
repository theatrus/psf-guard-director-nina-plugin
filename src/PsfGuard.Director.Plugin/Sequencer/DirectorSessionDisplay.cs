namespace PsfGuard.Director.Plugin.Sequencer;

// Display-only observations. Never serialized with a sequence or read as permits.
public sealed record DirectorSessionDisplay(
    string Phase, string Rig, string Project, string Target, string Goal, string Operation,
    string ProgramRevision, string Connectivity, string Safety, string QueueDepth,
    string LastCheckIn, string WaitReason, string Quality = "Off")
{
    public static DirectorSessionDisplay Empty { get; } = new(
        "Configuration preview - acquisition not armed", "-", "-", "-", "-", "-", "-", "Not connected", "Not evaluated", "-", "-", "-");
}
