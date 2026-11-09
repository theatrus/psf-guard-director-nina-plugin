namespace PsfGuard.Director.Plugin.Acquisition;

// Wall-clock scheduling boundary. Operation selection and fit remain in Rust;
// reaching this boundary must not masquerade as an operator cancellation.
internal sealed class NinaNightWindow(ulong start, ulong end, TimeProvider clock)
{
    internal ulong Start { get; } = start;
    internal ulong End { get; } = end > start ? end : throw new ArgumentOutOfRangeException(nameof(end));
    internal ulong Now => checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());
    internal bool Ended => Now >= End;
    // Translate the remaining local session duration into the planner's clock domain.
    internal ulong EndOn(ulong planningNow)
    {
        var now = Now;
        return checked(planningNow + (now < End ? End - now : 0));
    }

    internal TimeSpan Remaining(TimeSpan maximum)
    {
        if (maximum < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximum));
        var now = Now;
        return now >= End ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds, End - now));
    }

    internal Task WaitAsync(TimeSpan maximum, CancellationToken token) => Task.Delay(Remaining(maximum), clock, token);
}
