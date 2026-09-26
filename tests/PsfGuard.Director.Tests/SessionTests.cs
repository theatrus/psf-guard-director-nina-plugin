using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class SessionTests
{
    [Theory]
    [InlineData("session")]
    [InlineData("request")]
    [InlineData("protocol")]
    [InlineData("type")]
    [InlineData("extra")]
    [InlineData("length")]
    [InlineData("truncated")]
    public async Task BadRepliesInvalidateSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync(fault);
        if (fault == "truncated")
            await Assert.ThrowsAsync<EndOfStreamException>(() => peer.Session.PingAsync(default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.PingAsync(default));
        Assert.False(peer.Session.IsReady);
        await Assert.ThrowsAsync<IOException>(() => peer.Session.PingAsync(default));
    }

    [Fact]
    public async Task IncompatibleHandshakeNeverBecomesReady()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => TestPeer.CreateAsync("handshake"));
    }

    [Fact]
    public async Task CancellationBeforeDispatchKeepsSessionUsable()
    {
        await using var peer = await TestPeer.CreateAsync("none");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.Session.PingAsync(cancelled.Token));
        Assert.True(peer.Session.IsReady);
        await peer.Session.PingAsync(default);
    }

    [Fact]
    public async Task CancellationAfterDispatchInvalidatesSession()
    {
        await using var peer = await TestPeer.CreateAsync("stall");
        using var cancelled = new CancellationTokenSource();
        var request = peer.Session.PingAsync(cancelled.Token);
        await peer.PingSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task StalledResponseHasADeadline()
    {
        await using var peer = await TestPeer.CreateAsync("stall");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.Session.PingAsync(default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task DisposalCancelsInFlightRead()
    {
        await using var peer = await TestPeer.CreateAsync("stall");
        var request = peer.Session.PingAsync(default);
        await peer.PingSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await peer.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task CancellingAQueuedCallerDoesNotAbandonAnotherRequest()
    {
        await using var peer = await TestPeer.CreateAsync("stall");
        using var firstCancellation = new CancellationTokenSource();
        var first = peer.Session.PingAsync(firstCancellation.Token);
        await peer.PingSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var queuedCancellation = new CancellationTokenSource();
        var queued = peer.Session.PingAsync(queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(peer.Session.IsReady);
        Assert.False(first.IsCompleted);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(peer.Session.IsReady);
    }
}
