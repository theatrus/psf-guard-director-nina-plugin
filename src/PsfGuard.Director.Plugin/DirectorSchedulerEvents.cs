using System.Globalization;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

// Public NINA message contracts only: no TS assembly, container, or scheduling
// dependency. These messages describe activity; they never authorize equipment.
internal sealed class DirectorSchedulerEvents(IMessageBroker? broker, Func<bool> enabled,
    TimeProvider? timeProvider = null, TimeSpan? deliveryTimeout = null) : IAsyncDisposable
{
    internal const string SettingKey = "EmitTargetSchedulerEvents";
    internal static readonly Guid SenderId = new("03a1d13e-67eb-4e24-a407-82bce7e576a5");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan timeout = deliveryTimeout ?? TimeSpan.FromSeconds(2);
    private readonly Guid correlation = Guid.NewGuid();
    private string? targetId;
    private string? waitReason;
    private DateTimeOffset waitUntil;
    private bool published;
    private bool failed;

    private bool IsEnabled()
    {
        if (broker is not null && !failed && enabled()) return true;
        targetId = null;
        waitReason = null;
        return false;
    }

    internal async Task TargetStartingAsync(DirectorTarget target, ExposureRecipe recipe, string projectName,
        string filterName, DateTimeOffset end)
    {
        if (!IsEnabled()) return;
        // TS publishes NewTargetStart once per visit, then TargetStart for each
        // exposure plan (including its target preparation and native triggers).
        if (targetId != target.Id || waitReason is not null)
            await PublishAsync(TargetMessage("NewTargetStart", target, recipe, projectName, filterName, end));
        targetId = target.Id;
        waitReason = null;
        await PublishAsync(TargetMessage("TargetStart", target, recipe, projectName, filterName, end));
    }

    internal async Task WaitingAsync(string reason, TimeSpan nextCheck)
    {
        if (!IsEnabled()) return;
        var now = clock.GetUtcNow();
        // A one-second safety poll must not send one notification per second.
        if (waitReason == reason && now < waitUntil) return;
        waitReason = reason;
        targetId = null;
        waitUntil = now + nextCheck;
        await PublishAsync(new Message("TargetScheduler-WaitStart", waitUntil.LocalDateTime, 2,
            now, correlation, waitUntil, new Dictionary<string, object>
            {
                ["SecondsUntilNextTarget"] = (int)Math.Clamp(Math.Ceiling(nextCheck.TotalSeconds), 0, int.MaxValue),
                ["ProjectName"] = "",
                ["TargetName"] = "",
                ["Reason"] = reason,
                ["WaitEndTimeIsEstimate"] = true
            }));
    }

    private Message TargetMessage(string topic, DirectorTarget target, ExposureRecipe recipe, string projectName,
        string filterName, DateTimeOffset end) => new($"TargetScheduler-{topic}", target.Name, 2,
            clock.GetUtcNow(), correlation, end, new Dictionary<string, object>
            {
                ["ProjectName"] = projectName,
                ["TargetName"] = target.Name,
                ["Coordinates"] = new Coordinates(target.IcrsRaMas / 3600000d, target.IcrsDecMas / 3600000d,
                    Epoch.J2000, Coordinates.RAType.Degrees),
                ["Rotation"] = target.PositionAngleMas is { } angle ? angle / 3600000d : double.NaN,
                ["ExposureFilterName"] = filterName,
                ["ExposureLength"] = recipe.ExposureMs / 1000d,
                ["ExposureGain"] = recipe.Gain?.ToString(CultureInfo.InvariantCulture) ?? "(camera)",
                ["ExposureOffset"] = recipe.Offset?.ToString(CultureInfo.InvariantCulture) ?? "(camera)",
                ["ExposureBinning"] = $"{recipe.Binning.X}x{recipe.Binning.Y}"
            });

    private async Task PublishAsync(IMessage message)
    {
        if (!IsEnabled()) return;
        // Isolate synchronous as well as asynchronous subscriber failures. A
        // stuck integration is disabled for this session, never retried on each
        // exposure. Observe a late failure even if the time limit has elapsed.
        var delivery = Task.Run(async () =>
        {
            try { await broker!.Publish(message).ConfigureAwait(false); return true; }
            catch (Exception error) { Logger.Warning($"Director compatibility event failed ({error.GetType().Name})."); return false; }
        });
        try { failed = !await delivery.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { failed = true; }
        if (failed) Logger.Warning("Director Target Scheduler compatibility events disabled for this session because a subscriber failed or timed out. Imaging continues.");
        else published = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!published || !IsEnabled()) return;
        var now = clock.GetUtcNow();
        await PublishAsync(new Message("TargetScheduler-ContainerStopped", "Container Stopped", 1,
            now, correlation, null, new Dictionary<string, object> { ["StoppedAt"] = now.LocalDateTime }));
        published = false;
    }

    private sealed class Message(string topic, object content, int version, DateTimeOffset sentAt,
        Guid correlation, DateTimeOffset? expiration, IDictionary<string, object> headers) : IMessage
    {
        public string Topic { get; } = topic;
        public object Content { get; } = content;
        public string Sender => "PSF Guard Director";
        public Guid SenderId => DirectorSchedulerEvents.SenderId;
        public DateTimeOffset SentAt { get; } = sentAt;
        public Guid MessageId { get; } = Guid.NewGuid();
        public DateTimeOffset? Expiration { get; } = expiration;
        public int Version { get; } = version;
        public Guid? CorrelationId { get; } = correlation;
        public IDictionary<string, object> CustomHeaders { get; } = headers;
    }
}
