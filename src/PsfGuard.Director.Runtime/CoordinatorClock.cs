using System.Net;

namespace PsfGuard.Director.Runtime;

// Advance the coordinator's HTTP Date using elapsed time. A workload's
// admission timestamp can refine Date within its whole-second precision.
internal sealed class CoordinatorClock(TimeProvider time, DateTimeOffset? date, long receivedAt) : TimeProvider
{
    internal CoordinatorClock WithIssuance(ulong issuedAtMs)
    {
        if (date is not { } serverDate) return this;
        var dateMs = checked((ulong)serverDate.ToUnixTimeMilliseconds());
        return issuedAtMs >= dateMs && issuedAtMs - dateMs < 1000
            ? new(time, DateTimeOffset.FromUnixTimeMilliseconds((long)issuedAtMs), receivedAt)
            : this;
    }

    internal ulong NowMs => checked((ulong)GetUtcNow().ToUnixTimeMilliseconds());
    public override DateTimeOffset GetUtcNow() =>
        date is { } serverDate ? serverDate + time.GetElapsedTime(receivedAt) : time.GetUtcNow();
    public override long GetTimestamp() => time.GetTimestamp();
    public override long TimestampFrequency => time.TimestampFrequency;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        time.CreateTimer(callback, state, dueTime, period);
}

internal sealed record CoordinatorResponse(byte[] Bytes, string? ETag, HttpStatusCode Status, CoordinatorClock Clock);
