using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Moq;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.SimulatorProbe;

// Native exposures and files, with controlled flat illumination statistics.
// This checks orchestration; it does not validate a physical panel's light output.
public class CalibrationProbe : DispatchProxy, IDisposable
{
    private IImagingMediator inner = null!;
    private IFlatDeviceMediator panel = null!;
    private IImageSaveMediator saves = null!;
    internal ConcurrentQueue<(string Type, string Path)> Saved { get; } = new();
    internal ConcurrentQueue<string> Captured { get; } = new();
    internal static (IImagingMediator, CalibrationProbe) Wrap(IImagingMediator imaging, IFlatDeviceMediator panel, IImageSaveMediator saves)
    {
        var proxy = Create<IImagingMediator, CalibrationProbe>();
        var probe = (CalibrationProbe)proxy;
        probe.inner = imaging; probe.panel = panel; probe.saves = saves;
        saves.ImageSaved += probe.OnSaved;
        return (proxy, probe);
    }

    internal static void Configure(DirectorSessionOptions options)
    {
        options.ScheduledFlats = true;
        options.FlatsDuskMinutes = 720;
        options.FlatsPerFilter = 1;
        options.FlatMinimumSeconds = .1;
        options.FlatMaximumSeconds = 1;
        options.DarksWhileIdle = true;
        options.DarkExposureSeconds = "2";
        options.DarksPerSetting = 1;
        options.ClosePanelAtEnd = true;
    }

    private void OnSaved(object? sender, ImageSavedEventArgs args)
    {
        if (args.MetaData.GenericHeaders.Any(h => h.Key == "PGCALID"))
        {
            if (args.MetaData.GenericHeaders.Any(h => h.Key == "PGCAPID")) throw new InvalidDataException("Calibration counted as science.");
            Saved.Enqueue((args.MetaData.Image.ImageType, args.PathToImage.LocalPath));
        }
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.Name == nameof(IImagingMediator.CaptureImage) && args?[0] is CaptureSequence sequence)
        {
            var info = panel.GetInfo();
            var flat = sequence.ImageType == CaptureSequence.ImageTypes.FLAT;
            var calibration = flat || sequence.ImageType == CaptureSequence.ImageTypes.DARK;
            if (!info.Connected || info.DeviceId != "ASCOM.OmniSim.CoverCalibrator"
                || info.CoverState != (calibration ? CoverState.Closed : CoverState.Open) || info.LightOn != flat)
                throw new InvalidDataException($"Incorrect panel state before {sequence.ImageType}: {info.CoverState}, light {info.LightOn}.");
            Captured.Enqueue(sequence.ImageType);
        }
        object? result;
        try { result = method.Invoke(inner, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        return method.Name == nameof(IImagingMediator.PrepareImage) && args?[0] is IImageData image
            && image.MetaData.Image.ImageType == CaptureSequence.ImageTypes.FLAT && result is Task<IRenderedImage> task
            ? Measure(task, image) : result;
    }

    private static async Task<IRenderedImage> Measure(Task<IRenderedImage> task, IImageData image)
    {
        var result = await task;
        var half = (Math.Pow(2, image.Properties.BitDepth) - 1) / 2;
        image.SetImageStatistics(Mock.Of<IImageStatistics>(s => s.Mean == half && s.Median == half && s.BitDepth == image.Properties.BitDepth));
        return result;
    }

    internal async Task VerifyAsync(string directory, IImageDataFactory images, CancellationToken token)
    {
        if (Saved.Count(s => s.Type == "FLAT") != 2 || Saved.Count(s => s.Type == "DARK") != 2)
            throw new InvalidDataException("Expected two active-plan flats and two dark lengths.");
        foreach (var (kind, path) in Saved)
        {
            var image = await images.CreateFromFile(path, 16, false, NINA.Core.Enum.RawConverterEnum.FREEIMAGE, token);
            if (image.MetaData.Image.ImageType != kind || !image.MetaData.GenericHeaders.Any(h => h.Key == "PGCALID")
                || image.MetaData.GenericHeaders.Any(h => h.Key == "PGCAPID")) throw new InvalidDataException("Calibration FITS metadata mismatch.");
        }
        if (panel.GetInfo().CoverState != CoverState.Closed || panel.GetInfo().LightOn)
            throw new InvalidDataException("Director did not close and extinguish the panel at the end.");
        await File.WriteAllLinesAsync(Path.Combine(directory, "calibration-verified.txt"), Captured, token);
    }

    public void Dispose() => saves.ImageSaved -= OnSaved;
}
