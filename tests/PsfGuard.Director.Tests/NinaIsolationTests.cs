using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaIsolationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("relative", "00000000000000000000000000000000")]
    [InlineData("C:\\nina-smoke-test", "invalid")]
    public void RejectsUnscopedConfiguration(string? root, string? token)
    {
        Assert.Throws<InvalidOperationException>(() => StartupHook.ValidateRoot(root, token));
    }

    [Fact]
    public void RequiresMatchingOwnershipMarker()
    {
        var token = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), $"nina-smoke-{token}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<FileNotFoundException>(() => StartupHook.ValidateRoot(root, token));
            File.WriteAllText(Path.Combine(root, ".director-test-root"), Guid.NewGuid().ToString("N"));
            Assert.Throws<InvalidOperationException>(() => StartupHook.ValidateRoot(root, token));
            File.WriteAllText(Path.Combine(root, ".director-test-root"), token);
            Assert.Equal(root, StartupHook.ValidateRoot(root + Path.DirectorySeparatorChar, token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RejectsNormalNinaDataDirectory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA");
        Assert.Throws<InvalidOperationException>(() => StartupHook.ValidateRoot(root, Guid.NewGuid().ToString("N")));
    }
}
