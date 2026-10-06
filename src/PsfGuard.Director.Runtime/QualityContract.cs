using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

public sealed record QualityFrameContext(string RigId, string ConfigurationId, string TargetId,
    string RecipeFingerprint, string AnalysisFingerprint, uint Width, uint Height);
public sealed record QualityMetrics(uint? Stars, double? HfrPixels, double? BackgroundAdu, double? Eccentricity);
public sealed record QualityFrame(string CaptureId, ulong ObservedAtMs, QualityFrameContext Context, QualityMetrics Metrics);
public sealed record QualityReference(string Id, bool Approved, QualityFrame Frame,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImmutableArray<QualityFrame>? InitialGroup = null);
public sealed record QualityPolicy(uint MinimumReferenceStars = 20, double PoorStarRatio = 0.5,
    double GoodStarRatio = 0.85, double PoorBackgroundRatio = 1.5, double GoodBackgroundRatio = 1.2,
    double MaximumHfrRatio = 1.3, double MaximumEccentricity = 0.65,
    ulong EvidenceMaxAgeMs = 30000, ulong ReferenceMaxAgeMs = 21600000);
public enum QualityReason
{
    ReferenceUnapproved, IncompatibleContext, StaleEvidence, ReferenceExpired, SameCapture,
    MissingMetrics, InsufficientReference, FocusOrTracking, Ambiguous,
    StarLossAndBackgroundRise, ConsistentWithReference
}
/// <summary>Screening evidence only, not a grade, reservation or equipment permit.</summary>
public sealed record QualityAssessment(RecoveryVerdict Verdict, QualityReason Reason,
    double? StarRatio, double? BackgroundRatio, double? HfrRatio, bool ReferenceQualityUnknown);
public enum RestartBoundary { Unused, Settled, Unresolved, Unknown }
public enum RestartAdvice
{
    OperatorReviewRequired, WrongScope, ClockReversed, NightEnded, HoldExpired, TerminalStop,
    UncertainRecovery, UnsettledExecution, EquipmentNotQuiescent, WaitForSafety, RequestFreshAuthority
}
public sealed record RestartReview(string RigId, string ConfigurationId, string NightId, ulong NowMs,
    bool OperatorRequested, RestartBoundary Boundary, bool CameraIdle, bool MountStopped, bool GuiderStopped,
    PlannerSafety Safety, RecoveryMotion Motion);
/// <summary>RequestFreshAuthority cannot authorize replay or clear a persisted latch.</summary>
public sealed record RestartReviewed(RestartAdvice Advice, RecoveryRecord Record);
