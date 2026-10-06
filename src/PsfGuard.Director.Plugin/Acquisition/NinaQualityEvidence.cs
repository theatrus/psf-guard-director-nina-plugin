using System.Security.Cryptography;
using System.Text.Json;
using NINA.Core.Enum;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaQualityEvidence
{
    internal static string SettingsFingerprint(IProfileService profiles) => Hash(new
    {
        profiles.ActiveProfile.ImageSettings.StarSensitivity,
        profiles.ActiveProfile.ImageSettings.NoiseReduction
    });

    internal static QualityFrame Read(CaptureIntent intent, IImageData image, IImageStatistics statistics,
        string settings, ulong observedAtMs)
    {
        var analysis = image.StarDetectionAnalysis;
        var context = intent.Program ?? throw new InvalidOperationException("Quality evidence needs a bound recipe.");
        return new(intent.CaptureId.ToString("D"), observedAtMs,
            new(intent.RigId, intent.ConfigurationId, context.TargetId,
                Hash(new { context.FilterId, intent.ExposureSeconds, intent.BinX, intent.BinY, intent.Gain, intent.Offset, context.ReadoutMode }),
                Hash(new
                {
                    settings,
                    Detector = analysis?.GetType().AssemblyQualifiedName,
                    statistics.BitDepth,
                    HFRUnit = analysis is null ? null : NinaCompatibility.Read(analysis, "HFRUnit"),
                    Units = "native-adu-pixels-v1"
                }),
                checked((uint)image.Properties.Width), checked((uint)image.Properties.Height)),
            new(analysis?.DetectedStars is >= 0 ? (uint)analysis.DetectedStars : null,
                analysis is not null && NinaCompatibility.Read(analysis, "HFRUnit")?.ToString() == "Pixels" ? Positive(analysis.HFR) : null,
                statistics.BitDepth is > 0 and <= 16 ? Positive(statistics.Median) : null,
                analysis is not null && NinaCompatibility.Read(analysis, "Eccentricity") is double e and >= 0 and < 1 ? e : null));
    }

    private static double? Positive(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;
    private static string Hash(object value) => HashEncoding.Lower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
