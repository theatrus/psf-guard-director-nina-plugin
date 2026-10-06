namespace PsfGuard.Director.Plugin.Sequencer;

public sealed record DirectorChoice<T>(T Value, string Label);

public static class DirectorSessionChoices
{
    public static IReadOnlyList<DirectorChoice<DirectorQualityPolicy>> QualityPolicies { get; } =
        [new(DirectorQualityPolicy.Off, "Off"), new(DirectorQualityPolicy.Monitor, "Monitor only"),
         new(DirectorQualityPolicy.ParkAndStop, "Stop for night"), new(DirectorQualityPolicy.HoldAndProbe, "Hold and probe (experimental)")];
    public static IReadOnlyList<DirectorChoice<DirectorWeatherPolicy>> WeatherPolicies { get; } =
        [new(DirectorWeatherPolicy.StopForNight, "Stop for night"), new(DirectorWeatherPolicy.HoldAndResume, "Hold and resume")];
    public static IReadOnlyList<DirectorChoice<DirectorCheckInMode>> CheckInModes { get; } =
        [new(DirectorCheckInMode.Live, "Live"), new(DirectorCheckInMode.Deferred, "Deferred")];
    public static IReadOnlyList<DirectorChoice<DirectorAbortPolicy>> AbortPolicies { get; } =
        [new(DirectorAbortPolicy.ParkMount, "Park mount (clearance required)"),
         new(DirectorAbortPolicy.StopMount, "Stop slew and tracking (no park)")];
    public static IReadOnlyList<DirectorChoice<DirectorEnclosurePolicy>> EnclosurePolicies { get; } =
        [new(DirectorEnclosurePolicy.Unconfigured, "Not configured"), new(DirectorEnclosurePolicy.OpenAir, "Open air (no enclosure)"),
         new(DirectorEnclosurePolicy.RequireOpenShutter, "Require fully open enclosure")];
    public static IReadOnlyList<DirectorChoice<DirectorOperationOwner>> OperationOwners { get; } =
        [new(DirectorOperationOwner.Director, "Director defaults"), new(DirectorOperationOwner.Sequence, "Sequence instructions / triggers")];
    public static IReadOnlyList<DirectorChoice<DirectorSafetyPolicy>> SafetyPolicies { get; } =
        [new(DirectorSafetyPolicy.RequireMonitor, "Require connected safety monitor"), new(DirectorSafetyPolicy.Attended, "Attended operation")];
    public static IReadOnlyList<DirectorChoice<DirectorHorizonPolicy>> HorizonPolicies { get; } =
        [new(DirectorHorizonPolicy.NinaProfile, "NINA profile horizon"), new(DirectorHorizonPolicy.MinimumAltitude, "Fixed minimum altitude")];
}
