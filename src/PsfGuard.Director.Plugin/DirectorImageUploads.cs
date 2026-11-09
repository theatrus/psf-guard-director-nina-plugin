using System.IO;
using NINA.Core.Utility;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Plugin;

/// <summary>
/// The one upload queue in this N.I.N.A. process. Director Sessions add the
/// frames they save; the plugin starts the worker and shows the queue.
/// </summary>
internal static class DirectorImageUploads
{
    internal const string SettingKey = "UploadCapturedFrames";

    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSFGuardDirector", "uploads");

    private static readonly Lazy<DirectorUploadQueue> Shared = new(() =>
        new DirectorUploadQueue(Root, Connect, message => Logger.Info($"PSF Guard Director uploads: {message}")));

    internal static DirectorUploadQueue Queue => Shared.Value;

    /// <summary>
    /// A client for <paramref name="destination"/>, or null when this
    /// profile's current pairing is for another rig or is gone. The token is
    /// read again for each request.
    /// </summary>
    internal static IDirectorUploadServer? Connect(DirectorUploadDestination destination)
    {
        var pairing = DirectorCredentialStore.Read(destination.Endpoint, destination.ProfileId);
        if (pairing is null || pairing.Binding.RigId != destination.RigId || pairing.Binding.ProfileId != destination.ProfileId) return null;
        ValueTask<string?> Credential(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var current = DirectorCredentialStore.Read(destination.Endpoint, destination.ProfileId);
            if (current?.ClientId != pairing.ClientId || current.Binding != pairing.Binding)
                throw new InvalidOperationException("Pairing changed while uploading.");
            return ValueTask.FromResult<string?>(current.Token);
        }
        return new DirectorUploadClient(destination, Credential);
    }
}
