using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class DirectorPairingTests
{
    private static readonly Uri Endpoint = new("https://director.example/");
    private static readonly string Code = "psfdpt_" + new string('a', 64);
    private static readonly string Secret = "psfdrc_" + new string('b', 64);
    private static readonly CoordinatorBinding Binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static JsonObject Answer() => new()
    {
        ["success"] = true,
        ["error"] = null,
        ["status"] = "ready",
        ["data"] = new JsonObject
        {
            ["protocol_version"] = 1,
            ["coordinator_instance_id"] = Binding.CoordinatorInstanceId,
            ["catalog_id"] = Binding.CatalogId,
            ["rig_id"] = Binding.RigId,
            ["profile_id"] = Binding.ProfileId,
            ["client_id"] = Guid.NewGuid(),
            ["token"] = Secret,
            ["scopes"] = new JsonArray("program:read", "checkin:write", "status:write")
        }
    };
    private static HttpResponseMessage Response(JsonNode body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ExchangesOnlyDirectorCodeAndReturnsRedactedCredential()
    {
        using var client = new CoordinatorPairingClient(Endpoint, new Handler(async (request, token) =>
        {
            Assert.Equal("/api/director/v1/pair", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(Code, body["pairing_token"]!.GetValue<string>());
            Assert.Equal(Binding.ProfileId.ToString(), body["profile_id"]!.GetValue<string>());
            return Response(Answer());
        }), TimeSpan.FromSeconds(2));
        var pairing = await client.PairAsync(Code, Binding.ProfileId);
        Assert.Equal(Binding, pairing.Binding);
        Assert.DoesNotContain(Secret, pairing.ToString());
        Assert.False(CoordinatorPairingClient.ValidCode("psfpt_" + new string('a', 64)));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("rig")]
    [InlineData("version")]
    [InlineData("sync-token")]
    [InlineData("scope")]
    [InlineData("extra")]
    [InlineData("null")]
    public async Task RejectsInvalidPairingReplyWithoutLeakingSecret(string fault)
    {
        var body = Answer();
        var data = body["data"]!;
        switch (fault)
        {
            case "profile": data["profile_id"] = Guid.NewGuid(); break;
            case "rig": data["rig_id"] = Guid.Empty; break;
            case "version": data["protocol_version"] = 2; break;
            case "sync-token": data["token"] = "psfrc_" + new string('b', 64); break;
            case "scope": data["scopes"]!.AsArray().Add("admin"); break;
            case "extra": data["password"] = Secret; break;
            case "null": data["token"] = null; break;
        }
        using var client = new CoordinatorPairingClient(Endpoint, new Handler((_, _) => Task.FromResult(Response(body))), TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.PairAsync(Code, Binding.ProfileId));
        Assert.Equal(CoordinatorIntakeFailure.MalformedResponse, error.Failure);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task PairingDeadlineIncludesSlowResponseAndCallerCancellationRemainsCancellation()
    {
        using var client = new CoordinatorPairingClient(Endpoint, new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); throw new Exception(); }), TimeSpan.FromMilliseconds(20));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.PairAsync(Code, Binding.ProfileId));
        Assert.Equal(CoordinatorIntakeFailure.Timeout, error.Failure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PairAsync(Code, Binding.ProfileId, cancelled.Token));
    }

    [Fact]
    public async Task DeletedCredentialAndResetNeverDisableRepair()
    {
        CoordinatorPairing? saved = new(Binding, Guid.NewGuid(), Secret);
        var url = Endpoint.AbsoluteUri;
        using var model = new DirectorConnection(() => Binding.ProfileId, () => true, () => url, value => url = value,
            (_, _) => saved, (_, pairing) => saved = pairing, (_, _) => saved = null,
            (_, _, _, _, _) => Task.FromResult(new CoordinatorPairing(Binding, Guid.NewGuid(), Secret)));
        model.Reload();
        Assert.StartsWith("Paired", model.PairingStatus);
        saved = null; // Manual deletion outside the plugin.
        model.ResetPairingCommand.Execute(null);
        Assert.Equal("Not paired", model.PairingStatus);
        model.PairingCode = Code;
        Assert.True(model.PairCommand.CanExecute(null));
        model.PairCommand.Execute(null);
        await WaitFor(() => !model.IsBusy);
        Assert.NotNull(saved);
        Assert.Equal("", model.PairingCode);
        Assert.StartsWith("Paired", model.PairingStatus);
    }

    [Fact]
    public async Task ProfileSwitchDiscardsInFlightPairingAndClearsCode()
    {
        var profile = Binding.ProfileId;
        var response = new TaskCompletionSource<CoordinatorPairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        using var model = new DirectorConnection(() => profile, () => true, () => Endpoint.AbsoluteUri, _ => { },
            (_, _) => null, (_, _) => writes++, (_, _) => { }, (_, _, _, _, _) => response.Task);
        model.PairingCode = Code;
        model.PairCommand.Execute(null);
        Assert.False(model.IsEditable);
        profile = Guid.NewGuid();
        model.ProfileChanged();
        response.SetResult(new(Binding, Guid.NewGuid(), Secret));
        await WaitFor(() => !model.IsBusy);
        Assert.Equal(0, writes);
        Assert.Equal("Not paired", model.PairingStatus);
        Assert.Equal("", model.PairingCode);
    }

    [Fact]
    public void RunningRuntimeDisablesChangesAndRemoteHttpRequiresExplicitOptIn()
    {
        var idle = true;
        var url = "192.168.1.50:8080";
        using var model = new DirectorConnection(() => Binding.ProfileId, () => idle, () => url, value => url = value,
            (_, _) => null, (_, _) => { }, (_, _) => { });
        model.PairingCode = Code;
        Assert.False(model.PairCommand.CanExecute(null));
        model.AllowInsecureHttp = true;
        Assert.True(model.PairCommand.CanExecute(null));
        idle = false;
        Assert.False(model.PairCommand.CanExecute(null));
        model.ServerUrl = "https://other.example";
        Assert.Equal("192.168.1.50:8080", url);
    }

    [Fact]
    public void HttpConsentPersistsForExactOriginAcrossRestartAndProfileReturn()
    {
        var consent = "";
        var url = "192.168.1.50:8080";
        DirectorConnection Model() => new(() => Binding.ProfileId, () => true, () => url, value => url = value,
            (_, _) => new(Binding, Guid.NewGuid(), Secret), (_, _) => { }, (_, _) => { },
            readHttpConsent: () => consent, writeHttpConsent: value => consent = value);
        using (var first = Model()) first.AllowInsecureHttp = true;
        using var second = Model();
        second.Reload();
        Assert.True(second.AllowInsecureHttp);
        Assert.StartsWith("Paired", second.PairingStatus);
        second.ProfileChanged();
        Assert.True(second.AllowInsecureHttp);
        second.ServerUrl = "192.168.1.51:8080";
        Assert.False(second.AllowInsecureHttp);
        Assert.Null(second.ReadPairing());
        second.ServerUrl = url = "192.168.1.50:8080";
        Assert.True(second.AllowInsecureHttp);
    }

    [Fact]
    public void ActualWindowsVaultSurvivesManualDeletionAndRecreation()
    {
        var profile = Guid.NewGuid();
        var endpoint = new Uri("https://director-vault-test.invalid/" + Guid.NewGuid().ToString("N") + "/");
        var pairing = new CoordinatorPairing(Binding with { ProfileId = profile }, Guid.NewGuid(), Secret);
        try
        {
            DirectorCredentialStore.Store(endpoint, pairing);
            Assert.Equal(pairing.Binding, DirectorCredentialStore.Read(endpoint, profile)!.Binding);
            DirectorCredentialStore.Forget(endpoint, profile);
            Assert.Null(DirectorCredentialStore.Read(endpoint, profile));
            DirectorCredentialStore.Forget(endpoint, profile); // Reset is idempotent after external deletion.
            DirectorCredentialStore.Store(endpoint, pairing);
            Assert.Equal(Secret, DirectorCredentialStore.Read(endpoint, profile)!.Token);
            Assert.Null(DirectorCredentialStore.Read(endpoint, Guid.NewGuid()));
        }
        finally { DirectorCredentialStore.Forget(endpoint, profile); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
