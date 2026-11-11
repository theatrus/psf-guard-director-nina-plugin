using NINA.Core.Model;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;

namespace PsfGuard.Director.Plugin.Acquisition;

// Cover motion is local equipment preparation. It never grants light-exposure authority.
internal sealed class NinaFlatPanel(IProfileService profiles, IFlatDeviceMediator? panel, TimeSpan timeout)
{
    private readonly IProfile profile = profiles.ActiveProfile;
    private readonly string selected = profiles.ActiveProfile.FlatDeviceSettings.Id;
    private bool managed, failed;

    private FlatDeviceInfo? Read(bool cleanup = false)
    {
        if (!ReferenceEquals(profile, profiles.ActiveProfile) || profile.FlatDeviceSettings.Id != selected)
            throw new InvalidOperationException("The flat-panel selection changed. Start a new Director session.");
        if (failed && !cleanup) throw new InvalidOperationException("The flat panel did not finish its last operation. Check the cover before restarting Director.");
        var info = panel?.GetInfo();
        if (info?.Connected != true)
        {
            if (managed) throw new InvalidOperationException("The flat panel disconnected. Director cannot confirm the cover position.");
            return null;
        }
        if (info.DeviceId != selected)
            throw new InvalidOperationException("The connected flat panel does not match the N.I.N.A. profile.");
        managed = true;
        return info;
    }

    internal bool CanClose => Read() is { SupportsOpenClose: true };

    internal void RequireLightsReady()
    {
        var info = Read();
        if (info is not null && (info.LightOn || info.SupportsOpenClose && info.CoverState != CoverState.Open))
            throw new InvalidOperationException("The flat-panel light must be off and its cover open before imaging the sky.");
    }

    internal void RequireDarkReady()
    {
        if (Read() is not { SupportsOpenClose: true, CoverState: CoverState.Closed, LightOn: false })
            throw new InvalidOperationException("Dark frames require a connected motorized cover, confirmed closed, with the panel light off.");
    }

    internal void RequireFlatReady()
    {
        if (Read() is not { SupportsOpenClose: true, CoverState: CoverState.Closed, LightOn: true })
            throw new InvalidOperationException("Flat frames require the cover to stay closed and the panel light to stay on.");
    }

    internal async Task OpenForLightsAsync(Action check, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (Read() is null) return;
        await LightOffAsync(check, progress, token);
        if (Read() is { SupportsOpenClose: true, CoverState: not CoverState.Open })
            await OperateAsync("Opening the flat-panel cover", ct => panel!.OpenCover(progress, ct),
                i => i.CoverState == CoverState.Open, check, progress, token);
        RequireLightsReady();
    }

    internal async Task CloseAsync(Action check, IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        if (Read() is null) return;
        await LightOffAsync(check, progress, token);
        if (Read() is { SupportsOpenClose: true, CoverState: not CoverState.Closed })
            await OperateAsync("Closing the flat-panel cover", ct => panel!.CloseCover(progress, ct),
                i => i.CoverState == CoverState.Closed, check, progress, token);
    }

    internal async Task LightOffAsync(Action check, IProgress<ApplicationStatus> progress, CancellationToken token, bool cleanup = false)
    {
        if (Read(cleanup) is { LightOn: true })
            await OperateAsync("Turning off the flat-panel light", ct => panel!.ToggleLight(false, progress, ct),
                i => !i.LightOn, check, progress, token, cleanup);
    }

    private async Task OperateAsync(string name, Func<CancellationToken, Task> action, Func<FlatDeviceInfo, bool> done,
        Action check, IProgress<ApplicationStatus> progress, CancellationToken token, bool cleanup = false)
    {
        token.ThrowIfCancellationRequested(); check(); Read(cleanup);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        progress.Report(new ApplicationStatus { Status = name });
        try
        {
            var operation = action(deadline.Token);
            _ = operation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await operation.WaitAsync(deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested(); check();
                if (Read(cleanup) is { } info && done(info)) return;
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            failed = true;
            throw new TimeoutException($"{name} timed out. Check the flat panel before restarting Director.");
        }
        catch { failed = true; throw; }
    }
}
