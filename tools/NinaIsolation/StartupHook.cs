using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PsfGuard.Director.Tests")]

// Test-only .NET startup hook. Never include this assembly in a plugin package.
internal static class StartupHook
{
    public static void Initialize()
    {
        var root = ValidateRoot(Environment.GetEnvironmentVariable("DIRECTOR_NINA_TEST_ROOT"),
            Environment.GetEnvironmentVariable("DIRECTOR_NINA_TEST_TOKEN"));

        var core = Assembly.Load("NINA.Core");
        var field = core.GetType("NINA.Core.Utility.CoreUtil", throwOnError: true)!
            .GetField("APPLICATIONTEMPPATH", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException("NINA data directory contract changed.");
        field.SetValue(null, root);
        File.WriteAllText(Path.Combine(root, "isolation-ready.txt"), (string)field.GetValue(null)!);
    }

    internal static string ValidateRoot(string? root, string? token)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root)
            || !Guid.TryParseExact(token, "N", out _))
        {
            throw new InvalidOperationException("Missing isolated NINA test directory or token.");
        }
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Path.GetFileName(root).StartsWith("nina-smoke-", StringComparison.Ordinal)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || File.ReadAllText(Path.Combine(root, ".director-test-root")) != token)
        {
            throw new InvalidOperationException("NINA test directory ownership check failed.");
        }
        return root;
    }
}
