using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

// Immutable wire snapshots. Capability validation and scheduling remain in Rust.
public sealed record DirectorProgram(uint SchemaVersion, PlannerAssignment Assignment, DirectorConfiguration Configuration,
    ImmutableArray<DirectorTarget> Targets, ImmutableArray<ExposureRecipe> Recipes, ImmutableArray<GoalBinding> Bindings);
public sealed record DirectorTarget(string Id, string Name, uint IcrsRaMas, int IcrsDecMas, uint? PositionAngleMas);
public readonly record struct CameraBinning(short X, short Y);
public sealed record DirectorFilter(string Id, short? Position);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "support")]
[JsonDerivedType(typeof(CameraControl.Unsupported), "unsupported")]
[JsonDerivedType(typeof(CameraControl.Range), "range")]
[JsonDerivedType(typeof(CameraControl.Values), "values")]
public abstract record CameraControl
{
    public sealed record Unsupported : CameraControl;
    public sealed record Range(int Minimum, int Maximum) : CameraControl;
    public sealed record Values([property: JsonPropertyName("values")] ImmutableArray<int> Items) : CameraControl;
}
public sealed record DirectorConfiguration(string RigId, string Id, string CameraId, string? FilterWheelId,
    ImmutableArray<DirectorFilter> Filters, ImmutableArray<CameraBinning> BinningModes, ImmutableArray<short> ReadoutModes,
    CameraControl Gain, CameraControl Offset, ulong ExposureMinMs, ulong ExposureMaxMs, bool EnableSlewCenter, uint DitherEvery);
public sealed record ExposureRecipe(string Id, ulong ExposureMs, string FilterId, CameraBinning Binning,
    int? Gain, int? Offset, short ReadoutMode, uint? DitherOverride);
public sealed record GoalBinding(string GoalId, string TargetId, string RecipeId);
public sealed record DirectorPointing(string ConfigurationId, DirectorTarget Target);
public sealed record ProgramLocalState(DirectorConfiguration Configuration, DirectorPointing? PreviousPointing,
    bool MountParked, bool RotatorConnected, uint FilterExposuresSinceDither);
/// <summary>Saved settings and evidence only, never a hardware dispatch permit.</summary>
public sealed record CaptureBinding(LedgerIdentity Ledger, LedgerAttempt Attempt, DirectorTarget Target,
    ExposureRecipe Recipe, DirectorConfiguration Configuration);
public sealed record CaptureBindingLookup(CaptureBinding? Binding);

internal static class ProgramContract
{
    internal const uint Version = 1;

    internal static JsonNode Encode<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try { return JsonSerializer.SerializeToNode(value, PlannerContract.Options)!; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { throw new ArgumentException("Program snapshot cannot be serialized.", nameof(value), error); }
    }

