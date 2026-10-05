using Moq;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaMeridianFlipTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(600, true)]
    public void NativeTriggerReservesTheUpcomingInstructionDuration(int seconds, bool expected)
    {
        var f = new Fixture();
        f.Info.TimeToMeridianFlip = 5.0 / 60;
        var next = new Mock<ISequenceItem>();
        next.Setup(x => x.GetEstimatedDuration()).Returns(TimeSpan.FromSeconds(seconds));
        Assert.Equal(expected, f.Trigger.ShouldTrigger(null!, next.Object));
    }

    [Fact]
    public void AutomaticNativeTriggerIsNotSavedOrClonedIntoUserSequence()
    {
        var f = new Fixture();
        var session = new DirectorSessionContainer();
        session.AddRuntimeTrigger(f.Trigger);
        Assert.Single(session.GetTriggersSnapshot());
        var serializer = Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings
        { PreserveReferencesHandling = Newtonsoft.Json.PreserveReferencesHandling.Objects });
        var serialized = Newtonsoft.Json.Linq.JObject.FromObject(session, serializer);
        Assert.Empty((Newtonsoft.Json.Linq.JArray)serialized["Triggers"]!);
        var clone = (DirectorSessionContainer)session.Clone();
        Assert.Empty(clone.GetTriggersSnapshot());
        session.RemoveRuntimeTrigger(f.Trigger);
        session.Add(f.Trigger);
        Assert.Single((Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.FromObject(session, serializer)["Triggers"]!);
        Assert.Single(((DirectorSessionContainer)session.Clone()).GetTriggersSnapshot());
    }

    [Theory]
    [InlineData("Flip")]
    [InlineData("StopAutoguider")]
    [InlineData("SelectNewGuideStar")]
    [InlineData("ResumeAutoguider")]
    [InlineData("Recenter")]
    public async Task FailedNativeWorkflowStepCannotBeHiddenBySuccessfulVmResult(string step)
    {
        var f = new Fixture();
        // NINA's legacy collection assumes a WPF context, or no context.
        var steps = await Task.Run(() =>
        {
            var workflow = new NINA.WPF.Base.ViewModel.AutomatedWorkflow();
            workflow.Add(new NINA.WPF.Base.ViewModel.WorkflowStep(step, step, () => Task.FromResult(false)));
            return workflow;
        });
        f.Vm.SetupGet(x => x.Steps).Returns(steps);
        f.Vm.Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await f.Trigger.Run(new SequentialContainer(), new Progress<ApplicationStatus>(), default);
        Assert.Equal(SequenceEntityStatus.FAILED, f.Trigger.Status);
    }

    [Fact]
    public void NativePauseBeforeMeridianRemainsAHardLimit()
    {
        var f = new Fixture();
        f.Profile.SetupGet(x => x.MeridianFlipSettings.PauseTimeBeforeMeridian).Returns(4);
        f.Info.TimeToMeridianFlip = 5.0 / 60;
        var next = new Mock<ISequenceItem>();
        next.Setup(x => x.GetEstimatedDuration()).Returns(TimeSpan.FromSeconds(1));
        Assert.True(f.Trigger.ShouldTrigger(null!, next.Object));
    }

    [Theory]
    [InlineData("parked")]
    [InlineData("disconnected")]
    [InlineData("not-tracking")]
    public void NativeTriggerDoesNotFlipAnUnavailableMount(string state)
    {
        var f = new Fixture();
        if (state == "parked") f.Info.AtPark = true;
        if (state == "disconnected") f.Info.Connected = false;
        if (state == "not-tracking") f.Info.TrackingEnabled = false;
        Assert.False(f.Trigger.ShouldTrigger(null!, null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InheritedNativeFlipRunsBeforeGuardedDispatch(bool success)
    {
        var f = new Fixture();
        f.Vm.Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(success);
        var session = new DirectorSessionContainer();
        var factory = new Mock<INinaActionFactory>();
        factory.Setup(x => x.GetTrigger<MeridianFlipTrigger>()).Returns(f.Trigger);
        var options = new DirectorSessionOptions
        {
            Focus = DirectorOperationOwner.Sequence,
            Guiding = DirectorOperationOwner.Sequence,
            Dither = DirectorOperationOwner.Sequence,
            SlewCenter = DirectorOperationOwner.Sequence,
            MeridianFlip = DirectorOperationOwner.Director
        };
        using var native = new NinaNativeImaging(factory.Object, f.Profiles.Object,
            Mock.Of<IGuiderMediator>(), Mock.Of<IFocuserMediator>(), Mock.Of<IRotatorMediator>(), options);
        native.Install(session);
        var inner = new SequentialContainer();
        inner.AttachNewParent(session);
        var dispatch = new GuardedDispatch(native.CheckTriggers);
        inner.Add(dispatch);
        await inner.Run(new Progress<ApplicationStatus>(), default);
        Assert.Equal(success, dispatch.Dispatched);
        Assert.Equal(success ? SequenceEntityStatus.FINISHED : SequenceEntityStatus.FAILED, f.Trigger.Status);
        f.Vm.Verify(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(session.ActionHistory, x => x.Action == "Meridian flip" && x.Outcome == (success ? "Succeeded" : "Failed or interrupted"));
        native.Dispose();
        Assert.Empty(session.GetTriggersSnapshot());
    }

    private sealed class GuardedDispatch(Action check) : SequenceItem
    {
        internal bool Dispatched { get; private set; }
        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(600);
        public override object Clone() => throw new NotSupportedException();
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            check();
            Dispatched = true;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeRunPropagatesTheFlipResult(bool success)
    {
        var f = new Fixture();
        f.Vm.Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(success);
        await f.Trigger.Run(new SequentialContainer(), new Progress<ApplicationStatus>(), default);
        Assert.Equal(success ? SequenceEntityStatus.FINISHED : SequenceEntityStatus.FAILED, f.Trigger.Status);
        f.Vm.Verify(x => x.MeridianFlip(f.Info.Coordinates, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsType<NinaMeridianFlipTrigger>(f.Trigger.Clone());
    }

    [Fact]
    public async Task SwallowedNativeCancellationDoesNotBecomeSuccess()
    {
        var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        f.Vm.Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cancel.Cancel(); return Task.FromResult(true); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Trigger.Execute(new SequentialContainer(), new Progress<ApplicationStatus>(), cancel.Token));
    }

    [Fact]
    public void SequenceOwnershipPreservesUserFlipTriggerAndDirectorRefusesDuplicates()
    {
        var f = new Fixture();
        var parent = new SequentialContainer();
        var session = new DirectorSessionContainer();
        parent.Add(session);
        parent.Add(f.Trigger);
        Assert.Throws<InvalidOperationException>(() => NinaNativeImaging.ValidateOwnership(session, session.Options));
        session.Options.MeridianFlip = DirectorOperationOwner.Sequence;
        NinaNativeImaging.ValidateOwnership(session, session.Options);
        Assert.Same(f.Trigger, Assert.Single(parent.GetTriggersSnapshot()));
    }

    private sealed class Fixture
    {
        internal readonly Mock<IProfile> Profile = new() { DefaultValue = DefaultValue.Mock };
        internal readonly Mock<IProfileService> Profiles = new();
        internal readonly TelescopeInfo Info = new()
        {
            Connected = true,
            TrackingEnabled = true,
            AtPark = false,
            AtHome = false,
            Coordinates = new Coordinates(5, 20, Epoch.J2000, Coordinates.RAType.Hours),
            TimeToMeridianFlip = 0
        };
        internal readonly Mock<IMeridianFlipVM> Vm = new();
        internal readonly NinaMeridianFlipTrigger Trigger;
        internal Fixture()
        {
            Profiles.SetupGet(x => x.ActiveProfile).Returns(Profile.Object);
            var telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(x => x.GetInfo()).Returns(Info);
            telescope.Setup(x => x.GetCurrentPosition()).Returns(() => Info.Coordinates);
            var factory = new Mock<IMeridianFlipVMFactory>();
            factory.Setup(x => x.Create()).Returns(Vm.Object);
            Trigger = new(Profiles.Object, Mock.Of<ICameraMediator>(), telescope.Object, Mock.Of<IFocuserMediator>(),
                Mock.Of<IApplicationStatusMediator>(), factory.Object, Mock.Of<ISafetyMonitorMediator>());
        }
    }
}
