using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.SequenceItem.Guider;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaNativeImagingTests
{
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
        Assert.IsType<StartGuiding>(slots.BeforeNewTarget.Items[1]);
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
        Assert.Contains("RestoreGuiding", Assert.Throws<InvalidOperationException>(native.CheckTriggers).Message);
        native.Dispose();
        Assert.Empty(session.GetTriggersSnapshot());
        native.Dispose();
    }
    private sealed class CustomDither(IGuiderMediator guider) : Dither(guider, Mock.Of<IProfileService>());

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
            Focuser.Setup(g => g.GetInfo()).Returns(FocuserInfo);
            Rotator.Setup(g => g.GetInfo()).Returns(RotatorInfo);
        }
        internal NinaNativeImaging Create() => new(Factory.Object, Profiles.Object, Guider.Object, Focuser.Object, Rotator.Object, Options);
    }
}
