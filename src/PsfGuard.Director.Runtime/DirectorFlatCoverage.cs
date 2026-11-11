using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record FlatCoverageRecord(long SourceRowId, string Fingerprint, string? TargetGuid, string? TargetName,
    string ProfileId, long? LightSessionDate, long LightSessionId, long? FlatsTakenDate, string? FlatsType,
    string? FilterName, int? Gain, int? Offset, int? Bin, short? ReadoutMode, double? Rotation, double? Roi);
public sealed record FlatCoverageDecision(string RecordId, long SourceRowId, string Fingerprint, string? TargetGuid, string Reason);
public sealed record FlatCoverageAcknowledgement(string RecordId, long SourceRowId, string Fingerprint, string Status, string? Detail);

/// <summary>Completed flat batches and durable replacement requests. Never deletes image files.</summary>
public sealed class DirectorFlatCoverage
{
    private sealed record Batch(string Key, int Required, FlatCoverageRecord Record, string[] Images, bool Voided);
    private sealed record State(int Version, Batch[] Batches, FlatCoverageAcknowledgement[] Pending);
    private readonly CoordinatorStateFile file;
    private readonly Guid profile;
    public DirectorFlatCoverage(string root, Uri endpoint, CoordinatorBinding binding, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        profile = binding.ProfileId;
        file = new(root, new { origin = CoordinatorTransport.Normalize(endpoint, allowInsecureHttp).AbsoluteUri, binding }, "flat-coverage");
    }
    private State Read()
    {
        var state = file.Read<State>() ?? new(1, [], []);
        if (state.Version != 1 || state.Batches is null || state.Pending is null
            || state.Batches.Any(b => b.Record.ProfileId != profile.ToString("D") || b.Required is < 1 or > 1000 || b.Images is null)
            || state.Batches.Select(b => b.Record.SourceRowId).Distinct().Count() != state.Batches.Length)
            throw new InvalidDataException("Director flat history could not be read. Keep the history file and check the N.I.N.A. log.");
        return state;
    }
    public int Count(string key, int required) => Read().Batches.Where(b => b.Key == key && b.Required == required && !b.Voided)
        .Select(b => b.Images.Length).DefaultIfEmpty().Max();

    public void Saved(string key, int required, ExposureRecipe recipe, string filterName, double? rotation, string path)
    {
        if (required is < 1 or > 1000 || string.IsNullOrWhiteSpace(key) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Flat coverage requires an exact batch and saved image path.");
        using var lease = file.Lock();
        var state = Read();
        var batches = state.Batches.ToList();
        var index = batches.FindIndex(b => b.Key == key && b.Required == required && !b.Voided);
        if (index < 0)
        {
            // Stable positive ID within JSON's exact integer range; not a scheduler row.
            long id;
            do { id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & 0x1fffffffffffff; }
            while (id == 0 || batches.Any(b => b.Record.SourceRowId == id));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fingerprint = Hash(new { key, required, id, profile, recipe, rotation, created = now });
            batches.Add(new(key, required, new(id, fingerprint, null, null, profile.ToString("D"),
                now, 0, now, "Director panel", filterName, recipe.Gain, recipe.Offset, recipe.Binning.X, recipe.ReadoutMode, rotation, null), [], false));
            index = batches.Count - 1;
        }
        var batch = batches[index];
        var image = Hash(Path.GetFullPath(path));
        if (batch.Images.Contains(image) || batch.Images.Length >= required) return;
        batches[index] = batch with { Images = [.. batch.Images, image] };
        file.Write(state with { Batches = batches.ToArray() });
    }

    public FlatCoverageRecord[] Records() => Read().Batches.Where(b => b.Images.Length >= b.Required).Select(b => b.Record).ToArray();
    public FlatCoverageAcknowledgement[] Pending() => Read().Pending;

    public void Apply(IReadOnlyList<FlatCoverageDecision> decisions, bool acknowledge = true)
    {
        using var lease = file.Lock();
        var state = Read();
        var batches = state.Batches.ToArray();
        var pending = state.Pending.ToList();
        foreach (var decision in decisions)
        {
            if (!Guid.TryParseExact(decision.RecordId, "D", out var id) || id == Guid.Empty || decision.SourceRowId <= 0
                || decision.Fingerprint is not { Length: 64 } || decision.TargetGuid is not null)
                throw new InvalidDataException("PSF Guard returned an invalid flat replacement request.");
            var old = pending.SingleOrDefault(a => a.RecordId == decision.RecordId);
            if (old is not null)
            {
                if (old.SourceRowId != decision.SourceRowId || old.Fingerprint != decision.Fingerprint)
                    throw new InvalidDataException("A flat replacement request changed identity.");
                continue;
            }
            var index = Array.FindIndex(batches, b => b.Record.SourceRowId == decision.SourceRowId);
            var status = index < 0 ? "absent" : batches[index].Record.Fingerprint == decision.Fingerprint ? "removed" : "conflict";
            if (status == "removed") batches[index] = batches[index] with { Voided = true };
            if (acknowledge) pending.Add(new(decision.RecordId, decision.SourceRowId, decision.Fingerprint, status,
                status == "removed" ? "Director will take replacement flats. Image files were kept." : null));
        }
        // Persist replacement and acknowledgement together before any response to the server.
        file.Write(state with { Batches = batches, Pending = pending.ToArray() });
    }

    public void Acknowledged(IReadOnlyList<FlatCoverageAcknowledgement> sent)
    {
        using var lease = file.Lock();
        var state = Read();
        file.Write(state with { Pending = state.Pending.Except(sent).ToArray() });
    }
    public static string Hash<T>(T value) => HashEncoding.Lower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CoordinatorProgramContract.Options)));
}
