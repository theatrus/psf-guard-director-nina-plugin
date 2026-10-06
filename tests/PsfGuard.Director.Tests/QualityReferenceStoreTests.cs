using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class QualityReferenceStoreTests
{
    private static QualityFrame Frame(int index, bool poor = false) => new($"capture-{index}", (ulong)(1000 + index * 100),
        new("rig-test", "config", "target", "recipe", "nina-fast", 1000, 1000),
        new(poor ? 25U : 100U, 2, poor ? 2000 : 1000, 0.4));

    [Fact]
    public async Task ReferenceSurvivesRestartWithoutAdaptingToPoorFrames()
    {
        var root = Path.Combine(Path.GetTempPath(), "director-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory,
                Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName,
                Directory.CreateDirectory(Path.Combine(root, "recovery")).FullName);
            await runtime.StartAsync("rig-test");
            var store = new QualityReferenceStore(root, "night", runtime);
            QualityObservation? result = null;
            for (var i = 0; i < 5; i++) result = await store.ObserveAsync(Frame(i), Frame(i).ObservedAtMs, default);
            var reference = Assert.IsType<QualityReference>(result!.Reference);
            Assert.False(reference.Approved);
            Assert.Equal(5, reference.InitialGroup!.Value.Length);
            await runtime.StopAsync();
            await runtime.StartAsync("rig-test");
            store = new(root, "night", runtime);
            for (var i = 5; i < 12; i++)
            {
                result = await store.ObserveAsync(Frame(i, poor: true), Frame(i).ObservedAtMs, default);
                Assert.Equal(reference.Id, result.Reference!.Id);
                Assert.Equal(reference.Frame, result.Reference.Frame);
                Assert.True(result.Assessment!.ReferenceQualityUnknown);
                Assert.Equal(RecoveryVerdict.CorroboratedPoor, result.Assessment.Verdict);
            }
            Assert.False((await store.ObserveAsync(Frame(11), Frame(11).ObservedAtMs, default)).IsNew);
            Assert.False((await store.ObserveAsync(Frame(10), Frame(11).ObservedAtMs, default)).IsNew);
            var changed = Frame(12) with { Context = Frame(12).Context with { RecipeFingerprint = "short-exposure" } };
            Assert.Null((await store.ObserveAsync(changed, changed.ObservedAtMs, default)).Reference);
            Assert.Null((await new QualityReferenceStore(root, "another-night", runtime)
                .ObserveAsync(Frame(12), Frame(12).ObservedAtMs, default)).Reference);
            await runtime.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnstableInitialGroupStopsLearningAfterSixteenFrames()
    {
        var root = Path.Combine(Path.GetTempPath(), "director-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory,
                Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName,
                Directory.CreateDirectory(Path.Combine(root, "recovery")).FullName);
            await runtime.StartAsync("rig-test");
            var store = new QualityReferenceStore(root, "night", runtime);
            for (var i = 0; i < 24; i++)
            {
                var frame = Frame(i, poor: i < 16 && i % 2 == 0);
                var result = await store.ObserveAsync(frame, frame.ObservedAtMs, default);
                Assert.Equal(Math.Min(i + 1, 16), result.SamplesSeen);
                Assert.Null(result.Reference);
                Assert.Null(result.Assessment);
            }
            await runtime.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
