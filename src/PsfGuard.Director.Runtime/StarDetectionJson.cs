// Keep identical to theatrus/psf-guard-nina-plugin src/PsfGuard.Nina.Sync/StarDetection/StarDetectionJson.cs,
// apart from the namespace and using lines.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

/// <summary>
/// The JSON form of a star-detection settings report, as both PSF Guard
/// plugins send it: snake_case names and enums as strings.
/// </summary>
public static class StarDetectionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
