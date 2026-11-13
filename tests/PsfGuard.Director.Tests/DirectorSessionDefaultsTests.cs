using Moq;
using Newtonsoft.Json;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

// The defaults commands inspect the process-wide acquisition lease.
[Collection("Director equipment reporting")]
public sealed class DirectorSessionDefaultsTests
{
    [Fact]
    public void DefaultsAreProfileScopedAndDoNotArmNewSessions()
    {
        var f = new Fixture();
        var options = new DirectorSessionOptions
        {
            MeridianBeforeMinutes = 60,
            MeridianAfterMinutes = 0,
            AutofocusOnFilterChange = false,
            ForceGuideCalibration = true,
            DitherEveryExposures = 5,
            EnableAcquisition = true
        };
        f.Store.Save(options);
        Assert.True(options.EnableAcquisition);
        var first = new DirectorSessionContainer(f.Store);
        Assert.Equal(new(3600000, 0), first.Options.MeridianExclusion());
        Assert.False(first.Options.EnableAcquisition);
        Assert.False(first.Options.AutofocusOnFilterChange);
        Assert.True(first.Options.ForceGuideCalibration);
        Assert.Equal(5, first.Options.DitherEveryExposures);
        f.Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile("Other"));
        Assert.Equal(new(0, 0), f.Store.Read().MeridianExclusion());
        Assert.Equal(60, first.Options.MeridianBeforeMinutes);
        f.Profiles.SetupGet(p => p.ActiveProfile).Returns(f.Profile);
        Assert.Equal(60, f.Store.Read().MeridianBeforeMinutes);
    }

    [Fact]
    public void ToolboxClonesReadCurrentDefaultsButDuplicateAndSavedSessionsKeepOverrides()
    {
        var f = new Fixture();
        var prototype = new DirectorSessionContainer(f.Acquisition());
        f.Store.Save(new() { MeridianBeforeMinutes = 60 });
        var session = (DirectorSessionContainer)prototype.Clone();
        Assert.Equal(60, session.Options.MeridianBeforeMinutes);
        session.Options.MeridianAfterMinutes = 15;
        session.Options.EnableAcquisition = true;
        f.Store.Save(new() { MeridianBeforeMinutes = 30, MeridianAfterMinutes = 20 });
        var duplicate = (DirectorSessionContainer)session.Clone();
        Assert.Equal(new(3600000, 900000), duplicate.Options.MeridianExclusion());
        Assert.True(duplicate.Options.EnableAcquisition);
        Assert.Equal(new(1800000, 1200000), ((DirectorSessionContainer)prototype.Clone()).Options.MeridianExclusion());
        var serialization = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto, PreserveReferencesHandling = PreserveReferencesHandling.Objects };
        var json = JsonConvert.SerializeObject(session, serialization);
        var restored = JsonConvert.DeserializeObject<DirectorSessionContainer>(json, serialization)!;
        Assert.Equal(duplicate.Options.MeridianExclusion(), restored.Options.MeridianExclusion());
        Assert.DoesNotContain("DefaultsStatus", json);
        Assert.DoesNotContain("MeridianSummary", json);
    }

    [Fact]
    public void LoadingDefaultsIsExplicitAndPreservesHooksAndArming()
    {
        var f = new Fixture();
        var session = new DirectorSessionContainer(f.Store);
        session.Options.EnableAcquisition = true;
        session.BeforeNewTarget.Add(new WaitForTimeSpan());
        var previousOptions = session.Options;
        f.Store.Save(new() { MeridianBeforeMinutes = 60 });
        Assert.Equal(0, session.Options.MeridianBeforeMinutes);
        var notifications = new List<string?>();
        session.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        session.LoadDefaults();
        Assert.Contains(nameof(session.Options), notifications);
        Assert.NotSame(previousOptions, session.Options);
        Assert.Equal(60, session.Options.MeridianBeforeMinutes);
        Assert.True(session.Options.EnableAcquisition);
        Assert.Single(session.BeforeNewTarget.Items);
        session.Options.MeridianAfterMinutes = 10;
        session.SaveDefaults();
        Assert.Equal(new(3600000, 600000), f.Store.Read().MeridianExclusion());
        Assert.False(f.Store.Read().EnableAcquisition);
    }

    [Fact]
    public void RunningSequenceCannotReplaceOrSaveItsSettings()
    {
        var f = new Fixture();
        var session = new DirectorSessionContainer(f.Store);
        var root = new SequenceRootContainer();
        root.Add(session);
        root.Status = SequenceEntityStatus.RUNNING;
        Assert.False(session.LoadDefaultsCommand.CanExecute(null));
        Assert.Throws<InvalidOperationException>(session.LoadDefaults);
        Assert.Throws<InvalidOperationException>(session.SaveDefaults);
        root.Status = SequenceEntityStatus.CREATED;
        Assert.True(session.LoadDefaultsCommand.CanExecute(null));
    }

    [Fact]
    public void BadDefaultsAreVisibleAndCannotReplaceValidSavedValues()
    {
        var f = new Fixture();
        f.Store.Save(new() { MeridianBeforeMinutes = 60 });
        Assert.Throws<InvalidDataException>(() => f.Store.Save(new() { MeridianAfterMinutes = -1 }));
        Assert.Equal(60, f.Store.Read().MeridianBeforeMinutes);
        new PluginOptionsAccessor(f.Profiles.Object, new("03a1d13e-67eb-4e24-a407-82bce7e576a5"))
            .SetValueString(DirectorSessionDefaults.SettingKey, "{bad json}");
        var session = new DirectorSessionContainer(f.Store);
        Assert.False(session.Options.EnableAcquisition);
        Assert.Contains("Could not load profile defaults", session.DefaultsStatus);
        Assert.Throws<InvalidDataException>(session.LoadDefaults);
    }

    [Fact]
    public void DefaultsEditorDoesNotOverwriteSettingsSavedFromASequence()
    {
        var f = new Fixture();
        var plugin = new DirectorPlugin(f.Profiles.Object);
        var untouched = plugin.SessionDefaults;
        f.Store.Save(new() { MeridianBeforeMinutes = 60 });
        Assert.NotSame(untouched, plugin.SessionDefaults);
        Assert.Equal(60, plugin.SessionDefaults.Options.MeridianBeforeMinutes);
        var edited = plugin.SessionDefaults;
        edited.Options.MeridianAfterMinutes = 5;
        f.Store.Save(new() { MeridianBeforeMinutes = 45 });
        Assert.Same(edited, plugin.SessionDefaults);
        Assert.Throws<InvalidOperationException>(edited.Save);
        edited.ReloadCommand.Execute(null);
        Assert.Equal(45, edited.Options.MeridianBeforeMinutes);
        Assert.Equal(0, edited.Options.MeridianAfterMinutes);
        edited.Options.MeridianAfterMinutes = 10;
        edited.Save();
        Assert.Equal(new(2700000, 600000), f.Store.Read().MeridianExclusion());
    }

    [Fact]
    public void OldDefaultsEditorCannotWriteIntoAnotherProfile()
    {
        var f = new Fixture();
        var editor = new DirectorDefaultsEditor(f.Store);
        editor.Options.MeridianBeforeMinutes = 60;
        f.Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile("Other"));
        Assert.Throws<InvalidOperationException>(editor.Save);
        Assert.Equal(0, f.Store.Read().MeridianBeforeMinutes);
    }

    [Theory]
    [InlineData(60, 0, 3600000UL, 0UL)]
    [InlineData(0, 60, 0UL, 3600000UL)]
    [InlineData(45.5, 7.25, 2730000UL, 435000UL)]
    [InlineData(0, 0, 0UL, 0UL)]
    public void BeforeAndAfterRemainIndependent(double before, double after, ulong beforeMs, ulong afterMs)
    {
        var options = new DirectorSessionOptions();
        var changes = new List<string?>();
        options.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        options.MeridianBeforeMinutes = before;
        options.MeridianAfterMinutes = after;
        Assert.Equal(new(beforeMs, afterMs), options.MeridianExclusion());
        if (before + after > 0) Assert.Contains(nameof(options.MeridianSummary), changes);
        Assert.Equal(options.MeridianSummary, options.Clone().MeridianSummary);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(361)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidMeridianValuesCannotReachPlanner(double invalid)
    {
        var options = new DirectorSessionOptions { MeridianBeforeMinutes = invalid };
        Assert.Throws<InvalidOperationException>(() => options.MeridianExclusion());
        Assert.Contains("0 to 360", options.MeridianSummary);
    }

    [Theory]
    [InlineData(60, 0, 119, PlannerAction.Wait)] // Exposure would cross the start of avoidance.
    [InlineData(60, 0, 120, PlannerAction.Wait)]
    [InlineData(60, 0, 180, PlannerAction.Acquire)]
    [InlineData(0, 30, 180, PlannerAction.Wait)]
    [InlineData(0, 30, 210, PlannerAction.Acquire)]
    [InlineData(60, 15, 194, PlannerAction.Wait)]
    [InlineData(60, 15, 195, PlannerAction.Acquire)]
    [InlineData(0, 0, 180, PlannerAction.Acquire)]
    public async Task BundledRustPlannerEnforcesTsAsymmetricRule(double before, double after, ulong minute, PlannerAction expected)
    {
        const ulong start = 86400000, m = 60000;
        var options = new DirectorSessionOptions { MeridianBeforeMinutes = before, MeridianAfterMinutes = after };
        var goal = new PlannerGoal("target", 10, 10, 0, 0, 10, 5 * m, 0, [new(start, start + 360 * m)],
            new(new(start - 360 * m, start + 720 * m), [start + 180 * m]));
        var request = new PlannerRequest(new("assignment", 1, "rig-test", "config", start, start + 360 * m, [goal]),
            new("rig-test", "config", start + minute * m, start + 360 * m, PlannerSafety.Safe, true, false, options.MeridianExclusion()));
        await using var controller = new RuntimeController(ProcessTests.BundleDirectory);
        await controller.StartAsync("rig-test");
        var result = await controller.EvaluateAsync(request);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Decision!.Action);
        if (expected == PlannerAction.Wait)
        {
            var other = goal with { Id = "other-target", Priority = 1, Transits = goal.Transits! with { TransitsMs = [] } };
            var alternative = await controller.EvaluateAsync(request with { Assignment = request.Assignment with { Goals = [goal, other] } });
            Assert.Equal("other-target", alternative.Decision!.GoalId);
        }
    }

    private static IProfile Profile(string name)
    {
        var p = new Mock<IProfile>();
        p.SetupGet(p => p.Id).Returns(Guid.NewGuid());
        p.SetupGet(p => p.Name).Returns(name);
        p.SetupGet(p => p.PluginSettings).Returns(new PluginSettings());
        return p.Object;
    }

    private sealed class Fixture
    {
        internal IProfile Profile { get; } = DirectorSessionDefaultsTests.Profile("Test rig");
        internal Mock<IProfileService> Profiles { get; } = new();
        internal DirectorSessionDefaults Store { get; }
        internal Fixture() { Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile); Store = new(Profiles.Object); }
        internal DirectorAcquisition Acquisition() => new(Profiles.Object, Mock.Of<ICameraMediator>(), Mock.Of<ITelescopeMediator>(),
            Mock.Of<IFilterWheelMediator>(), Mock.Of<ISafetyMonitorMediator>(), Mock.Of<IImagingMediator>(),
            Mock.Of<IImageSaveMediator>(), Mock.Of<IImageHistoryVM>(), Mock.Of<INighttimeCalculator>(), Mock.Of<IDomeMediator>());
    }
}
