using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using PsfGuard.Director.Plugin.Acquisition;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class FlatCoverageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "director-flat-test-" + Guid.NewGuid().ToString("N"));
    private static readonly CoordinatorBinding Binding = CoordinatorProgramTests.Binding;
    private static readonly Uri Endpoint = CoordinatorProgramTests.Endpoint;
    private static ExposureRecipe Recipe => new("a", 1000, "L", new(1, 1), 100, 30, 0, null);
    private DirectorFlatCoverage Store() => new(root, Endpoint, Binding);
    private string Image(string name) => Path.Combine(root, name + ".fits");
    [Fact]
    public void RestartRetainsPartialCountsAndDuplicateSaveDoesNotIncreaseCoverage()
    {
        var store = Store(); store.Saved("night-camera-L", 2, Recipe, "L", 15, Image("a"));
        store = Store();
        Assert.Equal(1, store.Count("night-camera-L", 2)); Assert.Empty(store.Records());
        store.Saved("night-camera-L", 2, Recipe, "L", 15, Image("a"));
        Assert.Equal(1, store.Count("night-camera-L", 2));
        store.Saved("night-camera-L", 2, Recipe, "L", 15, Image("b"));
        var record = Assert.Single(Store().Records());
        Assert.Equal(Binding.ProfileId.ToString("D"), record.ProfileId);
        Assert.Null(record.TargetGuid);
        Assert.Equal(0, store.Count("different-night", 2));
        Assert.Equal(0, store.Count("night-camera-L", 3));
    }
    [Fact]
    public void VoidingPersistsBeforeAckAndReplacementGetsANewIdentityWithoutDeletingFiles()
    {
        var store = Store(); File.WriteAllText(Image("raw"), "original flat");
        store.Saved("k", 1, Recipe, "L", null, Image("raw"));
        var first = Assert.Single(store.Records());
        var decision = new FlatCoverageDecision(Guid.NewGuid().ToString("D"), first.SourceRowId, first.Fingerprint, null, "dust moved");
        store.Apply([decision]); store = Store(); store.Apply([decision]);
        Assert.Equal(0, store.Count("k", 1));
        Assert.Equal("removed", Assert.Single(store.Pending()).Status);
        Assert.Equal("original flat", File.ReadAllText(Image("raw")));
        store.Acknowledged(store.Pending()); Assert.Empty(Store().Pending());
        store.Saved("k", 1, Recipe, "L", null, Image("new"));
        Assert.Equal(1, store.Count("k", 1));
        Assert.Equal(2, store.Records().Select(r => r.SourceRowId).Distinct().Count());
        store.Apply([decision], acknowledge: false);
        Assert.Equal(1, store.Count("k", 1)); Assert.Empty(store.Pending());
    }
    [Fact]
    public void ChangedFingerprintCannotVoidNewCoverage()
    {
        var store = Store(); store.Saved("k", 1, Recipe, "L", null, Image("a"));
        var record = Assert.Single(store.Records());
        store.Apply([new(Guid.NewGuid().ToString("D"), record.SourceRowId, new string('f', 64), null, "old")]);
        Assert.Equal(1, store.Count("k", 1)); Assert.Equal("conflict", Assert.Single(store.Pending()).Status);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task OlderServerExplainsRequiredUpdateWithoutAnEndlessRetry(HttpStatusCode status)
    {
        using var client = new CoordinatorCalibrationClient(Endpoint, Binding, Store(), _ => ValueTask.FromResult<string?>("psfdrc_test"),
            new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DirectorAcquisition.RefreshCalibrationPlanAsync(client, CoordinatorProgramTests.Configuration, default));
        Assert.Contains("Update PSF Guard", error.Message);
        Assert.False(DirectorAcquisition.CanAutomaticallyRecover(error));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientChecksIdentityAndReceiptsBeforeChangingCoverage(bool wrongProfile)
    {
        var store = Store(); store.Saved("k", 1, Recipe, "L", null, Image("a"));
        var record = Assert.Single(store.Records());
        var decision = new FlatCoverageDecision(Guid.NewGuid().ToString("D"), record.SourceRowId, record.Fingerprint, null, "bad batch");
        using var client = new CoordinatorCalibrationClient(Endpoint, Binding, store, _ => ValueTask.FromResult<string?>("psfdrc_test"), new Handler(async request =>
        {
            Assert.Equal(Binding.ProfileId.ToString("D"), request.Headers.GetValues("X-PSF-Director-Profile").Single());
            var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal(record.SourceRowId, sent["records"]![0]!["source_row_id"]!.GetValue<long>());
            var answer = new
            {
                success = true,
                data = new
                {
                    protocol_version = 1,
                    coordinator_instance_id = Binding.CoordinatorInstanceId,
                    catalog_id = Binding.CatalogId,
                    rig_id = Binding.RigId,
                    profile_id = wrongProfile ? Guid.NewGuid() : Binding.ProfileId,
                    origin_id = Binding.ProfileId,
                    configuration = CoordinatorProgramTests.Configuration,
                    recipes = new[] { Recipe },
                    received = 1,
                    acknowledged = 0,
                    decisions = new[] { decision },
                    retired = Array.Empty<FlatCoverageDecision>()
                }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer, CoordinatorProgramContract.Options), Encoding.UTF8, "application/json") };
        }));
        if (wrongProfile)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(CoordinatorProgramTests.Configuration, default));
            Assert.Equal(1, store.Count("k", 1)); Assert.Empty(store.Pending());
        }
        else
        {
            var plan = await client.RefreshAsync(CoordinatorProgramTests.Configuration, default);
            Assert.Single(plan.Recipes); Assert.Equal(0, store.Count("k", 1)); Assert.Single(store.Pending());
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
