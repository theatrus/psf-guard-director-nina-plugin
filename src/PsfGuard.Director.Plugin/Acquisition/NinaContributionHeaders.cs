using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaContributionHeaders
{
    internal static void Write(IImageData image)
    {
        var headers = image.MetaData.GenericHeaders;
        headers.RemoveAll(header => header.Key is "PGGRMS" or "PGHFR");
        var rms = image.MetaData.Image.RecordedRMS;
        // NINA stores guider pixels. Its recorded guider scale, not the main
        // camera's plate scale, converts that exposure's RMS to arcseconds.
        if (rms is { DataPoints: >= 2, Scale: > 0 } && double.IsFinite(rms.Scale)
            && rms.Total >= 0 && double.IsFinite(rms.Total) && double.IsFinite(rms.Total * rms.Scale))
            headers.Add(new DoubleMetaDataHeader("PGGRMS", rms.Total * rms.Scale, "Recorded guiding RMS [arcsec]"));
        var stars = image.StarDetectionAnalysis;
        if (stars is not null && NinaCompatibility.Read(stars, "HFRUnit")?.ToString() == "Pixels"
            && stars.HFR > 0 && double.IsFinite(stars.HFR))
            headers.Add(new DoubleMetaDataHeader("PGHFR", stars.HFR, "Measured image HFR [pixels]"));
    }
}
