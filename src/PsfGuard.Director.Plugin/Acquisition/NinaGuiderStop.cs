using NINA.Equipment.Equipment.MyGuider.PHD2;

namespace PsfGuard.Director.Plugin.Acquisition;

internal static class NinaGuiderStop
{
    internal static async Task<bool> ConfirmPHD2StoppedAsync(
        Func<Task<GenericPhdMethodResponse>> readState, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = await readState().WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return response is { error: null, result: string state } && state == PhdAppState.STOPPED;
    }

    internal static async Task ConfirmAsync(NINA.Profile.Interfaces.IProfileService profiles, NINA.Equipment.Interfaces.Mediator.IGuiderMediator? guider, CancellationToken token)
    {
        var profile = profiles.ActiveProfile.Id;
        // Disconnection is not proof that a surviving remote guider stopped.
        if (guider?.GetInfo() is not { Connected: true } guide || guide.DeviceId != profiles.ActiveProfile.GuiderSettings.GuiderName
            || guider.GetDevice() is not NINA.Equipment.Interfaces.IGuider device || !device.Connected || device.Id != guide.DeviceId)
            throw new InvalidOperationException("Connect the bound guider and confirm it can stop before continuing.");
        var guideId = guide.DeviceId;
        var stopped = await guider.StopGuiding(token).WaitAsync(token);
        // PHD2's native action also returns false when already stopped. Query
        // the connected device again; cached event state cannot prove a stop.
        if (!stopped && device is PHD2Guider remote)
            stopped = await NinaGuiderStop.ConfirmPHD2StoppedAsync(
                () => remote.SendMessage(new Phd2GetAppState(), 3000), token);
        // NINA's local pulse/dither guider returns false when already idle. Its
        // in-process state is distinct from a remote guider's cached status.
        if (!stopped && device is not NINA.Equipment.Equipment.MyGuider.DirectGuider { State: "Idle", ShiftEnabled: false })
            throw new InvalidOperationException("The bound guider did not confirm stopping.");
        if (profiles.ActiveProfile.Id != profile || !guider.GetInfo().Connected || !device.Connected || !ReferenceEquals(guider.GetDevice(), device)
            || guider.GetInfo().DeviceId != guideId || device.Id != guideId || profiles.ActiveProfile.GuiderSettings.GuiderName != guideId)
            throw new InvalidOperationException("Guider context changed while stopping.");
    }
}
