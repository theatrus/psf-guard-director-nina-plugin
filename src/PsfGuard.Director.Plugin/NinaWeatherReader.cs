using System.Security.Cryptography;
using System.Text;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

/// NINA supplies reported current values. It does not expose the provider's
/// sensor observation time, so a broadcast is never labeled a new measurement.
internal sealed class NinaWeatherReader(IProfileService profiles, TimeProvider clock, Func<bool>? enabled = null) : IWeatherDataConsumer
{
    private readonly object gate = new();
    private WeatherSample? sample;
    private string? device;
    private bool disposed;
    internal string CurrentSourceKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profiles.ActiveProfile.WeatherDataSettings.Id ?? "No_Device"))).ToLowerInvariant();
    internal WeatherSample? Read()
    {
        lock (gate)
            return !disposed && enabled?.Invoke() != false && sample?.ProfileId == profiles.ActiveProfile.Id && device == profiles.ActiveProfile.WeatherDataSettings.Id ? sample : null;
    }
    private static double? Value(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max ? value : null;
    public void UpdateDeviceInfo(WeatherDataInfo info)
    {
        lock (gate)
        {
            try
            {
                if (disposed || enabled?.Invoke() == false) { sample = null; return; }
                var profile = profiles.ActiveProfile;
                device = profile.WeatherDataSettings.Id;
                var connected = info.Connected && !string.IsNullOrWhiteSpace(device) && device != "No_Device" && info.DeviceId == device;
                var name = connected ? info.Name : "Weather source disconnected";
                name = new string((name ?? "NINA weather").Where(c => !char.IsControl(c)).Take(128).ToArray());
                if (name.Length == 0) name = "NINA weather";
                var values = connected ? new WeatherValues(Value(info.CloudCover, 0, 100), Value(info.Temperature, -100, 70),
                    Value(info.DewPoint, -100, 70), Value(info.Humidity, 0, 100), Value(info.Pressure, 0, 1500),
                    Value(info.RainRate, 0, 10000), Value(info.SkyBrightness, 0, 1000000), Value(info.SkyQuality, 0, 40),
                    Value(info.SkyTemperature, -273.15, 150), Value(info.StarFWHM, 0, 100), Value(info.WindDirection, 0, 360),
                    Value(info.WindSpeed, 0, 200), Value(info.WindGust, 0, 200)) : new WeatherValues();
                sample = new(Guid.NewGuid(), profile.Id, checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds()), name,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device ?? "No_Device"))).ToLowerInvariant(), connected, values);
            }
            catch (Exception) { sample = null; device = null; }
        }
    }
    public void Dispose() { lock (gate) { disposed = true; sample = null; } }
}
