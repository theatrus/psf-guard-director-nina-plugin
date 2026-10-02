using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record NinaMotionEvidence(RecoveryMotion Motion, string Reason, ulong ValidUntilMs);

// Weather safety and clearance for mount motion are independent. This owner
// never opens a shutter and never resumes after losing commissioned clearance.
internal sealed class NinaEnclosureInterlock : IDomeConsumer
{
    private readonly object gate = new();
    private readonly IProfileService profiles;
    private readonly IProfile profile;
    private readonly Guid profileId;
    private readonly IDomeMediator dome;
    private readonly string deviceId;
    private readonly DirectorEnclosurePolicy policy;
    private readonly TimeProvider clock;
    private readonly TimeSpan maxAge;
    private readonly CancellationTokenSource interrupted = new();
    private readonly ITimer watchdog;
    private long? observed;
    private ulong observedUtc;
    private bool open, armed, stopped, disposed, profileChanged, receiving;
    private string reason = "Waiting for fresh enclosure clearance";

    internal NinaEnclosureInterlock(IProfileService profiles, IDomeMediator dome, DirectorEnclosurePolicy policy, TimeProvider clock)
    {
        this.profiles = profiles; this.dome = dome; this.policy = policy; this.clock = clock;
        profile = profiles.ActiveProfile; profileId = profile.Id; deviceId = profile.DomeSettings.Id;
        if (policy == DirectorEnclosurePolicy.Unconfigured || !Enum.IsDefined(policy))
            throw new InvalidOperationException("Select an enclosure clearance policy before acquisition.");
        if (policy == DirectorEnclosurePolicy.OpenAir ? deviceId != "No_Device" : string.IsNullOrWhiteSpace(deviceId) || deviceId == "No_Device")
            throw new InvalidOperationException("Enclosure policy does not match the configured NINA dome device.");
        var interval = profile.ApplicationSettings.DevicePollingInterval;
        if (!double.IsFinite(interval) || interval <= 0 || interval > 10)
            throw new InvalidOperationException("Enclosure monitoring requires a NINA polling interval of at most 10 seconds.");
        maxAge = TimeSpan.FromSeconds(Math.Max(5, interval * 3));
        profiles.BeforeProfileChanging += ProfileChanged;
        profiles.ProfileChanged += ProfileChanged;
        dome.Disconnected += Disconnected;
        dome.Closed += Closed;
        try
        {
            dome.RegisterConsumer(this);
            lock (gate) receiving = true;
            watchdog = clock.CreateTimer(_ => Read(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            Unsubscribe();
            try { dome.RemoveConsumer(this); } catch (Exception error) { NINA.Core.Utility.Logger.Error(error); }
            interrupted.Dispose();
            throw;
        }
    }

    internal CancellationToken Interrupted => interrupted.Token;
    internal void Arm()
    {
        lock (gate) { RequireClear(); armed = true; }
    }
    internal void RequireClear()
    {
        var evidence = Read();
        if (evidence.Motion != RecoveryMotion.Permitted) throw new InvalidOperationException(evidence.Reason);
    }
    internal NinaMotionEvidence Read()
    {
        lock (gate)
        {
            if (disposed || stopped) return new(RecoveryMotion.Unknown, reason, 0);
            try
            {
                if (profileChanged || !ReferenceEquals(profiles.ActiveProfile, profile) || profile.Id != profileId || profile.DomeSettings.Id != deviceId)
                    return Refuse("NINA profile or enclosure device changed");
                var info = dome.GetInfo();
                var now = checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());
                if (policy == DirectorEnclosurePolicy.OpenAir)
                    return info.Connected ? Refuse("Open-air policy cannot use a connected enclosure")
                        : new(RecoveryMotion.Permitted, "Commissioned open-air setup", checked(now + (ulong)maxAge.TotalMilliseconds));
                if (!info.Connected || info.DeviceId != deviceId) return Refuse("Required enclosure disconnected or changed");
                if (info.ShutterStatus != ShutterState.ShutterOpen)
                    return Refuse("Enclosure is not fully open; mount motion blocked", RecoveryMotion.Prohibited);
                var elapsed = observed is { } timestamp ? clock.GetElapsedTime(timestamp) : TimeSpan.MaxValue;
                if (!open || observed is null || elapsed < TimeSpan.Zero || elapsed >= maxAge
                    || now < observedUtc || now - observedUtc >= (ulong)maxAge.TotalMilliseconds)
                    return Refuse("Enclosure clearance is missing or stale");
                return new(RecoveryMotion.Permitted, "Enclosure is fully open",
                    Math.Min(checked(now + (ulong)Math.Floor((maxAge - elapsed).TotalMilliseconds)), checked(observedUtc + (ulong)maxAge.TotalMilliseconds)));
            }
            catch (Exception) { return Refuse("Enclosure clearance could not be read"); }
        }
    }

    public void UpdateDeviceInfo(DomeInfo info)
    {
        lock (gate)
        {
            // Registration can synchronously deliver NINA's cached info. Wait
            // for a subsequent device broadcast before treating it as fresh.
            if (disposed || stopped || !receiving) return;
            try
            {
                if (policy == DirectorEnclosurePolicy.OpenAir)
                {
                    if (info.Connected) Refuse("Open-air policy cannot use a connected enclosure");
                    return;
                }
                var timestamp = clock.GetTimestamp();
                var utc = checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());
                if (observed is { } previous && (clock.GetElapsedTime(previous, timestamp) < TimeSpan.Zero || utc < observedUtc))
                { Refuse("Enclosure observation clock moved backwards"); return; }
                if (armed && observed is { } last && (clock.GetElapsedTime(last, timestamp) >= maxAge || utc - observedUtc >= (ulong)maxAge.TotalMilliseconds))
                { Refuse("Enclosure updates resumed after a stale interval"); return; }
                observed = timestamp; observedUtc = utc;
                open = info.Connected && info.DeviceId == deviceId && info.ShutterStatus == ShutterState.ShutterOpen;
                if (!open) Refuse("Enclosure is unavailable or not fully open; mount motion blocked", RecoveryMotion.Prohibited);
            }
            catch (Exception) { open = false; observed = null; Refuse("Enclosure update could not be read"); }
        }
    }

