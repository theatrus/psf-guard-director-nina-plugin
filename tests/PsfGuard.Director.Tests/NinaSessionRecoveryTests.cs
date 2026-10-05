using NINA.Core.Model;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaSessionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafeStartupUsesDurableWeatherBudgetBeforeAnyLedger(bool roof)
    {
        await using var f = await Fixture.Create(weather: true, unsafeStartup: true, roof: roof);
        Assert.IsType<RecoveryPhase.WeatherHolding>(f.Recovery.Record!.Snapshot.Phase);
        Assert.Equal(1U, f.Recovery.Record.Snapshot.WeatherInterruptions);
        f.Clock.Now = f.Start + 1000;
        f.Conditions = new(PlannerSafety.Safe, RecoveryMotion.Permitted);
        await f.Recovery.ObserveWeatherAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.ResumeWeatherAsync(default, idle: true));
        for (var i = 2; i <= 7; i++)
        {
            f.Clock.Now = f.Start + (ulong)i * 1000;
            await f.Recovery.ObserveWeatherAsync(default);
        }
        await f.Recovery.ResumeWeatherAsync(default, idle: true);
        Assert.IsType<RecoveryPhase.Acquiring>(f.Recovery.Record.Snapshot.Phase);
        Assert.Equal(1U, f.Recovery.Record.Snapshot.WeatherInterruptions);
        Assert.True(f.Recovery.Record.Snapshot.WeatherHoldMs >= 6000);
    }
    [Fact]
    public async Task WeatherHoldRequiresSettledLedgerAndClosedRoofAllowsNormalNightExit()
    {
        await using var f = await Fixture.Create(weather: true);
        f.Conditions = new(PlannerSafety.Safe, RecoveryMotion.Unknown);
        await f.Recovery.InterruptWeatherAsync(true, default);
        Assert.IsType<RecoveryPhase.WeatherHolding>(f.Recovery.Record!.Snapshot.Phase);
        f.Conditions = new(PlannerSafety.Safe, RecoveryMotion.Permitted);
        await f.Recovery.ObserveWeatherAsync(default);
        for (var i = 1; i <= 6; i++)
        {
            f.Clock.Now = f.Start + (ulong)i * 1000;
            await f.Recovery.ObserveWeatherAsync(default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.ResumeWeatherAsync(default));
        Assert.IsType<RecoveryPhase.WeatherHolding>(f.Recovery.Record!.Snapshot.Phase);
        f.Conditions = new(PlannerSafety.Safe, RecoveryMotion.Unknown);
        f.Clock.Now = f.End;
        await f.Recovery.EndNightAsync(default, motionBlocked: true);
        var stopped = Assert.IsType<RecoveryPhase.Stopped>(f.Recovery.Record!.Snapshot.Phase);
        Assert.IsType<RecoveryCause.NightEnded>(stopped.Cause);
        Assert.Equal(RecoveryShutdown.MotionBlocked, stopped.Shutdown);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownFailureRetriesWithinDurableSessionBudget(bool exhausted)
    {
        await using var f = await Fixture.Create();
        var count = 0;
        Task Execute(CancellationToken ct) => ++count == 1 || exhausted
            ? Task.FromException(new SequenceEntityFailedException("known failure")) : Task.CompletedTask;
        var task = f.Recovery.ExecuteAsync(RecoveryOperation.Focus, "focuser", "target", Execute, _ => Task.CompletedTask, default);
        if (exhausted) await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        else await task;
        Assert.Equal(2, count);
        Assert.Equal(1U, f.Recovery.Record!.Snapshot.ProbesSpent);
        Assert.Equal(exhausted, f.Recovery.Record.Snapshot.Phase is RecoveryPhase.Stopping);
        if (!exhausted)
        {
            // A different target cannot buy another attempt after the shared
            // night budget is spent.
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.ExecuteAsync(RecoveryOperation.Guide,
                "guider", "other", _ => Task.FromException(new SequenceEntityFailedException("failed")), _ => Task.CompletedTask, default));
            Assert.Equal(1U, f.Recovery.Record.Snapshot.ProbesSpent);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("settle")]
    public async Task UnknownOrUnsettledFailureNeverRetries(string kind)
    {
        await using var f = await Fixture.Create();
        var count = 0;
        using var cancel = new CancellationTokenSource();
        Task Execute(CancellationToken ct)
        {
            count++;
            if (kind == "cancel") { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
            throw kind switch
            {
                "unknown" => new IOException("lost device"),
                "timeout" => new TimeoutException(),
                _ => new SequenceEntityFailedException("failed")
            };
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Recovery.ExecuteAsync(RecoveryOperation.Focus, "focuser", "target", Execute,
            _ => Task.FromException(new InvalidOperationException("still moving")), cancel.Token));
        Assert.Equal(1, count);
        Assert.Equal(0U, f.Recovery.Record!.Snapshot.ProbesSpent);
    }

    [Fact]
    public async Task SafetyChangeDuringCooldownNeverRunsRetry()
    {
        await using var f = await Fixture.Create();
        var count = 0;
        f.Report = _ => f.Conditions = new(PlannerSafety.Unsafe, RecoveryMotion.Permitted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.ExecuteAsync(RecoveryOperation.Guide, "guider", "target",
            _ => { count++; throw new SequenceEntityFailedException("failed"); }, _ => Task.CompletedTask, default));
        Assert.Equal(1, count);
        Assert.IsType<RecoveryPhase.Stopping>(f.Recovery.Record!.Snapshot.Phase);
    }

    [Fact]
    public async Task ReopeningNightDoesNotResetBudgetsAndNewRunCannotClearStop()
    {
        await using var f = await Fixture.Create();
        await f.Recovery.StopAsync(default);
        var permit = await f.Recovery.BeginParkAsync(default);
        Assert.NotNull(permit);
        await f.Recovery.FinishParkAsync(permit, RecoveryParkResult.Parked, default);
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.ExecuteAsync(RecoveryOperation.Focus, "focuser", "target",
            _ => { invoked = true; return Task.CompletedTask; }, _ => Task.CompletedTask, default));
        Assert.False(invoked);
        await f.Runtime.StopAsync();
        await f.Runtime.StartAsync("rig-test");
        var next = f.NewRecovery();
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.AdmitAsync("rig-test", "config", "new-night", f.Start, f.End, default));
        var current = (await f.Runtime.ReadRecoveryAsync()).Value!.Record!;
        Assert.Equal("night", current.Snapshot.Identity.NightId);
        Assert.Equal(RecoveryShutdown.Parked, Assert.IsType<RecoveryPhase.Stopped>(current.Snapshot.Phase).Shutdown);
    }

    [Fact]
    public void RecoverySettingsAreOptInAndValidated()
    {
        var options = new DirectorSessionOptions();
        Assert.False(options.RetryFocusAndGuiding);
        Assert.Empty(options.ValidateSettings());
        options.RetryCooldownSeconds = 600;
        Assert.NotEmpty(options.ValidateSettings());
        options.RetryCooldownSeconds = 1;
        options.MaximumRecoveryAttempts = 0;
        Assert.NotEmpty(options.ValidateSettings());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NightEndIsDistinctFromUnsafeOrOperatorStop(bool unsafeWeather)
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.EndNightAsync(default));
        if (unsafeWeather) f.Conditions = new(PlannerSafety.Unsafe, RecoveryMotion.Permitted);
        f.Clock.Now = f.End;
        if (unsafeWeather)
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.EndNightAsync(default));
        else
        {
            Assert.False(await f.Recovery.RefreshAsync(default, allowNightEnd: true));
            await f.Recovery.EndNightAsync(default);
            Assert.IsType<RecoveryCause.NightEnded>(Assert.IsType<RecoveryPhase.Stopping>(f.Recovery.Record!.Snapshot.Phase).Cause);
            var permit = await f.Recovery.BeginParkAsync(default);
            await f.Recovery.FinishParkAsync(permit!, RecoveryParkResult.Parked, default);
            await f.Recovery.EndNightAsync(default);
        }
    }

    [Fact]
    public async Task NightEndCannotConcealAnEarlierStopOrFailedPark()
    {
        await using var f = await Fixture.Create();
        await f.Recovery.StopAsync(default);
        f.Clock.Now = f.End;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Recovery.EndNightAsync(default));
        await using var g = await Fixture.Create();
        g.Clock.Now = g.End;
        await g.Recovery.EndNightAsync(default);
        var permit = await g.Recovery.BeginParkAsync(default);
        await g.Recovery.FinishParkAsync(permit!, RecoveryParkResult.Failed, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => g.Recovery.EndNightAsync(default));
    }

    [Fact]
    public async Task UnallocatedNightDoesNotAcknowledgeAnOlderNight()
    {
        await using var f = await Fixture.Create();
        await f.Recovery.StopAsync(default);
        var old = f.Recovery.Record!;
        f.Clock.Now = f.End;
        var next = f.NewRecovery();
        await next.EndNightAsync(default, "unallocated-night");
        Assert.Null(await next.BeginParkAsync(default));
        var current = (await f.Runtime.ReadRecoveryAsync()).Value!.Record!;
        Assert.Equal(old.Revision, current.Revision);
        Assert.Equal("night", current.Snapshot.Identity.NightId);
    }

    [Fact]
    public void SavedAndClonedRecoverySettingsRetainLocalPolicy()
    {
        var options = new DirectorSessionOptions
        {
            RetryFocusAndGuiding = true,
            RetryCooldownSeconds = 12,
            MaximumRecoveryMinutes = 4,
            MaximumRecoveryAttempts = 2,
            OnAbort = DirectorAbortPolicy.StopMount
        };
        var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<DirectorSessionOptions>(Newtonsoft.Json.JsonConvert.SerializeObject(options))!;
        var cloned = loaded.Clone();
        Assert.True(cloned.RetryFocusAndGuiding);
        Assert.Equal(12, cloned.RetryCooldownSeconds);
        Assert.Equal(4, cloned.MaximumRecoveryMinutes);
        Assert.Equal(2, cloned.MaximumRecoveryAttempts);
        Assert.Equal(DirectorAbortPolicy.StopMount, cloned.OnAbort);
        Assert.Empty(cloned.ValidateSettings());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "director-retry-" + Guid.NewGuid().ToString("N"));
        internal RuntimeController Runtime = null!;
        internal NinaSessionRecovery Recovery = null!;
        internal RecoveryConditions Conditions = new(PlannerSafety.Safe, RecoveryMotion.Permitted);
        internal Action<string>? Report;
        internal ulong Start = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        internal ulong End => Start + 3600000;
        internal readonly Clock Clock = new();
        internal bool Weather;
        internal NinaSessionRecovery NewRecovery() => new(Runtime, new()
        {
            RetryFocusAndGuiding = true,
            RetryCooldownSeconds = 1,
            MaximumRecoveryAttempts = 1,
            Weather = Weather ? DirectorWeatherPolicy.HoldAndResume : DirectorWeatherPolicy.StopForNight,
            StableSafeSeconds = 5
        },
            () => Conditions, () => { }, message => Report?.Invoke(message), Clock);
        internal static async Task<Fixture> Create(bool weather = false, bool unsafeStartup = false, bool roof = false)
        {
            var f = new Fixture { Weather = weather };
            if (unsafeStartup) f.Conditions = roof ? new(PlannerSafety.Safe, RecoveryMotion.Prohibited) : new(PlannerSafety.Unsafe, RecoveryMotion.Permitted);
            Directory.CreateDirectory(Path.Combine(f.root, "ledger"));
            Directory.CreateDirectory(Path.Combine(f.root, "recovery"));
            f.Runtime = new(ProcessTests.BundleDirectory, Path.Combine(f.root, "ledger"), Path.Combine(f.root, "recovery"));
            await f.Runtime.StartAsync("rig-test");
            f.Recovery = f.NewRecovery();
            await f.Recovery.AdmitAsync("rig-test", "config", "night", f.Start, f.End, default);
            return f;
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); Directory.Delete(root, true); }
    }

    private sealed class Clock : TimeProvider
    {
        internal ulong? Now;
        public override DateTimeOffset GetUtcNow() => Now is { } now ? DateTimeOffset.FromUnixTimeMilliseconds((long)now) : DateTimeOffset.UtcNow;
    }
}
