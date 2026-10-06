using System.Text.Json;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class QualityContractTests
{
    private static readonly QualityFrame ReferenceFrame = new("reference-capture", 1000,
        new("rig-test", "config", "target", "recipe", "nina-fast", 1000, 1000), new(100, 2, 1000, 0.4));
    private static readonly QualityReference Reference = new("approved-reference", true, ReferenceFrame);
    private static readonly QualityFrame Frame = ReferenceFrame with { CaptureId = "new-capture", ObservedAtMs = 2000 };
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, RecoveryContract.Options)!;

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("negative")]
    [InlineData("partial")]
    [InlineData("verdict")]
    [InlineData("unknown-verdict")]
    [InlineData("enum")]
    [InlineData("warning")]
    [InlineData("missing-warning")]
    public async Task MalformedAssessmentFaultsTheSession(string fault)
    {
        var assessment = Node(new QualityAssessment(RecoveryVerdict.ConfirmedGood, QualityReason.ConsistentWithReference, 1, 1, 1, false)).AsObject();
        switch (fault)
        {
            case "missing": assessment.Remove("star_ratio"); break;
            case "unknown": assessment["issued"] = true; break;
            case "negative": assessment["star_ratio"] = -1; break;
            case "partial": assessment["hfr_ratio"] = null; break;
            case "verdict": assessment["verdict"] = "corroborated_poor"; break;
            case "unknown-verdict": assessment["verdict"] = "unknown"; break;
            case "enum": assessment["reason"] = "clouds_guaranteed"; break;
            case "warning": assessment["reference_quality_unknown"] = true; break;
            case "missing-warning": assessment.Remove("reference_quality_unknown"); break;
        }
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ => new() { ["status"] = "quality_classified", ["assessment"] = assessment });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.ClassifyQualityAsync(new(), Reference, Frame, 2000, default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task WrongRigDoesNotSendQualityData()
    {
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ => throw new InvalidOperationException("Must not send"));
        var wrong = Frame with { Context = Frame.Context with { RigId = "other" } };
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.ClassifyQualityAsync(new(), Reference, wrong, 2000, default));
        Assert.True(peer.Session.IsReady);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("identity")]
    [InlineData("anchor")]
    [InlineData("group")]
    [InlineData("missing-group")]
    public async Task ChangedInitialReferenceFaultsTheSession(string fault)
    {
        var frames = Enumerable.Range(0, 5).Select(i => ReferenceFrame with
        {
            CaptureId = $"initial-{i}",
            ObservedAtMs = (ulong)(1000 + i * 100)
        }).ToImmutableArray();
        var reference = Node(new QualityReference("initial-group", false, frames[^1], frames)).AsObject();
        switch (fault)
        {
            case "approved": reference["approved"] = true; break;
            case "identity": reference["id"] = "different"; break;
            case "anchor": reference["frame"]!["capture_id"] = "different"; break;
            case "group": reference["initial_group"]![0]!["metrics"]!["stars"] = 200; break;
            case "missing-group": reference.Remove("initial_group"); break;
        }
        await using var peer = await TestPeer.CreateAsync("none", recovery: _ => new()
        {
            ["status"] = "quality_reference_built",
            ["reference"] = reference
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.BuildQualityReferenceAsync(new(), "initial-group", frames, default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task RealRuntimeClassifiesWithoutChangingRecoveryAndRestartReviewNeverClearsStop()
    {
        var root = Path.Combine(Path.GetTempPath(), "director-quality-test-" + Guid.NewGuid().ToString("N"));
        var ledger = Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName;
        var recovery = Directory.CreateDirectory(Path.Combine(root, "recovery")).FullName;
        try
        {
            await using var controller = new RuntimeController(ProcessTests.BundleDirectory, ledger, recovery);
            await controller.StartAsync("rig-test");
            var good = (await controller.ClassifyQualityAsync(new(), Reference, Frame, 2000)).Value!;
            Assert.Equal(RecoveryVerdict.ConfirmedGood, good.Verdict);
            Assert.False(File.Exists(Path.Combine(recovery, "recovery.sqlite")));
            var group = Enumerable.Range(0, 5).Select(i => ReferenceFrame with { CaptureId = $"initial-{i}", ObservedAtMs = (ulong)(1000 + i * 100) }).ToImmutableArray();
            Assert.Equal(RecoveryError.InsufficientSamples,
                (await controller.BuildQualityReferenceAsync(new(), "initial-group", group[..4])).Error);
            var unstable = group.SetItem(2, group[2] with { Metrics = new(20, 2, 2000, 0.4) });
            Assert.Equal(RecoveryError.UnstableBaseline,
                (await controller.BuildQualityReferenceAsync(new(), "initial-group", unstable)).Error);
            var baseline = (await controller.BuildQualityReferenceAsync(new(), "initial-group", group)).Value!;
            Assert.False(baseline.Approved);
            Assert.Equal(5, baseline.InitialGroup!.Value.Length);
            var provisional = (await controller.ClassifyQualityAsync(new(), baseline, Frame, 2000)).Value!;
            Assert.True(provisional.ReferenceQualityUnknown);
            Assert.Equal(RecoveryVerdict.ConfirmedGood, provisional.Verdict);
            var poor = (await controller.ClassifyQualityAsync(new(), Reference, Frame with { Metrics = new(20, 2, 2000, 0.4) }, 2000)).Value!;
            Assert.Equal(RecoveryVerdict.CorroboratedPoor, poor.Verdict);
            Assert.False(File.Exists(Path.Combine(recovery, "recovery.sqlite")));
            var identity = new RecoveryIdentity("rig-test", "config", "night", 1000, 100000);
            var policy = new RecoveryPolicy(1, RecoveryQualityMode.Pause, 2, 2, 100, 10000, 3, 1000, 500, 90000, 3, 10, true);
            await controller.OpenRecoveryAsync(identity, policy, 2000);
            var input = new RestartReview("rig-test", "config", "night", 2000, true, RestartBoundary.Settled,
                true, true, true, PlannerSafety.Safe, RecoveryMotion.Permitted);
            var review = (await controller.ReviewRestartAsync(input)).Value!;
            Assert.Equal(RestartAdvice.RequestFreshAuthority, review.Advice);
            Assert.Equal(0UL, review.Record.Revision);
            await controller.ApplyRecoveryAsync(new("night", "config", "stop", 0, 2001,
                new(PlannerSafety.Safe, RecoveryMotion.Permitted), new RecoveryEvent.StopNight()));
            await controller.StopAsync();
            await controller.StartAsync("rig-test");
            review = (await controller.ReviewRestartAsync(input with { NowMs = 2001 })).Value!;
            Assert.Equal(RestartAdvice.TerminalStop, review.Advice);
            Assert.Equal(1UL, review.Record.Revision);
            Assert.Single((await controller.ReadRecoveryEventsAsync("night", 0)).Value!.Events);
            await controller.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
