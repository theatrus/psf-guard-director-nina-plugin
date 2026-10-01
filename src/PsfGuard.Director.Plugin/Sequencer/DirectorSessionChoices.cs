namespace PsfGuard.Director.Plugin.Sequencer;

public sealed record DirectorChoice<T>(T Value, string Label);

public static class DirectorSessionChoices
{
    public static IReadOnlyList<DirectorChoice<DirectorOperationOwner>> OperationOwners { get; } =
        [new(DirectorOperationOwner.Director, "Director defaults"), new(DirectorOperationOwner.Sequence, "Sequence instructions / triggers")];
    public static IReadOnlyList<DirectorChoice<DirectorSafetyPolicy>> SafetyPolicies { get; } =
        [new(DirectorSafetyPolicy.RequireMonitor, "Require connected safety monitor"), new(DirectorSafetyPolicy.Attended, "Attended operation")];
    public static IReadOnlyList<DirectorChoice<DirectorHorizonPolicy>> HorizonPolicies { get; } =
        [new(DirectorHorizonPolicy.NinaProfile, "NINA profile horizon"), new(DirectorHorizonPolicy.MinimumAltitude, "Fixed minimum altitude")];
}
