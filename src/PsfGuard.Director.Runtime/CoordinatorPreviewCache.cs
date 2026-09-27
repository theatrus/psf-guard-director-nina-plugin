using System.Text.Json;

namespace PsfGuard.Director.Runtime;

/// <summary>Durable inspection history. An expired preview can detect immutable revision drift, but never authorize work.</summary>
public sealed class CoordinatorPreviewCache
{
    private sealed record Entry(int SchemaVersion, string Origin, CoordinatorBinding Binding, string Fingerprint,
        CoordinatorProgramEnvelope Envelope);
    private readonly CoordinatorStateFile file;
    private readonly string origin;
    private readonly CoordinatorBinding binding;
    private readonly DirectorConfiguration configuration;

    public CoordinatorPreviewCache(string root, Uri endpoint, CoordinatorBinding binding, DirectorConfiguration configuration,
        bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        origin = CoordinatorTransport.Normalize(endpoint, allowInsecureHttp).AbsoluteUri;
        this.binding = binding;
        this.configuration = configuration;
        file = new(root, new { origin, binding, configuration }, "preview");
    }

    /// <summary>Historical preview, including expired output. Fetch fresh output before displaying it as current.</summary>
    public CoordinatorProgramPreview? ReadPrevious()
    {
        var entry = file.Read<Entry>();
        if (entry is null) return null;
        if (entry.SchemaVersion != 1 || entry.Origin != origin || entry.Binding != binding)
            throw new InvalidDataException("Director preview cache identity mismatch.");
        var result = Decode(entry.Envelope, previous: null);
        if (result.Fingerprint != entry.Fingerprint) throw new InvalidDataException("Director preview cache integrity mismatch.");
        return result;
    }

    public void Store(CoordinatorProgramPreview preview)
    {
        if (preview.Origin != origin || preview.Binding != binding)
            throw new CoordinatorIntakeException(CoordinatorIntakeFailure.IdentityMismatch);
        using var lease = file.Lock();
        var checkedPreview = Decode(preview.Envelope, ReadPrevious());
        file.Write(new Entry(1, origin, binding, checkedPreview.Fingerprint, checkedPreview.Envelope));
    }

    private CoordinatorProgramPreview Decode(CoordinatorProgramEnvelope envelope, CoordinatorProgramPreview? previous)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { success = true, data = envelope, error = (string?)null, status = "ready" },
            CoordinatorProgramContract.Options);
        // Validate at issuance to retain expired history, without treating it as a current allocation.
        return CoordinatorProgramContract.Read(bytes, $"\"{envelope.Revision}\"", origin, binding, configuration, envelope.IssuedAtMs, previous);
    }
}
