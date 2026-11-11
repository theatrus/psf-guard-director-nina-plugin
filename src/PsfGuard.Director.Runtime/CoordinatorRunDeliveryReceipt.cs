using System.Security.Cryptography;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

// A reporting cache, never permission to resume a run or operate equipment.
// Hash the saved inputs rather than trusting file dates: restored databases and
// SQLite WAL writes must not hide records that have not reached the server.
internal sealed class CoordinatorRunDeliveryReceipt
{
    internal sealed record Receipt(int SchemaVersion, string Fingerprint, ulong Captures, ulong Operations, string Checksum);
    private sealed record Input(string Name, string Digest);
    private readonly string root;
    private readonly string archivePath;
    private readonly CoordinatorStateFile file;

    internal CoordinatorRunDeliveryReceipt(string root, Uri endpoint, CoordinatorBinding binding, Guid clientId,
        string archivePath, bool allowInsecureHttp)
    {
        this.root = root;
        this.archivePath = archivePath;
        file = new(root, new { origin = CoordinatorTransport.Normalize(endpoint, allowInsecureHttp).AbsoluteUri, binding, clientId }, "reported-run");
    }

    internal async Task<Receipt?> ReadCurrentAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            RefuseLink(file.Path);
            var receipt = file.Read<Receipt>();
            if (receipt is null || receipt.Fingerprint is not { Length: 64 } || !receipt.Fingerprint.All(char.IsAsciiHexDigitLower)
                || receipt.Captures > long.MaxValue || receipt.Operations > long.MaxValue
                || !ValidChecksum(receipt)) return null;
            if (receipt.Fingerprint != await FingerprintAsync(token).ConfigureAwait(false)) return null;
            if (receipt.SchemaVersion == 1)
            {
                receipt = receipt with { SchemaVersion = 2, Checksum = Checksum(receipt.Fingerprint, receipt.Captures, receipt.Operations) };
                // A valid old receipt remains useful even if its optional format
                // upgrade cannot be written (for example, on a read-only backup).
                try { token.ThrowIfCancellationRequested(); file.Write(receipt); }
                catch (Exception error) when (CacheFailure(error)) { }
            }
            return receipt;
        }
        catch (Exception error) when (CacheFailure(error)) { return null; }
    }

    // Call only after both feeds caught up, any required release succeeded,
    // and the runtime closed its database. A failed cache write is harmless.
    internal async Task StoreAsync(ulong captures, ulong operations, CancellationToken token)
    {
        try
        {
            var fingerprint = await FingerprintAsync(token).ConfigureAwait(false);
            if (fingerprint is null) return;
            token.ThrowIfCancellationRequested();
            RefuseLink(file.Path);
            file.Write(new Receipt(2, fingerprint, captures, operations, Checksum(fingerprint, captures, operations)));
        }
        catch (Exception error) when (CacheFailure(error)) { }
    }

    private async Task<string?> FingerprintAsync(CancellationToken token)
    {
        RefuseLink(root);
        var ledgerRoot = Path.Combine(root, "ledger");
        RefuseLink(ledgerRoot);
        var ledger = Path.Combine(ledgerRoot, "execution.sqlite");
        if (!File.Exists(archivePath) || !File.Exists(ledger) || new FileInfo(ledger).Length < 100) return null;
        var paths = new[] { archivePath, ledger, ledger + "-wal", ledger + "-journal" }
            .Concat(Directory.EnumerateFiles(root, "capture-cursor-*.json"))
            .Concat(Directory.EnumerateFiles(root, "preparation-cursor-*.json"))
            .Order(StringComparer.Ordinal);
        var inputs = new List<Input>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            RefuseLink(path);
            var digest = "absent";
            if (File.Exists(path))
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                digest = HashEncoding.Lower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            }
            inputs.Add(new(Path.GetRelativePath(root, path), digest));
        }
        return HashEncoding.Lower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inputs)));
    }

    private string Checksum(string fingerprint, ulong captures, ulong operations) => HashEncoding.Lower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope = Path.GetFileName(file.Path),
            schemaVersion = 2,
            fingerprint,
            captures,
            operations
        })));

    private bool ValidChecksum(Receipt receipt) => receipt.SchemaVersion switch
    {
        2 => receipt.Checksum == Checksum(receipt.Fingerprint, receipt.Captures, receipt.Operations),
        // The first receipts shipped with IPC 14; 0.1.0.17/18 used IPC 15.
        // IPC versions govern equipment decisions, not acknowledgement of
        // unchanged historical files. Migrate these known formats without
        // reopening ledgers or asking the server to accept their events again.
        1 => receipt.Checksum == LegacyChecksum(receipt, 14) || receipt.Checksum == LegacyChecksum(receipt, 15),
        _ => false
    };

    private string LegacyChecksum(Receipt receipt, int protocolVersion) => HashEncoding.Lower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope = Path.GetFileName(file.Path),
            schemaVersion = 1,
            ProtocolVersion = protocolVersion,
            EngineVersion = "0.3.0",
            fingerprint = receipt.Fingerprint,
            captures = receipt.Captures,
            operations = receipt.Operations
        })));

    private static bool CacheFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException;

    private static void RefuseLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Saved-run receipts do not follow linked storage.");
    }
}
