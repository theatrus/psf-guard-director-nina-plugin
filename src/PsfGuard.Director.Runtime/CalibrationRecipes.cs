namespace PsfGuard.Director.Runtime;

// A calibration recipe is an image format, not a target assignment or a light-exposure permit.
public sealed record DarkRecipe(ulong ExposureMs, CameraBinning Binning, int? Gain, int? Offset, short ReadoutMode, double? Temperature);

public static class CalibrationRecipes
{
    public static IReadOnlyList<DarkRecipe> Darks(IEnumerable<ExposureRecipe> active, ExposureRecipe fallback,
        bool includePlanExposures, IReadOnlyList<double> extraSeconds, IReadOnlyList<double> temperatures)
    {
        var recipes = active.ToArray();
        var modes = (recipes.Length == 0 ? [fallback] : recipes)
            .Select(r => (r.Binning, r.Gain, r.Offset, r.ReadoutMode)).Distinct().ToArray();
        var exposures = includePlanExposures
            ? recipes.Select(r => new DarkRecipe(r.ExposureMs, r.Binning, r.Gain, r.Offset, r.ReadoutMode, null)).ToList()
            : [];
        foreach (var seconds in extraSeconds)
        {
            if (!double.IsFinite(seconds) || seconds is < 0.001 or > 7200) throw new ArgumentException("Invalid dark exposure length.");
            foreach (var mode in modes)
                exposures.Add(new(checked((ulong)Math.Round(seconds * 1000)), mode.Binning, mode.Gain, mode.Offset, mode.ReadoutMode, null));
        }
        var steps = temperatures.Count == 0 ? new double?[] { null } : temperatures.Select(t => (double?)t).ToArray();
        if (steps.Any(t => t is { } n && (!double.IsFinite(n) || n is < -80 or > 40))) throw new ArgumentException("Invalid dark temperature.");
        return steps.SelectMany(t => exposures.Distinct().Select(r => r with { Temperature = t })).ToArray();
    }

    public static IReadOnlyList<ExposureRecipe> Flats(IEnumerable<ExposureRecipe> active) => active
        .DistinctBy(r => (r.FilterId, r.Binning, r.Gain, r.Offset, r.ReadoutMode)).ToArray();
}
