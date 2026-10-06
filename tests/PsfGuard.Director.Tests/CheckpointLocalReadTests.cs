using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class CheckpointLocalReadTests
{
    [Fact]
    public async Task CancelBeforeReadDoesNotEnterTheProtocol()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CheckpointLocalRead.ReadAsync(() =>
        {
            called = true;
            return Task.FromResult(42);
        }, cancellation.Token));
        Assert.False(called);
    }

    [Fact]
    public async Task CancellationDrainsTheInFlightReadBeforeStoppingDelivery()
    {
        using var cancellation = new CancellationTokenSource();
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = CheckpointLocalRead.ReadAsync(() => read.Task, cancellation.Token);
        cancellation.Cancel();
        Assert.False(delivery.IsCompleted);
        read.SetResult(42);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.True(read.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReadFailureStillPropagates()
    {
        var error = new IOException("protocol failed");
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => CheckpointLocalRead.ReadAsync(
            () => Task.FromException<int>(error), CancellationToken.None)));
    }

    [Fact]
    public async Task UncanceledReadReturnsItsExactResult()
    {
        Assert.Equal(42, await CheckpointLocalRead.ReadAsync(() => Task.FromResult(42), CancellationToken.None));
    }
}
