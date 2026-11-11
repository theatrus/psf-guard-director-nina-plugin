using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using NINA.Astrometry;
using NINA.Plugin.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.SimulatorProbe;

// Uses the host's actual broker and a normal public subscriber, with no TS DLL.
internal sealed class SchedulerEventsProbe : ISubscriber, IDisposable
{
    private static readonly string[] Topics = ["TargetScheduler-NewTargetStart", "TargetScheduler-TargetStart",
        "TargetScheduler-WaitStart", "TargetScheduler-ContainerStopped", "TargetScheduler-TargetComplete"];
    private readonly IMessageBroker broker;
    private readonly ConcurrentQueue<IMessage> messages = new();

    internal SchedulerEventsProbe(IMessageBroker broker)
    {
        this.broker = broker;
        foreach (var topic in Topics) broker.Subscribe(topic, this);
    }

    public Task OnMessageReceived(IMessage message)
    {
        if (message.SenderId == DirectorSchedulerEvents.SenderId) messages.Enqueue(message);
        return Task.CompletedTask;
    }

    internal async Task VerifyAsync(string run, bool enabled, bool requireWait, DirectorProgram program)
    {
        var observed = messages.ToArray();
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name?.StartsWith("NINA.Plugin.TargetScheduler", StringComparison.Ordinal) == true))
            throw new InvalidDataException("Compatibility fixture must run without Target Scheduler.");
        if (!enabled && observed.Length != 0) throw new InvalidDataException("Disabled compatibility published messages.");
        if (enabled)
        {
            if (!observed.Any(m => m.Topic == Topics[0]) || !observed.Any(m => m.Topic == Topics[1])
                || observed.LastOrDefault()?.Topic != Topics[3] || requireWait && !observed.Any(m => m.Topic == Topics[2]))
                throw new InvalidDataException("Native session did not publish target/wait/stop compatibility events.");
            foreach (var message in observed)
            {
                if (message.Sender != "PSF Guard Director" || message.Topic == Topics[4])
                    throw new InvalidDataException("Compatibility lost Director identity or claimed project completion.");
                if (message.Topic == Topics[2])
                {
                    if (message.Content is not DateTime until || message.Expiration?.LocalDateTime != until
                        || !Equals(message.CustomHeaders["WaitEndTimeIsEstimate"], true))
                        throw new InvalidDataException("Wait message has the wrong TS payload.");
                }
                if (message.Topic == Topics[0] || message.Topic == Topics[1])
                {
                    var target = program.Targets.Single(t => t.Name == (string)message.Content);
                    if (message.Version != 2 || message.Expiration is null
                        || message.CustomHeaders["Coordinates"] is not Coordinates coords || coords.Epoch != Epoch.J2000
                        || coords.RA != target.IcrsRaMas / 3600000d / 15 || coords.Dec != target.IcrsDecMas / 3600000d
                        || string.IsNullOrWhiteSpace((string)message.CustomHeaders["ProjectName"])
                        || string.IsNullOrWhiteSpace((string)message.CustomHeaders["ExposureFilterName"])
                        || !program.Recipes.Any(r => r.ExposureMs / 1000d == (double)message.CustomHeaders["ExposureLength"]))
                        throw new InvalidDataException("Target message does not match the issued program.");
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(run, "scheduler-events-verified.json"), JsonSerializer.Serialize(new
        {
            enabled,
            target_scheduler_loaded = false,
            events = observed.Select(m => new
            {
                m.Topic,
                m.Sender,
                m.SenderId,
                m.Version,
                m.SentAt,
                m.Expiration,
                content = m.Content.ToString(),
                headers = m.CustomHeaders.Where(h => h.Key != "Coordinates").ToDictionary(h => h.Key, h => h.Value.ToString())
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose() { foreach (var topic in Topics) broker.Unsubscribe(topic, this); }
}
