using System.Data.SQLite;
using System.IO;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record NinaOrientationSample(long UnixSeconds, double ModifiedJulianDate,
    double Ut1MinusUtcSeconds, double XArcseconds, double YArcseconds);
internal sealed record NinaOrientationEvidence(DirectorEarthOrientation Orientation, long SampleUnixSeconds,
    IReadOnlyList<NinaOrientationSample> Samples);

// NINA's daily IERS cache is evidence, not its DeltaUT convenience function
// (which may choose an arbitrarily old nearest row). Never initialize/migrate it.
internal static class NinaEarthOrientation
{
    internal static string DatabasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "NINA.sqlite");

    internal static NinaOrientationEvidence Read(string path, ulong startMs, ulong endMs, CancellationToken token)
    {
        var anchor = Anchor(startMs, endMs);
        token.ThrowIfCancellationRequested();
        var connectionString = new SQLiteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            ReadOnly = true,
            FailIfMissing = true,
            Pooling = false,
            DefaultTimeout = 2
        }.ConnectionString;
        using var connection = new SQLiteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT date, modifiedjuliandate, ut1_utc, x, y FROM earthrotationparameters WHERE date >= @first AND date <= @last ORDER BY date";
        command.Parameters.AddWithValue("@first", anchor - 86400);
        command.Parameters.AddWithValue("@last", anchor + 86400);
        using var cancellation = token.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var samples = new List<NinaOrientationSample>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (samples.Count >= 3) throw new InvalidDataException("NINA Earth-orientation cache has unexpected sample spacing.");
            samples.Add(new(reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
        }
        token.ThrowIfCancellationRequested();
        return Bind(samples, startMs, endMs);
    }

    internal static NinaOrientationEvidence Bind(IReadOnlyList<NinaOrientationSample> samples, ulong startMs, ulong endMs)
    {
        var anchor = Anchor(startMs, endMs);
        if (samples.Count != 3) throw new InvalidDataException("NINA Earth-orientation data does not bracket this session. Update NINA's astronomy data before starting.");
        for (var index = 0; index < 3; index++)
        {
            var row = samples[index];
            if (row.UnixSeconds != anchor + (index - 1) * 86400
                || !double.IsFinite(row.ModifiedJulianDate) || Math.Abs(row.ModifiedJulianDate - (40587 + row.UnixSeconds / 86400.0)) > 1e-6
                || !double.IsFinite(row.Ut1MinusUtcSeconds) || Math.Abs(row.Ut1MinusUtcSeconds) > 0.9
                || !double.IsFinite(row.XArcseconds) || Math.Abs(row.XArcseconds) > 3
                || !double.IsFinite(row.YArcseconds) || Math.Abs(row.YArcseconds) > 3)
                throw new InvalidDataException("NINA Earth-orientation evidence is missing, malformed or out of range.");
        }
        var middle = samples[1];
        // IPC 8 uses one fixed EOP sample. Limit observed daily drift using both
        // adjacent days; refuse discontinuities instead of hiding a leap or jump.
        // This is a daily approximation, not a bound on intra-day error. A
        // future series contract can interpolate while retaining provenance.
        foreach (var row in samples)
            if (Math.Abs(row.Ut1MinusUtcSeconds - middle.Ut1MinusUtcSeconds) > 0.002
                || Math.Abs(row.XArcseconds - middle.XArcseconds) > 0.01
                || Math.Abs(row.YArcseconds - middle.YArcseconds) > 0.01)
                throw new InvalidDataException("Earth orientation changes too much for the fixed-sample runtime contract.");
        const double radiansPerArcsecond = Math.PI / (180 * 3600);
        return new(new(middle.Ut1MinusUtcSeconds, middle.XArcseconds * radiansPerArcsecond,
            middle.YArcseconds * radiansPerArcsecond, startMs, checked(endMs + 1)), anchor, samples.ToArray());
    }

    private static long Anchor(ulong startMs, ulong endMs)
    {
        if (startMs >= endMs || endMs - startMs > 86400000 || startMs < 86400000 || endMs > long.MaxValue - 86400000)
            throw new ArgumentException("Earth-orientation evidence needs a bounded session of at most 24 hours.");
        var midpoint = startMs + (endMs - startMs) / 2;
        return checked((long)((midpoint + 43200000) / 86400000) * 86400);
    }
}
