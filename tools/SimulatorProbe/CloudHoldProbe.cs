using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.SimulatorProbe;

internal sealed partial class CoordinatorProbe
{
    internal CoordinatorBinding WeatherBinding => pairing.Binding;

    internal async Task ConfigureCloudForecastAsync(CancellationToken token)
    {
        var site = Guid.NewGuid();
        await OperatorAsync(HttpMethod.Post, "sites", new { id = site, name = "Isolated weather simulator" }, token);
        var settings = JsonNode.Parse((await OperatorAsync(HttpMethod.Get, $"preferences/rig/{RigId}", null, token)).GetRawText())!;
        settings["site_id"] = site.ToString("D");
        await OperatorAsync(HttpMethod.Put, $"preferences/rig/{RigId}", settings, token);
        // The provider is a loopback stand-in configured only in the scratch server.
        await OperatorAsync(HttpMethod.Put, $"sites/{site:D}/profile", new
        {
            expected_revision = 0,
            location = new { value = new { latitude_degrees = 35.0, longitude_degrees = -120.0, elevation_meters = 1000.0 }, source = new { kind = "manual" } },
            horizon = (object?)null,
            forecast = new { provider = "open_meteo", cloud_threshold_percent = 30, dew_limit_celsius = 2 }
        }, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var view = await OperatorAsync(HttpMethod.Get, $"rigs/{RigId}/weather", null, deadline.Token);
            if (view.GetProperty("forecast_conditions").GetProperty("state").GetString() == "cloudy") break;
            await Task.Delay(200, deadline.Token);
        }
    }
}

// Runs the production reporter and capture adapter against real NINA mediators.
// Only this disposable profile receives synthetic weather; no device is connected.
internal sealed class CloudHoldProbe : IAsyncDisposable
{
    internal const string Source = "director-isolated-weather";
    private readonly IProfileService profiles;
    private readonly IWeatherDataMediator mediator;
    private readonly string previousSource;
    private readonly CancellationTokenSource stop = new();
    private readonly Task broadcast;
    private double cover = 10;
    internal DirectorWeatherService Service { get; }

    internal CloudHoldProbe(IProfileService profiles, IWeatherDataMediator mediator)
    {
        this.profiles = profiles; this.mediator = mediator;
        previousSource = profiles.ActiveProfile.WeatherDataSettings.Id;
        profiles.ActiveProfile.WeatherDataSettings.Id = Source;
        Service = new(profiles, mediator);
        Service.Start();
        Send();
        broadcast = Task.Run(async () =>
        {
            try { while (true) { await Task.Delay(1000, stop.Token); Send(); } }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }
    private void Send() => mediator.Broadcast(new WeatherDataInfo
    {
        DeviceId = Source,
        Name = "Isolated weather simulator",
        Connected = true,
        CloudCover = Volatile.Read(ref cover),
        Temperature = 10,
        DewPoint = 4,
        Humidity = 65
    });
    private void SetCover(double value) { Volatile.Write(ref cover, value); Send(); Service.RequestRefresh(); }

    internal async Task PrepareAsync(CoordinatorProbe coordinator, DirectorSessionContainer session, CancellationToken token)
    {
        await coordinator.ConfigureCloudForecastAsync(token);
        session.Options.Weather = DirectorWeatherPolicy.HoldAndResume;
        session.Options.UseReportedCloudCover = session.Options.UseSiteCloudForecast = true;
        session.Options.StableSafeSeconds = 2;
        session.Options.MaximumWeatherMinutes = 3;
        session.Options.MaximumWeatherInterruptions = 3;
        SetCover(10);
        await WaitAsync(() => Service.Status == "Weather connected through PSF Guard"
            && Service.ReportedSummary.Contains("10% cloud", StringComparison.Ordinal), token);
    }

    internal async Task VerifyAsync(Task executing, CoordinatorProbe coordinator, DirectorSessionContainer session,
        DirectorAcquisition acquisition, ICameraMediator camera, string directory, CancellationToken token)
    {
        await WaitAsync(() => session.Display.Phase.StartsWith("Weather hold:", StringComparison.Ordinal), token);
        await Task.Delay(2500, token);
        if (camera.GetInfo().IsExposing || Saved() != 0 || executing.IsCompleted)
            throw new InvalidDataException("Cloudy central forecast did not prevent the first exposure.");
        var view = await coordinator.ReadRigStatusAsync(token);
        var weather = view.GetProperty("weather");
        if (weather.GetProperty("reported_cloud").GetProperty("state").GetString() != "clear"
            || weather.GetProperty("forecast_conditions").GetProperty("state").GetString() != "cloudy")
            throw new InvalidDataException("NINA and central forecast provenance was lost.");
        // The next forecast interval is clear. The normal reporter polls that
        // cached interval without making a new provider request.
        await WaitAsync(() => camera.GetInfo().IsExposing, token);
        SetCover(80);
        await WaitAsync(() => Service.ReportedSummary.Contains("80% cloud", StringComparison.Ordinal), token);
        await WaitAsync(() => session.Display.Phase.StartsWith("Weather hold:", StringComparison.Ordinal) && !camera.GetInfo().IsExposing, token);
        if (Saved() != 1 || executing.IsCompleted)
            throw new InvalidDataException("Cloud change interrupted a frame or allowed another exposure.");
        await Task.Delay(2500, token);
        if (Saved() != 1 || camera.GetInfo().IsExposing)
            throw new InvalidDataException("Reported cloud hold dispatched another frame.");
        SetCover(10);
        await WaitAsync(() => Saved() == 3, token);
        await File.WriteAllTextAsync(Path.Combine(directory, "cloud-hold-verified.txt"),
            "Central forecast held startup; NINA and forecast remained separate; in-flight frame saved; reported clouds held the next exposure; both clear resumed all three native captures.", token);

        int Saved() => acquisition.LastRunDirectory is not { } run ? 0 : Directory.GetFiles(run, "*.json", SearchOption.AllDirectories)
            .Where(p => Path.GetDirectoryName(p)!.Contains(Path.DirectorySeparatorChar + "journal" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(CaptureJournal.Read).Count(c => c.Phase == CapturePhase.Saved);
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(100));
        while (!ready()) await Task.Delay(100, deadline.Token);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await broadcast;
        await Service.DisposeAsync();
        mediator.Broadcast(new WeatherDataInfo { DeviceId = Source, Connected = false });
        profiles.ActiveProfile.WeatherDataSettings.Id = previousSource;
        stop.Dispose();
    }
}
