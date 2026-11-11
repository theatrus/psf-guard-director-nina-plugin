using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.SimulatorProbe;

internal static class SavedHistoryUpgradeProbe
{
    // Called only with this probe's isolated, already-reported simulator run.
    // Reproduce history written before the IPC 14 -> 15 runtime upgrade.
    internal static async Task SeedAsync(string runRoot, int count, CancellationToken token)
    {
        var receiptPath = Directory.GetFiles(runRoot, "reported-run-*.json").Single();
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath, token))!;
        var fingerprint = receipt["fingerprint"]!.GetValue<string>();
        var captures = receipt["captures"]!.GetValue<ulong>();
        var operations = receipt["operations"]!.GetValue<ulong>();
        receipt["schema_version"] = 1;
        receipt["checksum"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope = Path.GetFileName(receiptPath),
            schemaVersion = 1,
            ProtocolVersion = 14,
            EngineVersion = "0.3.0",
            fingerprint,
            captures,
            operations
        }))).ToLowerInvariant();
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString(), token);
        for (var i = 1; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var copy = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(runRoot)!, Guid.NewGuid().ToString("N"))).FullName;
            foreach (var source in Directory.EnumerateFiles(runRoot, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(runRoot, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
        }
    }
}
