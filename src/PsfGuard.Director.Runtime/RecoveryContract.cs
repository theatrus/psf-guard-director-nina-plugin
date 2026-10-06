using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

public enum RecoveryMotion { Permitted, Prohibited, Unknown }
public enum RecoveryQualityMode { Disabled, MonitorOnly, Pause, ParkAndStop }
public enum RecoveryVerdict { Unknown, CorroboratedPoor, ConfirmedGood }
public enum RecoveryOperation { Guide, Focus, Center, Slew, Capture, Other }
public enum RecoveryShutdown { NoParkRequested, MotionBlocked, Parked, ParkFailed, ParkUncertain }
public enum RecoveryParkResult { Parked, Failed, Uncertain }
public enum RecoveryError
{
    Disabled, UnsupportedVersion, InvalidInput, InvalidDirectory, Busy, Unavailable, WrongScope,
    Conflict, Corrupt, UnsupportedSchema, ForeignDatabase, LimitReached, WrongPhase,
    StaleEvidence, ChangedReference, ClockReversed, NotAdmitted, AcquisitionBlocked, InsufficientSamples, UnstableBaseline
}
public sealed record RecoveryIdentity(string RigId, string ConfigurationId, string NightId, ulong StartsAtMs, ulong EndsAtMs);
public sealed record RecoveryPolicy(ulong Revision, RecoveryQualityMode QualityMode, uint BadSamples, uint GoodProbes,
    ulong CooldownMs, ulong MaximumHoldMs, uint MaximumProbes, ulong OperationTimeoutMs, ulong EvidenceMaxAgeMs,
    ulong LatestResumeMs, uint MaximumConsecutiveFailures, uint MaximumTotalFailures, bool ParkOnStop,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecoveryWeatherPolicy? Weather = null);
public sealed record RecoveryWeatherPolicy(ulong StableSafeMs, ulong MaximumHoldMs, uint MaximumInterruptions);
public sealed record RecoveryConditions(PlannerSafety Safety, RecoveryMotion Motion);
public sealed record RecoveryFailure(string AttemptId, RecoveryOperation Operation, string DeviceId, string TargetId, bool Uncertain);
public sealed record RecoveryQualityContext(string TargetId, string FilterId, ulong ExposureMs, ushort BinX, ushort BinY,
    string ReferenceId, string Source, string AlgorithmRevision);
public sealed record RecoveryQualitySample(string RigId, string ConfigurationId, string CaptureId, ulong ObservedAtMs,
    RecoveryQualityContext Context, RecoveryVerdict Verdict);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RecoveryCause.Quality), "quality")]
