using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class CollaborationAuthTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static CollaborationAuthClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new Uri("https://collab.example/community/"), false, () => new Handler(send), TimeSpan.FromSeconds(2));

    [Fact]
    public async Task BrowserAuthAcceptsFloatingExpiryAndUsesPersonTokenOnlyToEnroll()
    {
        using var client = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login")) { Assert.Null(request.Headers.Authorization); return Task.FromResult(Json("""{"code":"one-time","url":"https://collab.example/approve","expiresIn":600.0}""")); }
            if (path.EndsWith("/auth/poll")) { Assert.Null(request.Headers.Authorization); Assert.Equal("?code=one-time", request.RequestUri.Query); return Task.FromResult(Json("""{"state":"done","token":"person-secret"}""")); }
            Assert.EndsWith("/agents", path); Assert.Equal("Bearer person-secret", request.Headers.Authorization!.ToString());
            return Task.FromResult(Json("""{"agent":{"id":"000000000001"},"token":"agent-secret"}"""));
        });
        var login = await client.LoginAsync(default);
        Assert.Equal(TimeSpan.FromMinutes(10), login.Lifetime);
        Assert.DoesNotContain("one-time", login.ToString());
        var approval = await client.PollAsync(login.Code, default);
        Assert.DoesNotContain("person-secret", approval.ToString());
        var credential = await client.EnrollAsync(approval.Token!, "Rig", default);
        Assert.Equal("000000000001", credential.AgentId);
        Assert.DoesNotContain("agent-secret", credential.ToString());
    }
    [Theory]
    [InlineData("http://remote.example/", true)]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("https://user:pass@remote.example/", false)]
    public void RefusesUnsafeEndpoint(string endpoint, bool consent) =>
        Assert.Throws<ArgumentException>(() => CollaborationAuthClient.Normalize(new(endpoint), consent));
    [Theory]
    [InlineData("""{"agent":{"id":"000000000001"},"token":"NEVER_LEAK_CREDENTIAL","token":"other"}""")]
    [InlineData("""{"agent":{"id":"../foreign"},"token":"NEVER_LEAK_CREDENTIAL"}""")]
    [InlineData("""{"agent":null,"token":"NEVER_LEAK_CREDENTIAL"}""")]
    [InlineData("""{"agent":{"id":42},"token":"NEVER_LEAK_CREDENTIAL"}""")]
    public async Task EnrollmentRejectsAmbiguousAndForeignIdentitiesWithoutEcho(string response)
    {
        using var client = Client(_ => Task.FromResult(Json(response)));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.PairAsync("code", "Rig", default));
        Assert.DoesNotContain("NEVER_LEAK_CREDENTIAL", error.ToString());
    }
    [Theory]
    [InlineData("null")]
    [InlineData("""{"ok":true,"protocol":"wrong","time":1,"version":"test"}""")]
    [InlineData("""{"ok":true,"protocol":1,"time":"wrong","version":"test"}""")]
    [InlineData("""{"ok":true,"protocol":1,"time":1,"version":"test","features":[42]}""")]
    public async Task MalformedDiscoveryHasTypedRedactedFailure(string response)
    {
        using var client = Client(_ => Task.FromResult(Json(response)));
        var error = await Assert.ThrowsAsync<CoordinatorIntakeException>(() => client.DiscoverAsync(default));
        Assert.Equal(CoordinatorIntakeFailure.MalformedResponse, error.Failure);
    }
    [Fact]
    public async Task MissingCredentialRetainsIdentityAndExplicitNewConnectionCanPair()
    {
        var profile = Guid.NewGuid(); var settings = ""; CollaborationCredential? saved = null; var enrollment = 0;
        using var model = new CollaborationConnection(() => profile, () => true, () => settings, value => settings = value,
            (_, _, _) => saved, (_, _, _, value) => saved = value, (_, _, _) => saved = null,
            (_, _) => Client(_ => Task.FromResult(++enrollment % 2 == 1
                ? Json("""{"ok":true,"protocol":1,"version":"test","time":1791171023.0,"features":["pairing"]}""")
                : Json("""{"agent":{"id":"000000000001"},"token":"agent-secret"}"""))));
        model.Reload(); model.ServerUrl = "https://collab.example/community/"; model.PairingCode = "PAIR";
        model.PairCommand.Execute(null);
        await Until(() => !model.IsBusy && model.Status.StartsWith("Registered"));
        Assert.Equal("", model.PairingCode);
        Assert.DoesNotContain("agent-secret", settings);
        saved = null; model.Reload();
        Assert.Contains("Credential missing", model.Status);
        Assert.False(model.PairCommand.CanExecute(null));
        model.NewConnectionCommand.Execute(null);
        model.ServerUrl = "https://collab.example/community/"; model.PairingCode = "NEW";
        Assert.True(model.PairCommand.CanExecute(null));
        using var history = JsonDocument.Parse(settings);
        Assert.Equal("000000000001", history.RootElement.GetProperty("History")[0].GetProperty("Agent").GetString());
    }
    [Fact]
    public async Task UncertainEnrollmentNeverAutomaticallyRetriesOrUnlocksPairing()
    {
        var settings = ""; var pairs = 0;
        using var model = new CollaborationConnection(() => Guid.Empty, () => true, () => settings, v => settings = v,
            (_, _, _) => null, (_, _, _, _) => { }, (_, _, _) => { },
            (_, _) => Client(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("health")) return Task.FromResult(Json("""{"ok":true,"protocol":1,"version":"test","time":1791171023.0,"features":["pairing"]}"""));
                pairs++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }));
        model.Reload(); model.ServerUrl = "https://collab.example/"; model.PairingCode = "PAIR"; model.PairCommand.Execute(null);
        await Until(() => !model.IsBusy);
        Assert.Equal(1, pairs);
        Assert.False(model.PairCommand.CanExecute(null));
        model.Reload(); Assert.Contains("outcome unknown", model.Status);
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}
