using System.Text;
using Moq;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Acquisition;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaConstraintTests
{
    [Theory]
    [InlineData("0 10\n90 20\n180 30\n270 20\n360 10", false)]
    [InlineData("[[10,0],[20,90],[30,180],[20,270],[10,360]]", true)]
    [InlineData("10 12\n350 30", false)]
    [InlineData("20 12\n350 30", false)]
    [InlineData("0 12\n350 30", false)]
    [InlineData("10 12\n360 30", false)]
    [InlineData("0 12\n180 35\n360 30", false)]
    [InlineData("# comments\n90,20\n180;30\n270\t20\n90 25", false)]
    [InlineData("0 -10\n100 0\n100.0001 80\n100.0002 0\n270 20", false)]
    [InlineData("0 0\n100 0\n100.00000000000001 80\n100.00000000000003 0", false)]
    public void ExportRetainsEveryBreakpointWithNativeInterpolationParity(string text, bool mw4)
    {
        using var f = new Fixture(text, mw4);
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        var result = reader.Refresh(f.Binding);
        Assert.Equal(0, result.Horizon.Points[0].AzimuthDegrees);
        Assert.Equal(360, result.Horizon.Points[^1].AzimuthDegrees);
        Assert.NotNull(result.Horizon.ContentSha256);
        Assert.DoesNotContain(f.Path, System.Text.Json.JsonSerializer.Serialize(result));
        // Verify densely as well as exactly at breakpoints, without using dense
        // sampling as the exported representation (narrow obstructions survive).
        for (var azimuth = -10.0; azimuth < 721; azimuth += 0.137)
            Assert.Equal(f.Astrometry.Object.Horizon.GetAltitude(azimuth), Interpolate(result.Horizon, azimuth), 8);
        foreach (var point in result.Horizon.Points)
            Assert.Equal(f.Astrometry.Object.Horizon.GetAltitude(point.AzimuthDegrees), Interpolate(result.Horizon, point.AzimuthDegrees), 8);
        Assert.Equal(1, reader.Generation);
    }

    [Theory]
    [InlineData("0 0\n90 10\nbad", false)]
    [InlineData("0 0\nNaN 10", false)]
    [InlineData("0 0\n90 Infinity", false)]
    [InlineData("0 0\n361 10", false)]
    [InlineData("0 0\n90 -91", false)]
    [InlineData("0 0\n0 5", false)]
    [InlineData("[]", true)]
    [InlineData("{}", true)]
    [InlineData("[[10,0],[20,360,30]]", true)]
    [InlineData("[[10,0],[20,400]]", true)]
    [InlineData("[[-1,0],[20,90]]", true)]
    [InlineData("[[10,0],[\"20\",90]]", true)]
    public void InvalidFilesNeverReloadOrBecomeFlat(string text, bool mw4)
    {
        using var f = new Fixture(text, mw4);
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        Assert.Throws<InvalidDataException>(() => reader.Refresh(f.Binding));
        f.Profiles.Verify(p => p.ChangeHorizon(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SamePathSameSizeEditsChangeRevisionAndReloadNativeCurve()
    {
        using var f = new Fixture("0 10\n180 20");
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        var first = reader.Refresh(f.Binding);
        var time = File.GetLastWriteTimeUtc(f.Path);
        File.WriteAllText(f.Path, "0 10\n180 80");
        File.SetLastWriteTimeUtc(f.Path, time);
        var second = reader.Refresh(f.Binding);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.NotEqual(first.Horizon.ContentSha256, second.Horizon.ContentSha256);
        Assert.Equal(80, f.Astrometry.Object.Horizon.GetAltitude(180));
        Assert.Equal(20, first.Horizon.Points.Single(p => p.AzimuthDegrees == 180).AltitudeDegrees);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cleared")]
    [InlineData("switched")]
    [InlineData("load-failure")]
    [InlineData("profile-switch")]
    [InlineData("location-event")]
    [InlineData("replacement-model")]
    public void RequiredHorizonAndContextFailuresBlockExport(string fault)
    {
        using var f = new Fixture("0 10\n180 20");
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        if (fault == "missing") File.Delete(f.Path);
        if (fault == "cleared") f.Astrometry.Object.HorizonFilePath = "";
        if (fault == "switched") f.Astrometry.Object.HorizonFilePath = f.Path + ".other";
        if (fault == "load-failure") f.AfterReload = () => f.Astrometry.Object.Horizon = null!;
        if (fault == "profile-switch") f.AfterReload = () => f.Profiles.Raise(p => p.BeforeProfileChanging += null, EventArgs.Empty);
        if (fault == "location-event") f.AfterReload = () => f.Profiles.Raise(p => p.LocationChanged += null, EventArgs.Empty);
        if (fault == "replacement-model") f.AfterReload = () => f.Astrometry.Object.Horizon = CustomHorizon.FromReader_Standard(new StringReader("0 80\n180 80"));
        if (fault == "missing") Assert.Throws<FileNotFoundException>(() => reader.Refresh(f.Binding));
        else if (fault == "replacement-model") Assert.Throws<InvalidDataException>(() => reader.Refresh(f.Binding));
        else Assert.Throws<InvalidOperationException>(() => reader.Refresh(f.Binding));
    }

    [Fact]
    public void DeclaredFixedMinimumIsExplicitAndRigExclusionsRemainAsymmetric()
    {
        using var f = new Fixture("0 10\n180 20");
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        var binding = f.Binding with { Mode = NinaHorizonMode.FixedMinimum, HorizonPath = null, MeridianExclusion = new(3600000, 0) };
        Assert.Throws<InvalidOperationException>(() => reader.Refresh(binding));
        f.Astrometry.Object.HorizonFilePath = "";
        f.Astrometry.Object.Horizon = null!;
        var first = reader.Refresh(binding);
        Assert.Empty(first.Horizon.Points);
        Assert.Null(first.Horizon.ContentSha256);
        Assert.Equal(3600000UL, first.MeridianExclusion.BeforeMs);
        Assert.Equal(0UL, first.MeridianExclusion.AfterMs);
        Assert.NotEqual(first.Revision, reader.Refresh(binding with { MeridianExclusion = new(0, 3600000) }).Revision);
        Assert.NotEqual(first.Revision, reader.Refresh(binding with { MinimumAltitudeDegrees = 40 }).Revision);
        f.Flip.Object.MaxMinutesAfterMeridian = 15;
        Assert.NotEqual(first.Revision, reader.Refresh(binding).Revision);
        f.Profiles.Verify(p => p.ChangeHorizon(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RefreshHoldsReadLockAndRejectsReentrancy()
    {
        using var f = new Fixture("0 10\n180 20");
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        f.AfterReload = () =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(f.Path, "0 0\n180 0"));
            Assert.Throws<InvalidOperationException>(() => reader.Refresh(f.Binding));
        };
        reader.Refresh(f.Binding);
    }

    [Fact]
    public void EventsInvalidateGenerationAndDisposeUnsubscribes()
    {
        using var f = new Fixture("0 10\n180 20");
        var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        f.Profiles.Raise(p => p.LocationChanged += null, EventArgs.Empty);
        f.Profiles.Raise(p => p.HorizonChanged += null, EventArgs.Empty);
        f.Profiles.Raise(p => p.ProfileChanged += null, EventArgs.Empty);
        Assert.Equal(3, reader.Generation);
        reader.Dispose();
        f.Profiles.Raise(p => p.HorizonChanged += null, EventArgs.Empty);
        Assert.Equal(3, reader.Generation);
        Assert.Throws<ObjectDisposedException>(() => reader.Refresh(f.Binding));
    }

    [Fact]
    public void SiteAndFlipChangesAreHashedAndInvalidCoordinatesFail()
    {
        using var f = new Fixture("0 10\n180 20");
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        var first = reader.Refresh(f.Binding);
        f.Astrometry.Object.Longitude = -100;
        var changed = reader.Refresh(f.Binding);
        Assert.NotEqual(first.Revision, changed.Revision);
        f.Flip.Object.PauseTimeBeforeMeridian = 12;
        Assert.NotEqual(changed.Revision, reader.Refresh(f.Binding).Revision);
        f.Astrometry.Object.Latitude = double.NaN;
        Assert.Throws<InvalidDataException>(() => reader.Refresh(f.Binding));
        f.Astrometry.Object.Latitude = 35;
        f.Flip.Object.MaxMinutesAfterMeridian = double.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() => reader.Refresh(f.Binding));
    }

    [Fact]
    public void DisposeDuringRefreshCannotReturnASnapshot()
    {
        using var f = new Fixture("0 10\n180 20");
        using var reader = new NinaConstraintSnapshot(f.Profiles.Object);
        f.AfterReload = reader.Dispose;
        Assert.Throws<ObjectDisposedException>(() => reader.Refresh(f.Binding));
    }

    [Fact]
    public void BoundedInputAndNarrowObstructionsArePreserved()
    {
        Assert.Throws<InvalidDataException>(() => NinaConstraintSnapshot.Parse(new byte[1024 * 1024 + 1], false));
        Assert.Throws<InvalidDataException>(() => NinaConstraintSnapshot.Parse(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat("0 0", 4097))), false));
        var result = NinaConstraintSnapshot.Parse(Encoding.UTF8.GetBytes("0 0\n10 0\n10.00001 85\n10.00002 0\n360 0"), false);
        Assert.Contains(new HorizonPoint(10.00001, 85), result.Points);
    }

    private static double Interpolate(NativeHorizon horizon, double azimuth)
    {
        if (azimuth < 0 || azimuth > 359)
        {
            azimuth %= 360;
            if (azimuth < 0) azimuth += 360;
        }
        var p = horizon.Points;
        var i = 1;
        while (p[i].AzimuthDegrees < azimuth) i++;
        var left = p[i - 1];
        var right = p[i];
        return left.AltitudeDegrees + (right.AltitudeDegrees - left.AltitudeDegrees)
            * ((azimuth - left.AzimuthDegrees) / (right.AzimuthDegrees - left.AzimuthDegrees));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("director-horizon-");
        internal readonly Mock<IProfileService> Profiles = new();
        internal readonly Mock<IProfile> Profile = new();
        internal readonly Mock<IAstrometrySettings> Astrometry = new();
        internal readonly Mock<IMeridianFlipSettings> Flip = new();
        internal readonly string Path;
        internal readonly NinaConstraintBinding Binding;
        internal Action? AfterReload;
        internal Fixture(string text, bool mw4 = false)
        {
            Path = System.IO.Path.Combine(directory.FullName, mw4 ? "horizon.hpts" : "horizon.txt");
            File.WriteAllText(Path, text);
            var id = Guid.NewGuid();
            Binding = new(id, NinaHorizonMode.RequiredFile, Path, 20, new(60000, 120000));
            Astrometry.SetupAllProperties();
            Flip.SetupAllProperties();
            Astrometry.Object.HorizonFilePath = Path;
            Astrometry.Object.Latitude = 35;
            Astrometry.Object.Longitude = -120;
            Astrometry.Object.Elevation = 1000;
            Flip.Object.MinutesAfterMeridian = 5;
            Flip.Object.MaxMinutesAfterMeridian = 10;
            Profile.SetupGet(p => p.Id).Returns(id);
            Profile.SetupGet(p => p.AstrometrySettings).Returns(Astrometry.Object);
            Profile.SetupGet(p => p.MeridianFlipSettings).Returns(Flip.Object);
            Profiles.SetupGet(p => p.ActiveProfile).Returns(Profile.Object);
            Profiles.Setup(p => p.ChangeHorizon(It.IsAny<string>())).Callback<string>(path =>
            {
                Astrometry.Object.Horizon = CustomHorizon.FromFilePath(path);
                Astrometry.Object.HorizonFilePath = path;
                Profiles.Raise(p => p.HorizonChanged += null, EventArgs.Empty);
                AfterReload?.Invoke();
            });
        }
        public void Dispose() => directory.Delete(true);
    }
}
