using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// Explicit local mappings. Names detect edits; they never establish remote identity.
internal sealed record NinaFilterBinding(string Id, short? Position, string? ExpectedName);
internal sealed record NinaEquipmentBinding(Guid ProfileId, string RigId, string ConstraintRevision,
    string CameraDeviceId, string? FilterWheelDeviceId, ImmutableArray<NinaFilterBinding> Filters,
    bool EnableSlewCenter, uint DitherEvery, string? TelescopeDeviceId = null);

internal sealed class NinaEquipmentSnapshot(IProfileService profiles, ICameraMediator camera, IFilterWheelMediator wheel,
    ITelescopeMediator? telescope = null)
{
    internal DirectorConfiguration Read(NinaEquipmentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        CheckId(binding.RigId);
        CheckId(binding.ConstraintRevision);
        if (binding.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(binding.CameraDeviceId)
            || binding.CameraDeviceId == "No_Device" || binding.Filters.IsDefaultOrEmpty || binding.Filters.Length > 256)
            throw new ArgumentException("An explicit profile, camera, and filter mapping are required.");
        var profile = profiles.ActiveProfile;
        // Mediator info is mutable. Two copied reads detect changes during export;
        // the eventual executor must still refresh at every native dispatch boundary.
        var first = ReadOnce(binding, profile);
        var second = ReadOnce(binding, profile);
        if (first.Id != second.Id || !ReferenceEquals(profiles.ActiveProfile, profile))
            throw new InvalidOperationException("NINA equipment changed while reading capabilities.");
        return second;
    }

    private DirectorConfiguration ReadOnce(NinaEquipmentBinding binding, IProfile profile)
    {
        var cameraInfo = camera.GetInfo();
        var wheelInfo = wheel.GetInfo();
        if (!ReferenceEquals(profiles.ActiveProfile, profile) || profile.Id != binding.ProfileId
            || profile.CameraSettings.Id != binding.CameraDeviceId || !cameraInfo.Connected
            || cameraInfo.DeviceId != binding.CameraDeviceId || cameraInfo.IsSubSampleEnabled)
            throw new InvalidOperationException("NINA profile or full-frame camera does not match the rig binding.");
        if (binding.FilterWheelDeviceId is { } expectedWheel)
        {
            if (string.IsNullOrWhiteSpace(expectedWheel) || expectedWheel == "No_Device"
                || profile.FilterWheelSettings.Id != expectedWheel || !wheelInfo.Connected || wheelInfo.DeviceId != expectedWheel)
                throw new InvalidOperationException("The configured filter wheel is not connected.");
        }
        else if (profile.FilterWheelSettings.Id != "No_Device" || wheelInfo.Connected || binding.Filters.Length != 1)
            throw new InvalidOperationException("Fixed-filter mode requires an explicitly wheel-free profile.");

        var nativeFilters = profile.FilterWheelSettings.FilterWheelFilters?.Take(257).Select(f => (f.Position, f.Name)).ToArray()
            ?? throw new InvalidDataException("NINA filter definitions are unavailable.");
        if (nativeFilters.Length > 256) throw new InvalidDataException("NINA filter definitions exceed the supported limit.");
        var filters = binding.Filters.OrderBy(f => f.Position).ThenBy(f => f.Id, StringComparer.Ordinal).Select(f =>
        {
            CheckId(f.Id);
            if (binding.FilterWheelDeviceId is null)
            {
                if (f.Position is not null || f.ExpectedName is not null) throw new ArgumentException("A fixed filter has no wheel slot or NINA name.");
            }
            else if (f.Position is null or < 0 || string.IsNullOrWhiteSpace(f.ExpectedName)
                || nativeFilters.Count(n => n.Position == f.Position) != 1
                || nativeFilters.Single(n => n.Position == f.Position).Name != f.ExpectedName)
                throw new InvalidOperationException("A filter slot or name changed. Review the local filter mapping.");
            return new DirectorFilter(f.Id, f.Position);
        }).ToImmutableArray();
        if (filters.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != filters.Length
            || filters.Select(f => f.Position).Distinct().Count() != filters.Length)
            throw new ArgumentException("Filter identities and slots must be unique.");

        var bins = cameraInfo.BinningModes?.Take(257).Select(b => new CameraBinning(b.X, b.Y)).OrderBy(b => b.X).ThenBy(b => b.Y).ToImmutableArray()
            ?? throw new InvalidDataException("NINA camera binning modes are unavailable.");
        if (bins.IsDefaultOrEmpty || bins.Length > 256 || bins.Any(b => b.X <= 0 || b.Y <= 0) || bins.Distinct().Count() != bins.Length)
            throw new InvalidDataException("NINA camera binning modes are not representable.");
        var readouts = cameraInfo.ReadoutModes?.Take(257).ToArray() ?? throw new InvalidDataException("NINA camera readout modes are unavailable.");
        if (readouts.Length is 0 or > 256 || readouts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("NINA camera readout modes are not representable.");
        CameraControl gain;
        if (!cameraInfo.CanSetGain) gain = new CameraControl.Unsupported();
        else
        {
            var gains = cameraInfo.Gains?.Take(257).ToArray() ?? throw new InvalidDataException("NINA gain capabilities are unavailable.");
            // NINA's ASCOM adapter optimistically keeps CanSetGain true until a
            // setter fails. Unreadable gain with both absent-range sentinels
            // exposes no usable control; never probe support with a write.
            if (gains.Length == 0 && !cameraInfo.CanGetGain && cameraInfo.GainMin == -1 && cameraInfo.GainMax == -1)
                gain = new CameraControl.Unsupported();
            else if (gains.Length == 0) gain = Range(cameraInfo.GainMin, cameraInfo.GainMax);
            else
            {
                if (gains.Length > 256 || gains.Any(g => g < 0) || gains.Distinct().Count() != gains.Length)
                    throw new InvalidDataException("NINA gain values are not representable.");
                gain = new CameraControl.Values(gains.Order().ToImmutableArray());
            }
        }
        CameraControl offset = cameraInfo.CanSetOffset ? Range(cameraInfo.OffsetMin, cameraInfo.OffsetMax) : new CameraControl.Unsupported();
        var minimum = ExposureLimit(cameraInfo.ExposureMin, roundUp: true);
        var maximum = ExposureLimit(cameraInfo.ExposureMax, roundUp: false);
        if (minimum > maximum) throw new InvalidDataException("NINA exposure interval has no supported millisecond duration.");
        var configuration = new DirectorConfiguration(binding.RigId, "pending", DeviceId(binding.ProfileId, binding.CameraDeviceId),
            binding.FilterWheelDeviceId is null ? null : DeviceId(binding.ProfileId, binding.FilterWheelDeviceId), filters, bins,
            Enumerable.Range(0, readouts.Length).Select(i => (short)i).ToImmutableArray(), gain, offset, minimum, maximum,
            binding.EnableSlewCenter, binding.DitherEvery);
        // Labels and driver revisions are local change evidence. Only the digest
        // and stable mapping IDs cross the wire; device paths stay on this host.
        var fingerprint = Hash(new
        {
            SchemaVersion = 1,
            binding.ProfileId,
            binding.ConstraintRevision,
            configuration,
            cameraInfo.DriverVersion,
            WheelDriverVersion = binding.FilterWheelDeviceId is null ? null : wheelInfo.DriverVersion,
            Readouts = readouts,
            Filters = binding.Filters.OrderBy(f => f.Position).ThenBy(f => f.Id, StringComparer.Ordinal)
        });
        if (binding.TelescopeDeviceId is { } expectedMount)
        {
            var info = telescope?.GetInfo();
            if (string.IsNullOrWhiteSpace(expectedMount) || expectedMount == "No_Device"
                || profile.TelescopeSettings?.Id != expectedMount || info is null || !info.Connected || info.DeviceId != expectedMount)
                throw new InvalidOperationException("The bound telescope is not connected in the active profile.");
            // Park/tracking/position are changing observations, not configuration.
            // Keep legacy camera-only fingerprints unchanged when no mount is bound.
            fingerprint = Hash(new
            {
                Base = fingerprint,
                TelescopeId = DeviceId(binding.ProfileId, expectedMount),
                info.DriverVersion,
                info.CanPark,
                info.CanSetPark,
                info.CanSetTrackingEnabled,
                info.CanSlew,
                info.CanSlewAltAz,
                info.CanSetPierSide
            });
        }
        return configuration with { Id = "nina-" + fingerprint };
    }

    private static CameraControl Range(int minimum, int maximum)
    {
        if (minimum < 0 || maximum < minimum) throw new InvalidDataException("NINA camera control range is not representable.");
        return new CameraControl.Range(minimum, maximum);
    }

    private static ulong ExposureLimit(double seconds, bool roundUp)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 9_007_199_254_740)
            throw new InvalidDataException("NINA camera exposure limit is not representable.");
        var milliseconds = roundUp ? Math.Ceiling(seconds * 1000) : Math.Floor(seconds * 1000);
        if (roundUp) milliseconds = Math.Max(1, milliseconds);
        if (milliseconds < 1) throw new InvalidDataException("NINA camera exposure limit is below one millisecond.");
        return checked((ulong)milliseconds);
    }

    private static string DeviceId(Guid profileId, string deviceId) => "device-" + Hash(new { profileId, deviceId });
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static void CheckId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("Invalid Director mapping identity.");
    }
}
