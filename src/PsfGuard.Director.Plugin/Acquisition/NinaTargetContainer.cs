using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// Native context only. Visibility decisions and acquisition authority stay in
// the shared core/session, not NINA's display-oriented nighttime calculations.
internal sealed class NinaTargetContainer : SequentialContainer, IDeepSkyObjectContainer
{
    private readonly IProfileService profiles;
    private readonly IProfile profile;
    private readonly Guid profileId;
    private readonly DirectorTarget expected;
    private readonly InputTarget target;
    private readonly IDeepSkyObject deepSkyObject;
    private readonly double latitude;
    private readonly double longitude;
    private readonly double elevation;
    private readonly TimeProvider clock;
    private int entered;

    internal NinaTargetContainer(IProfileService profiles, Guid profileId, DirectorTarget expected,
        NighttimeData nighttime, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(nighttime);
        ArgumentNullException.ThrowIfNull(expected);
        this.profiles = profiles;
        this.profile = profiles.ActiveProfile;
        this.profileId = profileId;
        this.expected = expected;
        this.clock = clock;
        if (profileId == Guid.Empty || profile.Id != profileId || expected.IcrsRaMas >= 1296000000
            || expected.IcrsDecMas is < -324000000 or > 324000000 || expected.PositionAngleMas >= 1296000000
            || string.IsNullOrWhiteSpace(expected.Id) || string.IsNullOrWhiteSpace(expected.Name))
            throw new ArgumentException("An explicit profile and valid immutable target are required.");
        latitude = profile.AstrometrySettings.Latitude;
        longitude = profile.AstrometrySettings.Longitude;
        elevation = profile.AstrometrySettings.Elevation;
        var horizon = profile.AstrometrySettings.Horizon;
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 || !double.IsFinite(longitude)
            || longitude is < -180 or > 180 || !double.IsFinite(elevation))
            throw new InvalidOperationException("Native target context needs a valid site.");
        target = new InputTarget(Angle.ByDegree(latitude), Angle.ByDegree(longitude), horizon)
        {
            TargetName = expected.Name,
            InputCoordinates = new InputCoordinates(new Coordinates(expected.IcrsRaMas / 3600000.0,
                expected.IcrsDecMas / 3600000.0, Epoch.J2000, Coordinates.RAType.Degrees)),
            PositionAngle = expected.PositionAngleMas is { } angle ? angle / 3600000.0 : double.NaN
        };
        deepSkyObject = target.DeepSkyObject;
        NighttimeData = nighttime;
        Name = expected.Name;
        ValidateContext();
    }

    public InputTarget Target
    {
        get => target;
        set { if (!ReferenceEquals(value, target)) throw new InvalidOperationException("An issued target context cannot be replaced."); }
    }
    public NighttimeData NighttimeData { get; }
    public override int Attempts
    {
        get => 1;
        set { if (value != 1) throw new InvalidOperationException("The Director session owns target retries."); }
    }
    public override object Clone() => throw new NotSupportedException("Issued target contexts cannot be cloned.");

    internal void ValidateContext()
    {
        var settings = profiles.ActiveProfile.AstrometrySettings;
        if (!ReferenceEquals(profiles.ActiveProfile, profile) || profile.Id != profileId || settings.Latitude != latitude || settings.Longitude != longitude
            || settings.Elevation != elevation
            || NighttimeData.ReferenceDate != NighttimeCalculator.GetReferenceDate(clock.GetLocalNow().DateTime))
            throw new InvalidOperationException("Native target site, profile, or reference night changed.");
        var expectedAngle = expected.PositionAngleMas is { } angle ? angle / 3600000.0 : double.NaN;
        if (target.TargetName != expected.Name || !target.PositionAngle.Equals(expectedAngle)
            || !ReferenceEquals(target.DeepSkyObject, deepSkyObject) || deepSkyObject.Name != expected.Name
            || !deepSkyObject.RotationPositionAngle.Equals(expectedAngle)
            || !Matches(target.InputCoordinates?.Coordinates) || !Matches(deepSkyObject.Coordinates))
            throw new InvalidOperationException("Native target context changed after issue.");
    }

    private bool Matches(Coordinates? coordinates) => coordinates is not null && coordinates.Epoch == Epoch.J2000
        && coordinates.RA == expected.IcrsRaMas / 3600000.0 / 15.0 && coordinates.Dec == expected.IcrsDecMas / 3600000.0;

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref entered, 1) != 0)
            throw new InvalidOperationException("This issued target context has already run.");
        token.ThrowIfCancellationRequested();
        ValidateContext();
        return base.Execute(progress, token);
    }
}
