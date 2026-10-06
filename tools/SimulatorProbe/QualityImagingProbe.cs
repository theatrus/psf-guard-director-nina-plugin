using System.Reflection;
using System.Runtime.ExceptionServices;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace PsfGuard.Director.SimulatorProbe;

// Test-only metric injection after real ASCOM acquisition and NINA preparation.
// This exercises policy transitions, not real-sky cloud-detection accuracy.
public class QualityImagingProbe : DispatchProxy
{
    internal IImagingMediator Inner = null!;
    private readonly Dictionary<string, int> counts = [];
    private string? firstFilter;
    internal int Measurements { get; private set; }

    internal static (IImagingMediator Imaging, QualityImagingProbe Probe) Wrap(IImagingMediator inner)
    {
        var proxy = Create<IImagingMediator, QualityImagingProbe>();
        var probe = (QualityImagingProbe)proxy;
        probe.Inner = inner;
        return (proxy, probe);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) throw new InvalidOperationException();
        object? result;
        try { result = method.Invoke(Inner, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        return method.Name == nameof(IImagingMediator.PrepareImage) && args?[0] is IImageData image && result is Task<IRenderedImage> prepared
            ? Measure(prepared, image) : result;
    }

    private async Task<IRenderedImage> Measure(Task<IRenderedImage> pending, IImageData image)
    {
        var result = await pending;
        var filter = image.MetaData.FilterWheel.Filter ?? "none";
        firstFilter ??= filter;
        var n = counts[filter] = counts.GetValueOrDefault(filter) + 1;
        var poor = filter == firstFilter && n is 6 or 7;
        image.StarDetectionAnalysis = Mock.Of<IStarDetectionAnalysis>(s => s.HFR == 2 && s.Eccentricity == 0.4
            && s.HFRUnit == StarMeasurementUnit.Pixels && s.DetectedStars == (poor ? 25 : 100));
        image.SetImageStatistics(Mock.Of<IImageStatistics>(s => s.BitDepth == 16 && s.Median == (poor ? 2000 : 1000)));
        Measurements++;
        return result;
    }
}