[JsonDerivedType(typeof(RecoveryCause.Equipment), "equipment")]
[JsonDerivedType(typeof(RecoveryCause.Safety), "safety")]
[JsonDerivedType(typeof(RecoveryCause.Enclosure), "enclosure")]
[JsonDerivedType(typeof(RecoveryCause.Operator), "operator")]
[JsonDerivedType(typeof(RecoveryCause.NightEnded), "night_ended")]
[JsonDerivedType(typeof(RecoveryCause.HoldExpired), "hold_expired")]
[JsonDerivedType(typeof(RecoveryCause.ProbeBudget), "probe_budget")]
[JsonDerivedType(typeof(RecoveryCause.RecoveryUncertain), "recovery_uncertain")]
[JsonDerivedType(typeof(RecoveryCause.FailureBudget), "failure_budget")]
public abstract record RecoveryCause
{
    public sealed record Quality(RecoveryQualityContext Context) : RecoveryCause;
    public sealed record Equipment(RecoveryFailure Failure) : RecoveryCause;
    public sealed record Safety : RecoveryCause;
    public sealed record Enclosure : RecoveryCause;
    public sealed record Operator : RecoveryCause;
    public sealed record NightEnded : RecoveryCause;
    public sealed record HoldExpired : RecoveryCause;
    public sealed record ProbeBudget : RecoveryCause;
    public sealed record RecoveryUncertain : RecoveryCause;
    public sealed record FailureBudget : RecoveryCause;
}
public sealed record RecoveryHold(RecoveryCause Cause, ulong StartedAtMs, ulong RetryAtMs, ulong ExpiresAtMs, uint Probes, uint GoodProbes);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(RecoveryPhase.Acquiring), "acquiring")]
[JsonDerivedType(typeof(RecoveryPhase.WeatherHolding), "weather_holding")]
[JsonDerivedType(typeof(RecoveryPhase.Holding), "holding")]
[JsonDerivedType(typeof(RecoveryPhase.Recovering), "recovering")]
[JsonDerivedType(typeof(RecoveryPhase.Stopping), "stopping")]
[JsonDerivedType(typeof(RecoveryPhase.Stopped), "stopped")]
public abstract record RecoveryPhase
{
    public sealed record Acquiring : RecoveryPhase;
    public sealed record WeatherHolding(RecoveryCause Cause, ulong StartedAtMs, ulong? StableSinceMs) : RecoveryPhase;
    public sealed record Holding(RecoveryHold Hold) : RecoveryPhase;
    public sealed record Recovering(RecoveryHold Hold, string AttemptId, ulong StartedAtMs, ulong DeadlineMs) : RecoveryPhase;
    public sealed record Stopping(RecoveryCause Cause, ulong DeadlineMs, string? ParkAttemptId) : RecoveryPhase;
    public sealed record Stopped(RecoveryCause Cause, RecoveryShutdown Shutdown) : RecoveryPhase;
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(RecoveryOutcome.Quality), "quality")]
[JsonDerivedType(typeof(RecoveryOutcome.EquipmentVerified), "equipment_verified")]
[JsonDerivedType(typeof(RecoveryOutcome.Failed), "failed")]
[JsonDerivedType(typeof(RecoveryOutcome.Uncertain), "uncertain")]
public abstract record RecoveryOutcome
{
    public sealed record Quality(RecoveryQualitySample Sample) : RecoveryOutcome;
    public sealed record EquipmentVerified : RecoveryOutcome;
    public sealed record Failed : RecoveryOutcome;
    public sealed record Uncertain : RecoveryOutcome;
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(RecoveryEvent.Tick), "tick")]
[JsonDerivedType(typeof(RecoveryEvent.WeatherInterrupted), "weather_interrupted")]
[JsonDerivedType(typeof(RecoveryEvent.ResumeWeather), "resume_weather")]
[JsonDerivedType(typeof(RecoveryEvent.Quality), "quality")]
[JsonDerivedType(typeof(RecoveryEvent.OperationFailure), "failure")]
[JsonDerivedType(typeof(RecoveryEvent.BeginRecovery), "begin_recovery")]
[JsonDerivedType(typeof(RecoveryEvent.RecoveryCompleted), "recovery_completed")]
[JsonDerivedType(typeof(RecoveryEvent.StopNight), "stop_night")]
[JsonDerivedType(typeof(RecoveryEvent.BeginPark), "begin_park")]
[JsonDerivedType(typeof(RecoveryEvent.ParkCompleted), "park_completed")]
public abstract record RecoveryEvent
{
    public sealed record Tick : RecoveryEvent;
    public sealed record WeatherInterrupted(bool Enclosure) : RecoveryEvent;
    public sealed record ResumeWeather : RecoveryEvent;
    public sealed record Quality(RecoveryQualitySample Sample) : RecoveryEvent;
    public sealed record OperationFailure(RecoveryFailure Failure) : RecoveryEvent;
    public sealed record BeginRecovery(string AttemptId) : RecoveryEvent;
    public sealed record RecoveryCompleted(string AttemptId, RecoveryOutcome Result) : RecoveryEvent;
    public sealed record StopNight : RecoveryEvent;
    public sealed record BeginPark(string AttemptId) : RecoveryEvent;
    public sealed record ParkCompleted(string AttemptId, RecoveryParkResult Result) : RecoveryEvent;
}
public sealed record RecoveryFailureCount(RecoveryOperation Operation, string DeviceId, uint Consecutive, uint Total);
public sealed record RecoverySnapshot(uint SchemaVersion, RecoveryIdentity Identity, RecoveryPolicy Policy, ulong LastEventMs,
    RecoveryPhase Phase, uint ConsecutiveBad, RecoveryQualityContext? QualityContext, ulong? LastQualityMs,
    ImmutableArray<RecoveryFailureCount> Failures, uint TotalFailures, ulong TotalHoldMs, uint ProbesSpent,
    uint WeatherInterruptions = 0, ulong WeatherHoldMs = 0);
