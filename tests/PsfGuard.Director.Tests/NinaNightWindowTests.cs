using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaNightWindowTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Milliseconds = 1000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Milliseconds);
    }

    [Fact]
    public void NightBoundsDoNotSlideAcrossAllocationsOrWaits()
    {
        var clock = new Clock();
        var night = new NinaNightWindow(1000, 5000, clock);
        Assert.Equal(TimeSpan.FromSeconds(4), night.Remaining(TimeSpan.FromMinutes(1)));
        Assert.Equal(4000UL, night.BoundValidity(4000));
        Assert.Equal(5000UL, night.BoundValidity(8000));
        clock.Milliseconds = 4000;
        Assert.Equal(TimeSpan.FromSeconds(1), night.Remaining(TimeSpan.FromMinutes(1)));
        clock.Milliseconds = 5000;
        Assert.True(night.Ended);
        Assert.Equal(TimeSpan.Zero, night.Remaining(TimeSpan.FromMinutes(1)));
        clock.Milliseconds = 6000;
        Assert.Equal(TimeSpan.Zero, night.Remaining(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ExpiredWaitFinishesNormallyButOperatorCancellationDoesNot()
    {
        var night = new NinaNightWindow(0, 1000, new Clock());
        await night.WaitAsync(TimeSpan.FromHours(1), default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => night.WaitAsync(TimeSpan.FromHours(1), cancel.Token));
    }

    [Fact]
    public void NativeStateKeepsNightAndFreshnessDeadlinesSeparate()
    {
        var state = new PlannerState("rig", "config", 1000, 2000, PlannerSafety.Safe, true, false, new(0, 0), 5000);
        Assert.Equal(2000UL, state.ConditionsValidUntilMs);
        Assert.Equal(5000UL, state.CompletionDeadlineMs);
    }

    [Fact]
    public async Task PinnedSidecarRequiresCompleteExposureToFitTheNight()
    {
        var request = PlannerTests.Request();
        await using var runtime = new RuntimeController(ProcessTests.BundleDirectory);
        await runtime.StartAsync("rig-test");
        request = request with { State = request.State with { CompletionDeadlineMs = 3500 } };
        Assert.Equal(PlannerAction.Acquire, (await runtime.EvaluateAsync(request)).Decision!.Action);
        request = request with { State = request.State with { NowMs = 2001 } };
        Assert.Equal(new(PlannerAction.Wait, "observing_night_window_too_short"), (await runtime.EvaluateAsync(request)).Decision);
        request = request with { State = request.State with { NowMs = 3500 } };
        Assert.Equal(new(PlannerAction.Complete, "observing_night_ended"), (await runtime.EvaluateAsync(request)).Decision);
        request = request with { State = request.State with { OperatorStop = true } };
        Assert.Equal(PlannerAction.Stop, (await runtime.EvaluateAsync(request)).Decision!.Action);
    }

    [Theory]
    [InlineData("reserved")]
    [InlineData("uncertain")]
    [InlineData("preparation")]
    public async Task NormalCompletionRequiresSettledLedgerEvidence(string pending)
    {
        var root = Directory.CreateTempSubdirectory("director-night-completion-");
        try
        {
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory, root.FullName);
            var request = PlannerTests.Request();
            await runtime.StartAsync("rig-test");
            Assert.NotNull((await runtime.OpenLedgerAsync(request)).Value);
            await DirectorAcquisition.EnsureSettledAsync(runtime, default);
            if (pending == "preparation")
                Assert.NotNull((await runtime.BeginPreparationAsync("prep", new("goal", "target", "recipe", "target", "L", 0, false, false, false, 0, null, 0),
                    new(0, 0, 0, 0, 0, 0, 0), request.State)).Value);
            else
            {
                Assert.Equal(ReservationKind.Created, (await runtime.ReserveAsync("capture", request.State)).Value!.Kind);
                if (pending == "uncertain") Assert.NotNull((await runtime.RecordAsync("capture", new LedgerEvidence.Uncertain("unconfirmed"))).Value);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => DirectorAcquisition.EnsureSettledAsync(runtime, default));
        }
        finally { root.Delete(true); }
    }
}
