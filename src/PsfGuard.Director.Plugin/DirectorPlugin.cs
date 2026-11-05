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
    internal CollaborationConnection CollaborationModel { get; }
    public object Collaboration => CollaborationModel;
    private bool refreshingConnections;
    private readonly StarDetectionSettingsReporter starDetection;
    private readonly List<INotifyPropertyChanged> watchedStarDetectionSettings = [];
    private string? starDetectionConnection;
    private readonly DirectorWeatherService? weather;
    public object? Weather => weather;

    [ImportingConstructor]
    public DirectorPlugin(IProfileService profileService, [Import(AllowDefault = true)] DirectorWeatherService? weather = null)
    {
        this.weather = weather;
        profiles = profileService;
        settings = new PluginOptionsAccessor(profiles, PluginId);
        runtime = new RuntimeController(Path.GetDirectoryName(typeof(DirectorPlugin).Assembly.Location)!);
        CollaborationModel = new(() => profiles.ActiveProfile.Id,
            () => initialized && checkInCancellation is null && !AcquisitionLease.IsActive && !ConnectionModel!.IsBusy && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            () => settings.GetValueString("CollaborationBindings", ""), value => settings.SetValueString("CollaborationBindings", value));
        ConnectionModel = new(() => profiles.ActiveProfile.Id,
            () => initialized && checkInCancellation is null && !AcquisitionLease.IsActive && !CollaborationModel.IsBusy && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            () => settings.GetValueString("CoordinatorUrl", ""), value => settings.SetValueString("CoordinatorUrl", value),
            readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""),
            writeHttpConsent: value => settings.SetValueString("HttpConsentOrigin", value));
        start = new AsyncCommand(StartRuntimeAsync,
            () => initialized && checkInCancellation is null && !AcquisitionLease.IsActive && !ConnectionModel.IsBusy && !CollaborationModel.IsBusy && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            ReportError);
        stop = new AsyncCommand(runtime.StopAsync,
            () => initialized && runtime.Status.State is RuntimeState.Starting or RuntimeState.Ready or RuntimeState.Faulted,
            ReportError);
        checkIn = new(CheckInAsync,
            () => initialized && checkInCancellation is null && !AcquisitionLease.IsActive && !ConnectionModel.IsBusy && !CollaborationModel.IsBusy
                && Volatile.Read(ref profileTransitions) == 0 && runtime.Status.State is RuntimeState.Stopped or RuntimeState.Faulted,
            error => { Logger.Error(error); checkInStatus = "Check-in failed; saved data retained. See the NINA log."; Refresh(); });
        cancelCheckIn = new(() => { checkInCancellation?.Cancel(); return Task.CompletedTask; },
            () => checkInCancellation is not null, ReportError);
        ConnectionModel.PropertyChanged += (_, _) => ConnectionsChanged();
        CollaborationModel.PropertyChanged += (_, _) => ConnectionsChanged();
        starDetection = new(CaptureStarDetectionSettings, SendStarDetectionSettingsAsync,
            CoordinatorStarDetectionClient.IsMissingRoute, CoordinatorStarDetectionClient.IsRefused,
            message => Logger.Info($"PSF Guard Director: {message}"));
    }

    private void ConnectionsChanged()
    {
        if (refreshingConnections) return;
        refreshingConnections = true;
        try
        {
            start.Refresh(); checkIn.Refresh();
            ConnectionModel.Changed(); CollaborationModel.Changed();
            // Pairing, reset or a new server URL can change where the settings go.
            var connection = ConnectionModel.ServerUrl + "\n" + ConnectionModel.PairingStatus;
            if (initialized && connection != starDetectionConnection)
            {
                starDetectionConnection = connection;
                starDetection.RequestSend();
                weather?.RequestRefresh();
            }
        }
        finally { refreshingConnections = false; }
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

    // Per profile, on by default, and separate from pairing and every other
    // option. Turning it back on sends the current settings once.
    internal const string SendStarDetectionSettingsKey = "SendStarDetectionSettings";
    public bool SendWeatherTelemetry
    {
        get => settings.GetValueBoolean("SendWeatherTelemetry", true);
        set
        {
            settings.SetValueBoolean("SendWeatherTelemetry", value);
            weather?.RequestRefresh();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SendWeatherTelemetry)));
        }
    }
    public bool SendStarDetectionSettings
    {
        get => settings.GetValueBoolean(SendStarDetectionSettingsKey, true);
        set
        {
            if (value == SendStarDetectionSettings) return;
            settings.SetValueBoolean(SendStarDetectionSettingsKey, value);
            starDetection.Enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SendStarDetectionSettings)));
        }
    }

    public override async Task Initialize()
    {
        await base.Initialize();
        profiles.ProfileChanged += ProfileChanged;
        runtime.StateChanged += RuntimeStateChanged;
        initialized = true;
        ConnectionModel.Reload();
        CollaborationModel.Reload();
        WatchStarDetectionSettings(profiles.ActiveProfile);
        starDetection.Enabled = SendStarDetectionSettings;
        starDetection.Start();
        weather?.Start();
        Refresh();
    }

    public override async Task Teardown()
    {
        initialized = false;
        checkInCancellation?.Cancel();
        if (checkInTask is not null) { try { await checkInTask; } catch (Exception error) { Logger.Error(error); } }
        WatchStarDetectionSettings(null);
        try { await starDetection.DisposeAsync(); }
        catch (Exception error) { Logger.Error(error); }
        try { if (weather is not null) await weather.DisposeAsync(); }
        catch (Exception error) { Logger.Error(error); }
        ConnectionModel.Dispose();
        CollaborationModel.Dispose();
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
            checkInStatus = $"{p.Runs} runs; {p.DeliveredEvents} events delivered; capture {p.AcknowledgedThrough}; operations {p.OperationsAcknowledgedThrough}";
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
        CollaborationModel.ProfileChanged();
        WatchStarDetectionSettings(profiles.ActiveProfile);
        starDetection.Enabled = SendStarDetectionSettings;
        starDetection.RequestSend();
        commandError = null;
        Refresh();
        try { await runtime.StopAsync(); }
        catch (Exception error) { ReportError(error); }
        finally { Interlocked.Decrement(ref profileTransitions); Refresh(); }
    }

    private sealed record StarDetectionDestination(Uri Endpoint, CoordinatorBinding Binding, Guid ClientId, bool AllowInsecureHttp);

    // Null until this profile is paired with the configured server. The report
    // carries no credential; the token is read again when the request is sent.
    private StarDetectionSettingsCapture? CaptureStarDetectionSettings()
    {
        if (!initialized) return null;
        var profile = profiles.ActiveProfile;
        var endpoint = ConnectionModel.ResolvedEndpoint;
        if (endpoint is null) return null;
        var pairing = DirectorCredentialStore.Read(endpoint, profile.Id);
        if (pairing is null || pairing.Binding.ProfileId != profile.Id) return null;
        var report = StarDetectionProfileReader.Read(profile, StarDetectionProfileReader.NinaVersion(),
            StarDetectionProfileReader.HocusFocusVersion(), DateTimeOffset.UtcNow);
        if (profiles.ActiveProfile.Id != profile.Id)
            throw new InvalidOperationException("The active NINA profile changed while Director read its star-detection settings.");
        var binding = pairing.Binding;
        return new($"{endpoint.AbsoluteUri}\n{binding.CoordinatorInstanceId:D}\n{binding.CatalogId:D}\n{binding.RigId:D}\n{binding.ProfileId:D}\n{pairing.ClientId:D}",
            report, new StarDetectionDestination(endpoint, binding, pairing.ClientId, ConnectionModel.AllowInsecureHttp));
    }

    private static async Task SendStarDetectionSettingsAsync(StarDetectionSettingsCapture capture, CancellationToken token)
    {
        var target = (StarDetectionDestination)capture.Destination!;
        ValueTask<string?> Credential(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var current = DirectorCredentialStore.Read(target.Endpoint, target.Binding.ProfileId);
            if (current?.ClientId != target.ClientId || current.Binding != target.Binding)
                throw new InvalidOperationException("Pairing changed while sending star-detection settings.");
            return ValueTask.FromResult<string?>(current.Token);
        }
        using var client = new CoordinatorStarDetectionClient(target.Endpoint, target.Binding, Credential, target.AllowInsecureHttp);
        await client.SendAsync(capture.Report, token);
    }

    // Check again as soon as NINA reports a change to a settings group that
    // holds star-detection settings. The reporter's once-a-minute check covers
    // changes NINA does not report.
    private void WatchStarDetectionSettings(IProfile? profile)
    {
        lock (watchedStarDetectionSettings)
        {
            foreach (var watched in watchedStarDetectionSettings) watched.PropertyChanged -= StarDetectionSettingChanged;
            watchedStarDetectionSettings.Clear();
            if (profile is null) return;
            foreach (var group in new INotifyPropertyChanged?[] { profile.ImageSettings, profile.FocuserSettings, profile.TelescopeSettings,
                profile.CameraSettings, profile.ApplicationSettings, profile.PluginSettings })
            {
                if (group is null) continue;
                group.PropertyChanged += StarDetectionSettingChanged;
                watchedStarDetectionSettings.Add(group);
            }
        }
    }

    private void StarDetectionSettingChanged(object? sender, PropertyChangedEventArgs args) => starDetection.RequestSend();

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SendStarDetectionSettings)));
        start.Refresh();
        stop.Refresh();
        checkIn.Refresh();
        cancelCheckIn.Refresh();
        ConnectionModel.Changed();
        CollaborationModel.Changed();
    }
}
