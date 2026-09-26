using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class PlannerTests
{
    internal static PlannerRequest Request() => new(
        new("assignment", 9007199254740993UL, "rig-test", "config", 1000, 100000,
            [new("goal", 1, 1, 0, 0, 2, 1000, 500, [new(1000, 100000)])]),
        new("rig-test", "config", 2000, 90000, PlannerSafety.Safe, true, false, new(0, 0)));

    private static JsonObject Response(JsonElement request) => new()
    {
        ["contract_version"] = RuntimeContract.ContractVersion,
        ["engine_version"] = RuntimeContract.EngineVersion,
        ["assignment_id"] = request.GetProperty("assignment").GetProperty("id").GetString(),
        ["assignment_revision"] = request.GetProperty("assignment").GetProperty("revision").GetUInt64(),
        ["status"] = "ok",
        ["decision"] = new JsonObject { ["action"] = "acquire", ["goal_id"] = "goal", ["reason"] = "highest_priority_feasible_goal" }
    };

    [Fact]
    public async Task TypedEvaluationPreservesLargeRevisionAndSharesRequestOrderWithHeartbeat()
    {
        var request = Request();
        await using var peer = await TestPeer.CreateAsync("none", input =>
        {
            Assert.Equal(request.Assignment.Revision, input.GetProperty("assignment").GetProperty("revision").GetUInt64());
            Assert.Equal("safe", input.GetProperty("state").GetProperty("safety").GetString());
            return Response(input);
        });
        await peer.Session.PingAsync(default);
        var result = await peer.Session.EvaluateAsync(request, default);
        Assert.Same(request, result.Snapshot);
        Assert.Equal(new(PlannerAction.Acquire, "highest_priority_feasible_goal", "goal"), result.Decision);
        Assert.Null(result.Error);
        await peer.Session.PingAsync(default);
        await peer.Session.ShutdownAsync(default);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("assignment")]
    [InlineData("engine")]
    [InlineData("contract")]
    [InlineData("action")]
    [InlineData("goal")]
    [InlineData("reason")]
    [InlineData("extra")]
    [InlineData("numeric-action")]
    [InlineData("invalid-error")]
    [InlineData("error-with-decision")]
    public async Task MalformedOrUncorrelatedDecisionInvalidatesSession(string fault)
    {
        await using var peer = await TestPeer.CreateAsync("none", input =>
        {
            var value = Response(input);
            switch (fault)
            {
                case "revision": value["assignment_revision"] = 1; break;
                case "assignment": value["assignment_id"] = "old"; break;
                case "engine": value["engine_version"] = "old"; break;
                case "contract": value["contract_version"] = 1; break;
                case "action": value["decision"]!["action"] = "slew"; break;
                case "goal": value["decision"]!["goal_id"] = "other-goal"; break;
                case "reason": value["decision"]!["reason"] = null; break;
                case "extra": value["decision"]!["retry"] = true; break;
                case "numeric-action": value["decision"]!["action"] = 0; break;
                case "invalid-error": value.Remove("decision"); value["status"] = "error"; value["code"] = "new-error"; break;
                case "error-with-decision": value["status"] = "error"; value["code"] = "invalid_goal"; break;
            }
            return value;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.Session.EvaluateAsync(Request(), default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public void DuplicateNestedDecisionFieldsAreRejected()
    {
        var request = Request();
        var response = Response(JsonSerializer.SerializeToElement(new { assignment = new { id = "assignment", revision = request.Assignment.Revision } }));
        var json = response.ToJsonString().Replace("\"action\":\"acquire\"", "\"action\":\"stop\",\"action\":\"acquire\"", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => PlannerContract.Decode(document.RootElement, request));
    }

    [Fact]
    public async Task BadCallerInputAndPreCancelledCallsDoNotPoisonSession()
    {
        await using var peer = await TestPeer.CreateAsync("none", Response);
        var request = Request();
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.EvaluateAsync(request with
        { Assignment = request.Assignment with { RigId = "wrong-rig" } }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Session.EvaluateAsync(request with
        { Assignment = request.Assignment with { Id = new string('x', PlannerContract.MaxRequestBytes) } }, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.Session.EvaluateAsync(request, cancelled.Token));
        Assert.True(peer.Session.IsReady);
        await peer.Session.EvaluateAsync(request, default);
    }

    [Fact]
    public async Task CancellationAfterEvaluationDispatchInvalidatesSessionButQueuedCancellationDoesNot()
    {
        await using var peer = await TestPeer.CreateAsync("stall", Response);
        using var activeCancellation = new CancellationTokenSource();
        var active = peer.Session.EvaluateAsync(Request(), activeCancellation.Token);
        await peer.PingSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var queuedCancellation = new CancellationTokenSource();
        var queued = peer.Session.EvaluateAsync(Request(), queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(peer.Session.IsReady);
        activeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task RealRustCoreOwnsSelectionPendingCreditSafetyAndConstraints()
    {
        await using var controller = new RuntimeController(ProcessTests.BundleDirectory, TimeSpan.FromMilliseconds(20));
        var request = Request();
        await Assert.ThrowsAsync<IOException>(() => controller.EvaluateAsync(request));
        await controller.StartAsync("rig-test");
        var result = await controller.EvaluateAsync(request);
        Assert.Equal(PlannerAction.Acquire, result.Decision!.Action);
        Assert.Equal("goal", result.Decision.GoalId);
        var goal = request.Assignment.Goals[0];
        var pending = request with { Assignment = request.Assignment with { Goals = [goal with { Pending = 1 }] } };
        Assert.Equal(new(PlannerAction.Wait, "pending_assessment"), (await controller.EvaluateAsync(pending)).Decision);
        var accepted = request with { Assignment = request.Assignment with { Goals = [goal with { Accepted = 1 }] } };
        Assert.Equal(PlannerAction.Complete, (await controller.EvaluateAsync(accepted)).Decision!.Action);
        var unsafeRequest = request with { State = request.State with { Safety = PlannerSafety.Unsafe, AtBoundary = false } };
        Assert.Equal(PlannerAction.Stop, (await controller.EvaluateAsync(unsafeRequest)).Decision!.Action);
        Assert.Equal(PlannerAction.Continue, (await controller.EvaluateAsync(request with { State = request.State with { AtBoundary = false } })).Decision!.Action);
        Assert.Equal(PlannerAction.CheckIn, (await controller.EvaluateAsync(request with { State = request.State with { NowMs = 100000 } })).Decision!.Action);
        var missingTransits = request with { State = request.State with { MeridianExclusion = new(1000, 2000) } };
        var error = await controller.EvaluateAsync(missingTransits);
        Assert.Null(error.Decision);
        Assert.Equal(PlannerError.IncompleteTransitCoverage, error.Error);
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
        var covered = missingTransits with
        {
            Assignment = request.Assignment with
            { Goals = [goal with { Transits = new(new(0, 200000), [2000]) }] }
        };
        Assert.Equal(PlannerAction.Wait, (await controller.EvaluateAsync(covered)).Decision!.Action);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.EvaluateAsync(request with { Assignment = request.Assignment with { RigId = "other" } }));
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
        await controller.StopAsync();
        await Assert.ThrowsAsync<IOException>(() => controller.EvaluateAsync(request));
        await controller.StartAsync("rig-test");
        Assert.Equal(PlannerAction.Acquire, (await controller.EvaluateAsync(request)).Decision!.Action);
    }

    [Fact]
    public async Task ConcurrentEvaluationsAndHeartbeatRemainCorrelated()
    {
        await using var controller = new RuntimeController(ProcessTests.BundleDirectory, TimeSpan.FromMilliseconds(10));
        await controller.StartAsync("rig-test");
        var requests = Enumerable.Range(1, 40).Select(i => Request() with
        { Assignment = Request().Assignment with { Id = $"assignment-{i}", Revision = (ulong)i } }).ToArray();
        var results = await Task.WhenAll(requests.Select(request => controller.EvaluateAsync(request)));
        for (var i = 0; i < requests.Length; i++) Assert.Same(requests[i], results[i].Snapshot);
        Assert.Equal(RuntimeState.Ready, controller.Status.State);
    }

    [Fact]
    public async Task SlowPreparationChangesTheRustSelectedGoal()
    {
        await using var controller = new RuntimeController(ProcessTests.BundleDirectory);
        await controller.StartAsync("rig-test");
        var request = Request();
        var shortGoal = request.Assignment.Goals[0] with { Priority = 20, EligibleWindows = [new(1000, 6000)] };
        var longGoal = shortGoal with { Id = "later", Priority = 10, EligibleWindows = [new(1000, 100000)] };
        request = request with { Assignment = request.Assignment with { Goals = [shortGoal, longGoal] } };
        Assert.Equal("goal", (await controller.EvaluateAsync(request)).Decision!.GoalId);
        var afterPreparation = request with { State = request.State with { NowMs = 5001 } };
        Assert.Equal("later", (await controller.EvaluateAsync(afterPreparation)).Decision!.GoalId);
    }

    [Fact]
    public async Task InvalidCoreShapeHasNoDecisionAndDoesNotPoisonController()
    {
        await using var controller = new RuntimeController(ProcessTests.BundleDirectory);
        await controller.StartAsync("rig-test");
        var request = Request();
        var invalid = request with { Assignment = request.Assignment with { Goals = [null!] } };
        var result = await controller.EvaluateAsync(invalid);
        Assert.Equal(PlannerError.InvalidJson, result.Error);
        Assert.Null(result.Decision);
        Assert.Equal(PlannerAction.Acquire, (await controller.EvaluateAsync(request)).Decision!.Action);
    }

    [Fact]
    public async Task EvaluationTimeoutInvalidatesSession()
    {
        await using var peer = await TestPeer.CreateAsync("stall", Response);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.Session.EvaluateAsync(Request(), default));
        Assert.False(peer.Session.IsReady);
    }

    [Fact]
    public async Task DisposalCancelsEvaluationBeforeWaitingForItsGate()
    {
        await using var peer = await TestPeer.CreateAsync("stall", Response);
        var task = peer.Session.EvaluateAsync(Request(), default);
        await peer.PingSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await peer.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(peer.Session.IsReady);
    }
}