    internal static void CheckRig(DirectorConfiguration configuration, string rigId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.RigId != rigId) throw new ArgumentException("Program configuration belongs to another rig.");
    }

    private static (DirectorTarget Target, ExposureRecipe Recipe) Resolve(DirectorProgram program, string goalId)
    {
        var matches = program.Bindings.Where(b => b.GoalId == goalId).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Program goal binding is missing or ambiguous.");
        var targets = program.Targets.Where(t => t.Id == matches[0].TargetId).ToArray();
        var recipes = program.Recipes.Where(r => r.Id == matches[0].RecipeId).ToArray();
        if (targets.Length != 1 || recipes.Length != 1) throw new InvalidDataException("Program target or recipe is missing or ambiguous.");
        return (targets[0], recipes[0]);
    }

    internal static CaptureBinding ReadBinding(JsonElement value, DirectorProgram program, LedgerIdentity identity, string captureId)
    {
        PipeProtocol.RequireFields(value, "ledger", "attempt", "target", "recipe", "configuration");
        var ledger = LedgerContract.ReadIdentity(value.GetProperty("ledger"), program.Assignment);
        if (ledger != identity) throw new InvalidDataException("Capture binding belongs to another ledger.");
        var attempt = LedgerContract.ReadAttempt(value.GetProperty("attempt"), program.Assignment, captureId);
        var resolved = Resolve(program, attempt.GoalId);
        RequireExact(value.GetProperty("target"), JsonSerializer.SerializeToElement(resolved.Target, PlannerContract.Options));
        RequireExact(value.GetProperty("recipe"), JsonSerializer.SerializeToElement(resolved.Recipe, PlannerContract.Options));
        RequireExact(value.GetProperty("configuration"), JsonSerializer.SerializeToElement(program.Configuration, PlannerContract.Options));
        return new(ledger, attempt, resolved.Target, resolved.Recipe, program.Configuration);
    }

    // Validate every field against the opened immutable snapshot. Unlike record
    // equality, this compares immutable-array contents and rejects duplicate keys.
    private static void RequireExact(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind) throw new InvalidDataException("Program field type mismatch.");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var fields = expected.EnumerateObject().ToArray();
                PipeProtocol.RequireFields(actual, fields.Select(f => f.Name).ToArray());
                foreach (var field in fields) RequireExact(actual.GetProperty(field.Name), field.Value);
                break;
            case JsonValueKind.Array:
                if (actual.GetArrayLength() != expected.GetArrayLength()) throw new InvalidDataException("Program array mismatch.");
                for (var i = 0; i < expected.GetArrayLength(); i++) RequireExact(actual[i], expected[i]);
                break;
            case JsonValueKind.Number:
                if (expected.TryGetUInt64(out var unsigned))
                {
                    if (!actual.TryGetUInt64(out var other) || other != unsigned) throw new InvalidDataException("Program integer mismatch.");
                }
                else if (!expected.TryGetInt64(out var signed) || !actual.TryGetInt64(out var other) || signed != other)
                    throw new InvalidDataException("Program integer mismatch.");
                break;
            case JsonValueKind.String:
                if (actual.GetString() != expected.GetString()) throw new InvalidDataException("Program text mismatch.");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                break;
            default: throw new InvalidDataException("Invalid program field.");
        }
    }

    internal static void CheckCommand(PreparationCommand command, DirectorProgram program)
    {
        var (target, recipe) = Resolve(program, command.GoalId);
        if (command.TargetId != target.Id || command.RecipeId != recipe.Id)
            throw new InvalidDataException("Preparation command differs from its program binding.");
        if (command.Operation is PreparationOperation.SwitchFilter filter && filter.FilterId != recipe.FilterId
            || command.Operation is PreparationOperation.SetReadoutMode mode && mode.Mode != recipe.ReadoutMode
            || command.Operation is PreparationOperation.Center center && (center.Rotate != target.PositionAngleMas.HasValue || !program.Configuration.EnableSlewCenter))
            throw new InvalidDataException("Preparation settings differ from the program.");
    }

    internal static void CheckRecord(PreparationRecord record, DirectorProgram program)
    {
        Resolve(program, record.GoalId);
        if (record.Pending is not null) CheckCommand(record.Pending, program);
        foreach (var observation in record.Observations) CheckCommand(observation.Command, program);
    }

    internal static void CheckNext(PreparationNext next, DirectorProgram program)
    {
        if (next is PreparationNext.Run run) CheckCommand(run.Command, program);
        if (next is PreparationNext.ReadyToReserve ready) Resolve(program, ready.GoalId);
    }

    internal static void CheckPage(PreparationEventPage page, DirectorProgram program)
    {
        foreach (var item in page.Events)
        {
            switch (item.Data)
            {
                case PreparationEventData.Started started:
                    var (target, recipe) = Resolve(program, started.Context.GoalId);
                    var context = started.Context;
                    if (context.TargetId != target.Id || context.RecipeId != recipe.Id || context.FilterId != recipe.FilterId
                        || context.ReadoutMode != recipe.ReadoutMode || context.DitherOverride != recipe.DitherOverride
                        || context.DitherEvery != program.Configuration.DitherEvery || context.EnableSlewCenter != program.Configuration.EnableSlewCenter)
                        throw new InvalidDataException("Preparation event differs from its program binding.");
                    break;
                case PreparationEventData.Issued issued: CheckCommand(issued.Command, program); break;
                case PreparationEventData.Completed completed: CheckCommand(completed.Observation.Command, program); break;
            }
        }
    }
}
