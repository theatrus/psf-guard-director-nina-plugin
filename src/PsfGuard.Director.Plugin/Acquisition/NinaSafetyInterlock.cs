using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed record NinaSafetyEvidence(PlannerSafety Safety, string Reason, ulong ValidUntilMs);

// The attended safety policy can admit a profile with no monitor configured.
// A configured monitor always supplies fresh native evidence.
internal sealed class NinaSafetyInterlock : ISafetyMonitorConsumer
{
    private readonly object gate = new();
    private readonly IProfileService profiles;
    private readonly IProfile profile;
    private readonly Guid profileId;
    private readonly ISafetyMonitorMediator monitor;
    private readonly string deviceId;
    internal bool Monitored { get; }
    private readonly TimeProvider clock;
    private readonly TimeSpan maxAge;
    private CancellationTokenSource interrupted = new();
    private readonly ITimer watchdog;
    private long? observed;
    private ulong observedUtc;
    private long refusalRevision;
    internal long RefusalRevision { get { lock (gate) return refusalRevision; } }
    private bool safe, connected, armed, disposed, profileInvalidated;
    private string reason = "Waiting for a fresh safety-monitor update";

    internal NinaSafetyInterlock(IProfileService profiles, ISafetyMonitorMediator monitor, TimeProvider clock, IProfile? unmonitoredProfile = null)
    {
        this.profiles = profiles; this.monitor = monitor; this.clock = clock;
        profile = profiles.ActiveProfile;
        profileId = profile.Id;
        deviceId = profile.SafetyMonitorSettings.Id;
        Monitored = !(ReferenceEquals(profile, unmonitoredProfile) && deviceId == "No_Device");
        if (Monitored && (string.IsNullOrWhiteSpace(deviceId) || deviceId == "No_Device"))
            throw new InvalidOperationException("Director requires a configured safety monitor.");
        var interval = Monitored ? profile.ApplicationSettings.DevicePollingInterval : 0;
        if (Monitored && (!double.IsFinite(interval) || interval <= 0 || interval > 10))
            throw new InvalidOperationException("Safety monitoring requires a NINA polling interval of at most 10 seconds.");
        maxAge = Monitored ? TimeSpan.FromSeconds(Math.Max(5, interval * 3)) : TimeSpan.FromSeconds(1);
        profiles.BeforeProfileChanging += ProfileChanged;
        profiles.ProfileChanged += ProfileChanged;
        try
        {
            if (Monitored)
            {
                monitor.IsSafeChanged += SafeChanged;
                monitor.Disconnected += Disconnected;
                monitor.RegisterConsumer(this);
            }
            watchdog = clock.CreateTimer(_ => Read(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            Unsubscribe();
            try { if (Monitored) monitor.RemoveConsumer(this); }
            catch (Exception error) { NINA.Core.Utility.Logger.Error(error); }
            interrupted.Dispose();
            throw;
        }
    }

    internal CancellationToken Interrupted => interrupted.Token;

    internal NinaSafetyEvidence ReadCurrent() { lock (gate) return ReadLocked(current: true); }
    internal void Rearm(long expectedRevision)
    {
        lock (gate)
        {
            if (ReadLocked(current: true).Safety != PlannerSafety.Safe || refusalRevision != expectedRevision) throw new InvalidOperationException(reason);
            // Old native dispatch guards retain their canceled token forever.
            _ = ObserveCancellationAsync(interrupted.CancelAsync());
            interrupted = new();
            armed = true;
        }
    }

    internal void Arm()
    {
        lock (gate)
        {
            if (ReadLocked().Safety != PlannerSafety.Safe) throw new InvalidOperationException(reason);
            armed = true;
        }
    }

    internal NinaSafetyEvidence Read()
    {
        lock (gate) return ReadLocked();
    }

    private NinaSafetyEvidence ReadLocked(bool current = false)
    {
        if (disposed) return new(PlannerSafety.Unknown, "Safety interlock disposed", 0);
        try
        {
            if (profileInvalidated || !ReferenceEquals(profiles.ActiveProfile, profile) || profile.Id != profileId
                || profile.SafetyMonitorSettings.Id != deviceId)
                return Refuse("NINA profile or safety monitor changed");
            var info = monitor.GetInfo();
            var now = checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());
            if (!Monitored)
            {
                if (info.Connected) return Refuse("A safety monitor connected during attended operation. Start a new run.");
                if (interrupted.IsCancellationRequested) return new(PlannerSafety.Unsafe, reason, 0);
                reason = "Attended operation without a safety monitor";
                return new(PlannerSafety.Safe, reason, checked(now + (ulong)maxAge.TotalMilliseconds));
            }
            if (!info.Connected || info.DeviceId != deviceId) return Refuse("Required safety monitor disconnected or changed");
            if (!info.IsSafe) return Refuse("Safety monitor reports unsafe", PlannerSafety.Unsafe);
            var elapsed = observed is { } timestamp ? clock.GetElapsedTime(timestamp) : TimeSpan.MaxValue;
            if (!connected || !safe || observed is null || elapsed < TimeSpan.Zero || elapsed >= maxAge
                || now < observedUtc || now - observedUtc >= (ulong)maxAge.TotalMilliseconds)
                return Refuse("Waiting for a current reading from the safety monitor");
            if (!current && interrupted.IsCancellationRequested) return new(PlannerSafety.Unsafe, reason, 0);
            reason = "Safety monitor is safe";
            // Bound wall-clock validity by both clocks; moving the wall clock
            // backwards cannot extend freshness for a previously observed sample.
            var remaining = Math.Floor((maxAge - elapsed).TotalMilliseconds);
            return new(PlannerSafety.Safe, reason, Math.Min(checked(now + (ulong)remaining), checked(observedUtc + (ulong)maxAge.TotalMilliseconds)));
        }
        catch (Exception) { return Refuse("Safety monitor could not be read"); }
    }

    public void UpdateDeviceInfo(SafetyMonitorInfo deviceInfo)
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                var timestamp = clock.GetTimestamp();
                var utc = checked((ulong)clock.GetUtcNow().ToUnixTimeMilliseconds());
                if (observed is { } previous && (clock.GetElapsedTime(previous, timestamp) < TimeSpan.Zero || utc < observedUtc))
                {
                    Refuse("Safety-monitor observation clock moved backwards");
                    return;
                }
                if (armed && !interrupted.IsCancellationRequested && observed is { } last && (clock.GetElapsedTime(last, timestamp) >= maxAge
                    || utc - observedUtc >= (ulong)maxAge.TotalMilliseconds))
                {
                    Refuse("Safety-monitor updates resumed after a stale interval");
                    return;
                }
                observed = timestamp;
                observedUtc = utc;
                connected = deviceInfo.Connected && deviceInfo.DeviceId == deviceId;
                safe = connected && deviceInfo.IsSafe;
                if (!safe) Refuse(connected ? "Safety monitor reports unsafe" : "Required safety monitor disconnected or changed",
                    connected ? PlannerSafety.Unsafe : PlannerSafety.Unknown);
            }
            catch (Exception)
            {
                observed = null;
                connected = safe = false;
                Refuse("Safety-monitor update could not be read");
            }
        }
    }

    private NinaSafetyEvidence Refuse(string message, PlannerSafety state = PlannerSafety.Unknown)
    {
        refusalRevision++;
        reason = message;
        if (armed && !interrupted.IsCancellationRequested)
        {
            // Native callbacks must not escape into NINA's broadcast thread.
            _ = ObserveCancellationAsync(interrupted.CancelAsync());
        }
        return new(state, reason, 0);
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error) { NINA.Core.Utility.Logger.Error(error); }
    }

    private void SafeChanged(object? sender, IsSafeEventArgs args)
    {
        if (args.IsSafe) return; // A boolean event cannot refresh device identity.
        lock (gate) { if (!disposed) { safe = false; Refuse("Safety monitor reports unsafe", PlannerSafety.Unsafe); } }
    }
    private Task Disconnected(object sender, EventArgs args)
    {
        lock (gate) { if (!disposed) { connected = false; Refuse("Required safety monitor disconnected"); } }
        return Task.CompletedTask;
    }
    private void ProfileChanged(object? sender, EventArgs args)
    {
        lock (gate) { if (!disposed) { profileInvalidated = true; Refuse("NINA profile changed"); } }
    }
    private void Unsubscribe()
    {
        profiles.BeforeProfileChanging -= ProfileChanged;
        profiles.ProfileChanged -= ProfileChanged;
        monitor.IsSafeChanged -= SafeChanged;
        monitor.Disconnected -= Disconnected;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Refuse("Safety interlock stopped");
            disposed = true;
        }
        watchdog.Dispose();
        Unsubscribe();
        if (Monitored) monitor.RemoveConsumer(this);
        // Keep the canceled token source alive for native operations still
        // unwinding; disposal must not turn a safety stop into a token error.
    }
}
