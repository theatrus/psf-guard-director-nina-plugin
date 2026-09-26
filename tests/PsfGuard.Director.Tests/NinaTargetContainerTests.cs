using Moq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Utility;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaTargetContainerTests
{
    [Theory]
    [InlineData(0U, -324000000, null)]
    [InlineData(1295999999U, 324000000, 1295999999U)]
    [InlineData(648000000U, 72000000, 108000000U)]
    public void NativeNestedAndTriggerContextsResolveExactImmutableTarget(uint ra, int dec, uint? angle)
    {
        using var f = new Fixture(new("target", "Target", ra, dec, angle));
        var inner = new SequentialContainer();
        f.Container.Add(inner);
        Assert.Same(f.Container, ItemUtility.FindDeepSkyObjectContainer(inner));
        var context = ItemUtility.RetrieveContextCoordinates(inner);
        Assert.Equal(ra / 3600000.0, context.Coordinates.RADegrees);
        Assert.Equal(dec / 3600000.0, context.Coordinates.Dec);
        Assert.Equal(Epoch.J2000, context.Coordinates.Epoch);
        Assert.True((angle is null ? double.NaN : angle.Value / 3600000.0).Equals(context.PositionAngle));
        var runnerContext = ItemUtility.CreateTriggerRunnerContext(inner);
        Assert.Same(f.Container.Target, ((IDeepSkyObjectContainer)runnerContext).Target);
        Assert.Same(f.Nighttime, ((IDeepSkyObjectContainer)runnerContext).NighttimeData);
        f.Container.ValidateContext();
    }

    [Theory]
    [InlineData("ra")]
    [InlineData("dec")]
    [InlineData("epoch")]
    [InlineData("name")]
    [InlineData("angle")]
    [InlineData("dso-name")]
    [InlineData("dso-angle")]
    public void MutableNativeTargetCannotSilentlyChangeAnIssuedContext(string fault)
    {
        using var f = new Fixture();
        var target = f.Container.Target;
        switch (fault)
        {
            case "ra": target.InputCoordinates.Coordinates.RA += 0.1; break;
            case "dec": target.InputCoordinates.Coordinates.Dec += 0.1; break;
            case "epoch": target.InputCoordinates.Coordinates.Epoch = Epoch.JNOW; break;
            case "name": target.TargetName = "Other"; break;
            case "angle": target.PositionAngle = 0; break;
            case "dso-name": target.DeepSkyObject.Name = "Other"; break;
            case "dso-angle": target.DeepSkyObject.RotationPositionAngle = 0; break;
        }
        Assert.Throws<InvalidOperationException>(f.Container.ValidateContext);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("profile-object")]
    [InlineData("latitude")]
    [InlineData("longitude")]
    [InlineData("elevation")]
    [InlineData("night")]
    public void ChangedSiteProfileOrNightCannotReuseTargetContext(string fault)
    {
        using var f = new Fixture();
        switch (fault)
        {
            case "profile": f.Profile.SetupGet(x => x.Id).Returns(Guid.NewGuid()); break;
            case "profile-object":
                var replacement = new Mock<IProfile>();
                replacement.SetupGet(x => x.Id).Returns(f.Profile.Object.Id);
                replacement.SetupGet(x => x.AstrometrySettings).Returns(f.Astrometry.Object);
                f.Profiles.SetupGet(x => x.ActiveProfile).Returns(replacement.Object);
                break;
            case "latitude": f.Astrometry.SetupGet(x => x.Latitude).Returns(11); break;
            case "longitude": f.Astrometry.SetupGet(x => x.Longitude).Returns(21); break;
            case "elevation": f.Astrometry.SetupGet(x => x.Elevation).Returns(100); break;
            case "night": f.Clock.Now = f.Clock.Now.AddDays(1); break;
        }
        Assert.Throws<InvalidOperationException>(f.Container.ValidateContext);
    }

    [Fact]
    public void ControlledHorizonReloadDoesNotInvalidateCoordinates()
    {
        using var f = new Fixture();
        // The boundary owner separately checks the full constraint revision.
        f.Astrometry.SetupGet(x => x.Horizon).Returns(CustomHorizon.FromReader_Standard(new System.IO.StringReader("0 10\n360 10")));
        f.Container.ValidateContext();
    }

    [Fact]
    public async Task TransientContainerCannotCloneRetryReplaceTargetOrRunTwice()
    {
        using var f = new Fixture();
        Assert.Throws<NotSupportedException>(() => f.Container.Clone());
        Assert.Throws<InvalidOperationException>(() => f.Container.Attempts = 2);
        Assert.Throws<InvalidOperationException>(() => f.Container.Target = new(Angle.Zero, Angle.Zero, null));
        await f.Container.Execute(new Progress<ApplicationStatus>(), default);
        f.Container.ResetProgress();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Container.Execute(new Progress<ApplicationStatus>(), default));
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 26, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Mock<IProfile> Profile = new();
        internal readonly Mock<IProfileService> Profiles = new();
        internal readonly Mock<IAstrometrySettings> Astrometry = new();
        internal readonly Clock Clock = new();
        internal readonly NighttimeData Nighttime;
        internal readonly NinaTargetContainer Container;
        internal Fixture(DirectorTarget? target = null)
        {
            var id = Guid.NewGuid();
            Profile.SetupGet(x => x.Id).Returns(id);
            Profile.SetupGet(x => x.AstrometrySettings).Returns(Astrometry.Object);
            Profiles.SetupGet(x => x.ActiveProfile).Returns(Profile.Object);
            Astrometry.SetupGet(x => x.Latitude).Returns(10);
            Astrometry.SetupGet(x => x.Longitude).Returns(20);
            Nighttime = new(Clock.Now.DateTime, NighttimeCalculator.GetReferenceDate(Clock.Now.DateTime), default, null, null, null, null, null, null);
            Container = new(Profiles.Object, id, target ?? new("target", "Target", 648000000, 72000000, null), Nighttime, Clock);
        }
        public void Dispose() => Nighttime.Ticker.Stop();
    }
}
