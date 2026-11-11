using System.Collections.Concurrent;
using Moq;
using NINA.Astrometry;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DirectorSchedulerEventsTests
{
    private static readonly DirectorTarget Target = new("m31", "M31", 41_000_000, -7_200_000, 45 * 3_600_000);
    private static readonly ExposureRecipe Recipe = new("ha", 120_500, "filter-1", new(2, 2), 100, 30, 0, null);

    [Fact]
    public void CompatibilityIsOffByDefaultAndStoredPerProfile()
    {
        var profile = new Mock<IProfile>();
        profile.SetupGet(p => p.PluginSettings).Returns(new NINA.Profile.PluginSettings());
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        var plugin = new DirectorPlugin(profiles.Object);
        Assert.False(plugin.EmitTargetSchedulerEvents);
        plugin.EmitTargetSchedulerEvents = true;
        Assert.True(new DirectorPlugin(profiles.Object).EmitTargetSchedulerEvents);
        var other = new Mock<IProfile>();
        other.SetupGet(p => p.PluginSettings).Returns(new NINA.Profile.PluginSettings());
        profiles.SetupGet(p => p.ActiveProfile).Returns(other.Object);
        Assert.False(plugin.EmitTargetSchedulerEvents);
        profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        plugin.EmitTargetSchedulerEvents = false;
        Assert.False(new DirectorPlugin(profiles.Object).EmitTargetSchedulerEvents);
    }

    [Fact]
    public async Task PublishesTsPayloadsWithDirectorIdentityAndOrderedVisitBoundaries()
    {
        var messages = new ConcurrentQueue<IMessage>();
        var clock = new Clock();
        var end = clock.GetUtcNow().AddHours(1);
        var broker = Broker(m => { messages.Enqueue(m); return Task.CompletedTask; });
        await using (var events = new DirectorSchedulerEvents(broker, () => true, clock))
        {
            await events.TargetStartingAsync(Target, Recipe, "Andromeda", "Ha", end);
            await events.TargetStartingAsync(Target, Recipe with { Gain = null, Offset = null }, "Andromeda", "Ha", end);
            await events.WaitingAsync("Waiting for Safe", TimeSpan.FromSeconds(30));
            clock.Advance(1);
            await events.WaitingAsync("Waiting for Safe", TimeSpan.FromSeconds(30));
            clock.Advance(30);
            await events.WaitingAsync("Waiting for Safe", TimeSpan.FromSeconds(30));
            await events.TargetStartingAsync(Target, Recipe, "Andromeda", "Ha", end);
            await events.TargetStartingAsync(Target with { Id = "m32", Name = "M32", PositionAngleMas = null }, Recipe, "M32 project", "Ha", end);
        }
        var all = messages.ToArray();
        Assert.Equal(new[] { "NewTargetStart", "TargetStart", "TargetStart", "WaitStart", "WaitStart", "NewTargetStart", "TargetStart", "NewTargetStart", "TargetStart", "ContainerStopped" },
            all.Select(m => m.Topic.Replace("TargetScheduler-", "")));
        Assert.All(all, m => { Assert.Equal("PSF Guard Director", m.Sender); Assert.Equal(DirectorSchedulerEvents.SenderId, m.SenderId); });
        Assert.Equal(all.Length, all.Select(m => m.MessageId).Distinct().Count());
        Assert.Single(all.Select(m => m.CorrelationId).Distinct());
        var start = all[1];
        Assert.Equal(2, start.Version);
        Assert.Equal("M31", start.Content);
        Assert.Equal(end, start.Expiration);
        Assert.Equal("Andromeda", start.CustomHeaders["ProjectName"]);
        Assert.Equal("M31", start.CustomHeaders["TargetName"]);
        var coordinates = Assert.IsType<Coordinates>(start.CustomHeaders["Coordinates"]);
        Assert.Equal(Epoch.J2000, coordinates.Epoch);
        Assert.Equal(Target.IcrsRaMas / 3_600_000d / 15, coordinates.RA);
        Assert.Equal(-2, coordinates.Dec);
        Assert.Equal(45d, start.CustomHeaders["Rotation"]);
        Assert.Equal("Ha", start.CustomHeaders["ExposureFilterName"]);
        Assert.Equal(120.5, start.CustomHeaders["ExposureLength"]);
        Assert.Equal("100", start.CustomHeaders["ExposureGain"]);
        Assert.Equal("30", start.CustomHeaders["ExposureOffset"]);
        Assert.Equal("2x2", start.CustomHeaders["ExposureBinning"]);
        Assert.Equal("(camera)", all[2].CustomHeaders["ExposureGain"]);
        Assert.Equal("(camera)", all[2].CustomHeaders["ExposureOffset"]);
        Assert.True(double.IsNaN(Assert.IsType<double>(all[8].CustomHeaders["Rotation"])));
        var wait = all[3];
        Assert.Equal(wait.SentAt.AddSeconds(30).LocalDateTime, Assert.IsType<DateTime>(wait.Content));
        Assert.Equal(wait.SentAt.AddSeconds(30), wait.Expiration);
        Assert.Equal(30, wait.CustomHeaders["SecondsUntilNextTarget"]);
        Assert.Equal(true, wait.CustomHeaders["WaitEndTimeIsEstimate"]);
        Assert.Equal("Waiting for Safe", wait.CustomHeaders["Reason"]);
        Assert.Equal("", wait.CustomHeaders["TargetName"]);
        Assert.False(wait.CustomHeaders.ContainsKey("Coordinates"));
        Assert.Equal(1, all[^1].Version);
        Assert.Equal(all[^1].SentAt.LocalDateTime, all[^1].CustomHeaders["StoppedAt"]);
    }

    [Fact]
    public async Task OffSuppressesAllMessagesAndCanBeChangedDuringSession()
    {
        var messages = new ConcurrentQueue<IMessage>();
        var enabled = false;
        var events = new DirectorSchedulerEvents(Broker(m => { messages.Enqueue(m); return Task.CompletedTask; }), () => enabled);
        await events.TargetStartingAsync(Target, Recipe, "M31", "Ha", DateTimeOffset.UtcNow.AddHours(1));
        await events.WaitingAsync("No work", TimeSpan.FromSeconds(30));
        Assert.Empty(messages);
        enabled = true;
        await events.WaitingAsync("No work", TimeSpan.FromSeconds(30));
        Assert.Single(messages);
        enabled = false;
        await events.WaitingAsync("No work", TimeSpan.FromSeconds(30));
        await events.TargetStartingAsync(Target, Recipe, "M31", "Ha", DateTimeOffset.UtcNow.AddHours(1));
        await events.DisposeAsync();
        Assert.Single(messages);
    }

    [Fact]
    public async Task MissingBrokerIsSupportedWithoutTsInstalled()
    {
        await using var events = new DirectorSchedulerEvents(null, () => true);
        await events.TargetStartingAsync(Target, Recipe, "M31", "Ha", DateTimeOffset.UtcNow);
        await events.WaitingAsync("No work", TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrStuckSubscriberCannotFailOrKeepDelayingAcquisition(bool stuck)
    {
        var calls = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = Broker(_ => { Interlocked.Increment(ref calls); return stuck ? blocked.Task : throw new InvalidOperationException("consumer failed"); });
        try
        {
            await using var events = new DirectorSchedulerEvents(broker, () => true, deliveryTimeout: TimeSpan.FromMilliseconds(50));
            await events.TargetStartingAsync(Target, Recipe, "M31", "Ha", DateTimeOffset.UtcNow).WaitAsync(TimeSpan.FromSeconds(5));
            await events.WaitingAsync("No work", TimeSpan.FromSeconds(30));
            Assert.Equal(1, calls);
        }
        finally { blocked.TrySetResult(); }
    }

    private static IMessageBroker Broker(Func<IMessage, Task> publish)
    {
        var broker = new Mock<IMessageBroker>();
        broker.Setup(b => b.Publish(It.IsAny<IMessage>())).Returns(publish);
        return broker.Object;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 10, 23, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
