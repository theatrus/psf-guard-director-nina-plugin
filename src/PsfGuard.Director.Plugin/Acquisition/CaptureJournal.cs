using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record CaptureIntent(Guid CaptureId, Guid ProfileId, string RigId, string ConfigurationId,
    string AssignmentId, ulong AssignmentRevision, string GoalId, string CameraDeviceId,
    double ExposureSeconds, string TargetName, double RaDegrees, double DecDegrees,
    double PositionAngle, short BinX = 1, short BinY = 1, int Gain = -1, int Offset = -1);

internal enum CapturePhase { Reserved, Capturing, Downloaded, SaveQueued, Saved, Failed, Interrupted, SaveUncertain, CaptureUncertain }
internal sealed record CaptureDestination(string Directory, string Pattern, string Format);

internal sealed record CaptureEvidence(int SchemaVersion, CaptureIntent Intent, CaptureDestination Destination, CapturePhase Phase,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, int? NinaImageId = null, string? SavedPath = null,
    double? CaptureAndDownloadMs = null, double? ProcessingAndSaveMs = null,
    double? TotalMs = null, string? ErrorType = null, string? Filter = null);

// One immutable attempt identity per file. Existing attempts are never overwritten
// by a retry, even if their last observed outcome was unsuccessful or ambiguous.
internal sealed class CaptureJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        WriteIndented = true
    };
    private readonly string path;
    internal CaptureEvidence Evidence { get; private set; }

    internal CaptureJournal(string root, CaptureIntent intent, CaptureDestination destination, DateTimeOffset now)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Journal root must be absolute.", nameof(root));
        Validate(intent);
        Validate(destination);
        var directory = Path.Combine(root, intent.ProfileId.ToString("N"));
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, $"{intent.CaptureId:N}.json");
        Evidence = new(1, intent, destination, CapturePhase.Reserved, now, now);
        Write(Evidence, overwrite: false);
    }

    internal static void Validate(CaptureIntent intent)
    {
        static bool Id(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128
            && value.All(c => c is >= '!' and <= '~');
        if (intent.CaptureId == Guid.Empty || intent.ProfileId == Guid.Empty
            || !Id(intent.RigId) || !Id(intent.ConfigurationId) || !Id(intent.AssignmentId) || !Id(intent.GoalId)
            || intent.AssignmentRevision == 0 || string.IsNullOrWhiteSpace(intent.CameraDeviceId)
            || intent.CameraDeviceId.Length > 1024 || intent.CameraDeviceId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(intent.TargetName)
            || intent.TargetName.Length > 256 || intent.TargetName.Any(char.IsControl)
            || !double.IsFinite(intent.ExposureSeconds) || intent.ExposureSeconds <= 0 || intent.ExposureSeconds > 86400
            || !double.IsFinite(intent.RaDegrees) || intent.RaDegrees is < 0 or >= 360
            || !double.IsFinite(intent.DecDegrees) || intent.DecDegrees is < -90 or > 90
            || !double.IsFinite(intent.PositionAngle) || intent.PositionAngle is < 0 or >= 360
            || intent.BinX is < 1 or > 16 || intent.BinY is < 1 or > 16
            || intent.Gain < -1 || intent.Offset < -1)
            throw new ArgumentException("Invalid capture intent.", nameof(intent));
    }

    internal void Record(CaptureEvidence evidence)
    {
        if (evidence.Intent != Evidence.Intent || evidence.Destination != Evidence.Destination
            || evidence.SchemaVersion != Evidence.SchemaVersion || evidence.StartedAt != Evidence.StartedAt)
            throw new InvalidOperationException("Capture identity cannot change.");
        var validTransition = (Evidence.Phase, evidence.Phase) switch
        {
            (CapturePhase.Reserved, CapturePhase.Capturing or CapturePhase.Failed or CapturePhase.Interrupted) => true,
            (CapturePhase.Capturing, CapturePhase.Downloaded or CapturePhase.CaptureUncertain) => true,
            (CapturePhase.Downloaded, CapturePhase.SaveQueued or CapturePhase.Failed or CapturePhase.Interrupted) => true,
            (CapturePhase.SaveQueued, CapturePhase.Saved or CapturePhase.Failed or CapturePhase.SaveUncertain) => true,
            _ => false
        };
        if (!validTransition) throw new InvalidOperationException("Invalid capture evidence transition.");
        Write(evidence, overwrite: true);
        Evidence = evidence;
    }

    private void Write(CaptureEvidence evidence, bool overwrite)
    {
        Validate(evidence);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions);
        if (bytes.Length > 65536) throw new InvalidDataException("Capture journal exceeds its size limit.");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static CaptureEvidence Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 65536) throw new InvalidDataException("Capture journal exceeds its size limit.");
        var evidence = JsonSerializer.Deserialize<CaptureEvidence>(file, JsonOptions)
            ?? throw new InvalidDataException("Empty capture journal.");
        Validate(evidence);
        return evidence;
    }

    private static void Validate(CaptureEvidence evidence)
    {
        if (evidence.SchemaVersion != 1 || evidence.Intent is null || evidence.Destination is null || !Enum.IsDefined(evidence.Phase))
            throw new InvalidDataException("Unsupported capture journal.");
        Validate(evidence.Intent);
        Validate(evidence.Destination);
        if (new[] { evidence.CaptureAndDownloadMs, evidence.ProcessingAndSaveMs, evidence.TotalMs }
            .Any(value => value is { } number && (!double.IsFinite(number) || number < 0)))
            throw new InvalidDataException("Invalid capture timing.");
        if (evidence.Phase == CapturePhase.Saved && (evidence.NinaImageId is null or < 0
            || string.IsNullOrEmpty(evidence.SavedPath) || !Path.IsPathFullyQualified(evidence.SavedPath)
            || evidence.CaptureAndDownloadMs is null || evidence.ProcessingAndSaveMs is null || evidence.TotalMs is null))
            throw new InvalidDataException("Saved capture has incomplete evidence.");
    }

    private static void Validate(CaptureDestination destination)
    {
        if (string.IsNullOrEmpty(destination.Directory) || !Path.IsPathFullyQualified(destination.Directory)
            || destination.Directory.Length > 16384 || string.IsNullOrWhiteSpace(destination.Pattern)
            || destination.Pattern.Length > 16384 || destination.Format is not ("FITS" or "XISF"))
            throw new ArgumentException("Invalid capture destination.", nameof(destination));
    }
}
