using System.IO;
using System.Text.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

// Internal recipe adapter, not a sequencer or a hardware authorization source.
internal sealed class NinaProgramCapture(NinaEquipmentSnapshot equipment, ICameraMediator camera,
    IFilterWheelMediator wheel, NinaCaptureAdapter capture)
{
    internal Task<CaptureEvidence> CaptureAsync(LedgerReservation reservation, CaptureBinding binding,
        NinaEquipmentBinding local, Func<CancellationToken, Task> revalidateAtDispatch,
        IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(revalidateAtDispatch);
        var intent = CreateIntent(reservation, binding, local);
        return capture.CaptureAsync(intent, async cancellation =>
        {
            await revalidateAtDispatch(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            CheckPrepared(binding, local);
        }, progress, token);
    }

    internal CaptureIntent CreateIntent(LedgerReservation reservation, CaptureBinding binding, NinaEquipmentBinding local)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(local);
        if (reservation.Kind != ReservationKind.Created || reservation.Decision is not null
            || reservation.Attempt != binding.Attempt || binding.Attempt.Evidence is not LedgerEvidence.Reserved
            || !Guid.TryParseExact(binding.Attempt.CaptureId, "D", out var captureId) || captureId == Guid.Empty)
            throw new InvalidOperationException("Only a matching new GUID capture reservation can enter the native adapter.");
        CheckConfiguration(binding, local);
        var recipe = binding.Recipe;
        var target = binding.Target;
        var configuration = binding.Configuration;
        // Rust owns recipe validation. These checks also prevent accidental use
        // of hand-constructed DTOs with native 'use current setting' sentinels.
        if (recipe.ExposureMs < configuration.ExposureMinMs || recipe.ExposureMs > configuration.ExposureMaxMs
            || !configuration.BinningModes.Contains(recipe.Binning) || !configuration.ReadoutModes.Contains(recipe.ReadoutMode)
            || configuration.Filters.Count(f => f.Id == recipe.FilterId) != 1
            || !Supported(configuration.Gain, recipe.Gain) || !Supported(configuration.Offset, recipe.Offset))
            throw new InvalidDataException("The capture recipe is not explicit and supported by the bound configuration.");
        var intent = new CaptureIntent(captureId, local.ProfileId, binding.Ledger.RigId, binding.Ledger.ConfigurationId,
            binding.Ledger.AssignmentId, binding.Ledger.AssignmentRevision, binding.Attempt.GoalId, local.CameraDeviceId,
            recipe.ExposureMs / 1000.0, target.Name, target.IcrsRaMas / 3600000.0, target.IcrsDecMas / 3600000.0,
            target.PositionAngleMas is { } angle ? angle / 3600000.0 : null, recipe.Binning.X, recipe.Binning.Y,
            recipe.Gain ?? -1, recipe.Offset ?? -1,
            new(binding.Ledger.LedgerId, target.Id, recipe.Id, recipe.FilterId, recipe.ReadoutMode));
        CaptureJournal.Validate(intent);
        return intent;
    }

    internal void CheckPrepared(CaptureBinding binding, NinaEquipmentBinding local)
    {
        CheckConfiguration(binding, local);
        // NINA StartExposure selects this LIGHT-image setting, not ReadoutMode.
        if (camera.GetInfo().ReadoutModeForNormalImages != binding.Recipe.ReadoutMode)
            throw new InvalidOperationException("NINA normal-image readout mode no longer matches the prepared recipe.");
        if (local.FilterWheelDeviceId is not null)
        {
            var filter = local.Filters.Single(f => f.Id == binding.Recipe.FilterId);
            var info = wheel.GetInfo();
            if (!info.Connected || info.DeviceId != local.FilterWheelDeviceId || info.IsMoving
                || info.SelectedFilter is not { } selected || selected.Position != filter.Position || selected.Name != filter.ExpectedName)
                throw new InvalidOperationException("NINA filter wheel no longer matches the prepared recipe.");
        }
    }

    private void CheckConfiguration(CaptureBinding binding, NinaEquipmentBinding local)
    {
        var current = equipment.Read(local);
        if (binding.Ledger.RigId != local.RigId || binding.Ledger.ConfigurationId != current.Id
            || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(binding.Configuration))
            throw new InvalidOperationException("The current NINA equipment differs from the reserved configuration.");
    }

    private static bool Supported(CameraControl control, int? value) => control switch
    {
        CameraControl.Unsupported => value is null,
        CameraControl.Range range => value is >= 0 && value >= range.Minimum && value <= range.Maximum,
        CameraControl.Values values => value is >= 0 && values.Items.Contains(value.Value),
        _ => false
    };
}
