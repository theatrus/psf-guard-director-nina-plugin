using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal enum NinaHorizonMode { RequiredFile, FixedMinimum }
// The owner must persist this declaration; a cleared NINA path is not opt-out.
internal sealed record NinaConstraintBinding(Guid ProfileId, NinaHorizonMode Mode, string? HorizonPath,
    double MinimumAltitudeDegrees, PlannerMeridianExclusion MeridianExclusion);
internal readonly record struct HorizonPoint(double AzimuthDegrees, double AltitudeDegrees);
internal sealed record NativeHorizon(string Format, string? ContentSha256, ImmutableArray<HorizonPoint> Points);
internal sealed record NativeSite(double LatitudeDegrees, double LongitudeDegrees, double ElevationMeters);
internal sealed record NativeFlip(double EarliestMinutesAfter, double LatestMinutesAfter, double PauseMinutesBefore,
    bool Recenter, int SettleSeconds, bool UseSideOfPier, bool AutoFocus, bool RotateImage);
internal sealed record NinaConstraints(Guid ProfileId, NativeSite Site, NativeHorizon Horizon,
    double MinimumAltitudeDegrees, PlannerMeridianExclusion MeridianExclusion, NativeFlip Flip, string Revision);

/// <summary>Local data export only. Visibility, flip execution, and scheduling stay outside this reader.</summary>
internal sealed class NinaConstraintSnapshot : IDisposable
{
    private readonly IProfileService profiles;
    private long generation;
    private long profileGeneration;
    private int reading;
    private volatile bool disposed;

    internal NinaConstraintSnapshot(IProfileService profiles)
    {
        this.profiles = profiles;
        profiles.BeforeProfileChanging += ProfileChanged;
        profiles.ProfileChanged += ProfileChanged;
        profiles.LocationChanged += Changed;
        profiles.HorizonChanged += Changed;
    }

