using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class CoordinatorEquipmentTests
{
    private static readonly CoordinatorBinding Binding = CoordinatorProgramTests.Binding;
    private static readonly Guid ClientId = Guid.NewGuid(), ReportId = Guid.NewGuid();
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static DirectorConfiguration Configuration()
    {
        return CoordinatorProgramTests.Configuration;
    }
    private static JsonObject Ack() => new()
    {
        ["success"] = true,
        ["error"] = null,
        ["status"] = "ready",
        ["data"] = new JsonObject
        {
            ["coordinator_instance_id"] = Binding.CoordinatorInstanceId,
            ["catalog_id"] = Binding.CatalogId,
            ["rig_id"] = Binding.RigId,
            ["profile_id"] = Binding.ProfileId,
            ["client_id"] = ClientId,
            ["report_id"] = ReportId,
            ["received_at_ms"] = 1010,
            ["accepted_revision"] = null
        }
    };
    private static HttpResponseMessage Response(JsonObject ack) => new(HttpStatusCode.OK)
    { Content = new StringContent(ack.ToJsonString(), Encoding.UTF8, "application/json") };
    private static CoordinatorEquipmentClient Client(Handler handler) => new(CoordinatorProgramTests.Endpoint, _ => ValueTask.FromResult<string?>("psfdrc_test"), handler);
    private static Task<CoordinatorEquipmentAcknowledgement> Send(CoordinatorEquipmentClient client) => client.ReportAsync(Binding, ClientId, ReportId,
        Configuration(), ImmutableDictionary<string, string>.Empty.Add("filter-0", "H-alpha"), 1000);

    [Fact]
    public async Task ReportsExactNativeEvidenceWithoutReceivingAcquisitionAuthority()
    {
        using var client = Client(new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/prefix/api/director/v1/rigs/{Binding.RigId:D}/equipment-reports", request.RequestUri!.AbsolutePath);
            Assert.Equal(Binding.ProfileId.ToString("D"), request.Headers.GetValues("X-PSF-Director-Profile").Single());
            var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(ReportId.ToString("D"), input["report_id"]!.GetValue<string>());
            Assert.Equal("H-alpha", input["filter_names"]!["filter-0"]!.GetValue<string>());
            Assert.Null(input["token"]);
            return Response(Ack());
        }));
        Assert.Null((await Send(client)).AcceptedRevision);
    }

    [Theory]
    [InlineData("coordinator_instance_id")]
    [InlineData("catalog_id")]
    [InlineData("rig_id")]
    [InlineData("profile_id")]
    [InlineData("client_id")]
    [InlineData("report_id")]
    [InlineData("unknown")]
    [InlineData("received_at_ms")]
    [InlineData("accepted_revision")]
    public async Task WrongOrMalformedAcknowledgementsFailClosed(string field)
    {
        var ack = Ack();
        ack["data"]![field] = field is "received_at_ms" or "accepted_revision" ? JsonValue.Create(0) : JsonValue.Create(Guid.NewGuid());
        using var client = Client(new Handler((_, _) => Task.FromResult(Response(ack))));
        Assert.Equal(CoordinatorIntakeFailure.InvalidAcknowledgement, (await Assert.ThrowsAsync<CoordinatorIntakeException>(() => Send(client))).Failure);
    }
}
