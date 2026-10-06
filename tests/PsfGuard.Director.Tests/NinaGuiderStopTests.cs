using Newtonsoft.Json;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using PsfGuard.Director.Plugin.Acquisition;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class NinaGuiderStopTests
{
    [Theory]
    [InlineData("Stopped", true)]
    [InlineData("Guiding", false)]
    [InlineData("Calibrating", false)]
    [InlineData("LostLock", false)]
    [InlineData("Paused", false)]
    [InlineData("Looping", false)]
    [InlineData("Selected", false)]
    [InlineData("", false)]
    public async Task OnlyAFreshStoppedResponseConfirmsQuiescence(string state, bool expected)
    {
        var response = JsonConvert.DeserializeObject<GenericPhdMethodResponse>(
            JsonConvert.SerializeObject(new { jsonrpc = "2.0", result = state }))!;
        var queried = false;
        Assert.Equal(expected, await NinaGuiderStop.ConfirmPHD2StoppedAsync(() =>
        {
            queried = true;
            return Task.FromResult(response);
        }, CancellationToken.None));
        Assert.True(queried);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"result\":true}")]
    [InlineData("{\"result\":\"Stopped\",\"error\":{\"code\":1,\"message\":\"failed\"}}")]
    public async Task MissingMalformedOrErroredResponsesCannotConfirmAStop(string json)
    {
        var response = JsonConvert.DeserializeObject<GenericPhdMethodResponse>(json);
        Assert.False(await NinaGuiderStop.ConfirmPHD2StoppedAsync(() => Task.FromResult(response!), CancellationToken.None));
    }

    [Fact]
    public async Task CanceledConfirmationCannotBeginAQuery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var queried = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NinaGuiderStop.ConfirmPHD2StoppedAsync(() =>
        {
            queried = true;
            return Task.FromResult(new GenericPhdMethodResponse { result = "Stopped" });
        }, cancellation.Token));
        Assert.False(queried);
    }

    [Fact]
    public async Task CancellationDoesNotAcceptAnUnfinishedQuery()
    {
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<GenericPhdMethodResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = NinaGuiderStop.ConfirmPHD2StoppedAsync(() => response.Task, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation);
        response.SetResult(new GenericPhdMethodResponse { result = "Stopped" });
    }

    [Fact]
    public async Task CommunicationFailureCannotConfirmAStop()
    {
        var failure = new IOException("PHD2 disconnected");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => NinaGuiderStop.ConfirmPHD2StoppedAsync(
            () => Task.FromException<GenericPhdMethodResponse>(failure), CancellationToken.None)));
    }
}
