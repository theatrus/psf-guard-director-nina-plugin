using System.Globalization;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed class NinaPointingOffsetException(double arcminutes) : InvalidOperationException(
    $"The mount is {arcminutes.ToString("F1", CultureInfo.InvariantCulture)} arcminutes from the selected target (limit 3 arcminutes).")
{
    internal double Arcminutes { get; } = arcminutes;
}

// A successful science exposure resets the budget. A failed recenter must not
// fall back into the outer park/unpark/session-restart loop.
internal sealed class NinaPointingRecovery
{
    private bool attempted;
    internal bool HasAttempted => attempted;

    internal bool TryBegin(bool directorOwnsCenter)
    {
        if (!directorOwnsCenter || attempted) return false;
        attempted = true;
        return true;
    }

    internal void ExposureSaved() => attempted = false;

    internal static NinaPointingOffsetException? Find(Exception error)
    {
        // N.I.N.A. wraps sequence failures, but cancellation or multiple failures
        // (for example a failed journal write) must never become a local retry.
        while (error is not (AggregateException or OperationCanceledException))
        {
            if (error is NinaPointingOffsetException offset) return offset;
            if (error.InnerException is not { } inner) break;
            error = inner;
        }
        return null;
    }
}
