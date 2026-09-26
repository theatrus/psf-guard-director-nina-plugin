using System.Security.Cryptography;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public static class RuntimeContract
{
    public const int ProtocolVersion = 7;
    public const int ContractVersion = 2;
    public const string RuntimeVersion = "0.6.0";
    public const string EngineVersion = "0.2.0";
    public const string ExecutableName = "psf-guard-director-runtime.exe";
    internal const int MaxFrameBytes = 266240;

    public static string PinnedHash { get; } = ReadPin();

    private static string ReadPin()
    {
        using var stream = typeof(RuntimeContract).Assembly.GetManifestResourceStream("Director.RuntimeLock")
            ?? throw new InvalidDataException("Runtime pin is missing.");
        using var document = JsonDocument.Parse(stream);
        var pin = document.RootElement;
        if (pin.GetProperty("protocol_version").GetInt32() != ProtocolVersion ||
            pin.GetProperty("contract_version").GetInt32() != ContractVersion ||
            pin.GetProperty("runtime_version").GetString() != RuntimeVersion ||
            pin.GetProperty("engine_version").GetString() != EngineVersion)
            throw new InvalidDataException("Runtime pin and host contract disagree.");
        var hash = pin.GetProperty("executable_sha256").GetString();
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Runtime pin has no valid SHA-256.");
        return hash;
    }
}

internal sealed class RuntimeBundle : IDisposable
{
    private readonly FileStream lease;
    internal string Executable { get; }

    private RuntimeBundle(string executable, FileStream lease)
    {
        Executable = executable;
        this.lease = lease;
    }

    internal static async Task<RuntimeBundle> OpenAsync(string pluginDirectory, CancellationToken token)
    {
        var executable = Path.GetFullPath(Path.Combine(pluginDirectory, "runtime", RuntimeContract.ExecutableName));
        // Hold a read-only share until the child exits so the verified executable
        // cannot be replaced or rewritten between hashing and process launch.
        var lease = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (lease.Length is <= 0 or > 100_000_000)
                throw new InvalidDataException("Runtime executable has an invalid size.");
            var actual = await SHA256.HashDataAsync(lease, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(RuntimeContract.PinnedHash)))
                throw new InvalidDataException("Runtime checksum mismatch. Reinstall the Director bundle.");
            return new RuntimeBundle(executable, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose() => lease.Dispose();
}
