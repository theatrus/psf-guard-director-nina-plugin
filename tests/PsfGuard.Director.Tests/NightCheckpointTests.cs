using System.Text.Json.Nodes;
using Newtonsoft.Json;
using NINA.Sequencer.Container;
using NINA.Sequencer.Conditions;
using Moq;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NightCheckpointTests
{
    [Fact]
    public void RestartDefaultsOffAndOperatorIntentIsNeverSavedOrCloned()
    {
        var session = new DirectorSessionContainer { ResumeRecordedNight = true };
        Assert.False(session.Options.AllowSettledRestart);
        var json = JsonConvert.SerializeObject(session, new JsonSerializerSettings { PreserveReferencesHandling = PreserveReferencesHandling.Objects });
        Assert.DoesNotContain("ResumeRecordedNight", json);
        Assert.False(((DirectorSessionContainer)session.Clone()).ResumeRecordedNight);
        Assert.True(session.ConsumeRestartRequest());
        Assert.False(session.ConsumeRestartRequest());
        session.Options.AllowSettledRestart = true;
        Assert.NotEmpty(session.Options.ValidateSettings());
        session.Options.AutomaticWorkloads = true;
        Assert.Empty(session.Options.ValidateSettings());
    }

    [Fact]
    public void UnjournaledAncestorLifecycleBlocksRestartRecording()
    {
        var session = new DirectorSessionContainer();
        var parent = new SequentialContainer();
        parent.Add(session);
        NinaRestartAdmission.ValidateLifecycle(session);
        parent.Add(Mock.Of<ISequenceCondition>());
        Assert.Throws<InvalidOperationException>(() => NinaRestartAdmission.ValidateLifecycle(session));
    }

    [Fact]
    public void ActiveBoundaryCannotBeReopenedOrForgivenByNewSettings()
    {
        var root = Directory.CreateTempSubdirectory("director-night-boundary-");
        try
        {
            var rig = Guid.NewGuid();
            var identity = new RecoveryIdentity(rig.ToString("D"), "config", "night", 1000, 10000);
            var binding = new CoordinatorBinding(Guid.NewGuid(), Guid.NewGuid(), rig, Guid.NewGuid());
            var options = new DirectorSessionOptions { AutomaticWorkloads = true, AllowSettledRestart = true };
            var scope = DirectorAcquisition.RestartScope(binding, Guid.NewGuid(), new("https://example.test/"), "config", options);
            var checkpoint = new CoordinatorNightCheckpoint(root.FullName, identity, scope);
            Assert.Throws<InvalidDataException>(() => checkpoint.RequireIdle());
            checkpoint.Begin();
            Assert.Equal(RestartBoundary.Unused, checkpoint.RequireIdle());
            checkpoint.MarkDispatched("run");
            checkpoint = new(root.FullName, identity, scope);
            Assert.Throws<InvalidDataException>(() => checkpoint.RequireIdle());
            Assert.Throws<InvalidDataException>(() => checkpoint.Begin());
            Assert.Throws<InvalidDataException>(() => checkpoint.MarkSettled("another-run"));
            Assert.Throws<InvalidDataException>(() => new CoordinatorNightCheckpoint(root.FullName, identity with { EndsAtMs = 20000 }, scope).RequireIdle());
            Assert.Throws<InvalidDataException>(() => new CoordinatorNightCheckpoint(root.FullName, identity, scope with { PolicyHash = new('f', 64) }).RequireIdle());
            checkpoint.MarkSettled("run");
            Assert.Equal(RestartBoundary.Settled, new CoordinatorNightCheckpoint(root.FullName, identity, scope).RequireIdle());
            checkpoint.MarkDispatched("next-run");
            var file = Directory.GetFiles(root.FullName, "*.json").Single();
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            json["idle"] = true;
            File.WriteAllText(file, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => checkpoint.RequireIdle());
        }
        finally { root.Delete(true); }
    }
}
