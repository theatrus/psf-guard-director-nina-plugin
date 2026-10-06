using System.Security.Cryptography;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record NightCheckpointScope(CoordinatorBinding Binding, Guid ClientId, string Origin,
    string ConfigurationId, string PolicyHash);

// A crash boundary, never a launch permit. Active owners cannot mark themselves
// idle until every hook, native operation, check-in and server release has settled.
public sealed class CoordinatorNightCheckpoint
{
    private sealed record State(int Version, RecoveryIdentity Identity, NightCheckpointScope Scope, bool Idle, string? RunId, string Checksum);
    private readonly CoordinatorStateFile file;
    private readonly RecoveryIdentity identity;
    private readonly NightCheckpointScope scope;

    public CoordinatorNightCheckpoint(string root, RecoveryIdentity identity, NightCheckpointScope scope)
    {
        LedgerContract.CheckId(identity.NightId);
        CoordinatorCheckpointClient.ValidateBinding(scope.Binding);
        if (scope.ClientId == Guid.Empty || identity.RigId != scope.Binding.RigId.ToString("D")
            || identity.ConfigurationId != scope.ConfigurationId || identity.EndsAtMs <= identity.StartsAtMs
            || scope.PolicyHash.Length != 64 || !scope.PolicyHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid night checkpoint scope.");
        this.identity = identity; this.scope = scope;
        file = new(root, new { identity.NightId, scope.Binding.RigId }, "night-boundary");
    }

    public void Begin()
    {
        using var lease = file.Lock();
        if (Read() is not null) throw new InvalidDataException("This night boundary already exists.");
        Write(true, null);
    }

    public RestartBoundary RequireIdle()
    {
        var state = Read() ?? throw new InvalidDataException("No proven restart boundary exists for this night.");
        if (!state.Idle) throw new InvalidDataException("The previous owner may have interrupted an allocation or hook; restart is blocked.");
        return state.RunId is null ? RestartBoundary.Unused : RestartBoundary.Settled;
    }

    public void MarkDispatched(string runId)
    {
        LedgerContract.CheckId(runId);
        using var lease = file.Lock();
        RequireIdle();
        Write(false, runId);
    }

    public void MarkSettled(string runId)
    {
        using var lease = file.Lock();
        var state = Read() ?? throw new InvalidDataException("Missing active night boundary.");
        if (state.Idle || state.RunId != runId) throw new InvalidDataException("Only the active owner can settle this boundary.");
        Write(true, runId);
    }

    private State? Read()
    {
        var state = file.Read<State>();
        if (state is not null && (state.Version != 1 || state.Identity != identity || state.Scope != scope
            || state.Checksum != Checksum(state.Idle, state.RunId) || !state.Idle && state.RunId is null))
            throw new InvalidDataException("The recorded night scope or policy changed; restart is blocked.");
        return state;
    }
    private void Write(bool idle, string? runId) => file.Write(new State(1, identity, scope, idle, runId, Checksum(idle, runId)));
    private string Checksum(bool idle, string? runId) => HashEncoding.Lower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { identity, scope, idle, runId }, CoordinatorProgramContract.Options)));
}