    private NinaMotionEvidence Refuse(string message, RecoveryMotion motion = RecoveryMotion.Unknown)
    {
        reason = message;
        if (armed && !stopped)
        {
            stopped = true;
            _ = ObserveCancellationAsync(interrupted.CancelAsync());
        }
        return new(motion, message, 0);
    }
    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error) { NINA.Core.Utility.Logger.Error(error); }
    }
    private Task Disconnected(object sender, EventArgs args)
    {
        lock (gate) { if (!disposed && policy != DirectorEnclosurePolicy.OpenAir) { open = false; Refuse("Required enclosure disconnected"); } }
        return Task.CompletedTask;
    }
    private Task Closed(object sender, EventArgs args)
    {
        lock (gate) { if (!disposed) { open = false; Refuse("Enclosure closed; mount motion blocked", RecoveryMotion.Prohibited); } }
        return Task.CompletedTask;
    }
    private void ProfileChanged(object? sender, EventArgs args)
    {
        lock (gate) { if (!disposed) { profileChanged = true; Refuse("NINA profile changed"); } }
    }
    private void Unsubscribe()
    {
        profiles.BeforeProfileChanging -= ProfileChanged;
        profiles.ProfileChanged -= ProfileChanged;
        dome.Disconnected -= Disconnected;
        dome.Closed -= Closed;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Refuse("Enclosure interlock stopped"); disposed = true;
        }
        watchdog.Dispose(); Unsubscribe(); dome.RemoveConsumer(this);
        // Native operations may still be unwinding with the canceled token.
    }
}
