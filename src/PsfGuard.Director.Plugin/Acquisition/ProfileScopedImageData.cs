using System.Windows.Media.Imaging;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Equipment.Model;
using Nito.AsyncEx;

namespace PsfGuard.Director.Plugin.Acquisition;

// NINA's queue chooses FileSaveInfo from the active profile at dequeue time.
// Keep its queue/events/writer, but bind writes to the capture's original profile.
internal sealed class ProfileScopedImageData(IImageData image, FileSaveInfo settings) : IImageData
{
    private readonly FileSaveInfo snapshot = Copy(settings);
    public IImageArray Data => image.Data;
    public ImageProperties Properties => image.Properties;
    public AsyncLazy<IImageStatistics> Statistics => image.Statistics;
    public IStarDetectionAnalysis StarDetectionAnalysis { get => image.StarDetectionAnalysis; set => image.StarDetectionAnalysis = value; }
    public ImageMetaData MetaData => image.MetaData;
    public void SetImageStatistics(IImageStatistics statistics) => image.SetImageStatistics(statistics);
    public IRenderedImage RenderImage() => image.RenderImage();
    public BitmapSource RenderBitmapSource() => image.RenderBitmapSource();
    public ImagePatterns GetImagePatterns() => image.GetImagePatterns();
    public Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, CancellationToken cancelToken = default, bool forceFileType = false) =>
        image.SaveToDisk(Copy(snapshot), cancelToken, forceFileType);
    public Task<string> SaveToDisk(FileSaveInfo fileSaveInfo, CancellationToken token, bool forceFileType, IList<ImagePattern> customPatterns) =>
        image.SaveToDisk(Copy(snapshot), token, forceFileType, customPatterns);

#pragma warning disable CS0612, CS0618
    [Obsolete("Use SaveToDisk.")]
    public Task<string> PrepareSave(FileSaveInfo fileSaveInfo, CancellationToken cancelToken = default) => image.PrepareSave(Copy(snapshot), cancelToken);
    [Obsolete("Use SaveToDisk.")]
    public string FinalizeSave(string file, string pattern, IList<ImagePattern> customPatterns) => image.FinalizeSave(file, snapshot.FilePattern, customPatterns);
#pragma warning restore CS0612, CS0618

    internal static FileSaveInfo Snapshot(IProfile profile)
    {
        var value = profile.ImageFileSettings;
        return new FileSaveInfo
        {
            FilePath = value.FilePath,
            FilePattern = value.GetFilePattern(CaptureSequence.ImageTypes.LIGHT),
            FileType = value.FileType,
            TIFFCompressionType = value.TIFFCompressionType,
            XISFCompressionType = value.XISFCompressionType,
            XISFChecksumType = value.XISFChecksumType,
            XISFByteShuffling = value.XISFByteShuffling,
            FITSCompressionType = value.FITSCompressionType,
            FITSAddFzExtension = value.FITSAddFzExtension,
            FITSUseLegacyWriter = value.FITSUseLegacyWriter,
            SaveNativeCameraRaw = profile.CameraSettings?.SaveNativeCameraRaw ?? true
        };
    }

    private static FileSaveInfo Copy(FileSaveInfo value) => new()
    {
        FilePath = value.FilePath,
        FilePattern = value.FilePattern,
        ForceExtension = value.ForceExtension,
        FileType = value.FileType,
        TIFFCompressionType = value.TIFFCompressionType,
        XISFCompressionType = value.XISFCompressionType,
        XISFChecksumType = value.XISFChecksumType,
        XISFByteShuffling = value.XISFByteShuffling,
        FITSCompressionType = value.FITSCompressionType,
        FITSAddFzExtension = value.FITSAddFzExtension,
        FITSUseLegacyWriter = value.FITSUseLegacyWriter,
        SaveNativeCameraRaw = value.SaveNativeCameraRaw
    };
}
