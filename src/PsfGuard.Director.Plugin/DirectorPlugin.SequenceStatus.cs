using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NINA.Core.Utility;
using NINA.Sequencer.Interfaces.Mediator;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

public sealed partial class DirectorPlugin
{
    private readonly ISequenceMediator? sequence;
    private CancellationTokenSource? sequenceReporting;
    private Task? sequenceReportingTask;
    private DispatcherTimer? localStatusTimer;
    private static readonly TimeSpan SequenceHeartbeat = TimeSpan.FromSeconds(15);
    private const string SequenceActivityKey = "ReportSequenceActivity";
    public DirectorSessionDisplay Overview { get; private set; } = DirectorSessionDisplay.Empty with
    { Phase = "No sequence running", Operation = "Start a sequence containing Director Session to begin imaging." };

    private void UpdateOverview(NinaSequenceActivity? activity)
    {
        var session = DirectorSessionObservation.Read(profiles.ActiveProfile.Id);
        var next = session is not null && (session.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING || activity?.Running != true)
            ? session.CurrentDisplay with { Phase = session.IsTriggerRunning ? session.CurrentDisplay.Phase : session.SessionStatus }
            : DirectorSessionDisplay.Empty with
            {
                Phase = activity?.Running == true ? "N.I.N.A. sequence running" : "No sequence running",
                Operation = activity?.Running == true ? activity.Operation ?? "Waiting for the next instruction"
                    : "Start a sequence containing Director Session to begin imaging."
            };
        if (next == Overview) return;
        Overview = next;
        PropertyChanged?.Invoke(this, new(nameof(Overview)));
        PropertyChanged?.Invoke(this, new(nameof(AcquisitionStatus)));
    }

    public bool ReportSequenceActivity
    {
        get => settings.GetValueBoolean(SequenceActivityKey, true);
        set
        {
            if (value == ReportSequenceActivity) return;
            settings.SetValueBoolean(SequenceActivityKey, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReportSequenceActivity)));
        }
    }

    private sealed record SequenceDestination(Uri Endpoint, Guid Profile, long Generation, bool AllowInsecureHttp);

    private SequenceDestination? SequenceReportDestination()
    {
        if (!initialized || AcquisitionLease.IsActive || !ReportSequenceActivity || ConnectionModel.ResolvedEndpoint is not { } endpoint)
            return null;
        return new(endpoint, profiles.ActiveProfile.Id, Interlocked.Read(ref profileGeneration), ConnectionModel.AllowInsecureHttp);
    }

    private void StartSequenceReporting()
    {
        if (sequence is null) return;
        // A slow server request must not freeze the local status page.
        if (Application.Current?.Dispatcher is { } dispatcher)
            localStatusTimer = dispatcher.Invoke(() =>
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += LocalStatusTick;
                timer.Start();
                LocalStatusTick(null, EventArgs.Empty);
                return timer;
            });
        sequenceReporting = new();
        sequenceReportingTask = ReportSequenceAsync(sequence, sequenceReporting.Token);
    }

    private async Task StopSequenceReportingAsync()
    {
        if (localStatusTimer is { } timer)
        {
            timer.Dispatcher.Invoke(() => { timer.Stop(); timer.Tick -= LocalStatusTick; });
            localStatusTimer = null;
        }
        if (sequenceReporting is not { } cancellation) return;
        sequenceReporting = null;
        using (cancellation)
        {
            cancellation.Cancel();
            if (sequenceReportingTask is not null) await sequenceReportingTask;
        }
        sequenceReportingTask = null;
    }

    private void LocalStatusTick(object? sender, EventArgs e)
    {
        if (!initialized || sequence is null) return;
        UpdateOverview(NinaSequenceActivity.Read(sequence));
    }

    private async Task ReportSequenceAsync(ISequenceMediator mediator, CancellationToken token)
    {
        var dispatcher = Application.Current?.Dispatcher;
        SequenceDestination? destination = null;
        NinaSequenceActivity? sent = null;
        Guid? client = null;
        string? session = null;
        long? failedAt = null;
        long sentAt = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    // Timestamp the observation before checking Director ownership. A delayed
                    // request must not replace a newer snapshot from the acquisition owner.
                    var observedAt = DateTimeOffset.UtcNow;
                    (SequenceDestination? Destination, NinaSequenceActivity? Activity) Capture()
                    {
                        // Local status must work while Director owns acquisition,
                        // when unpaired, and when server reporting is switched off.
                        var activity = NinaSequenceActivity.Read(mediator);
                        var target = SequenceReportDestination();
                        return (target, activity);
                    }
                    var (current, activity) = dispatcher is null ? Capture()
                        : await dispatcher.InvokeAsync(Capture, DispatcherPriority.Background, token).Task.ConfigureAwait(false);
                    if (current != destination || activity is null)
                    {
                        destination = current;
                        sent = null;
                        client = null;
                        session = null;
                        failedAt = null;
                    }
                    if (current is null || activity is null || !activity.Running && session is null) continue;
                    if (failedAt is { } failed && Stopwatch.GetElapsedTime(failed) < SequenceHeartbeat) continue;
                    if (activity == sent && Stopwatch.GetElapsedTime(sentAt) < SequenceHeartbeat) continue;

                    var pairing = DirectorCredentialStore.Read(current.Endpoint, current.Profile);
                    if (pairing is null)
                    {
                        session = null;
                        sent = null;
                        failedAt = Stopwatch.GetTimestamp();
                        continue;
                    }
                    if (client != pairing.ClientId)
                    {
                        client = pairing.ClientId;
                        session = null;
                    }
                    if (!activity.Running && session is null) continue;
                    session ??= Guid.NewGuid().ToString("D");
                    ValueTask<string?> Credential(CancellationToken ct)
                    {
                        ct.ThrowIfCancellationRequested();
                        var active = DirectorCredentialStore.Read(current.Endpoint, current.Profile);
                        if (SequenceReportDestination() != current || active?.ClientId != pairing.ClientId || active.Binding != pairing.Binding)
                            throw new InvalidOperationException("Director sequence reporting context changed.");
                        return ValueTask.FromResult<string?>(active.Token);
                    }
                    using var reporter = new CoordinatorSessionReporter(current.Endpoint, pairing.Binding, Credential, current.AllowInsecureHttp);
                    await reporter.ReportSequenceAsync(session, activity.Running, activity.Operation, observedAt, token).ConfigureAwait(false);
                    sent = activity;
                    sentAt = Stopwatch.GetTimestamp();
                    failedAt = null;
                    if (!activity.Running) session = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (failedAt is null)
                        Logger.Warning($"Director sequence activity could not be reported ({(error is CoordinatorIntakeException intake ? intake.Failure.ToString() : error.GetType().Name)}).");
                    failedAt = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
