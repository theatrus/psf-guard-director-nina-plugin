using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorBinding(Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId, Guid ProfileId);
public sealed record CoordinatorGoalLink(string GoalId, Guid ProjectId, string ProjectName, ulong ActivationRevision,
    Guid ObjectiveId, Guid ContributionId, string PanelId, Guid SourceProjectGuid, Guid TargetGuid,
    Guid ExposureplanGuid, string BandpassId, string Purpose);
public sealed record CoordinatorLimits(double MinimumAltitudeDegrees, double MaximumAltitudeDegrees,
    PlannerMeridianExclusion MeridianExclusion);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(CoordinatorRotation.Fixed), "fixed")]
[JsonDerivedType(typeof(CoordinatorRotation.Manual), "manual")]
[JsonDerivedType(typeof(CoordinatorRotation.Rotator), "rotator")]
public abstract record CoordinatorRotation
{
    public sealed record Fixed(double AngleDegrees) : CoordinatorRotation;
    public sealed record Manual(double AngleDegrees) : CoordinatorRotation;
    public sealed record Rotator : CoordinatorRotation;
}
public sealed record CoordinatorRigContext(ulong ProfileRevision, DirectorSite? Site, DirectorHorizon Horizon,
    CoordinatorLimits Limits, CoordinatorRotation? Rotation);
public sealed record CoordinatorProgramEnvelope(Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId,
    string Revision, ulong IssuedAtMs, DirectorProgram Program, ImmutableArray<CoordinatorGoalLink> Links,
    CoordinatorRigContext Rig, ImmutableArray<string> Omitted);

/// <summary>Inspected compiler output, not an issued allocation or permission to acquire.</summary>
public sealed class CoordinatorProgramPreview
{
    public CoordinatorBinding Binding { get; }
    public CoordinatorProgramEnvelope Envelope { get; }
    public string ETag { get; }
    internal string Origin { get; }
    internal string Fingerprint { get; }

    internal CoordinatorProgramPreview(CoordinatorBinding binding, CoordinatorProgramEnvelope envelope,
        string etag, string origin, string fingerprint) =>
        (Binding, Envelope, ETag, Origin, Fingerprint) = (binding, envelope, etag, origin, fingerprint);
}

internal static class CoordinatorProgramContract
{
    internal const int MaximumBytes = 1048576;
    internal static readonly JsonSerializerOptions Options = new(PlannerContract.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 32
    };

    internal static CoordinatorProgramPreview Read(byte[] bytes, string etag, string origin,
        CoordinatorBinding binding, DirectorConfiguration expected, ulong now, CoordinatorProgramPreview? previous)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            CheckTree(root);
            PipeProtocol.RequireFields(root, "success", "data", "error", "status");
            if (root.GetProperty("success").ValueKind != JsonValueKind.True
                || root.GetProperty("error").ValueKind != JsonValueKind.Null
                || root.GetProperty("status").GetString() != "ready") throw new InvalidDataException();
            var envelope = root.GetProperty("data").Deserialize<CoordinatorProgramEnvelope>(Options)
                ?? throw new InvalidDataException();
            if (envelope.CoordinatorInstanceId != binding.CoordinatorInstanceId || envelope.CatalogId != binding.CatalogId
                || envelope.RigId != binding.RigId) throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
            if (envelope.Revision.Length != 64 || envelope.Revision.Any(c => !char.IsAsciiHexDigitLower(c))
                || etag != $"\"{envelope.Revision}\"") throw new InvalidDataException();
            var program = envelope.Program;
            var assignment = program.Assignment;
            if (program.SchemaVersion != ProgramContract.Version) throw new CoordinatorIntakeException(CoordinatorIntakeFailure.UnsupportedProgram);
            if (assignment.RigId != binding.RigId.ToString("D") || program.Configuration.RigId != assignment.RigId
                || assignment.ConfigurationId != expected.Id
                || JsonSerializer.Serialize(program.Configuration, Options) != JsonSerializer.Serialize(expected, Options))
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ConfigurationMismatch);
            if (assignment.Revision == 0 || assignment.ValidFromMs >= assignment.ExpiresAtMs
                || envelope.IssuedAtMs < assignment.ValidFromMs || envelope.IssuedAtMs > now
                || now < assignment.ValidFromMs || now >= assignment.ExpiresAtMs)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ExpiredOrFutureProgram);
            if (JsonSerializer.SerializeToUtf8Bytes(program, Options).Length > PlannerContract.MaxRequestBytes)
                throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ResponseTooLarge);
            CheckLinks(envelope);
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope, Options)));
            if (previous is not null)
            {
                if (previous.Binding != binding || previous.Origin != origin)
                    throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
                var prior = previous.Envelope;
                if ((prior.Revision == envelope.Revision || prior.Program.Assignment.Id == assignment.Id
                    && prior.Program.Assignment.Revision == assignment.Revision) && previous.Fingerprint != fingerprint)
                    throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ChangedImmutableProgram);
            }
            return new(binding, envelope, etag, origin, fingerprint);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or NotSupportedException or OverflowException or FormatException)
        {
            // Server-controlled text and JSON exception paths never reach logs/UI.
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.MalformedResponse);
        }
    }

    internal static void CheckTree(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                CheckTree(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new InvalidDataException();
                CheckTree(item);
            }
    }

    private static void CheckLinks(CoordinatorProgramEnvelope envelope)
    {
        var program = envelope.Program;
        static HashSet<string> Ids(IEnumerable<string> source)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in source)
                if (string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(c => c < '!' || c > '~') || !ids.Add(id))
                    throw new InvalidDataException();
            return ids;
        }
        var goals = Ids(program.Assignment.Goals.Select(g => g.Id));
        var targets = Ids(program.Targets.Select(t => t.Id));
        var recipes = Ids(program.Recipes.Select(r => r.Id));
        if (goals.Count == 0 || !goals.SetEquals(Ids(program.Bindings.Select(b => b.GoalId)))
            || !goals.SetEquals(Ids(envelope.Links.Select(l => l.GoalId)))) throw new InvalidDataException();
        foreach (var binding in program.Bindings)
            if (!targets.Contains(binding.TargetId) || !recipes.Contains(binding.RecipeId)) throw new InvalidDataException();
        foreach (var link in envelope.Links)
        {
            if (link.ProjectId == Guid.Empty || link.ObjectiveId == Guid.Empty || link.ContributionId == Guid.Empty
                || link.SourceProjectGuid == Guid.Empty || link.TargetGuid == Guid.Empty || link.ExposureplanGuid == Guid.Empty
                || link.ActivationRevision == 0 || !Guid.TryParse(link.GoalId, out var goal) || goal != link.ExposureplanGuid)
                throw new InvalidDataException();
            var binding = program.Bindings.Single(b => b.GoalId == link.GoalId);
            if (binding.TargetId != $"target-{link.TargetGuid:D}" || binding.RecipeId != $"recipe-{link.ContributionId:D}")
                throw new InvalidDataException();
        }
    }
}
