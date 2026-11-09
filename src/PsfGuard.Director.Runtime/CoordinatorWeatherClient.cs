using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

public sealed record WeatherValues(double? CloudPercent = null, double? TemperatureCelsius = null,
    double? DewPointCelsius = null, double? HumidityPercent = null, double? PressureHpa = null,
    double? RainMmPerHour = null, double? SkyBrightnessLux = null, double? SkyQualityMagArcsec2 = null,
    double? SkyTemperatureCelsius = null, double? SeeingArcsec = null, double? WindDirectionDegrees = null,
    [property: JsonPropertyName("wind_speed_m_s")] double? WindSpeedMS = null,
    [property: JsonPropertyName("wind_gust_m_s")] double? WindGustMS = null);
public sealed record WeatherSample(Guid SampleId, Guid ProfileId, ulong ReportedAtMs,
    string SourceName, string SourceKey, bool Connected, WeatherValues Values);
public sealed record WeatherObservation(Guid RigId, WeatherSample Sample, ulong ReceivedAtMs);
public sealed record CloudAssessment(string State, string Reason, ulong ValidForMs);
public sealed record WeatherForecastHour(ulong StartMs, ulong EndMs, double CloudTotal,
    double? CloudLow, double? CloudMid, double? CloudHigh, double? TemperatureCelsius = null,
    double? DewPointCelsius = null, double? HumidityPercent = null);
public sealed record WeatherForecast(string Provider, string Attribution, ulong FetchedAtMs,
    ulong ExpiresAtMs, WeatherForecastHour? Hour, bool Fresh);
public sealed record RigWeather(Guid RigId, Guid? SiteId, ulong EvaluatedAtMs, int CloudThresholdPercent,
    WeatherObservation? Reported, WeatherForecast? Forecast, CloudAssessment ReportedCloud, CloudAssessment ForecastConditions, int? DewLimitCelsius = null);

/// Reads PSF Guard's cached forecast; never contacts a weather provider or handles its key.
public sealed class CoordinatorWeatherClient : IDisposable
{
    private readonly CoordinatorTransport transport;
    private readonly CoordinatorBinding binding;
    public CoordinatorWeatherClient(Uri endpoint, CoordinatorBinding binding,
        Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false)
        : this(endpoint, binding, credential, CoordinatorTransport.Handler(), allowInsecureHttp) { }
    internal CoordinatorWeatherClient(Uri endpoint, CoordinatorBinding binding,
        Func<CancellationToken, ValueTask<string?>> credential, HttpMessageHandler handler, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        this.binding = binding;
        transport = new(endpoint, credential, handler, allowInsecureHttp);
    }

    public async Task<RigWeather> ReadAsync(WeatherSample? sample, CancellationToken token)
    {
        if (sample is not null && (sample.ProfileId != binding.ProfileId || sample.SampleId == Guid.Empty))
            throw new ArgumentException("Weather must belong to the paired profile.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/weather",
                sample is null ? null : JsonSerializer.SerializeToUtf8Bytes(sample, CoordinatorProgramContract.Options), deadline.Token, binding.ProfileId);
            using var document = JsonDocument.Parse(response.Bytes);
            var root = document.RootElement;
            CoordinatorProgramContract.CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var data = root.GetProperty("data");
            if (sample is not null)
            {
                PipeProtocol.RequireFields(data, "sample_id", "profile_id", "stored", "weather");
                if (data.GetProperty("sample_id").GetGuid() != sample.SampleId || data.GetProperty("profile_id").GetGuid() != binding.ProfileId
                    || data.GetProperty("stored").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException();
                data = data.GetProperty("weather");
            }
            PipeProtocol.RequireFields(data, "rig_id", "site_id", "evaluated_at_ms", "cloud_threshold_percent", "dew_limit_celsius", "reported", "forecast", "reported_cloud", "forecast_conditions");
            foreach (var key in new[] { "reported_cloud", "forecast_conditions" })
                PipeProtocol.RequireFields(data.GetProperty(key), "state", "reason", "valid_for_ms");
            var result = data.Deserialize<RigWeather>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException();
            if (result.RigId != binding.RigId || result.EvaluatedAtMs == 0 || result.CloudThresholdPercent is < 0 or > 100
                || result.DewLimitCelsius is < 0 or > 20 || result.Reported is { } report && report.RigId != binding.RigId) throw new InvalidDataException();
            foreach (var cloud in new[] { result.ReportedCloud, result.ForecastConditions })
                if (cloud is null || cloud.State is not ("clear" or "cloudy" or "dewy" or "unknown") || cloud.ValidForMs > 30_000
                    || (cloud.State == "unknown") != (cloud.ValidForMs == 0) || string.IsNullOrWhiteSpace(cloud.Reason) || cloud.Reason.Length > 128)
                    throw new InvalidDataException();
            if (result.ReportedCloud.State != "unknown" && (result.Reported is not { Sample.Connected: true } current
                || current.Sample.ProfileId != binding.ProfileId || current.Sample.Values.CloudPercent is not { } cover
                || !double.IsFinite(cover) || cover is < 0 or > 100)) throw new InvalidDataException();
            if (result.ForecastConditions.State != "unknown" && (result.Forecast is not { Fresh: true, Hour: { } hour } forecast
                || forecast.Provider is not ("open_meteo" or "met_norway") || forecast.ExpiresAtMs <= result.EvaluatedAtMs
                || hour.StartMs > result.EvaluatedAtMs || hour.EndMs <= result.EvaluatedAtMs
                || !double.IsFinite(hour.CloudTotal) || hour.CloudTotal is < 0 or > 100)) throw new InvalidDataException();
            if (result.ReportedCloud.State == "dewy" || result.ForecastConditions.State == "dewy" &&
                (result.DewLimitCelsius is null || result.Forecast?.Hour is not { TemperatureCelsius: { } air, DewPointCelsius: { } dew }
                    || !double.IsFinite(air) || !double.IsFinite(dew) || air is < -100 or > 70 || dew is < -100 or > 70))
                throw new InvalidDataException();
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or FormatException or OverflowException)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.InvalidAcknowledgement); }
    }
    public void Dispose() => transport.Dispose();
}
