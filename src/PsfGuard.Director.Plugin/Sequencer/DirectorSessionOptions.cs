using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace PsfGuard.Director.Plugin.Sequencer;

public enum DirectorOperationOwner { Director, Sequence }
public enum DirectorSafetyPolicy { RequireMonitor, Attended }
public enum DirectorHorizonPolicy { NinaProfile, MinimumAltitude }
public enum DirectorEnclosurePolicy { Unconfigured, OpenAir, RequireOpenShutter }
public enum DirectorAbortPolicy { ParkMount, StopMount }
public enum DirectorWeatherPolicy { StopForNight, HoldAndResume }
public enum DirectorCheckInMode { Live, Deferred }
public enum DirectorQualityPolicy { Off, Monitor, ParkAndStop, HoldAndProbe }

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
    private DirectorEnclosurePolicy enclosure;
    [JsonProperty] public DirectorEnclosurePolicy Enclosure { get => enclosure; set => Set(ref enclosure, value); }
    private DirectorAbortPolicy onAbort = DirectorAbortPolicy.ParkMount;
    [JsonProperty] public DirectorAbortPolicy OnAbort { get => onAbort; set => Set(ref onAbort, value); }
    private bool retryFocusAndGuiding;
    private DirectorQualityPolicy quality;
    private int poorQualityFrames = 3, goodQualityProbes = 2, maximumOperationFailures = 3;
    [JsonProperty] public DirectorQualityPolicy Quality { get => quality; set => Set(ref quality, value); }
    [JsonProperty] public int PoorQualityFrames { get => poorQualityFrames; set => Set(ref poorQualityFrames, value); }
    [JsonProperty] public int GoodQualityProbes { get => goodQualityProbes; set => Set(ref goodQualityProbes, value); }
    [JsonProperty] public int MaximumOperationFailures { get => maximumOperationFailures; set => Set(ref maximumOperationFailures, value); }
    private DirectorWeatherPolicy weather;
    private int stableSafeSeconds = 300, maximumWeatherMinutes = 360, maximumWeatherInterruptions = 10;
    [JsonProperty] public DirectorWeatherPolicy Weather { get => weather; set => Set(ref weather, value); }
    [JsonProperty] public int StableSafeSeconds { get => stableSafeSeconds; set => Set(ref stableSafeSeconds, value); }
    [JsonProperty] public int MaximumWeatherMinutes { get => maximumWeatherMinutes; set => Set(ref maximumWeatherMinutes, value); }
    [JsonProperty] public int MaximumWeatherInterruptions { get => maximumWeatherInterruptions; set => Set(ref maximumWeatherInterruptions, value); }
    private int retryCooldownSeconds = 60, maximumRecoveryMinutes = 10, maximumRecoveryAttempts = 3;
    [JsonProperty] public bool RetryFocusAndGuiding { get => retryFocusAndGuiding; set => Set(ref retryFocusAndGuiding, value); }
    [JsonProperty] public int RetryCooldownSeconds { get => retryCooldownSeconds; set => Set(ref retryCooldownSeconds, value); }
    [JsonProperty] public int MaximumRecoveryMinutes { get => maximumRecoveryMinutes; set => Set(ref maximumRecoveryMinutes, value); }
    [JsonProperty] public int MaximumRecoveryAttempts { get => maximumRecoveryAttempts; set => Set(ref maximumRecoveryAttempts, value); }
    private DirectorCheckInMode checkInMode;
    [JsonProperty] public DirectorCheckInMode CheckInMode { get => checkInMode; set => Set(ref checkInMode, value); }
    private DirectorOperationOwner startup, slewCenter, focus, guiding, dither, meridianFlip, shutdown;
    private bool enableAcquisition;
    [JsonProperty] public bool EnableAcquisition { get => enableAcquisition; set => Set(ref enableAcquisition, value); }
    private bool automaticWorkloads;
    private bool allowSettledRestart;
    [JsonProperty] public bool AllowSettledRestart { get => allowSettledRestart; set => Set(ref allowSettledRestart, value); }
    [JsonProperty] public bool AutomaticWorkloads { get => automaticWorkloads; set => Set(ref automaticWorkloads, value); }
    private bool localTargetScheduling;
    [JsonProperty] public bool LocalTargetScheduling { get => localTargetScheduling; set => Set(ref localTargetScheduling, value); }

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
        if (AllowSettledRestart && !AutomaticWorkloads) issues.Add("Settled-night restart requires automatic workloads and fresh server admission.");
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
        Range(RetryCooldownSeconds, 1, 3600, "Recovery cooldown (seconds)");
        Range(MaximumRecoveryMinutes, 1, 120, "Total recovery time (minutes)");
        Range(MaximumRecoveryAttempts, 1, 10, "Recovery attempts per session");
        Range(PoorQualityFrames, 2, 20, "Poor quality frames before stopping");
        Range(GoodQualityProbes, 1, 10, "Good probes before resuming");
        Range(MaximumOperationFailures, 1, 100, "Equipment failures per night");
        if (!Enum.IsDefined(Quality)) issues.Add("Unknown image quality policy.");
        if (Quality == DirectorQualityPolicy.HoldAndProbe && GoodQualityProbes > MaximumRecoveryAttempts)
            issues.Add("Good probes required cannot exceed the recovery attempt limit.");
        Range(StableSafeSeconds, 1, 3600, "Stable Safe/Open interval (seconds)");
        Range(MaximumWeatherMinutes, 1, 1440, "Total weather hold time (minutes)");
        Range(MaximumWeatherInterruptions, 1, 100, "Weather interruptions per night");
        if (!Enum.IsDefined(Weather)) issues.Add("Unknown weather policy.");
        if (StableSafeSeconds >= MaximumWeatherMinutes * 60) issues.Add("Stable Safe/Open interval must be shorter than the weather hold limit.");
        if (RetryCooldownSeconds >= MaximumRecoveryMinutes * 60)
            issues.Add("Recovery cooldown must be shorter than the total recovery time.");
        if (!Enum.IsDefined(Safety) || !Enum.IsDefined(Horizon)) issues.Add("Unknown safety or horizon policy.");
        if (!Enum.IsDefined(Enclosure)) issues.Add("Unknown enclosure clearance policy.");
        if (!Enum.IsDefined(OnAbort)) issues.Add("Unknown abort policy.");
        if (!Enum.IsDefined(CheckInMode)) issues.Add("Unknown check-in mode.");
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
