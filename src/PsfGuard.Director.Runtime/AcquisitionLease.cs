namespace PsfGuard.Director.Runtime;

/// <summary>Single local Director equipment owner. Server one-shot admission is
/// still mandatory; neither the lock nor a persisted file is launch authority.</summary>
public sealed class AcquisitionLease : IDisposable
{
    private readonly FileStream file;
    private readonly TimeProvider clock;
    private readonly long timestamp;
    private readonly DateTimeOffset utc;
    private static int active;
    private bool disposed;
    public static bool IsActive => Volatile.Read(ref active) != 0;

    public AcquisitionLease(string root, TimeProvider? clock = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Absolute local state root required.");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("Another Director session owns the equipment.");
        try
        {
            this.clock = clock ?? TimeProvider.System;
            timestamp = this.clock.GetTimestamp();
            utc = this.clock.GetUtcNow();
            Directory.CreateDirectory(root);
            file = new(Path.Combine(root, "acquisition.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch { Interlocked.Exchange(ref active, 0); throw; }
    }

    public void CheckClock()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var elapsed = clock.GetElapsedTime(timestamp);
        if (elapsed < TimeSpan.Zero || Math.Abs(((clock.GetUtcNow() - utc) - elapsed).TotalSeconds) > 2)
            throw new InvalidOperationException("System clock changed. Director requires a new reconciled session.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { file.Dispose(); }
        finally { Interlocked.Exchange(ref active, 0); }
    }
}
