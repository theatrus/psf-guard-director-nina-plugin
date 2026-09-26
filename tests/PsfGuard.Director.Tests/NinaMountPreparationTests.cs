using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem.Telescope;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaMountPreparationTests
{
    private static readonly IProgress<ApplicationStatus> Progress = new Progress<ApplicationStatus>();

    [Fact]
    public void UnboundLegacyConfigurationKeepsItsOriginalFingerprint()
    {
        var f = new Fixture();
        var unbound = f.Native.Binding with { TelescopeDeviceId = null };
        var original = new NinaEquipmentSnapshot(f.Native.Profiles.Object, f.Native.CameraMediator.Object, f.Native.WheelMediator.Object).Read(unbound);
        Assert.Equal(original.Id, f.Reader.Read(unbound).Id);
    }

    [Fact]
    public void MountChangeBetweenCopiedReadsCannotExportAConfiguration()
    {
        var f = new Fixture();
        var reads = 0;
        f.Mount.Setup(x => x.GetInfo()).Returns(() =>
        {
            if (++reads == 2) f.Info.DriverVersion = "2";
            return f.Info;
        });
        Assert.Throws<InvalidOperationException>(() => f.Reader.Read(f.Native.Binding));
    }

    [Fact]
    public void MountFingerprintTracksIdentityAndCapabilitiesButNotPositionOrParkState()
    {
        var f = new Fixture();
        var initial = f.Reader.Read(f.Native.Binding).Id;
        f.Info.AtPark = false;
        f.Info.Slewing = true;
        f.Info.TrackingEnabled = true;
        Assert.Equal(initial, f.Reader.Read(f.Native.Binding).Id);
        f.Info.DriverVersion = "2";
        Assert.NotEqual(initial, f.Reader.Read(f.Native.Binding).Id);
        var driverChanged = f.Reader.Read(f.Native.Binding).Id;
        f.Info.CanSlew = false;
        Assert.NotEqual(driverChanged, f.Reader.Read(f.Native.Binding).Id);
        var renamed = f.Native.Binding with { TelescopeDeviceId = "Other Mount" };
        f.Settings.SetupGet(x => x.Id).Returns("Other Mount");
        f.Info.DeviceId = "Other Mount";
        Assert.NotEqual(initial, f.Reader.Read(renamed).Id);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("disconnected")]
    [InlineData("device")]
    [InlineData("missing-mediator")]
    public void MountExportRejectsIncompleteOrChangedBindings(string fault)
    {
        var f = new Fixture();
        if (fault == "profile") f.Settings.SetupGet(x => x.Id).Returns("Changed");
        if (fault == "disconnected") f.Info.Connected = false;
        if (fault == "device") f.Info.DeviceId = "Changed";
        var reader = fault == "missing-mediator"
            ? new NinaEquipmentSnapshot(f.Native.Profiles.Object, f.Native.CameraMediator.Object, f.Native.WheelMediator.Object)
            : f.Reader;
        Assert.Throws<InvalidOperationException>(() => reader.Read(f.Native.Binding));
    }

    [Fact]
    public async Task NativeUnparkReportsSuccessAndCannotReplayAfterReset()
    {
        var f = new Fixture();
        f.Mount.Setup(x => x.UnparkTelescope(Progress, It.IsAny<CancellationToken>())).ReturnsAsync(() => { f.Info.AtPark = false; return true; });
        var issued = f.Create();
        Assert.IsAssignableFrom<UnparkScope>(issued.Item);
        await issued.Item.Run(Progress, default);
        Assert.Equal(SequenceEntityStatus.FINISHED, issued.Item.Status);
        Assert.IsType<PreparationOutcome.Succeeded>(issued.Fence.Completion!.Outcome);
        Assert.Throws<NotSupportedException>(() => issued.Item.Clone());
        issued.Item.ResetProgress();
        await issued.Item.Run(Progress, default);
        Assert.Equal(SequenceEntityStatus.FAILED, issued.Item.Status);
        f.Mount.Verify(x => x.UnparkTelescope(Progress, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("parked")]
    [InlineData("disconnected")]
    [InlineData("driver")]
    [InlineData("cancel")]
    public async Task UnconfirmedUnparkNeverCreditsSuccessfulPreparation(string fault)
    {
        var f = new Fixture();
        f.Mount.Setup(x => x.UnparkTelescope(Progress, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            if (fault == "cancel") throw new OperationCanceledException();
            if (fault != "parked") f.Info.AtPark = false;
            if (fault == "disconnected") f.Info.Connected = false;
            if (fault == "driver") f.Info.DriverVersion = "2";
            return fault != "false";
        });
        var issued = f.Create();
        await Assert.ThrowsAnyAsync<Exception>(() => issued.Item.Execute(Progress, default));
        Assert.IsType<PreparationOutcome.Uncertain>(issued.Fence.Completion!.Outcome);
    }

    [Theory]
    [InlineData("slewing")]
    [InlineData("device")]
    public async Task BoundaryChangesPreventUnparkDispatch(string fault)
    {
        var f = new Fixture();
        var issued = f.Create(_ =>
        {
            if (fault == "slewing") f.Info.Slewing = true;
            else f.Info.DeviceId = "Changed";
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => issued.Item.Execute(Progress, default));
        Assert.IsType<PreparationOutcome.Failed>(issued.Fence.Completion!.Outcome);
        f.Mount.Verify(x => x.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void UnparkNeedsExplicitLocalMountBinding()
    {
        var f = new Fixture();
        var factory = new NinaPreparationItems(f.Native.Profiles.Object, f.Native.CameraMediator.Object, f.Native.WheelMediator.Object, f.Reader, TimeProvider.System);
        Assert.Throws<NotSupportedException>(() => factory.Create(f.Next, f.Program, f.Native.Binding, _ => Task.CompletedTask));
        Assert.Throws<NotSupportedException>(() => f.Factory.Create(f.Next, f.Program, f.Native.Binding with { TelescopeDeviceId = null }, _ => Task.CompletedTask));
    }

    private sealed class Fixture
    {
        internal readonly NinaEquipmentTests.Fixture Native = new();
        internal readonly Mock<ITelescopeMediator> Mount = new();
        internal readonly Mock<ITelescopeSettings> Settings = new();
        internal readonly TelescopeInfo Info = new() { Connected = true, DeviceId = "Mount", DriverVersion = "1", AtPark = true, CanPark = true, CanSlew = true };
        internal readonly NinaEquipmentSnapshot Reader;
        internal readonly NinaPreparationItems Factory;
        internal readonly DirectorProgram Program;
        internal readonly PreparationNext Next = new PreparationNext.Run(new("prep", 1, "goal", "target", "recipe", new PreparationOperation.Unpark()));
        internal Fixture()
        {
            Native.Binding = Native.Binding with { TelescopeDeviceId = "Mount" };
            Native.Profile.SetupGet(x => x.TelescopeSettings).Returns(Settings.Object);
            Settings.SetupGet(x => x.Id).Returns("Mount");
            Mount.Setup(x => x.GetInfo()).Returns(Info);
            Reader = new(Native.Profiles.Object, Native.CameraMediator.Object, Native.WheelMediator.Object, Mount.Object);
            var config = Reader.Read(Native.Binding);
            Program = new(1, PlannerTests.Request().Assignment with { ConfigurationId = config.Id }, config,
                [new("target", "Target", 648000000, 72000000, null)],
                [new("recipe", 1000, "filter-l", new(1, 1), 40, null, 1, null)], [new("goal", "target", "recipe")]);
            Factory = new(Native.Profiles.Object, Native.CameraMediator.Object, Native.WheelMediator.Object, Reader, TimeProvider.System, Mount.Object);
        }
        internal NinaIssuedItem Create(Func<CancellationToken, Task>? validate = null) =>
            Factory.Create(Next, Program, Native.Binding, validate ?? (_ => Task.CompletedTask));
    }
}
