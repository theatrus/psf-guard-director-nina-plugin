using System.IO;
using System.Security.Cryptography;
using System.Text;
using NINA.Core.Utility;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

/// <summary>
/// The one upload queue in this N.I.N.A. process. Director Sessions add the
/// frames they save; the plugin starts and stops the worker and shows the
/// queue. The queue is never disposed: a frame offered while the worker is
/// stopped is still written to disk, and the next start uploads it.
/// </summary>
internal static class DirectorImageUploads
{
    internal const string SettingKey = "UploadCapturedFrames";

    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSFGuardDirector", "uploads");

    private static readonly Lazy<DirectorUploadQueue> Shared = new(() =>
        new DirectorUploadQueue(Root, new DirectorUploadConnector(PairingKey, Connect),
            message => Logger.Info($"PSF Guard Director uploads: {message}")));

    internal static DirectorUploadQueue Queue => Shared.Value;

    /// <summary>
    /// An identity for this profile's current pairing when it is for the
    /// destination's rig; null when the pairing is gone or for another rig.
    /// It is a hash and holds no secret.
    /// </summary>
    internal static string? PairingKey(DirectorUploadDestination destination)
    {
        var pairing = DirectorCredentialStore.Read(destination.Endpoint, destination.ProfileId);
        if (pairing is null || pairing.Binding.RigId != destination.RigId || pairing.Binding.ProfileId != destination.ProfileId) return null;
        var binding = pairing.Binding;
        return HashEncoding.Lower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{pairing.ClientId:D}\n{binding.CoordinatorInstanceId:D}\n{binding.CatalogId:D}\n{binding.RigId:D}\n{binding.ProfileId:D}\n{pairing.Token}")));
    }

    /// <summary>
    /// A client bound to the destination's current pairing. Each request reads
    /// the token again and fails when the pairing has changed; the queue then
    /// builds a new client.
    /// </summary>
    internal static IDirectorUploadServer Connect(DirectorUploadDestination destination)
    {
        var pairing = DirectorCredentialStore.Read(destination.Endpoint, destination.ProfileId)
            ?? throw new InvalidOperationException("This profile is not paired.");
        ValueTask<string?> Credential(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var current = DirectorCredentialStore.Read(destination.Endpoint, destination.ProfileId);
            if (current?.ClientId != pairing.ClientId || current.Binding != pairing.Binding || current.Token != pairing.Token)
                throw new InvalidOperationException("Pairing changed while uploading.");
            return ValueTask.FromResult<string?>(current.Token);
        }
        return new DirectorUploadClient(destination, Credential);
    }
}
