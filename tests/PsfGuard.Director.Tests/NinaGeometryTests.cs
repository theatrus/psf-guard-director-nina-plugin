using System.Collections.Immutable;
using System.Text.Json;
using Moq;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaGeometryTests
{
    private const ulong Start = 1790409600000;
    private static NinaGeometryInputs Inputs() => new(9007199254740993UL, 89,
        new(0.1, 0.000001, -0.000001, Start, Start + 120001), [new("goal", -89, 89, 0)]);

    [Theory]
    [InlineData("0 -10\n100 0\n100.00000000000001 80\n100.00000000000003 0\n360 12", false)]
    [InlineData("[[10,0],[20,90],[30,180],[20,270],[12,360]]", true)]
    public void NativeBreakpointsAndIdentityAreExportedWithoutPaths(string text, bool mw4)
    {
        using var f = new Fixture(text, mw4);
        var input = Inputs();
        var result = f.Read(input);
        var rig = result.Constraints.Rig;
        Assert.Equal(result.Configuration.Id, rig.ConfigurationId);
        Assert.Equal(f.Devices.Binding.RigId, rig.RigId);
        Assert.Equal(input.Revision, rig.Revision);
        Assert.Same(input.Orientation, rig.Orientation);
        Assert.Equal(input.Goals, result.Constraints.Goals);
        Assert.Equal(-89, rig.MinimumAltitudeDegrees);
        Assert.Equal(89, rig.MaximumAltitudeDegrees);
        var points = Assert.IsType<DirectorHorizon.Custom>(rig.Horizon).Points;
        Assert.Equal(result.Native.Horizon.Points.Select(p => new DirectorHorizonPoint(p.AzimuthDegrees, p.AltitudeDegrees)), points);
        Assert.NotEqual(points[0].AltitudeDegrees, points[^1].AltitudeDegrees);
        Assert.DoesNotContain(f.Path, JsonSerializer.Serialize(result.Constraints));
        Assert.DoesNotContain(f.Path, JsonSerializer.Serialize(result));
        Assert.Equal(f.Devices.Binding.ConstraintRevision, result.Native.Revision);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("site")]
    [InlineData("flip")]
    [InlineData("minimum")]
    [InlineData("meridian")]
    public void LocalChangesCannotReuseTheOldEquipmentConfiguration(string fault)
    {
        using var f = new Fixture();
        var original = f.Read();
        switch (fault)
        {
            case "file": File.WriteAllText(f.Path, "0 -89\n180 80\n360 -89"); break;
            case "site": f.Astrometry.Object.Latitude = 36; break;
            case "flip": f.Flip.Object.MaxMinutesAfterMeridian = 20; break;
            case "minimum": f.Local = f.Local with { MinimumAltitudeDegrees = 30 }; break;
            case "meridian": f.Local = f.Local with { MeridianExclusion = new(60000, 120000) }; break;
        }
        Assert.Throws<InvalidOperationException>(() => f.Read());
        f.Devices.Binding = f.Devices.Binding with { ConstraintRevision = f.Reader.Refresh(f.Local).Revision };
        var changed = f.Read();
        Assert.NotEqual(original.Configuration.Id, changed.Configuration.Id);
        Assert.NotEqual(original.Native.Revision, changed.Native.Revision);
    }

    [Theory]
    [InlineData("same-path-edit")]
    [InlineData("location-event")]
    [InlineData("driver")]
    [InlineData("flip")]
    [InlineData("dispose")]
    public void ChangesDuringCombinedExportAreRejected(string fault)
    {
        using var f = new Fixture();
        var reads = 0;
        f.Devices.CameraMediator.Setup(m => m.GetInfo()).Returns(() =>
        {
            if (++reads == 1)
            {
                if (fault == "same-path-edit") File.WriteAllText(f.Path, "0 -89\n180 80\n360 -89");
                if (fault == "location-event") f.Devices.Profiles.Raise(p => p.LocationChanged += null, EventArgs.Empty);
                if (fault == "flip") f.Flip.Object.MaxMinutesAfterMeridian = 25;
            }
            if (fault == "driver" && reads == 3) f.Devices.Camera.DriverVersion = "changed";
            if (fault == "dispose" && reads == 3) f.Reader.Dispose();
            return f.Devices.Camera;
        });
        Assert.Throws<InvalidOperationException>(() => f.Read());
    }

    [Fact]
    public void RequiredHorizonNeverBecomesAnImplicitFixedMinimum()
    {
        using var f = new Fixture();
        f.Astrometry.Object.HorizonFilePath = "";
        f.Astrometry.Object.Horizon = null!;
        Assert.Throws<InvalidOperationException>(() => f.Read());
        f.Local = f.Local with { Mode = NinaHorizonMode.FixedMinimum, HorizonPath = null };
        Assert.Throws<InvalidOperationException>(() => f.Read());
        f.Devices.Binding = f.Devices.Binding with { ConstraintRevision = f.Reader.Refresh(f.Local).Revision };
        Assert.IsType<DirectorHorizon.FixedMinimum>(f.Read().Constraints.Rig.Horizon);
    }

    [Fact]
    public void InputsMustBeExplicitAndProfileScoped()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => f.Read(Inputs() with { Revision = 0 }));
        Assert.Throws<ArgumentException>(() => f.Read(Inputs() with { MaximumAltitudeDegrees = double.NaN }));
        Assert.Throws<ArgumentException>(() => f.Read(Inputs() with { Goals = default }));
        Assert.Throws<ArgumentNullException>(() => f.Read(Inputs() with { Orientation = null! }));
        f.Local = f.Local with { ProfileId = Guid.NewGuid() };
        Assert.Throws<ArgumentException>(() => f.Read());
    }

    [Fact]
    public async Task NativeExportGoesThroughTheRealSharedGeometryLedger()
    {
        using var f = new Fixture();
        var exported = f.Read();
        var configuration = exported.Configuration;
        var baseline = PlannerTests.Request();
        var assignment = baseline.Assignment with
        {
            ConfigurationId = configuration.Id,
            ValidFromMs = Start,
            ExpiresAtMs = Start + 60000,
            Goals = [baseline.Assignment.Goals[0] with { EligibleWindows = [new(Start, Start + 60000)] }]
        };
        var program = new DirectorProgram(1, assignment, configuration,
            [new("target", "M42", 298800000, -18000000, null)],
            [new("recipe", 1000, "filter-l", new(1, 1), 40, null, 0, null)], [new("goal", "target", "recipe")]);
        var state = baseline.State with { ConfigurationId = configuration.Id, NowMs = Start, ConditionsValidUntilMs = Start + 120000 };
        var directory = Directory.CreateTempSubdirectory("director-native-geometry-");
        try
        {
            await using var runtime = new RuntimeController(ProcessTests.BundleDirectory, directory.FullName);
            await runtime.StartAsync("rig-test");
            var invalid = exported.Constraints with { Rig = exported.Constraints.Rig with { Orientation = Inputs().Orientation with { ValidUntilMs = Start } } };
            Assert.Equal(LedgerError.InvalidSnapshot, (await runtime.OpenGeometryAsync(program, invalid, state)).Error);
            Assert.Null((await runtime.OpenGeometryAsync(program, exported.Constraints, state)).Error);
            Assert.Equal(PlannerAction.Acquire, (await runtime.EvaluateGeometryAsync(f.Read().Constraints, state)).Value!.Action);
            var local = new ProgramLocalState(configuration, new(configuration.Id, program.Targets[0]), false, false, 0);
            Assert.True((await runtime.BeginGeometryPreparationAsync("prep", "goal", local, new(0, 0, 0, 0, 0, 0, 0), f.Read().Constraints, state)).Value!.Created);
            for (var i = 0; i < 2; i++)
            {
                var command = Assert.IsType<PreparationNext.Run>((await runtime.AdvanceGeometryPreparationAsync("prep", f.Read().Configuration, f.Read().Constraints, state)).Value).Command;
                await runtime.CompletePreparationAsync(new("prep", command.Ordinal, Start, 1, new PreparationOutcome.Succeeded()));
            }
            Assert.IsType<PreparationNext.ReadyToReserve>((await runtime.AdvanceGeometryPreparationAsync("prep", configuration, f.Read().Constraints, state)).Value);
            var stricter = f.Read(Inputs() with { Goals = [new("goal", -89, 89, 1)] });
            var reservation = (await runtime.ReserveGeometryPreparedAsync("prep", "capture", stricter.Configuration, stricter.Constraints, state)).Value!;
            Assert.Equal(ReservationKind.Decision, reservation.Kind);
            Assert.Equal("observing_constraints_changed", reservation.Decision!.Reason);
            Assert.Null((await runtime.FindUnresolvedAttemptAsync()).Value!.Attempt);
            await runtime.StopAsync();
        }
        finally { directory.Delete(true); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("director-geometry-native-");
        internal readonly NinaEquipmentTests.Fixture Devices = new();
        internal readonly Mock<IAstrometrySettings> Astrometry = new();
        internal readonly Mock<IMeridianFlipSettings> Flip = new();
        internal readonly string Path;
        internal readonly NinaConstraintSnapshot Reader;
        internal NinaConstraintBinding Local;
        private readonly NinaGeometrySnapshot exporter;
        internal Fixture(string text = "0 -89\n180 -89\n360 -89", bool mw4 = false)
        {
            Path = System.IO.Path.Combine(directory.FullName, mw4 ? "horizon.hpts" : "horizon.txt");
            File.WriteAllText(Path, text);
            Astrometry.SetupAllProperties();
            Flip.SetupAllProperties();
            Astrometry.Object.HorizonFilePath = Path;
            Astrometry.Object.Latitude = 35;
            Astrometry.Object.Longitude = -120;
            Astrometry.Object.Elevation = 1000;
            Flip.Object.MinutesAfterMeridian = 5;
            Flip.Object.MaxMinutesAfterMeridian = 10;
            Devices.Profile.SetupGet(p => p.AstrometrySettings).Returns(Astrometry.Object);
            Devices.Profile.SetupGet(p => p.MeridianFlipSettings).Returns(Flip.Object);
            Devices.Profiles.Setup(p => p.ChangeHorizon(It.IsAny<string>())).Callback<string>(path =>
            {
                Astrometry.Object.Horizon = CustomHorizon.FromFilePath(path);
                Astrometry.Object.HorizonFilePath = path;
                Devices.Profiles.Raise(p => p.HorizonChanged += null, EventArgs.Empty);
            });
            Reader = new(Devices.Profiles.Object);
            Local = new(Devices.Binding.ProfileId, NinaHorizonMode.RequiredFile, Path, -89, new(0, 0));
            Devices.Binding = Devices.Binding with { ConstraintRevision = Reader.Refresh(Local).Revision };
            exporter = new(Reader, new NinaEquipmentSnapshot(Devices.Profiles.Object, Devices.CameraMediator.Object, Devices.WheelMediator.Object));
        }
        internal NinaGeometryExport Read(NinaGeometryInputs? inputs = null) => exporter.Read(Local, Devices.Binding, inputs ?? Inputs());
        public void Dispose()
        {
            Reader.Dispose();
            directory.Delete(true);
        }
    }
}
