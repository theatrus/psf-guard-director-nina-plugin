namespace PsfGuard.Director.Runtime;

public sealed record CloudWeatherEvidence(bool Clear, string Reason);

/// The server evaluates cloud thresholds. This cache only enforces scope and
/// the short decision lease, including elapsed request time and clock changes.
public sealed class CloudWeatherCache(TimeProvider clock)
{
    private readonly object gate = new();
    private Entry? entry;
    private sealed record Entry(Guid Profile, Uri Endpoint, CoordinatorBinding Binding, RigWeather Weather, long Stamp, DateTimeOffset Utc);
    public void Clear() { lock (gate) entry = null; }
    public void Store(Guid profile, Uri endpoint, CoordinatorBinding binding, RigWeather weather, long requestStamp, DateTimeOffset requestUtc)
    {
        if (profile != binding.ProfileId || weather.RigId != binding.RigId) throw new ArgumentException("Weather scope mismatch.");
        lock (gate) entry = new(profile, endpoint, binding, weather, requestStamp, requestUtc);
    }
    public RigWeather? Latest { get { lock (gate) return entry?.Weather; } }
    public CloudWeatherEvidence Read(Guid profile, Uri endpoint, CoordinatorBinding binding, bool useReported, bool useForecast)
    {
        if (!useReported && !useForecast) return new(true, "Cloud holds disabled");
        lock (gate)
        {
            if (entry is not { } current || current.Profile != profile || current.Endpoint != endpoint || current.Binding != binding)
                return new(false, "Waiting for PSF Guard weather for this profile and rig");
            var elapsed = clock.GetElapsedTime(current.Stamp);
            var wall = clock.GetUtcNow() - current.Utc;
            if (elapsed < TimeSpan.Zero || wall < TimeSpan.Zero) return new(false, "Weather observation clock changed");
            foreach (var (selected, cloud, label) in new[] {
                (useReported, current.Weather.ReportedCloud, "NINA reported cloud cover"),
                (useForecast, current.Weather.ForecastConditions, "PSF Guard site forecast") })
            {
                if (!selected) continue;
                if (cloud.State == "unknown") return new(false, $"{label}: unavailable or stale ({cloud.Reason})");
                var lease = TimeSpan.FromMilliseconds(cloud.ValidForMs);
                if (elapsed >= lease || wall >= lease) return new(false, $"{label}: PSF Guard weather decision expired");
                if (cloud.State == "dewy") return new(false, $"{label}: air is within the site's dew-point limit");
                if (cloud.State != "clear") return new(false, $"{label}: above the site's {current.Weather.CloudThresholdPercent}% cloud threshold");
            }
            return new(true, "Selected cloud sources are clear");
        }
    }
}