public sealed record RecoveryRecord(ulong Revision, RecoverySnapshot Snapshot);
public sealed record RecoveryRequest(string NightId, string ConfigurationId, string EventId, ulong ExpectedRevision,
    ulong NowMs, RecoveryConditions Conditions, RecoveryEvent Event);
public sealed record RecoveryIssued(string Operation, string AttemptId, ulong DeadlineMs);
public sealed record RecoveryOpened(bool Created, RecoveryRecord Record);
public sealed record RecoveryCurrent(RecoveryRecord? Record);
/// <summary>Committed input is not a native permit. Replays and readback never issue work.</summary>
public sealed record RecoveryApplied(bool NewlyApplied, RecoveryRecord Record, RecoveryIssued? Issued);
public sealed record RecoveryJournalEvent(ulong Revision, RecoveryRequest Request);
public sealed record RecoveryPage(ImmutableArray<RecoveryJournalEvent> Events, ulong NextCursor);
public sealed record RecoveryResult<T>(T? Value, RecoveryError? Error) where T : class;

internal static class RecoveryContract
{
    internal const int Version = 2;
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
        Converters =
        {
            new StrictEnum<PlannerSafety>(), new StrictEnum<RecoveryMotion>(), new StrictEnum<RecoveryQualityMode>(),
            new StrictEnum<RecoveryVerdict>(), new StrictEnum<RecoveryOperation>(), new StrictEnum<RecoveryShutdown>(),
            new StrictEnum<RecoveryParkResult>(), new StrictEnum<QualityReason>(),
            new StrictEnum<RestartBoundary>(), new StrictEnum<RestartAdvice>()
        }
    };

    private sealed class StrictEnum<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            PlannerContract.ParseEnum<T>(reader.GetString());

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new InvalidDataException("Unknown recovery enum value.");
            writer.WriteStringValue(JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()));
        }
    }

    internal static T Read<T>(JsonElement value)
    {
        CheckJson(value);
        return value.Deserialize<T>(Options) ?? throw new InvalidDataException("Missing recovery response.");
    }

    internal static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate recovery field.");
                CheckJson(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJson(item);
        else if (value.ValueKind == JsonValueKind.String && !LedgerContract.ValidId(value.GetString()))
            throw new InvalidDataException("Invalid recovery identity or code.");
    }

    internal static RecoveryRecord Record(JsonElement value, string rig, string? night = null, string? configuration = null)
    {
        var record = Read<RecoveryRecord>(value);
        var state = record.Snapshot;
        if (record.Revision > 100000 || state.SchemaVersion != 1 || state.Identity.RigId != rig
            || night is not null && state.Identity.NightId != night
            || configuration is not null && state.Identity.ConfigurationId != configuration
            || state.Identity.StartsAtMs >= state.Identity.EndsAtMs || state.Identity.EndsAtMs > long.MaxValue
            || state.LastEventMs < state.Identity.StartsAtMs || state.LastEventMs > long.MaxValue
            || state.Policy.Revision == 0 || state.ProbesSpent > state.Policy.MaximumProbes
            || state.Failures.IsDefault || state.Failures.Length > 64)
            throw new InvalidDataException("Recovery snapshot identity or bounds mismatch.");
        return record;
    }

    internal static RecoveryResult<T> Decode<T>(JsonElement response, string status, Func<JsonElement, T> read) where T : class
    {
        try
        {
            CheckJson(response);
            if (response.GetProperty("status").GetString() == "error")
            {
                PipeProtocol.RequireFields(response, "status", "code");
                return new(null, PlannerContract.ParseEnum<RecoveryError>(response.GetProperty("code").GetString()));
            }
            if (response.GetProperty("status").GetString() != status) throw new InvalidDataException("Unexpected recovery response.");
            return new(read(response), null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Malformed recovery response.", error); }
    }
}