    internal long Generation => Interlocked.Read(ref generation);
    internal bool IsCurrent(long expectedGeneration) => !disposed && Generation == expectedGeneration;
    private void Changed(object? sender, EventArgs args) => Interlocked.Increment(ref generation);
    private void ProfileChanged(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref profileGeneration);
        Changed(sender, args);
    }

    internal NinaConstraints Refresh(NinaConstraintBinding binding)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.ProfileId == Guid.Empty || !double.IsFinite(binding.MinimumAltitudeDegrees)
            || binding.MinimumAltitudeDegrees is < -90 or > 90 || !Enum.IsDefined(binding.Mode))
            throw new ArgumentException("Invalid declared rig constraints.");
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
            throw new InvalidOperationException("Constraint refresh is already running.");
        try
        {
            var profile = profiles.ActiveProfile;
            var profileEpoch = Interlocked.Read(ref profileGeneration);
            var epoch = Generation;
            CheckProfile();
            var site = Site(profile);
            var flip = Flip(profile);
            NativeHorizon horizon;
            CustomHorizon? loadedHorizon = null;
            if (binding.Mode == NinaHorizonMode.FixedMinimum)
            {
                if (binding.HorizonPath is not null || !string.IsNullOrWhiteSpace(profile.AstrometrySettings.HorizonFilePath)
                    || profile.AstrometrySettings.Horizon is not null)
                    throw new InvalidOperationException("Fixed-minimum mode requires an explicitly empty NINA horizon.");
                horizon = new("fixed-minimum", null, []);
            }
            else
            {
                var path = binding.HorizonPath;
                var ninaPath = profile.AstrometrySettings.HorizonFilePath;
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                    || string.IsNullOrWhiteSpace(ninaPath) || !Path.IsPathFullyQualified(ninaPath)
                    || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(ninaPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The declared horizon file no longer matches NINA. Review the rig constraints.");
                // Deny writes/rename while NINA reloads exactly the bytes being exported.
                // This uses supported profile APIs, never private horizon arrays.
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 1024 * 1024) throw new InvalidDataException("Horizon file exceeds one MiB.");
                var bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
                var mw4 = Path.GetExtension(path) == ".hpts"; // Match NINA's case-sensitive format selection.
                horizon = Parse(bytes, mw4);
                CheckProfile();
                profiles.ChangeHorizon(path);
                CheckProfile();
                if (profile.AstrometrySettings.HorizonFilePath != path || profile.AstrometrySettings.Horizon is not { } loaded)
                    throw new InvalidOperationException("NINA could not load the declared horizon.");
                VerifyNativeCurve(horizon.Points, loaded);
                loadedHorizon = loaded;
            }
            CheckProfile();
            // ChangeHorizon synchronously raises one event. Any additional event
            // or model replacement means this export raced another local change.
            var expectedEpoch = binding.Mode == NinaHorizonMode.RequiredFile ? unchecked(epoch + 1) : epoch;
            if (Generation != expectedEpoch || !ReferenceEquals(profile.AstrometrySettings.Horizon, loadedHorizon)
                || Site(profile) != site || Flip(profile) != flip)
                throw new InvalidOperationException("NINA site or flip constraints changed during refresh.");
            ObjectDisposedException.ThrowIf(disposed, this);
            var value = new NinaConstraints(binding.ProfileId, site, horizon, binding.MinimumAltitudeDegrees,
                binding.MeridianExclusion, flip, "pending");
            var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Value = value }));
            return value with { Revision = "constraints-" + Convert.ToHexStringLower(digest) };

            void CheckProfile()
            {
                if (!ReferenceEquals(profiles.ActiveProfile, profile) || profile.Id != binding.ProfileId
                    || Interlocked.Read(ref profileGeneration) != profileEpoch)
                    throw new InvalidOperationException("NINA profile changed during constraint refresh.");
            }
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    internal static NativeHorizon Parse(byte[] bytes, bool mw4)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Horizon file exceeds one MiB.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var map = new SortedDictionary<double, double>();
        var count = 0;
        void Add(double azimuth, double altitude)
        {
            if (++count > 4096 || !double.IsFinite(azimuth) || !double.IsFinite(altitude)
                || azimuth is < 0 or > 360 || altitude < (mw4 ? 0 : -90) || altitude > 90)
                throw new InvalidDataException("Invalid or excessive horizon coordinates.");
            map[azimuth] = altitude; // NINA retains the last occurrence of an azimuth.
        }
        if (mw4)
        {
            using var json = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("MW4 horizon must be an array.");
            foreach (var point in json.RootElement.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2
                    || point[0].ValueKind != JsonValueKind.Number || point[1].ValueKind != JsonValueKind.Number)
                    throw new InvalidDataException("MW4 horizon must contain altitude/azimuth pairs.");
                Add(point[1].GetDouble(), point[0].GetDouble());
            }
        }
        else
        {
            while (reader.ReadLine() is { } raw)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var fields = line.Split(['\t', ' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 2
                    || !double.TryParse(fields[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var azimuth)
                    || !double.TryParse(fields[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var altitude))
                    throw new InvalidDataException("Standard horizon must contain azimuth/altitude pairs.");
                Add(azimuth, altitude);
            }
        }
        if (map.Count < 2) throw new InvalidDataException("A horizon needs two distinct azimuths.");
        // Match NINA's endpoint completion, not circular interpolation between
        // nearest samples. Ties use the lowest azimuth. Keep explicit 0/360 discontinuities.
        if (!map.ContainsKey(0) && !map.ContainsKey(360))
        {
            var first = map.First();
            var last = map.Last();
            map[0] = map[360] = 360 - last.Key < first.Key ? last.Value : first.Value;
        }
        else if (!map.ContainsKey(0)) map[0] = map[360];
        else if (!map.ContainsKey(360)) map[360] = map[0];
        return new(mw4 ? "nina-mw4-v1" : "nina-standard-v1", Convert.ToHexStringLower(SHA256.HashData(bytes)),
            map.Select(p => new HorizonPoint(p.Key, p.Value)).ToImmutableArray());
    }

    private static void VerifyNativeCurve(ImmutableArray<HorizonPoint> points, CustomHorizon loaded)
    {
        void Check(double azimuth, double expected)
        {
            var actual = loaded.GetAltitude(azimuth);
            if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-9)
                throw new InvalidDataException("NINA horizon interpolation differs from the exported curve.");
        }
        for (var i = 0; i < points.Length - 1; i++)
        {
            Check(points[i].AzimuthDegrees, points[i].AltitudeDegrees);
            var span = points[i + 1].AzimuthDegrees - points[i].AzimuthDegrees;
            var midpoint = points[i].AzimuthDegrees + span / 2;
            // At adjacent representable doubles the midpoint can round to an
            // endpoint. Compare its actual fraction, not an assumed 50 percent.
            var fraction = (midpoint - points[i].AzimuthDegrees) / span;
            Check(midpoint, points[i].AltitudeDegrees
                + fraction * (points[i + 1].AltitudeDegrees - points[i].AltitudeDegrees));
        }
        Check(360, points[0].AltitudeDegrees);
        if (loaded.GetMinAltitude() != points.Min(p => p.AltitudeDegrees)
            || loaded.GetMaxAltitude() != points.Max(p => p.AltitudeDegrees))
            throw new InvalidDataException("NINA horizon extrema differ from the exported curve.");
    }

    private static NativeSite Site(IProfile profile)
    {
        var s = profile.AstrometrySettings;
        var site = new NativeSite(s.Latitude, s.Longitude, s.Elevation);
        if (!double.IsFinite(site.LatitudeDegrees) || site.LatitudeDegrees is < -90 or > 90
            || !double.IsFinite(site.LongitudeDegrees) || site.LongitudeDegrees is < -180 or > 180
            || !double.IsFinite(site.ElevationMeters)) throw new InvalidDataException("Invalid NINA site coordinates.");
        return site;
    }

    private static NativeFlip Flip(IProfile profile)
    {
        var f = profile.MeridianFlipSettings;
        var flip = new NativeFlip(f.MinutesAfterMeridian, f.MaxMinutesAfterMeridian, f.PauseTimeBeforeMeridian,
            f.Recenter, f.SettleTime, f.UseSideOfPier, f.AutoFocusAfterFlip, f.RotateImageAfterFlip);
        if (!double.IsFinite(flip.EarliestMinutesAfter) || !double.IsFinite(flip.LatestMinutesAfter)
            || !double.IsFinite(flip.PauseMinutesBefore) || flip.EarliestMinutesAfter > flip.LatestMinutesAfter
            || flip.SettleSeconds < 0) throw new InvalidDataException("Invalid NINA flip settings.");
        return flip;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        profiles.BeforeProfileChanging -= ProfileChanged;
        profiles.ProfileChanged -= ProfileChanged;
        profiles.LocationChanged -= Changed;
        profiles.HorizonChanged -= Changed;
    }
}
