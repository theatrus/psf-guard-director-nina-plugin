using System.IO;
using System.Security.Cryptography;
using System.Windows;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Acquisition;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.SimulatorProbe;

internal sealed class UploadProbe(IProfileService profiles) : IAsyncDisposable
{
    private DirectorPlugin? plugin;
    internal async Task ConfigureAsync(CoordinatorProbe fixture, CancellationToken token)
    {
        if (fixture.ImageUploads is not ("on" or "off") || !fixture.Endpoint.IsLoopback
            || fixture.UploadDirectory is null || !Directory.Exists(fixture.UploadDirectory))
            throw new InvalidDataException("Uploads require an isolated loopback receive folder.");
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            // Use the packaged plugin in this probe's load context, as the native
            // acquisition fixture does. NINA loads its settings export separately.
            plugin = new DirectorPlugin(profiles);
            await plugin.Initialize();
            if (plugin.UploadCapturedFrames) throw new InvalidDataException("File uploads did not default off.");
            plugin.ConnectionModel.Changed();
            if (fixture.ImageUploads == "on") plugin.UploadCapturedFrames = true;
        }).Task.Unwrap();
        using var client = DirectorImageUploads.Connect(fixture.UploadDestination) ?? throw new InvalidDataException("Upload pairing is missing.");
        if ((await client.GetStateAsync(token)).Kind != DirectorUploadStateKind.Enabled)
            throw new InvalidDataException("The isolated server must allow uploads even when the plugin switch is off.");
    }

    internal async Task VerifyAsync(CoordinatorProbe fixture,
        IReadOnlyCollection<CaptureEvidence> captures, string run, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var expected = fixture.ImageUploads == "on" ? captures.Count : 0;
        string[] received;
        while (true)
        {
            received = Directory.GetFiles(fixture.UploadDirectory!, "*.fits", SearchOption.AllDirectories);
            var summary = DirectorImageUploads.Queue.Summarize(fixture.UploadDestination);
            if (summary.Refused != 0) throw new InvalidDataException("The server refused a native captured frame.");
            if (received.Length == expected && summary.Queued == 0) break;
            await Task.Delay(200, deadline.Token);
        }
        foreach (var path in received)
        {
            var source = captures.Single(c => Path.GetFileName(c.SavedPath) == Path.GetFileName(path)).SavedPath!;
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source, token));
            var receivedHash = SHA256.HashData(await File.ReadAllBytesAsync(path, token));
            if (!originalHash.SequenceEqual(receivedHash))
                throw new InvalidDataException("The received image differs from N.I.N.A.'s saved image.");
        }
        if (expected > 0)
        {
            using var client = DirectorImageUploads.Connect(fixture.UploadDestination)!;
            var repeated = await client.UploadAsync(captures.First().SavedPath!, token);
            if (repeated.Outcome != DirectorUploadOutcome.Stored || !repeated.AlreadyPresent)
                throw new InvalidDataException("A repeated native upload did not identify the existing image.");
        }
        await Application.Current.Dispatcher.InvokeAsync(() => plugin!.UploadCapturedFrames = false);
        await File.WriteAllTextAsync(Path.Combine(run, "uploads-verified.txt"),
            $"Live image records; uploads {fixture.ImageUploads}; {captures.Count} native saves; {received.Length} identical received files; queue empty.", token);
    }

    public async ValueTask DisposeAsync()
    {
        if (plugin is not null)
            await Application.Current.Dispatcher.InvokeAsync(plugin.Teardown).Task.Unwrap();
    }
}
