using Moq;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;
using CameraControl = PsfGuard.Director.Runtime.CameraControl;

namespace PsfGuard.Director.Tests;

public sealed class NinaEquipmentTests
{
    [Fact]
    public void SnapshotCopiesCapabilitiesAndUsesExplicitStableMappings()
    {
        var f = new Fixture();
        var result = f.Read();
        Assert.Equal(new DirectorFilter("filter-l", 2), Assert.Single(result.Filters));
        Assert.Equal(new CameraBinning(1, 1), result.BinningModes[0]);
        Assert.Equal(new short[] { 0, 1 }, result.ReadoutModes);
        Assert.Equal(new[] { 0, 40, 100 }, Assert.IsType<CameraControl.Values>(result.Gain).Items);
        Assert.IsType<CameraControl.Unsupported>(result.Offset);
        Assert.Equal(2UL, result.ExposureMinMs);
        Assert.Equal(60000UL, result.ExposureMaxMs);
        Assert.StartsWith("device-", result.CameraId);
        Assert.DoesNotContain("Camera Driver", result.CameraId);
        Assert.Equal(f.Read().Id, result.Id);
        f.Camera.BinningModes[0].X = 3;
        f.Camera.Gains[1] = 60;
        Assert.Equal(new CameraBinning(1, 1), result.BinningModes[0]);
        Assert.Equal(40, Assert.IsType<CameraControl.Values>(result.Gain).Items[1]);
        Assert.NotEqual(result.Id, f.Read().Id);
    }

    [Fact]
    public void UnorderedCapabilitySetsAreCanonicalButReadoutOrderIsSignificant()
    {
        var f = new Fixture();
        var before = f.Read();
        f.Camera.Gains = [100, 0, 40];
        f.Camera.BinningModes = new([new(2, 2), new(1, 1)]);
        Assert.Equal(before.Id, f.Read().Id);
        f.Camera.ReadoutModes = ["Fast", "Normal"];
        Assert.NotEqual(before.Id, f.Read().Id);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("camera")]
    [InlineData("camera-disconnected")]
    [InlineData("wheel")]
    [InlineData("wheel-disconnected")]
    [InlineData("slot")]
    [InlineData("name")]
    [InlineData("subframe")]
    public void ContextChangesBlockExport(string fault)
    {
        var f = new Fixture();
        switch (fault)
        {
            case "profile": f.Profile.SetupGet(p => p.Id).Returns(Guid.NewGuid()); break;
            case "camera": f.Camera.DeviceId = "other"; break;
            case "camera-disconnected": f.Camera.Connected = false; break;
            case "wheel": f.Wheel.DeviceId = "other"; break;
            case "wheel-disconnected": f.Wheel.Connected = false; break;
            case "slot": f.Filters[0].Position = 3; break;
            case "name": f.Filters[0].Name = "R"; break;
            case "subframe": f.Camera.IsSubSampleEnabled = true; break;
        }
        Assert.Throws<InvalidOperationException>(() => f.Read());
    }

    [Theory]
    [InlineData("empty-bins")]
    [InlineData("duplicate-bins")]
    [InlineData("negative-bin")]
    [InlineData("empty-readout")]
    [InlineData("oversized-readout")]
    [InlineData("oversized-bins")]
    [InlineData("oversized-gain")]
    [InlineData("negative-gain")]
    [InlineData("duplicate-gain")]
    [InlineData("bad-range")]
    [InlineData("bad-offset")]
    [InlineData("nan-exposure")]
    [InlineData("tiny-exposure")]
    [InlineData("reversed-exposure")]
    public void UnrepresentableCapabilitiesDoNotBecomeDefaults(string fault)
    {
        var f = new Fixture();
        switch (fault)
        {
            case "empty-bins": f.Camera.BinningModes = new([]); break;
            case "duplicate-bins": f.Camera.BinningModes = new([new(1, 1), new(1, 1)]); break;
            case "negative-bin": f.Camera.BinningModes[0].X = -1; break;
            case "empty-readout": f.Camera.ReadoutModes = []; break;
            case "oversized-readout": f.Camera.ReadoutModes = Enumerable.Repeat("Normal", 257); break;
            case "oversized-bins": f.Camera.BinningModes = new(Enumerable.Range(1, 257).Select(i => new BinningMode((short)i, (short)i))); break;
            case "oversized-gain": f.Camera.Gains = Enumerable.Range(0, 257).ToArray(); break;
            case "negative-gain": f.Camera.Gains = [-1]; break;
            case "duplicate-gain": f.Camera.Gains = [1, 1]; break;
            case "bad-range": f.Camera.Gains = []; f.Camera.GainMin = 10; f.Camera.GainMax = 1; break;
            case "bad-offset": f.Camera.CanSetOffset = true; f.Camera.OffsetMin = -1; break;
            case "nan-exposure": f.Camera.ExposureMin = double.NaN; break;
            case "tiny-exposure": f.Camera.ExposureMax = 0.0001; break;
            case "reversed-exposure": f.Camera.ExposureMin = 70; break;
        }
        Assert.Throws<InvalidDataException>(() => f.Read());
    }

