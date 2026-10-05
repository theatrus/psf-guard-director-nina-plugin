using System.Globalization;

// Test-only ASTAP-shaped output. Never use this executable for sky evidence.
public static class SyntheticSolve
{
    public static int Run(string[] args, string directory)
    {
        var root = Directory.GetParent(directory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (root is null || !Path.GetFileName(root).StartsWith("nina-smoke-", StringComparison.Ordinal)
            || !File.Exists(Path.Combine(root, ".director-test-root"))
            || !Guid.TryParseExact(File.ReadAllText(Path.Combine(root, ".director-test-root")), "N", out _)
            || !File.Exists(Path.Combine(root, "isolation-ready.txt"))
            || File.ReadAllText(Path.Combine(root, "isolation-ready.txt")) != root) return 4;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length % 2 != 0) return 2;
        for (var i = 0; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1])) return 2;
        if (!values.TryGetValue("-f", out var file) || !File.Exists(file)
            || !Path.GetFullPath(file).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !values.TryGetValue("-ra", out var ra) || !values.TryGetValue("-spd", out var spd)) return 2;
        if (!double.TryParse(ra, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(spd, NumberStyles.Float, CultureInfo.InvariantCulture, out var southPolarDistance)
            || !double.IsFinite(hours) || hours is < 0 or >= 24
            || !double.IsFinite(southPolarDistance) || southPolarDistance is < 0 or > 180) return 3;
        File.WriteAllLines(Path.ChangeExtension(file, ".ini"), [
            "PLTSOLVD=T", $"CRVAL1={(hours * 15).ToString("R", CultureInfo.InvariantCulture)}",
            $"CRVAL2={(southPolarDistance - 90).ToString("R", CultureInfo.InvariantCulture)}", "CRPIX1=256", "CRPIX2=256",
            "CD1_1=-0.0004", "CD1_2=0", "CD2_1=0", "CD2_2=0.0004"
        ]);
        File.AppendAllText(Path.Combine(directory, "synthetic-solves.log"), $"{file}\n");
        return 0;
    }
}
