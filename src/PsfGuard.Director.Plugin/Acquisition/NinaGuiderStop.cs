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
}
