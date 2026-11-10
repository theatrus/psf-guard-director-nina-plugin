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
            if (receipt is null || receipt.SchemaVersion != 1 || receipt.Captures > long.MaxValue || receipt.Operations > long.MaxValue
                || receipt.Checksum != Checksum(receipt.Fingerprint, receipt.Captures, receipt.Operations)) return null;
            return receipt.Fingerprint == await FingerprintAsync(token).ConfigureAwait(false) ? receipt : null;
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
            file.Write(new Receipt(1, fingerprint, captures, operations, Checksum(fingerprint, captures, operations)));
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
            schemaVersion = 1,
            RuntimeContract.ProtocolVersion,
            RuntimeContract.EngineVersion,
            fingerprint,
            captures,
            operations
        })));

    private static bool CacheFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException;

    private static void RefuseLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Saved-run receipts do not follow linked storage.");
    }
}
