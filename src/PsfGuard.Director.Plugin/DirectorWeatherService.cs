using System.ComponentModel;
using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Profile;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

[Export(typeof(DirectorWeatherService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class DirectorWeatherService : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly Guid PluginId = new("03a1d13e-67eb-4e24-a407-82bce7e576a5");
    private readonly IProfileService profiles;
    private readonly IWeatherDataMediator mediator;
    private readonly PluginOptionsAccessor settings;
    private readonly NinaWeatherReader reader;
    private readonly CloudWeatherCache cache = new(TimeProvider.System);
    private readonly SemaphoreSlim changed = new(0, 1);
    private CancellationTokenSource? lifetime;
    private Task? loop;
    private string status = "Weather not started";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Status => status;
    public string ForecastSummary => cache.Latest?.Forecast is { } f
        ? $"Last forecast: {(f.Provider == "open_meteo" ? "Open-Meteo" : "MET Norway")}; {ForecastDetails(f.Hour)}{(f.Fresh ? "" : "; stale")}" : "Forecast: unavailable";
    public string ReportedSummary => cache.Latest?.Reported is { } r
        ? $"Last NINA report: {r.Sample.SourceName}; {(r.Sample.Connected ? r.Sample.Values.CloudPercent is { } cover ? cover.ToString("0.#") + "% cloud" : "cloud cover unavailable" : "disconnected")}" : "NINA reported weather: unavailable";
    public string Attribution => cache.Latest?.Forecast?.Attribution ?? "";
    private static string ForecastDetails(WeatherForecastHour? hour)
    {
        if (hour is null) return "outside forecast coverage";
        var parts = new List<string> { $"{hour.CloudTotal:0.#}% cloud" };
        if (hour.TemperatureCelsius is { } air) parts.Add($"air {air:0.#} °C");
        if (hour.DewPointCelsius is { } dew) parts.Add($"dew point {dew:0.#} °C");
        if (hour.HumidityPercent is { } humidity) parts.Add($"{humidity:0.#}% humidity");
        return string.Join("; ", parts);
    }

    [ImportingConstructor]
    public DirectorWeatherService(IProfileService profiles, IWeatherDataMediator mediator)
    {
        this.profiles = profiles; this.mediator = mediator;
        settings = new(profiles, PluginId);
        reader = new(profiles, TimeProvider.System, () => settings.GetValueBoolean("SendWeatherTelemetry", true));
    }

    public void Start()
    {
        if (lifetime is not null) return;
        lifetime = new();
        profiles.BeforeProfileChanging += ProfileChanged;
        profiles.ProfileChanged += ProfileChanged;
        mediator.RegisterConsumer(reader);
        loop = Task.Run(() => RunAsync(lifetime.Token));
    }
    public void RequestRefresh() { if (changed.CurrentCount == 0) { try { changed.Release(); } catch (SemaphoreFullException) { } } }
    private void ProfileChanged(object? sender, EventArgs args) { cache.Clear(); RequestRefresh(); Refresh(); }
    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private void SetStatus(string next) { if (next == status) return; status = next; Refresh(); }

    public CloudWeatherEvidence Read(Guid profile, Uri endpoint, CoordinatorBinding binding, bool reported, bool forecast)
    {
        if (reported && !settings.GetValueBoolean("SendWeatherTelemetry", true))
            return new(false, "NINA weather telemetry is disabled in Director settings");
        if (reported && cache.Latest?.Reported?.Sample.SourceKey != reader.CurrentSourceKey)
            return new(false, "Waiting for weather from the selected NINA source");
        return cache.Read(profile, endpoint, binding, reported, forecast);
    }

    private async Task RunAsync(CancellationToken token)
    {
        Guid? sent = null;
        while (!token.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(15);
            try
            {
                var profile = profiles.ActiveProfile.Id;
                using var connection = new DirectorConnection(() => profiles.ActiveProfile.Id, () => false,
                    () => settings.GetValueString("CoordinatorUrl", ""), _ => { },
                    readHttpConsent: () => settings.GetValueString("HttpConsentOrigin", ""));
                var endpoint = connection.ResolvedEndpoint;
                var pairing = connection.ReadPairing();
                if (endpoint is null || pairing is null)
                { cache.Clear(); SetStatus("Pair Director with PSF Guard to share weather"); }
                else
                {
                    var sample = settings.GetValueBoolean("SendWeatherTelemetry", true) ? reader.Read() : null;
                    if (sample?.SampleId == sent) sample = null;
                    ValueTask<string?> Credential(CancellationToken ct)
                    {
                        ct.ThrowIfCancellationRequested();
                        var current = DirectorCredentialStore.Read(endpoint, profile);
                        if (profiles.ActiveProfile.Id != profile || current?.ClientId != pairing.ClientId || current.Binding != pairing.Binding)
                            throw new InvalidOperationException("Weather pairing changed.");
                        if (sample is not null && (!settings.GetValueBoolean("SendWeatherTelemetry", true) || sample.SourceKey != reader.CurrentSourceKey))
                            throw new InvalidOperationException("Weather source or telemetry consent changed.");
                        return ValueTask.FromResult<string?>(current.Token);
                    }
                    using var client = new CoordinatorWeatherClient(endpoint, pairing.Binding, Credential, connection.AllowInsecureHttp);
                    var stamp = TimeProvider.System.GetTimestamp(); var utc = DateTimeOffset.UtcNow;
                    var result = await client.ReadAsync(sample, token).ConfigureAwait(false);
                    await Credential(token);
                    cache.Store(profile, endpoint, pairing.Binding, result, stamp, utc);
                    if (sample is not null) sent = sample.SampleId;
                    SetStatus("Weather connected through PSF Guard"); Refresh();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (CoordinatorIntakeException error)
            {
                if (error.Failure is not (CoordinatorIntakeFailure.Transport or CoordinatorIntakeFailure.Timeout or CoordinatorIntakeFailure.ServerUnavailable or CoordinatorIntakeFailure.Busy)) cache.Clear();
                SetStatus(error.Failure == CoordinatorIntakeFailure.NotFound ? "PSF Guard weather endpoint unavailable; update the server" : "PSF Guard weather unavailable; decisions expire automatically");
                if (error.Failure == CoordinatorIntakeFailure.NotFound) delay = TimeSpan.FromMinutes(5);
            }
            catch (Exception error) { Logger.Error(error.GetType().Name); cache.Clear(); SetStatus("Weather pairing or profile unavailable"); }
            try { await changed.WaitAsync(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetime is null) return;
        lifetime.Cancel();
        if (loop is not null) await loop.ConfigureAwait(false);
        profiles.BeforeProfileChanging -= ProfileChanged;
        profiles.ProfileChanged -= ProfileChanged;
        mediator.RemoveConsumer(reader);
        reader.Dispose();
        cache.Clear();
        lifetime.Dispose(); lifetime = null;
    }
}
