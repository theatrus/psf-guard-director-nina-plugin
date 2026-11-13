using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Sequencer;

public sealed partial class DirectorSessionOptions
{
    public string MeridianSummary
    {
        get
        {
            if (!Valid(MeridianBeforeMinutes) || !Valid(MeridianAfterMinutes))
                return "Enter 0 to 360 minutes on each side of the meridian.";
            if (MeridianBeforeMinutes == 0 && MeridianAfterMinutes == 0)
                return "No extra time is excluded around target transit. N.I.N.A. still handles meridian flips.";
            if (MeridianAfterMinutes == 0)
                return $"Stop imaging this target {MeridianBeforeMinutes:g} minutes before transit. It becomes eligible again at transit.";
            if (MeridianBeforeMinutes == 0)
                return $"Stop imaging this target at transit. It becomes eligible again {MeridianAfterMinutes:g} minutes after transit.";
            return $"Stop imaging this target {MeridianBeforeMinutes:g} minutes before transit. It becomes eligible again {MeridianAfterMinutes:g} minutes after transit.";
        }
    }

    internal PlannerMeridianExclusion MeridianExclusion()
    {
        if (!Valid(MeridianBeforeMinutes) || !Valid(MeridianAfterMinutes))
            throw new InvalidOperationException("Meridian avoidance must be between 0 and 360 minutes on each side.");
        return new(checked((ulong)(MeridianBeforeMinutes * 60000)), checked((ulong)(MeridianAfterMinutes * 60000)));
    }

    private static bool Valid(double minutes) => double.IsFinite(minutes) && minutes is >= 0 and <= 360;
}
