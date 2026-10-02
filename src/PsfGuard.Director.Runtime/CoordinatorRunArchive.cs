using System.Security.Cryptography;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record CoordinatorSavedRun(CoordinatorAllocationEnvelope Allocation, DirectorConstraints Constraints,
    PlannerState InitialState, LedgerIdentity Ledger, bool AutomaticWorkload, bool LocalTargetScheduling,
    bool Completed = false, bool Released = false);

/// <summary>Original ledger inputs for reporting only. Never launch or dispatch from this record.</summary>
public sealed class CoordinatorRunArchive
{
    private sealed record Entry(int SchemaVersion, string Origin, CoordinatorBinding Binding, Guid ClientId,
        CoordinatorSavedRun Run, string Checksum);
    private readonly CoordinatorStateFile file;
    private readonly string origin;
    private readonly CoordinatorBinding binding;
    private readonly Guid clientId;

    public CoordinatorRunArchive(string root, Uri endpoint, CoordinatorBinding binding, Guid clientId, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        if (clientId == Guid.Empty) throw new ArgumentException("A paired client is required.");
        origin = CoordinatorTransport.Normalize(endpoint, allowInsecureHttp).AbsoluteUri;
        this.binding = binding; this.clientId = clientId;
        file = new(root, new { origin, binding, clientId }, "checkin-run");
    }

    public CoordinatorSavedRun? Read()
    {
        var entry = file.Read<Entry>();
        if (entry is null) return null;
        if (entry.SchemaVersion != 1 || entry.Origin != origin || entry.Binding != binding || entry.ClientId != clientId
            || entry.Run is null || entry.Checksum != Checksum(entry.Run))
            throw new InvalidDataException("Saved Director run identity or integrity mismatch.");
        Validate(entry.Run);
        return entry.Run;
    }

    public void Store(CoordinatorSavedRun run)
    {
        Validate(run);
        if (run.Completed || run.Released) throw new InvalidDataException("A new run cannot assert completed work.");
        using var lease = file.Lock();
        var old = Read();
        if (old is not null && Checksum(old) != Checksum(run)) throw new InvalidDataException("Saved run cannot be replaced.");
        Write(run);
    }

    // Call only after native shutdown and checking for unresolved ledger operations.
    public void MarkCompleted()
    {
        using var lease = file.Lock();
        var run = Read() ?? throw new InvalidDataException("Missing saved run.");
        Write(run with { Completed = true });
    }

    public void MarkReleased()
    {
        using var lease = file.Lock();
        var run = Read() ?? throw new InvalidDataException("Missing saved run.");
        if (!run.Completed || !run.AutomaticWorkload) throw new InvalidDataException("Unfinished work cannot be released.");
        Write(run with { Released = true });
    }

    internal CoordinatorAllocation Allocation(CoordinatorSavedRun run) => CoordinatorAllocation.Read(
        CoordinatorAllocation.Wrap(run.Allocation), origin, binding, clientId, run.Allocation.Snapshot.Program.Configuration,
        run.Allocation.AdmittedAtMs); // Validate historical scope, never current equipment authority.

    private void Validate(CoordinatorSavedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Allocation?.Snapshot?.Program?.Configuration is null)
            throw new InvalidDataException("Missing saved allocation.");
        var program = Allocation(run).Envelope.Snapshot.Program;
        var ledger = run.Ledger;
        if (ledger is null || !Guid.TryParseExact(ledger.LedgerId, "D", out var id) || id == Guid.Empty
            || ledger.AssignmentId != program.Assignment.Id || ledger.AssignmentRevision != program.Assignment.Revision
            || ledger.ConfigurationId != program.Configuration.Id || ledger.RigId != binding.RigId.ToString("D")
            || run.InitialState is null || run.InitialState.RigId != ledger.RigId || run.InitialState.ConfigurationId != ledger.ConfigurationId
            || run.Constraints?.Rig is null || run.Constraints.Rig.RigId != ledger.RigId || run.Constraints.Rig.ConfigurationId != ledger.ConfigurationId
            || run.Released && (!run.Completed || !run.AutomaticWorkload))
            throw new InvalidDataException("Saved Director run does not match its ledger.");
    }
    private void Write(CoordinatorSavedRun run) => file.Write(new Entry(1, origin, binding, clientId, run, Checksum(run)));
    private string Checksum(CoordinatorSavedRun run) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { origin, binding, clientId, run }, CoordinatorProgramContract.Options)));
}
