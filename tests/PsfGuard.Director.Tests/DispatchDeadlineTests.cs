using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DispatchDeadlineTests
{
    private static PlannerDispatchCheck Check(ulong evaluated = 1000, ulong? latest = 1002) =>
        new(new(PlannerAction.Acquire, "ready", "goal"), evaluated, latest);

    [Fact]
    public void InclusiveEndpointAndSubMillisecondRoundingAreExact()
    {
        Check().EnsureWithinDeadline(1002, TimeSpan.FromMilliseconds(2));
        Check().EnsureWithinDeadline(1001, TimeSpan.FromTicks(1));
        Assert.Throws<InvalidOperationException>(() => Check().EnsureWithinDeadline(1002, TimeSpan.FromTicks(20001)));
        Check(1000, 1000).EnsureWithinDeadline(1000, TimeSpan.Zero);
        Assert.Throws<InvalidOperationException>(() => Check(1000, 1000).EnsureWithinDeadline(1000, TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void WallClockAndMonotonicAgeMustBothFit()
    {
        Assert.Throws<InvalidOperationException>(() => Check().EnsureWithinDeadline(999, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => Check().EnsureWithinDeadline(1003, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => Check().EnsureWithinDeadline(1000, TimeSpan.FromMilliseconds(3)));
        Assert.Throws<InvalidOperationException>(() => Check().EnsureWithinDeadline(1000, TimeSpan.FromTicks(-1)));
        Assert.Throws<InvalidOperationException>(() => Check(latest: null).EnsureWithinDeadline(1000, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => Check(latest: 999).EnsureWithinDeadline(1000, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => (Check() with { Decision = new(PlannerAction.Stop, "unsafe") }).EnsureWithinDeadline(1000, TimeSpan.Zero));
    }

    [Fact]
    public void LargeIntegerTimestampsDoNotLosePrecisionOrOverflow()
    {
        const ulong start = 9007199254740993;
        Check(start, start + 1).EnsureWithinDeadline(start + 1, TimeSpan.FromMilliseconds(1));
        Assert.Throws<InvalidOperationException>(() => Check(start, start + 1).EnsureWithinDeadline(start + 2, TimeSpan.Zero));
        Check(ulong.MaxValue - 1, ulong.MaxValue).EnsureWithinDeadline(ulong.MaxValue, TimeSpan.FromTicks(1));
    }
}
