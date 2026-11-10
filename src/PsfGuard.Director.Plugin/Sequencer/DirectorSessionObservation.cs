namespace PsfGuard.Director.Plugin.Sequencer;

// Local display only. Do not retain old sequences or use these observations to
// authorize operations. A profile switch must never show another rig's activity.
internal static class DirectorSessionObservation
{
    private static readonly object Gate = new();
    private static Guid profile;
    private static WeakReference<DirectorSessionContainer>? latest;

    internal static void Observe(Guid profileId, DirectorSessionContainer session)
    {
        lock (Gate) { profile = profileId; latest = new(session); }
    }

    internal static DirectorSessionContainer? Read(Guid profileId)
    {
        lock (Gate) return profile == profileId && latest?.TryGetTarget(out var session) == true ? session : null;
    }
}