    [Fact]
    public void RangesAndFixedFilterAreExplicit()
    {
        var f = new Fixture();
        f.Camera.Gains = [];
        f.Camera.GainMin = 10;
        f.Camera.GainMax = 200;
        f.Camera.CanSetOffset = true;
        f.Camera.OffsetMin = 0;
        f.Camera.OffsetMax = 50;
        f.WheelSettings.SetupGet(p => p.Id).Returns("No_Device");
        f.Wheel.Connected = false;
        f.Binding = f.Binding with { FilterWheelDeviceId = null, Filters = [new("osc", null, null)] };
        var result = f.Read();
        Assert.Equal(new CameraControl.Range(10, 200), result.Gain);
        Assert.Equal(new CameraControl.Range(0, 50), result.Offset);
        Assert.Null(result.FilterWheelId);
        Assert.Null(result.Filters[0].Position);
        f.WheelSettings.SetupGet(p => p.Id).Returns("Wheel Driver");
        Assert.Throws<InvalidOperationException>(() => f.Read());
    }

    [Fact]
    public void AscomAbsentGainSentinelsDoNotAdvertiseAControl()
    {
        var f = new Fixture();
        f.Camera.CanSetGain = true;
        f.Camera.CanGetGain = false;
        f.Camera.Gains = [];
        f.Camera.GainMin = -1;
        f.Camera.GainMax = -1;
        Assert.IsType<CameraControl.Unsupported>(f.Read().Gain);
        f.Camera.CanGetGain = true;
        Assert.Throws<InvalidDataException>(() => f.Read());
        f.Camera.CanGetGain = false;
        f.Camera.GainMax = 10;
        Assert.Throws<InvalidDataException>(() => f.Read());
    }

    [Fact]
    public void ChangingMediatorSnapshotDuringReadFailsClosed()
    {
        var f = new Fixture();
        var reads = 0;
        f.CameraMediator.Setup(m => m.GetInfo()).Returns(() =>
        {
            if (++reads == 2) f.Camera.DriverVersion = "new";
            return f.Camera;
        });
        Assert.Throws<InvalidOperationException>(() => f.Read());
    }

    [Fact]
    public void RevisionChangesIncludeConstraintsOptionsAndDriver()
    {
        var f = new Fixture();
        var initial = f.Read().Id;
        f.Binding = f.Binding with { ConstraintRevision = "horizon-2" };
        var constraints = f.Read().Id;
        Assert.NotEqual(initial, constraints);
        f.Binding = f.Binding with { DitherEvery = 2 };
        var options = f.Read().Id;
        Assert.NotEqual(constraints, options);
        f.Camera.DriverVersion = "new";
        Assert.NotEqual(options, f.Read().Id);
    }

    [Fact]
    public async Task NativeSnapshotIsAcceptedByTheSharedRustProgramValidator()
    {
        var f = new Fixture();
        var configuration = f.Read();
        var request = PlannerTests.Request();
        var program = new DirectorProgram(1, request.Assignment with { ConfigurationId = configuration.Id }, configuration,
            [new("target", "M42", 298800000, -18000000, null)],
            [new("recipe", 1000, "filter-l", new(1, 1), 40, null, 0, null)], [new("goal", "target", "recipe")]);
        var directory = Directory.CreateTempSubdirectory("director-native-capabilities-");
        try
        {
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await runtime.StartAsync("rig-test");
            var opened = await runtime.OpenProgramAsync(program, request.State with { ConfigurationId = configuration.Id });
            Assert.Null(opened.Error);
            Assert.Equal(configuration.Id, opened.Value!.ConfigurationId);
            await runtime.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    internal sealed class Fixture
    {
        internal readonly Mock<IProfile> Profile = new();
        internal readonly Mock<IProfileService> Profiles = new();
        internal readonly Mock<ICameraMediator> CameraMediator = new();
        internal readonly Mock<IFilterWheelMediator> WheelMediator = new();
        internal readonly Mock<IFilterWheelSettings> WheelSettings = new();
        internal readonly ObserveAllCollection<FilterInfo> Filters = new([new() { Position = 2, Name = "L" }]);
        internal readonly CameraInfo Camera = new()
        {
            Connected = true,
            DeviceId = "Camera Driver",
            DriverVersion = "1",
            ExposureMin = 0.0011,
            ExposureMax = 60.0009,
            BinningModes = new([new(1, 1), new(2, 2)]),
            ReadoutModes = new[] { "Normal", "Fast" },
            CanSetGain = true,
            Gains = new List<int> { 0, 40, 100 },
            CanSetOffset = false
        };
        internal readonly FilterWheelInfo Wheel = new() { Connected = true, DeviceId = "Wheel Driver", DriverVersion = "1" };
        internal NinaEquipmentBinding Binding;
        internal Fixture()
        {
            var profileId = Guid.NewGuid();
            Binding = new(profileId, "rig-test", "constraints-1", "Camera Driver", "Wheel Driver", [new("filter-l", 2, "L")], true, 0);
            var cameraSettings = new Mock<ICameraSettings>();
            cameraSettings.SetupGet(s => s.Id).Returns("Camera Driver");
            Profile.SetupGet(p => p.Id).Returns(profileId);
            Profile.SetupGet(p => p.CameraSettings).Returns(cameraSettings.Object);
            Profile.SetupGet(p => p.FilterWheelSettings).Returns(WheelSettings.Object);
            WheelSettings.SetupGet(s => s.Id).Returns("Wheel Driver");
            WheelSettings.SetupGet(s => s.FilterWheelFilters).Returns(Filters);
            Profiles.SetupGet(s => s.ActiveProfile).Returns(Profile.Object);
            CameraMediator.Setup(m => m.GetInfo()).Returns(Camera);
            WheelMediator.Setup(m => m.GetInfo()).Returns(Wheel);
        }
        internal DirectorConfiguration Read() => new NinaEquipmentSnapshot(Profiles.Object, CameraMediator.Object, WheelMediator.Object).Read(Binding);
    }
}
