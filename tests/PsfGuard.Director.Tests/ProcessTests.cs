using System.Diagnostics;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class ProcessTests
{
    internal static string BundleDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "runtime.lock.json")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new IOException("Run tests from a checkout with the pinned runtime fetched.");
        }
    }

    [Fact]
    public async Task RealSidecarNegotiatesShutsDownAndLeavesNoOrphan()
    {
        var session = await RuntimeSession.StartAsync(BundleDirectory, "rig-test", default);
        using var child = Process.GetProcessById(session.ProcessId!.Value);
        await using (session)
        {
            Assert.True(session.IsReady);
            await session.PingAsync(default);
            await session.ShutdownAsync(default);
            Assert.False(session.IsReady);
            Assert.True(child.HasExited);
            Assert.Equal(0, session.ExitCode);
        }
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task ChildCrashCannotLeaveSessionUsable()
    {
        await using var session = await RuntimeSession.StartAsync(BundleDirectory, "rig-test", default);
        using var child = Process.GetProcessById(session.ProcessId!.Value);
        child.Kill();
        await child.WaitForExitAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => session.PingAsync(default));
        Assert.False(session.IsReady);
    }

    [Fact]
    public async Task BundleCannotChangeBetweenVerificationAndExit()
    {
        using var bundle = await RuntimeBundle.OpenAsync(BundleDirectory, default);
        Assert.Throws<IOException>(() => new FileStream(bundle.Executable, FileMode.Open, FileAccess.Write, FileShare.Read));
    }

    [Fact]
    public async Task ModifiedRuntimeIsRejectedBeforeLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "director-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "runtime", RuntimeContract.ExecutableName), [1, 2, 3]);
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeSession.StartAsync(root, "rig-test", default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ControllerStartsHeartbeatsStopsAndChangesRig()
    {
        await using var controller = new RuntimeController(BundleDirectory, TimeSpan.FromMilliseconds(50));
        Assert.Equal(RuntimeState.Stopped, controller.Status.State);
        await controller.StartAsync("first-rig");
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
        await Task.Delay(200);
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
        await controller.StartAsync("second-rig");
        Assert.Equal("second-rig", controller.Status.RigId);
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
        await controller.StopAsync();
        Assert.Equal(RuntimeState.Stopped, controller.Status.State);
        await controller.StartAsync("second-rig");
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
    }

    [Fact]
    public async Task MissingBundleProducesActionableState()
    {
        await using var controller = new RuntimeController(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await controller.StartAsync("rig-test");
        Assert.Equal(RuntimeState.Faulted, controller.Status.State);
        Assert.Contains("Runtime missing", controller.Status.Message);
    }

    [Fact]
    public async Task ThrowingStatusObserverCannotOrphanChild()
    {
        await using var controller = new RuntimeController(BundleDirectory);
        controller.StateChanged += (_, _) => throw new InvalidOperationException("Observer failed");
        await controller.StartAsync("rig-test");
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
        await controller.StopAsync();
        Assert.Equal(RuntimeState.Stopped, controller.Status.State);
    }

    [Fact]
    public async Task StopDuringStartupCancelsTheNewSession()
    {
        await using var controller = new RuntimeController(BundleDirectory);
        Task? stop = null;
        controller.StateChanged += (_, _) =>
        {
            if (controller.Status.State == RuntimeState.Starting) stop = controller.StopAsync();
        };
        await controller.StartAsync("rig-test");
        Assert.NotNull(stop);
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RuntimeState.Stopped, controller.Status.State);
    }
}
