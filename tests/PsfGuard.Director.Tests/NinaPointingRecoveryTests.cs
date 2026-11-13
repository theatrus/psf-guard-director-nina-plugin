using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaPointingRecoveryTests
{
    [Fact]
    public void RecenterIsBoundedUntilAnExposureSaves()
    {
        var recovery = new NinaPointingRecovery();
        Assert.False(recovery.TryBegin(directorOwnsCenter: false));
        Assert.True(recovery.TryBegin(directorOwnsCenter: true));
        Assert.False(recovery.TryBegin(directorOwnsCenter: true));
        recovery.ExposureSaved();
        Assert.True(recovery.TryBegin(directorOwnsCenter: true));
    }

    [Fact]
    public void NativeFailureWrappersRetainOffsetButMixedFailuresCannotRetry()
    {
        var offset = new NinaPointingOffsetException(12.5);
        var sequenceFailure = new InvalidOperationException("Director exposure", offset);
        Assert.Same(offset, NinaPointingRecovery.Find(sequenceFailure));
        Assert.Contains("12.5 arcminutes", offset.Message);
        Assert.Null(NinaPointingRecovery.Find(new AggregateException(sequenceFailure, new IOException("journal failed"))));
        Assert.Null(NinaPointingRecovery.Find(new OperationCanceledException("canceled", offset)));
        Assert.Null(NinaPointingRecovery.Find(new InvalidOperationException("mount disconnected")));
    }

    [Fact]
    public void NoFitWaitKeepsNewPlansAndNormalNightEndDoesNotPromiseMoreWork()
    {
        var wait = DirectorSessionText.ForDecision(new(PlannerAction.Wait, "observing_night_window_too_short"));
        Assert.Equal("No assigned exposure fits before the session ends", wait.Phase);
        Assert.Contains("checking for new plans", wait.Detail);
        Assert.Equal("Session time ended; finishing up", DirectorSessionText.FinishedAllocation(true, false));
        Assert.Equal("Session time ended; finishing up", DirectorSessionText.FinishedAllocation(true, true));
    }
}
