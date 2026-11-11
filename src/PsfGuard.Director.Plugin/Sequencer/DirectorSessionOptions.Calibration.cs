using System.Globalization;
using Newtonsoft.Json;

namespace PsfGuard.Director.Plugin.Sequencer;

public enum DirectorDuskTiming { Before, After }

public sealed partial class DirectorSessionOptions
{
    private bool closePanelAtEnd, scheduledFlats, darksWhileIdle, darksWhileWeatherHeld, darksFromPlans = true;
    private double flatsDuskMinutes = 30, flatMinimumSeconds = 0.1, flatMaximumSeconds = 30;
    private int flatsPerFilter = 25, flatBrightness = 50, darksPerSetting = 25;
    private string darkExposureSeconds = "", darkTemperatures = "";

    [JsonProperty] public bool ClosePanelAtEnd { get => closePanelAtEnd; set => Set(ref closePanelAtEnd, value); }
    [JsonProperty] public bool ScheduledFlats { get => scheduledFlats; set => Set(ref scheduledFlats, value); }
    private DirectorDuskTiming flatsTiming;
    [JsonProperty] public DirectorDuskTiming FlatsTiming { get => flatsTiming; set => Set(ref flatsTiming, value); }
    [JsonProperty] public double FlatsDuskMinutes { get => flatsDuskMinutes; set => Set(ref flatsDuskMinutes, value); }
    internal double FlatsDuskOffsetMinutes => FlatsTiming == DirectorDuskTiming.Before ? -FlatsDuskMinutes : FlatsDuskMinutes;
    [JsonProperty] public int FlatsPerFilter { get => flatsPerFilter; set => Set(ref flatsPerFilter, value); }
    [JsonProperty] public int FlatBrightness { get => flatBrightness; set => Set(ref flatBrightness, value); }
    [JsonProperty] public double FlatMinimumSeconds { get => flatMinimumSeconds; set => Set(ref flatMinimumSeconds, value); }
    [JsonProperty] public double FlatMaximumSeconds { get => flatMaximumSeconds; set => Set(ref flatMaximumSeconds, value); }
    [JsonProperty] public bool DarksWhileIdle { get => darksWhileIdle; set => Set(ref darksWhileIdle, value); }
    [JsonProperty] public bool DarksWhileWeatherHeld { get => darksWhileWeatherHeld; set => Set(ref darksWhileWeatherHeld, value); }
    [JsonProperty] public bool DarksFromPlans { get => darksFromPlans; set => Set(ref darksFromPlans, value); }
    [JsonProperty] public string DarkExposureSeconds { get => darkExposureSeconds; set => Set(ref darkExposureSeconds, value ?? ""); }
    [JsonProperty] public string DarkTemperatures { get => darkTemperatures; set => Set(ref darkTemperatures, value ?? ""); }
    [JsonProperty] public int DarksPerSetting { get => darksPerSetting; set => Set(ref darksPerSetting, value); }

    internal static double[] ParseCalibrationList(string value, double minimum, double maximum, string label)
    {
        var parts = value.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 32) throw new ArgumentException($"{label} allows up to 32 values.");
        return parts.Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && number >= minimum && number <= maximum
            ? number : throw new ArgumentException($"{label} must contain numbers from {minimum} to {maximum}, separated by commas. Use a dot for decimals."))
            .Distinct().ToArray();
    }

    private IEnumerable<string> ValidateCalibration()
    {
        if (ScheduledFlats)
        {
            if (!Enum.IsDefined(FlatsTiming) || !double.IsFinite(FlatsDuskMinutes) || FlatsDuskMinutes is < 0 or > 720)
                yield return "Flat time must be within 720 minutes before or after astronomical dusk.";
            if (FlatsPerFilter is < 1 or > 1000) yield return "Flats per filter must be between 1 and 1000.";
            if (FlatBrightness is < 0 or > 100) yield return "Flat-panel brightness must be between 0 and 100 percent.";
            if (!double.IsFinite(FlatMinimumSeconds) || !double.IsFinite(FlatMaximumSeconds)
                || FlatMinimumSeconds < 0.001 || FlatMaximumSeconds > 600 || FlatMaximumSeconds < FlatMinimumSeconds)
                yield return "Flat exposure limits must be between 0.001 and 600 seconds, with the minimum no higher than the maximum.";
        }
        if (DarksWhileIdle || DarksWhileWeatherHeld)
        {
            if (DarksPerSetting is < 1 or > 1000) yield return "Darks per setting must be between 1 and 1000.";
            string? error = null;
            try
            {
                var exposures = ParseCalibrationList(DarkExposureSeconds, 0.001, 7200, "Dark exposure lengths (seconds)");
                ParseCalibrationList(DarkTemperatures, -80, 40, "Dark temperatures (°C)");
                if (!DarksFromPlans && exposures.Length == 0) error = "Use active-plan dark exposures or enter at least one exposure length.";
            }
            catch (ArgumentException ex) { error = ex.Message; }
            if (error is not null) yield return error;
        }
    }
}
