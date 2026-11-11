using Moq;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.Trigger;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

[CollectionDefinition("Director equipment reporting", DisableParallelization = true)]
public sealed class DirectorEquipmentReportingCollection;

[Collection("Director equipment reporting")]
public sealed class NinaNativeImagingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AutofocusChoicesIndependentlyControlTargetSetupAndFilterTrigger(bool targetStart, bool filterChange)
    {
        var f = new Fixture();
        f.Options.AutofocusOnTargetStart = targetStart;
        f.Options.AutofocusOnFilterChange = filterChange;
        f.Options.Guiding = f.Options.MeridianFlip = DirectorOperationOwner.Sequence;
        f.Factory.Setup(x => x.GetItem<NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus>()).Returns(() =>
            new NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus(f.Profiles.Object, Mock.Of<IImageHistoryVM>(),
                Mock.Of<ICameraMediator>(c => c.GetInfo() == new NINA.Equipment.Equipment.MyCamera.CameraInfo { Connected = true }),
                Mock.Of<IFilterWheelMediator>(), f.Focuser.Object, Mock.Of<IAutoFocusVMFactory>()));
        var filter = new Mock<ISequenceTrigger>().Object;
        f.Factory.Setup(x => x.GetTrigger<NINA.Sequencer.Trigger.Autofocus.AutofocusAfterFilterChange>()).Returns(filter);
        f.Factory.Setup(x => x.GetTrigger<NINA.Sequencer.Trigger.Autofocus.AutofocusAfterTimeTrigger>()).Returns(new Mock<ISequenceTrigger>().Object);
        f.Factory.Setup(x => x.GetTrigger<NINA.Sequencer.Trigger.Autofocus.AutofocusAfterTemperatureChangeTrigger>()).Returns(new Mock<ISequenceTrigger>().Object);
        using var native = f.Create();
        var slots = new NinaInstructionSlots();
        native.ConfigureTargetSetup(slots);
        Assert.Equal(targetStart ? 2 : 0, slots.Clone().BeforeNewTarget.Items.Count);
        var session = new DirectorSessionContainer();
        native.Install(session);
        Assert.Equal(filterChange, session.GetTriggersSnapshot().Contains(filter));
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<DirectorSessionOptions>(Newtonsoft.Json.JsonConvert.SerializeObject(f.Options))!.Clone();
        Assert.Equal(targetStart, restored.AutofocusOnTargetStart);
        Assert.Equal(filterChange, restored.AutofocusOnFilterChange);
    }

    [Fact]
    public void OlderSequencesKeepTheirAutofocusDefaults()
    {
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<DirectorSessionOptions>("{}")!;
        Assert.True(restored.AutofocusOnTargetStart);
        Assert.True(restored.AutofocusOnFilterChange);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalibrationSettingReachesNativeGuidingAndSurvivesCloning(bool force)
    {
        var f = new Fixture();
        f.Options.Focus = DirectorOperationOwner.Sequence;
        f.Options.ForceGuideCalibration = force;
        f.GuiderInfo.CanClearCalibration = true;
        var device = new Mock<NINA.Equipment.Interfaces.IGuider>();
        device.SetupGet(g => g.Connected).Returns(true);
        device.SetupGet(g => g.Id).Returns("guider");
        f.Guider.Setup(g => g.GetDevice()).Returns(device.Object);
        var stopped = false;
        f.Guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).ReturnsAsync(() => stopped = true);
        f.Guider.Setup(g => g.StartGuiding(force, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Assert.Equal(force, stopped); return true; });
        f.Factory.Setup(x => x.GetItem<StartGuiding>()).Returns(() => new StartGuiding(f.Guider.Object));
        using var native = f.Create();
        native.CheckEquipment();
        var slots = new NinaInstructionSlots();
        native.ConfigureTargetSetup(slots);
        var item = (SequenceItem)slots.Clone().BeforeNewTarget.Items[1];
        await item.Execute(new Progress<ApplicationStatus>(), default);
        f.Guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), force ? Times.Once() : Times.Never());
        f.Guider.Verify(g => g.StartGuiding(force, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(force, Newtonsoft.Json.JsonConvert.DeserializeObject<DirectorSessionOptions>(
            Newtonsoft.Json.JsonConvert.SerializeObject(f.Options))!.Clone().ForceGuideCalibration);
    }

    [Fact]
    public void CustomGuidingKeepsCalibrationInstructionsWithoutAddingAnotherStart()
    {
        var f = new Fixture();
        f.Options.Focus = f.Options.Guiding = DirectorOperationOwner.Sequence;
        f.Options.ForceGuideCalibration = true; // A saved Director setting cannot override the user's instruction.
        var session = new DirectorSessionContainer();
        session.Options.Guiding = DirectorOperationOwner.Sequence;
        session.BeforeNewTarget.Add(new StartGuiding(f.Guider.Object) { ForceCalibration = true });
        NinaNativeImaging.ValidateOwnership(session, session.Options);
        using var native = f.Create();
        native.ConfigureTargetSetup(session.Slots);
        var start = Assert.IsType<StartGuiding>(Assert.Single(session.Slots.Clone().BeforeNewTarget.Items));
        Assert.True(start.ForceCalibration);
        f.Factory.Verify(x => x.GetItem<StartGuiding>(), Times.Never);
    }

    [Fact]
    public void RecalibrationRequiresAGuiderThatCanClearCalibration()
    {
        var f = new Fixture();
        f.Options.ForceGuideCalibration = true;
        using var native = f.Create();
        Assert.Contains("cannot reset calibration", Assert.Throws<InvalidOperationException>(native.CheckEquipment).Message);
    }

    [Fact]
    public async Task FailedStopCannotProceedToCalibrationOrGuideStart()
    {
        var f = new Fixture();
        f.Options.Focus = DirectorOperationOwner.Sequence;
        f.Options.ForceGuideCalibration = true;
        f.GuiderInfo.CanClearCalibration = true;
        var device = new Mock<NINA.Equipment.Interfaces.IGuider>();
        device.SetupGet(g => g.Connected).Returns(true);
        device.SetupGet(g => g.Id).Returns("guider");
        f.Guider.Setup(g => g.GetDevice()).Returns(device.Object);
        f.Guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        f.Factory.Setup(x => x.GetItem<StartGuiding>()).Returns(() => new StartGuiding(f.Guider.Object));
        using var native = f.Create();
        var slots = new NinaInstructionSlots();
        native.ConfigureTargetSetup(slots);
        var item = (SequenceItem)slots.Clone().BeforeNewTarget.Items[1];
        Assert.Contains("did not confirm stopping", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            item.Execute(new Progress<ApplicationStatus>(), default))).Message);
        f.Guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void DisabledUserHookDoesNotDisableNativeDefaults()
    {
        var f = new Fixture();
        f.Options.Focus = DirectorOperationOwner.Sequence;
        f.Factory.Setup(x => x.GetItem<StartGuiding>()).Returns(() => new StartGuiding(f.Guider.Object));
        var slots = new NinaInstructionSlots();
        var user = slots.BeforeNewTarget;
        user.Status = SequenceEntityStatus.DISABLED;
        using var native = f.Create();
        native.ConfigureTargetSetup(slots);
        Assert.NotEqual(SequenceEntityStatus.DISABLED, slots.BeforeNewTarget.Status);
        Assert.Same(user, slots.BeforeNewTarget.Items[0]);
        Assert.Equal(SequenceEntityStatus.DISABLED, user.Status);
        Assert.IsType<NinaEnsureGuiding>(slots.BeforeNewTarget.Items[1]);
        Assert.Equal(2, slots.Clone().BeforeNewTarget.Items.Count);
    }

    [Theory]
    [InlineData(SequenceEntityStatus.FAILED)]
    [InlineData(SequenceEntityStatus.RUNNING)]
    [InlineData(SequenceEntityStatus.SKIPPED)]
    public void UnfinishedNativeTriggerCannotAuthorizeCaptureOrRelease(SequenceEntityStatus status)
    {
        var f = new Fixture();
        f.Options.Focus = f.Options.MeridianFlip = DirectorOperationOwner.Sequence;
        var trigger = NinaCompatibility.Create<NINA.Sequencer.Trigger.Guider.RestoreGuiding>(Mock.Of<ISafetyMonitorMediator>(), f.Guider.Object);
        f.Factory.Setup(x => x.GetTrigger<NINA.Sequencer.Trigger.Guider.RestoreGuiding>()).Returns(trigger);
        var session = new DirectorSessionContainer();
        using var native = f.Create();
        native.Install(session);
        trigger.Status = status;
        Assert.Contains("Check guiding", Assert.Throws<InvalidOperationException>(native.CheckTriggers).Message);
        native.Dispose();
        Assert.Empty(session.GetTriggersSnapshot());
        native.Dispose();
    }

    [Fact]
    public async Task PerExposureTriggerChecksGuidingWithoutForcingTargetCalibration()
    {
        var f = new Fixture();
        f.Options.Focus = f.Options.MeridianFlip = DirectorOperationOwner.Sequence;
        f.Options.ForceGuideCalibration = true;
        f.GuiderInfo.CanClearCalibration = true;
        f.Guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var trigger = NinaCompatibility.Create<NINA.Sequencer.Trigger.Guider.RestoreGuiding>(Mock.Of<ISafetyMonitorMediator>(), f.Guider.Object);
        f.Factory.Setup(x => x.GetTrigger<NINA.Sequencer.Trigger.Guider.RestoreGuiding>()).Returns(trigger);
        using var native = f.Create();
        native.Install(new DirectorSessionContainer());
        var cloned = (NINA.Sequencer.Trigger.Guider.RestoreGuiding)trigger.Clone();
        var check = Assert.IsType<NinaEnsureGuiding>(Assert.Single(cloned.TriggerRunner.GetItemsSnapshot()));
        await check.Execute(new Progress<ApplicationStatus>(), default);
        f.Guider.Verify(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        f.Guider.Verify(g => g.StartGuiding(true, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        f.Guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
    }
    private sealed class CustomDither(IGuiderMediator guider) : Dither(guider, Mock.Of<IProfileService>());

    [Fact]
    public void EquipmentReportShowsItsOwnFailureEvenWithAnInheritedOwnershipConflict()
    {
        var f = new Fixture();
        f.Profile.SetupGet(p => p.PluginSettings).Returns(new NINA.Profile.PluginSettings());
        var root = Directory.CreateTempSubdirectory("director-equipment-report-");
        try
        {
            var acquisition = new DirectorAcquisition(f.Profiles.Object, Mock.Of<ICameraMediator>(), Mock.Of<ITelescopeMediator>(),
                Mock.Of<IFilterWheelMediator>(), Mock.Of<ISafetyMonitorMediator>(), Mock.Of<IImagingMediator>(),
                Mock.Of<IImageSaveMediator>(), Mock.Of<IImageHistoryVM>(),
                Mock.Of<INighttimeCalculator>(), Mock.Of<IDomeMediator>(),
                f.Factory.Object, f.Guider.Object, f.Focuser.Object, f.Rotator.Object)
            { LocalStateRoot = root.FullName };
            var session = new DirectorSessionContainer(acquisition);
            session.Options.Enclosure = DirectorEnclosurePolicy.OpenAir;
            var parent = new SequentialContainer { Name = "Imaging plan" };
            var sibling = new SequentialContainer { Name = "Other target" };
            parent.Add(sibling);
            parent.Add(session);
            var trigger = new DitherHook(f.Guider.Object);
            sibling.Add(trigger);
            NinaNativeImaging.ValidateOwnership(session, session.Options);

            sibling.Remove(trigger);
            parent.Add(trigger);
            session.Options.EnableAcquisition = true;
            Assert.False(session.Validate());
            var issue = Assert.Single(session.ConfigurationIssues, value => value.Contains("Director ownership of Dither"));
            Assert.Contains("Inherited from Imaging plan → trigger Custom cadence", issue);
            Assert.Contains("Set Dither to Sequence instructions / triggers", issue);

            Assert.True(session.ReportEquipmentCommand.CanExecute(null));
            session.ReportEquipmentCommand.Execute(null);
            Assert.Equal("Equipment report failed. Configure and pair a Director coordinator first.", session.SessionStatus);
            Assert.True(session.ReportEquipmentCommand.CanExecute(null));

            session.Options.Dither = DirectorOperationOwner.Sequence;
            NinaNativeImaging.ValidateOwnership(session, session.Options);
            Assert.Same(trigger, Assert.Single(parent.GetTriggersSnapshot()));
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class DitherHook : SequenceTrigger
    {
        private readonly IGuiderMediator guider;

        internal DitherHook(IGuiderMediator guider)
        {
            this.guider = guider;
            Name = "Custom cadence";
            TriggerRunner.Add(new CustomDither(guider) { Name = "Dither after a group" });
        }

        public override object Clone() => new DitherHook(guider);
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => true;
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
            throw new InvalidOperationException("Ownership validation must not execute the trigger.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDefaultsRefuseDuplicateUserInstructionsIncludingSubclasses(bool derived)
    {
        var session = new DirectorSessionContainer();
        var guider = Mock.Of<IGuiderMediator>(g => g.GetInfo() == new NINA.Equipment.Equipment.MyGuider.GuiderInfo { Connected = true });
        var item = derived ? new CustomDither(guider) : new Dither(guider, Mock.Of<IProfileService>());
        session.AfterEachExposure.Add(item);
        Assert.Throws<InvalidOperationException>(() => NinaNativeImaging.ValidateOwnership(session, session.Options));
        item.Status = SequenceEntityStatus.DISABLED;
        NinaNativeImaging.ValidateOwnership(session, session.Options);
        item.Status = SequenceEntityStatus.CREATED;
        session.Options.Dither = DirectorOperationOwner.Sequence;
        NinaNativeImaging.ValidateOwnership(session, session.Options);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("guider")]
    [InlineData("guider-disconnected")]
    [InlineData("focuser")]
    [InlineData("focuser-disconnected")]
    [InlineData("rotator")]
    public void BoundNativeDevicesCannotChange(string change)
    {
        var f = new Fixture();
        using var native = f.Create();
        native.CheckEquipment();
        switch (change)
        {
            case "profile": f.Profile.SetupGet(p => p.Id).Returns(Guid.NewGuid()); break;
            case "guider": f.GuiderInfo.DeviceId = "other"; break;
            case "guider-disconnected": f.GuiderInfo.Connected = false; break;
            case "focuser": f.FocuserInfo.DeviceId = "other"; break;
            case "focuser-disconnected": f.FocuserInfo.Connected = false; break;
            case "rotator": f.RotatorInfo.DeviceId = "other"; break;
        }
        Assert.Throws<InvalidOperationException>(native.CheckEquipment);
    }

    [Fact]
    public async Task NativeDitherFailureAndCancellationAreNotSuccess()
    {
        var f = new Fixture();
        f.Factory.Setup(x => x.GetItem<Dither>()).Returns(() => new Dither(f.Guider.Object, f.Profiles.Object));
        f.Guider.Setup(x => x.Dither(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        using var native = f.Create();
        var block = new DirectorSessionContainer().AfterEachExposure;
        await Assert.ThrowsAnyAsync<Exception>(() => native.PrepareAsync(new PreparationOperation.Dither(), block,
            new Progress<ApplicationStatus>(), CancellationToken.None));
        f.Guider.Setup(x => x.Dither(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.PrepareAsync(new PreparationOperation.Dither(), block,
            new Progress<ApplicationStatus>(), canceled.Token));
    }

    [Fact]
    public void NativeOwnershipAndDeviceIdsArePartOfEquipmentIdentity()
    {
        var f = new Fixture();
        var first = NinaNativeImaging.ScopeFor(f.Profiles.Object, f.Options);
        f.Options.Focus = DirectorOperationOwner.Sequence;
        Assert.NotEqual(first, NinaNativeImaging.ScopeFor(f.Profiles.Object, f.Options));
        f.Options.Focus = DirectorOperationOwner.Director;
        f.Profile.SetupGet(p => p.FocuserSettings.Id).Returns("other");
        Assert.NotEqual(first, NinaNativeImaging.ScopeFor(f.Profiles.Object, f.Options));
    }

    private sealed class Fixture
    {
        internal Mock<IProfile> Profile = new() { DefaultValue = DefaultValue.Mock };
        internal Mock<IProfileService> Profiles = new();
        internal Mock<INinaActionFactory> Factory = new();
        internal Mock<IGuiderMediator> Guider = new();
        internal Mock<IFocuserMediator> Focuser = new();
        internal Mock<IRotatorMediator> Rotator = new();
        internal NINA.Equipment.Equipment.MyGuider.GuiderInfo GuiderInfo = new() { Connected = true, DeviceId = "guider" };
        internal NINA.Equipment.Equipment.MyFocuser.FocuserInfo FocuserInfo = new() { Connected = true, DeviceId = "focuser" };
        internal NINA.Equipment.Equipment.MyRotator.RotatorInfo RotatorInfo = new() { Connected = true, DeviceId = "rotator" };
        internal DirectorSessionOptions Options = new();
        internal Fixture()
        {
            Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile.Object);
            Profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
            Profile.SetupGet(p => p.GuiderSettings.GuiderName).Returns("guider");
            Profile.SetupGet(p => p.FocuserSettings.Id).Returns("focuser");
            Profile.SetupGet(p => p.RotatorSettings.Id).Returns("rotator");
            Guider.Setup(g => g.GetInfo()).Returns(GuiderInfo);
            var device = new Mock<NINA.Equipment.Interfaces.IGuider>();
            device.SetupGet(g => g.Connected).Returns(true);
            device.SetupGet(g => g.Id).Returns("guider");
            Guider.Setup(g => g.GetDevice()).Returns(device.Object);
            Focuser.Setup(g => g.GetInfo()).Returns(FocuserInfo);
            Rotator.Setup(g => g.GetInfo()).Returns(RotatorInfo);
        }
        internal NinaNativeImaging Create() => new(Factory.Object, Profiles.Object, Guider.Object, Focuser.Object, Rotator.Object, Options);
    }
}
