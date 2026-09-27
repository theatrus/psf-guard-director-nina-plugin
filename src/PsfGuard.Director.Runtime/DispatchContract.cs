namespace PsfGuard.Director.Runtime;

/// <summary>A one-use dispatch feasibility result, never recovery or hardware authority.</summary>
public sealed record PlannerDispatchCheck(PlannerDecision Decision, ulong EvaluatedAtMs, ulong? LatestStartMs)
{
    /// <summary>Check wall-clock and monotonic age immediately before native dispatch, after local revalidation.</summary>
    public void EnsureWithinDeadline(ulong nowMs, TimeSpan elapsed)
    {
        if (Decision.Action != PlannerAction.Acquire || LatestStartMs is not { } latest
            || latest < EvaluatedAtMs || nowMs < EvaluatedAtMs || nowMs > latest || elapsed.Ticks < 0)
            throw new InvalidOperationException("The Director dispatch window is no longer valid.");
        // Round up: sub-millisecond transport or hook time must never gain slack.
        var ticks = (ulong)elapsed.Ticks;
        var milliseconds = ticks / TimeSpan.TicksPerMillisecond + (ticks % TimeSpan.TicksPerMillisecond == 0 ? 0UL : 1UL);
        if (milliseconds > latest - EvaluatedAtMs)
            throw new InvalidOperationException("The Director dispatch deadline elapsed during validation.");
    }
}
