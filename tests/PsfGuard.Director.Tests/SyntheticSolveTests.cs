using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class SyntheticSolveTests
{
    [Fact]
    public void SolverRequiresIsolationAndWritesDegreesFromHours()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nina-smoke-{Guid.NewGuid():N}");
        var solver = Directory.CreateDirectory(Path.Combine(root, "synthetic-solver")).FullName;
        try
        {
            var frame = Path.Combine(root, "snapshot.fits");
            File.WriteAllText(frame, "synthetic test input");
            string[] args = ["-f", frame, "-ra", "2.5", "-spd", "100"];
            Assert.Equal(4, SyntheticSolve.Run(args, solver));
            File.WriteAllText(Path.Combine(root, ".director-test-root"), Guid.NewGuid().ToString("N"));
            File.WriteAllText(Path.Combine(root, "isolation-ready.txt"), root);
            Assert.Equal(0, SyntheticSolve.Run(args, solver));
            var output = File.ReadAllLines(Path.ChangeExtension(frame, ".ini"));
            Assert.Contains("CRVAL1=37.5", output);
            Assert.Contains("CRVAL2=10", output);
            Assert.Equal(3, SyntheticSolve.Run(["-f", frame, "-ra", "NaN", "-spd", "100"], solver));
            Assert.Equal(2, SyntheticSolve.Run(["-f", frame, "-ra"], solver));
            Assert.Equal(2, SyntheticSolve.Run(["-f", frame, "-f", frame], solver));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
