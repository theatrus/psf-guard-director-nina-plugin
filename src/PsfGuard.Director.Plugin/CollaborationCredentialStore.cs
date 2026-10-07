using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

internal static class CollaborationCredentialStore
{
    private sealed record Entry(string Origin, Guid Profile, string Agent, string Token)
    { public override string ToString() => "Collaboration credential (redacted)"; }
    private static string Target(Uri endpoint, Guid profile, Guid connection) => "PSFGuard.Director.Collaboration/" + profile.ToString("D") + "/" + connection.ToString("D") + "/"
        + HashEncoding.Lower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));
    internal static CollaborationCredential? Read(Uri endpoint, Guid profile, Guid connection)
    {
        try
        {
            var text = DirectorCredentialStore.ReadSecret(Target(endpoint, profile, connection));
            if (text is null) return null;
            var entry = JsonSerializer.Deserialize<Entry>(text);
            if (entry is null || entry.Origin != endpoint.AbsoluteUri || entry.Profile != profile
                || !CollaborationAuthClient.ValidAgent(entry.Agent) || !CollaborationAuthClient.ValidSecret(entry.Token)) throw new InvalidOperationException();
            return new(entry.Agent, entry.Token);
        }
        catch (Exception) { throw new InvalidOperationException("Collaboration credential unavailable; original identity retained."); }
    }
    internal static void Store(Uri endpoint, Guid profile, Guid connection, CollaborationCredential credential) =>
        DirectorCredentialStore.WriteSecret(Target(endpoint, profile, connection), JsonSerializer.Serialize(new Entry(endpoint.AbsoluteUri, profile, credential.AgentId, credential.Token)));
    internal static void Forget(Uri endpoint, Guid profile, Guid connection) => DirectorCredentialStore.WriteSecret(Target(endpoint, profile, connection), null);
}
