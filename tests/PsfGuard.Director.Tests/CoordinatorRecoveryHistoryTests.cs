using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class CoordinatorRecoveryHistoryTests
{
    [Fact]
    public void ArchivingPreservesUncertainRecoveryAndCaptureEvidenceForANewSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"director-history-{Guid.NewGuid():N}");
        var rig = Guid.NewGuid();
        try
        {
            var recovery = Directory.CreateDirectory(Path.Combine(root, "recovery", rig.ToString("D"))).FullName;
            File.WriteAllText(Path.Combine(recovery, "recovery.sqlite"), "interrupted operation journal");
            var captures = Directory.CreateDirectory(Path.Combine(root, "captures")).FullName;
            File.WriteAllText(Path.Combine(captures, "saved.fits"), "raw image");
            var archive = CoordinatorRecoveryHistory.Archive(root, rig)!;
            Assert.Equal("interrupted operation journal", File.ReadAllText(Path.Combine(archive, "recovery.sqlite")));
            Assert.Equal("raw image", File.ReadAllText(Path.Combine(captures, "saved.fits")));
            Assert.False(Directory.Exists(recovery));
            Assert.Null(CoordinatorRecoveryHistory.Archive(root, rig));
            Directory.CreateDirectory(recovery);
            File.WriteAllText(Path.Combine(recovery, "recovery.sqlite"), "new session");
            var second = CoordinatorRecoveryHistory.Archive(root, rig);
            Assert.NotEqual(archive, second);
            Assert.Equal("interrupted operation journal", File.ReadAllText(Path.Combine(archive, "recovery.sqlite")));
        }
        finally { Directory.Delete(root, true); }
    }
}
