using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

// Complete immutable inputs only. Geometry validation and computation belong to Rust.
public sealed record DirectorConstraints(uint SchemaVersion, DirectorRigConstraints Rig, ImmutableArray<DirectorGoalLimits> Goals);
public sealed record DirectorRigConstraints(string RigId, string ConfigurationId, ulong Revision,
    DirectorSite Site, DirectorEarthOrientation Orientation, DirectorHorizon Horizon,
    double MinimumAltitudeDegrees, double MaximumAltitudeDegrees, PlannerMeridianExclusion MeridianExclusion);
public sealed record DirectorSite(double LatitudeDegrees, double LongitudeDegrees, double ElevationMeters);
public sealed record DirectorEarthOrientation(double Ut1MinusUtcSeconds, double PolarMotionXRadians, double PolarMotionYRadians,
    ulong ValidFromMs, ulong ValidUntilMs);
public sealed record DirectorGoalLimits(string GoalId, double MinimumAltitudeDegrees, double MaximumAltitudeDegrees, double HorizonOffsetDegrees);
public readonly record struct DirectorHorizonPoint(double AzimuthDegrees, double AltitudeDegrees);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(DirectorHorizon.FixedMinimum), "fixed_minimum")]
[JsonDerivedType(typeof(DirectorHorizon.Custom), "custom")]
public abstract record DirectorHorizon
{
    public sealed record FixedMinimum : DirectorHorizon;
    public sealed record Custom(ImmutableArray<DirectorHorizonPoint> Points) : DirectorHorizon;
}

internal static class GeometryContract
{
    internal const uint Version = 1;

    internal static JsonNode Encode(DirectorConstraints constraints, string rigId)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(constraints.Rig);
        if (constraints.Rig.RigId != rigId) throw new ArgumentException("Geometry constraints belong to another rig.");
        return ProgramContract.Encode(constraints);
    }
}
