using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows;
using System.Windows.Input;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class DirectorPlugin : PluginBase, INotifyPropertyChanged
{
    private static readonly Guid PluginId = new("03a1d13e-67eb-4e24-a407-82bce7e576a5");
    private readonly IProfileService profiles;
    private readonly PluginOptionsAccessor settings;
    private readonly RuntimeController runtime;
    private readonly AsyncCommand start;
    private readonly AsyncCommand stop;
    private readonly AsyncCommand checkIn;
    private readonly AsyncCommand cancelCheckIn;
    private CancellationTokenSource? checkInCancellation;
    private Task<CoordinatorRunCheckInProgress>? checkInTask;
    private string checkInStatus = "No check-in this session";
    private bool initialized;
    private int profileTransitions;
    private long profileGeneration;
    private string? commandError;
    internal DirectorConnection ConnectionModel { get; }
    public object Connection => ConnectionModel;

    [ImportingConstructor]
    public DirectorPlugin(IProfileService profileService)
    {
        profiles = profileService;
        settings = new PluginOptionsAccessor(profiles, PluginId);
        runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!);
        ConnectionModel = new(() => profiles.ActiveProfile.Id,
            () => initialized && checkInCancellation is null && !AcquisitionLease.IsActive && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            () => settings.GetValueString("CoordinatorUrl", ""), value => settings.SetValueString("CoordinatorUrl", value),
            readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""),
            writeHttpConsent: value => settings.SetValueString("HttpConsentOrigin", value));
        start = new AsyncCommand(StartRuntimeAsync,
            () => initialized && checkInCancellation is null && !AcquisitionLease.IsActive && !ConnectionModel.IsBusy && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            ReportError);
        stop = new AsyncCommand(runtime.StopAsync,
            () => initialized && runtime.Status.State is RuntimeState.Starting or RuntimeState.Ready or RuntimeState.Faulted,
            ReportError);
        checkIn = new(CheckInAsync,
            () => initialized && checkInCancellation is null && !AcquisitionLease.IsActive && !ConnectionModel.IsBusy
                && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            error => { Logger.Error(error); checkInStatus = "Check-in failed; saved data retained. See the NINA log."; Refresh(); });
        cancelCheckIn = new(() => { checkInCancellation?.Cancel(); return Task.CompletedTask; },
            () => checkInCancellation is not null, ReportError);
        ConnectionModel.PropertyChanged += (_, _) => { start.Refresh(); checkIn.Refresh(); };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand StartRuntimeCommand => start;
    public ICommand StopRuntimeCommand => stop;
    public ICommand CheckInCommand => checkIn;
    public ICommand CancelCheckInCommand => cancelCheckIn;
    public string CheckInStatus => checkInStatus;
    public string RuntimeStatus => commandError ?? runtime.Status.Message;
    public string ProfileName => profiles.ActiveProfile.Name;
    public string EngineVersion => RuntimeContract.EngineVersion;
    public string AcquisitionStatus => "Not armed";

    public override async Task Initialize()
    {
        await base.Initialize();
        profiles.ProfileChanged += ProfileChanged;
        runtime.StateChanged += RuntimeStateChanged;
        initialized = true;
        ConnectionModel.Reload();
        Refresh();
    }

    public override async Task Teardown()
    {
        initialized = false;
        checkInCancellation?.Cancel();
        if (checkInTask is not null) { try { await checkInTask; } catch (Exception error) { Logger.Error(error); } }
        ConnectionModel.Dispose();
        profiles.ProfileChanged -= ProfileChanged;
        runtime.StateChanged -= RuntimeStateChanged;
        try { await runtime.DisposeAsync(); }
        catch (Exception error) { Logger.Error(error); }
        finally { await base.Teardown(); }
    }

    private async Task StartRuntimeAsync()
    {
        commandError = null;
        var generation = Interlocked.Read(ref profileGeneration);
        var pairing = ConnectionModel.ReadPairing();
        if (pairing is null && !string.IsNullOrWhiteSpace(ConnectionModel.ServerUrl))
            throw new InvalidOperationException("Pair the configured coordinator before starting the runtime.");
        var rigId = pairing?.Binding.RigId.ToString("D") ?? settings.GetValueString("RigId", "");
        if (!Guid.TryParse(rigId, out _))
        {
            rigId = Guid.NewGuid().ToString("D");
            settings.SetValueString("RigId", rigId);
        }
        await runtime.StartAsync(rigId);
        if (generation != Interlocked.Read(ref profileGeneration) || !initialized) await runtime.StopAsync();
        Refresh();
    }

    private async Task CheckInAsync()
    {
        using var cancellation = new CancellationTokenSource();
        checkInCancellation = cancellation;
        checkInStatus = "Reading saved runs";
        Refresh();
        var updates = new InlineProgress<CoordinatorRunCheckInProgress>(p =>
        {
            if (!ReferenceEquals(checkInCancellation, cancellation)) return;
            checkInStatus = $"{p.Runs} runs; {p.DeliveredEvents} events delivered; cursor {p.AcknowledgedThrough}";
            Refresh();
        });
        try
        {
            checkInTask = new DirectorCheckInService(profiles).RunAsync(updates, cancellation.Token);
            var result = await checkInTask;
            checkInStatus = $"Checked in {result.Runs} runs; {result.DeliveredEvents} events; {result.ReleasedWorkloads} workloads released";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { checkInStatus = "Check-in cancelled; acknowledged progress saved"; }
        finally { checkInCancellation = null; checkInTask = null; Refresh(); }
    }

    private async void ProfileChanged(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref profileGeneration);
        checkInCancellation?.Cancel();
        Interlocked.Increment(ref profileTransitions);
        ConnectionModel.ProfileChanged();
        commandError = null;
        Refresh();
        try { await runtime.StopAsync(); }
        catch (Exception error) { ReportError(error); }
        finally { Interlocked.Decrement(ref profileTransitions); Refresh(); }
    }

    private void RuntimeStateChanged(object? sender, EventArgs args)
    {
        var status = runtime.Status;
        Logger.Info($"PSF Guard Director runtime: {status.State}");
        if (status.Error is not null) Logger.Error(status.Error);
        Refresh();
    }

    private void ReportError(Exception error)
    {
        Logger.Error(error);
        commandError = "Director operation failed. See the NINA log.";
        Refresh();
    }

    private void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Refresh);
            return;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RuntimeStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckInStatus)));
        start.Refresh();
        stop.Refresh();
        checkIn.Refresh();
        cancelCheckIn.Refresh();
        ConnectionModel.Changed();
    }
}
