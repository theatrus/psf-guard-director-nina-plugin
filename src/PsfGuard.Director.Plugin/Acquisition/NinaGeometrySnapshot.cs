using System.Collections.Immutable;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record NinaGeometryInputs(ulong Revision, double MaximumAltitudeDegrees,
    DirectorEarthOrientation Orientation, ImmutableArray<DirectorGoalLimits> Goals);
internal sealed record NinaGeometryExport(NinaConstraints Native, DirectorConfiguration Configuration, DirectorConstraints Constraints);

// Export only. The runtime owns astronomy and visibility; the caller still owns
// session authority and must refresh again at each actual dispatch boundary.
internal sealed class NinaGeometrySnapshot(NinaConstraintSnapshot constraints, NinaEquipmentSnapshot equipment)
{
    internal NinaGeometryExport Read(NinaConstraintBinding local, NinaEquipmentBinding devices, NinaGeometryInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Orientation);
        if (local.ProfileId != devices.ProfileId || inputs.Revision == 0 || inputs.Goals.IsDefaultOrEmpty
            || !double.IsFinite(inputs.MaximumAltitudeDegrees))
            throw new ArgumentException("Geometry requires matching profile bindings and explicit observing inputs.");

        var native = constraints.Refresh(local);
        if (native.Revision != devices.ConstraintRevision)
            throw new InvalidOperationException("Native constraints changed. Rebuild the rig configuration before planning.");
        var generation = constraints.Generation;
        var configuration = equipment.Read(devices);
        if (!constraints.IsCurrent(generation))
            throw new InvalidOperationException("Native constraints changed during equipment export.");

        // Bracket equipment export with fresh disk/native reads. A same-path
        // edit does not necessarily raise a NINA event.
        var confirmed = constraints.Refresh(local);
        generation = constraints.Generation;
        if (confirmed.Revision != native.Revision || equipment.Read(devices).Id != configuration.Id
            || !constraints.IsCurrent(generation))
            throw new InvalidOperationException("Native constraints or equipment changed during geometry export.");

        DirectorHorizon horizon = local.Mode == NinaHorizonMode.FixedMinimum
            ? new DirectorHorizon.FixedMinimum()
            : new DirectorHorizon.Custom(native.Horizon.Points
                .Select(p => new DirectorHorizonPoint(p.AzimuthDegrees, p.AltitudeDegrees)).ToImmutableArray());
        var rig = new DirectorRigConstraints(devices.RigId, configuration.Id, inputs.Revision,
            new(native.Site.LatitudeDegrees, native.Site.LongitudeDegrees, native.Site.ElevationMeters),
            inputs.Orientation, horizon, native.MinimumAltitudeDegrees, inputs.MaximumAltitudeDegrees, native.MeridianExclusion);
        return new(native, configuration, new(1, rig, inputs.Goals));
    }
}
