using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace PsfGuard.Director.Plugin.Sequencer;

public enum DirectorOperationOwner { Director, Sequence }
public enum DirectorSafetyPolicy { RequireMonitor, Attended }
public enum DirectorHorizonPolicy { NinaProfile, MinimumAltitude }

// Requested local policy, not an issued program or permission to operate equipment.
[JsonObject(MemberSerialization.OptIn)]
public sealed class DirectorSessionOptions : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    [JsonProperty] public int SchemaVersion { get; private set; } = 1;
    private double maximumHours = 12, minimumAltitude = 20, maximumAltitude = 90;
    private double meridianBefore, meridianAfter;
    private int ditherEvery = 3, hookTimeout = 600, saveTimeout = 120, checkInMinutes = 5, statusSeconds = 15;
    private bool parkOnWait = true, allowOffline = true, checkInAtStart = true, checkInAtEnd = true, checkInOnTarget = true, liveStatus = true;
    private DirectorSafetyPolicy safety = DirectorSafetyPolicy.RequireMonitor;
    private DirectorHorizonPolicy horizon = DirectorHorizonPolicy.NinaProfile;
    private DirectorOperationOwner startup, slewCenter, focus, guiding, dither, meridianFlip, shutdown;

    [JsonProperty] public double MaximumHours { get => maximumHours; set => Set(ref maximumHours, value); }
    [JsonProperty] public double MinimumAltitude { get => minimumAltitude; set => Set(ref minimumAltitude, value); }
    [JsonProperty] public double MaximumAltitude { get => maximumAltitude; set => Set(ref maximumAltitude, value); }
    [JsonProperty] public double MeridianBeforeMinutes { get => meridianBefore; set => Set(ref meridianBefore, value); }
    [JsonProperty] public double MeridianAfterMinutes { get => meridianAfter; set => Set(ref meridianAfter, value); }
    [JsonProperty] public DirectorSafetyPolicy Safety { get => safety; set => Set(ref safety, value); }
    [JsonProperty] public DirectorHorizonPolicy Horizon { get => horizon; set => Set(ref horizon, value); }
    [JsonProperty] public bool ParkOnWait { get => parkOnWait; set => Set(ref parkOnWait, value); }
    [JsonProperty] public DirectorOperationOwner Startup { get => startup; set => Set(ref startup, value); }
    [JsonProperty] public DirectorOperationOwner SlewCenter { get => slewCenter; set => Set(ref slewCenter, value); }
    [JsonProperty] public DirectorOperationOwner Focus { get => focus; set => Set(ref focus, value); }
    [JsonProperty] public DirectorOperationOwner Guiding { get => guiding; set => Set(ref guiding, value); }
    [JsonProperty] public DirectorOperationOwner Dither { get => dither; set => Set(ref dither, value); }
    [JsonProperty] public DirectorOperationOwner MeridianFlip { get => meridianFlip; set => Set(ref meridianFlip, value); }
    [JsonProperty] public DirectorOperationOwner Shutdown { get => shutdown; set => Set(ref shutdown, value); }
    [JsonProperty] public int DitherEveryExposures { get => ditherEvery; set => Set(ref ditherEvery, value); }
    [JsonProperty] public int HookTimeoutSeconds { get => hookTimeout; set => Set(ref hookTimeout, value); }
    [JsonProperty] public int SaveTimeoutSeconds { get => saveTimeout; set => Set(ref saveTimeout, value); }
    [JsonProperty] public bool AllowOffline { get => allowOffline; set => Set(ref allowOffline, value); }
    [JsonProperty] public bool CheckInAtStart { get => checkInAtStart; set => Set(ref checkInAtStart, value); }
    [JsonProperty] public bool CheckInAtEnd { get => checkInAtEnd; set => Set(ref checkInAtEnd, value); }
    [JsonProperty] public bool CheckInOnTarget { get => checkInOnTarget; set => Set(ref checkInOnTarget, value); }
    [JsonProperty] public int CheckInMinutes { get => checkInMinutes; set => Set(ref checkInMinutes, value); }
    [JsonProperty] public bool LiveStatus { get => liveStatus; set => Set(ref liveStatus, value); }
    [JsonProperty] public int StatusSeconds { get => statusSeconds; set => Set(ref statusSeconds, value); }

    internal DirectorSessionOptions Clone()
    {
        var copy = (DirectorSessionOptions)MemberwiseClone();
        copy.PropertyChanged = null;
        return copy;
    }

    internal IReadOnlyList<string> ValidateSettings()
    {
        var issues = new List<string>();
        if (SchemaVersion != 1) issues.Add("Unsupported Director session settings version.");
        Range(MaximumHours, 0.01, 24, "Session duration (hours)");
        Range(MinimumAltitude, 0, 90, "Minimum altitude");
        Range(MaximumAltitude, 0, 90, "Maximum altitude");
        if (MinimumAltitude >= MaximumAltitude) issues.Add("Minimum altitude must be below maximum altitude.");
        Range(MeridianBeforeMinutes, 0, 360, "Before-meridian avoidance (minutes)");
        Range(MeridianAfterMinutes, 0, 360, "After-meridian avoidance (minutes)");
        Range(DitherEveryExposures, 1, 10000, "Dither exposure interval");
        Range(HookTimeoutSeconds, 1, 7200, "Instruction timeout (seconds)");
        Range(SaveTimeoutSeconds, 1, 1800, "Image-save timeout (seconds)");
        Range(CheckInMinutes, 1, 1440, "Check-in interval (minutes)");
        Range(StatusSeconds, 5, 3600, "Live-status interval (seconds)");
        if (!Enum.IsDefined(Safety) || !Enum.IsDefined(Horizon)) issues.Add("Unknown safety or horizon policy.");
        if (new[] { Startup, SlewCenter, Focus, Guiding, Dither, MeridianFlip, Shutdown }.Any(value => !Enum.IsDefined(value)))
            issues.Add("Unknown operation owner.");
        return issues;

        void Range(double value, double minimum, double maximum, string name)
        {
            if (!double.IsFinite(value) || value < minimum || value > maximum)
                issues.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
