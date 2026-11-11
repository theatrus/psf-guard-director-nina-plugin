using Moq;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem.Guider;
using PsfGuard.Director.Plugin.Acquisition;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaEnsureGuidingTests
{
    [Theory]
    [InlineData("Guiding", true)]
    [InlineData("Stopped", false)]
    [InlineData("Selected", false)]
    [InlineData("Calibrating", false)]
    [InlineData("LostLock", false)]
    [InlineData("Paused", false)]
    [InlineData("Looping", false)]
    public async Task FreshStateControlsWhetherNativeStartAndItsEventAreIssued(string state, bool alreadyGuiding)
    {
        var fixture = new Fixture();
        // Deliberately stale display state must not decide whether to restart.
        fixture.Device.SetupGet(d => d.State).Returns(alreadyGuiding ? "Stopped" : "Guiding");
        var reads = 0;
        var item = fixture.Item((_, token) => NinaEnsureGuiding.ReadPHD2GuidingAsync(() =>
        {
            reads++;
            return Task.FromResult(Response(state));
        }, token));
        Assert.True(item.Validate());
        await ((NinaEnsureGuiding)item.Clone()).Execute(new Progress<ApplicationStatus>(), default);
        Assert.Equal(1, reads);
        fixture.Guider.Verify(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
            alreadyGuiding ? Times.Never() : Times.Once());
        fixture.Guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"result\":true}")]
    [InlineData("{\"result\":\"\"}")]
    [InlineData("{\"result\":\"Unknown\"}")]
    [InlineData("{\"result\":\"Guiding\",\"error\":{\"code\":1,\"message\":\"failed\"}}")]
    public async Task InvalidResponseCannotSkipTheCheckOrStartGuiding(string json)
    {
        var fixture = new Fixture();
        var item = fixture.Item((_, token) => NinaEnsureGuiding.ReadPHD2GuidingAsync(
            () => Task.FromResult(JsonConvert.DeserializeObject<GenericPhdMethodResponse>(json)!), token));
        Assert.Contains("valid guiding state", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            item.Execute(new Progress<ApplicationStatus>(), default))).Message);
        fixture.Guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("configured-guider")]
    [InlineData("disconnected")]
    [InlineData("device-disconnected")]
    [InlineData("device-replaced")]
    public async Task BindingChangesWhileReadingStateCannotBeUsedToSkipOrStart(string change)
    {
        var fixture = new Fixture();
        var item = fixture.Item((_, _) =>
        {
            switch (change)
            {
                case "profile": fixture.Profiles.SetupGet(p => p.ActiveProfile).Returns(Mock.Of<IProfile>()); break;
                case "configured-guider": fixture.Profile.SetupGet(p => p.GuiderSettings.GuiderName).Returns("other"); break;
                case "disconnected": fixture.Info.Connected = false; break;
                case "device-disconnected": fixture.Device.SetupGet(d => d.Connected).Returns(false); break;
                case "device-replaced": fixture.Guider.Setup(g => g.GetDevice()).Returns(Mock.Of<IGuider>(d => d.Connected && d.Id == "guider")); break;
            }
            return Task.FromResult(true);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => item.Execute(new Progress<ApplicationStatus>(), default));
        fixture.Guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationDuringStateQueryCannotStartGuiding()
    {
        var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        var item = fixture.Item((_, token) => NinaEnsureGuiding.ReadPHD2GuidingAsync(() =>
        {
            canceled.Cancel();
            return Task.FromResult(Response("Stopped"));
        }, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => item.Execute(new Progress<ApplicationStatus>(), canceled.Token));
        fixture.Guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransportFailureDoesNotStartGuiding()
    {
        var fixture = new Fixture();
        var item = fixture.Item((_, token) => NinaEnsureGuiding.ReadPHD2GuidingAsync(
            () => Task.FromException<GenericPhdMethodResponse>(new IOException("disconnected")), token));
        Assert.Contains("Could not check PHD2 guiding", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            item.Execute(new Progress<ApplicationStatus>(), default))).Message);
        fixture.Guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloneCannotRebindToAnotherProfile()
    {
        var fixture = new Fixture();
        var item = fixture.Item((_, _) => Task.FromResult(true));
        fixture.Profiles.SetupGet(p => p.ActiveProfile).Returns(Mock.Of<IProfile>(p => p.GuiderSettings.GuiderName == "guider"));
        var cloned = (NinaEnsureGuiding)item.Clone();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cloned.Execute(new Progress<ApplicationStatus>(), default));
        fixture.Guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NativeStartFailureStillFailsTheInstruction()
    {
        var fixture = new Fixture();
        fixture.Guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var item = fixture.Item((_, _) => Task.FromResult(false));
        Assert.Contains("Failed to start guiding", (await Assert.ThrowsAsync<NINA.Core.Model.SequenceEntityFailedException>(() =>
            item.Execute(new Progress<ApplicationStatus>(), default))).Message);
    }

    [Fact]
    public async Task OtherGuidersKeepTheirNativeDeviceSpecificStartBehavior()
    {
        var fixture = new Fixture();
        fixture.Device.SetupGet(d => d.State).Returns("Guiding");
        var item = new NinaEnsureGuiding(new StartGuiding(fixture.Guider.Object), fixture.Profiles.Object, fixture.Guider.Object);
        await item.Execute(new Progress<ApplicationStatus>(), default);
        fixture.Guider.Verify(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static GenericPhdMethodResponse Response(string state) => JsonConvert.DeserializeObject<GenericPhdMethodResponse>(
        JsonConvert.SerializeObject(new { jsonrpc = "2.0", result = state }))!;

    private sealed class Fixture
    {
        internal readonly Mock<IProfile> Profile = new() { DefaultValue = DefaultValue.Mock };
        internal readonly Mock<IProfileService> Profiles = new();
        internal readonly Mock<IGuiderMediator> Guider = new();
        internal readonly Mock<IGuider> Device = new();
        internal readonly GuiderInfo Info = new() { Connected = true, DeviceId = "guider" };
        internal Fixture()
        {
            Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile.Object);
            Profile.SetupGet(p => p.GuiderSettings.GuiderName).Returns("guider");
            Device.SetupGet(d => d.Connected).Returns(true);
            Device.SetupGet(d => d.Id).Returns("guider");
            Guider.Setup(g => g.GetDevice()).Returns(Device.Object);
            Guider.Setup(g => g.GetInfo()).Returns(Info);
            Guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }
        internal NinaEnsureGuiding Item(Func<IGuider, CancellationToken, Task<bool>> read) => new(new StartGuiding(Guider.Object), Profiles.Object, Guider.Object, read);
    }
}
