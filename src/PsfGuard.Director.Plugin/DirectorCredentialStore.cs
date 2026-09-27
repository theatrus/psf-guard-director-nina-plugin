using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

internal static class DirectorCredentialStore
{
    private sealed record Entry(int Version, string Origin, CoordinatorBinding Binding, Guid ClientId, string Token)
    {
        public override string ToString() => "Director credential (redacted)";
    }

    internal static string Target(Uri endpoint, Guid profile) => "PSFGuard.Director/" + profile.ToString("D") + "/" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));

    internal static CoordinatorPairing? Read(Uri endpoint, Guid profile)
    {
        var value = ReadSecret(Target(endpoint, profile));
        if (value is null) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(value);
            if (entry is null || entry.Version != 1 || entry.Origin != endpoint.AbsoluteUri || entry.Binding.ProfileId != profile
                || entry.Binding.CoordinatorInstanceId == Guid.Empty || entry.Binding.CatalogId == Guid.Empty || entry.Binding.RigId == Guid.Empty
                || entry.ClientId == Guid.Empty || !CoordinatorPairingClient.ValidToken(entry.Token))
                throw new InvalidOperationException();
            return new CoordinatorPairing(entry.Binding, entry.ClientId, entry.Token);
        }
        catch (Exception) { throw new InvalidOperationException("Director credential is invalid. Reset pairing and use a new code."); }
    }

    internal static void Store(Uri endpoint, CoordinatorPairing pairing) => WriteSecret(Target(endpoint, pairing.Binding.ProfileId),
        JsonSerializer.Serialize(new Entry(1, endpoint.AbsoluteUri, pairing.Binding, pairing.ClientId, pairing.Token)));
    internal static void Forget(Uri endpoint, Guid profile) => WriteSecret(Target(endpoint, profile), null);

    private static string? ReadSecret(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 ? null : throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("Director credential is invalid.");
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    private static void WriteSecret(string target, string? secret)
    {
        if (secret is null)
        {
            if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 2560) throw new InvalidOperationException("Director credential is too large.");
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = 1,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = 2,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
