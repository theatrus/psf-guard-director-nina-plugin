using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsfGuard.Director.Runtime;

namespace PsfGuard.Director.Tests;

internal sealed class TestPeer : IAsyncDisposable
{
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
    private readonly string fault;
    private Task? worker;
    internal TaskCompletionSource PingSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal RuntimeSession Session { get; private set; } = null!;

    private TestPeer(NamedPipeServerStream server, string fault)
    {
        this.server = server;
        this.fault = fault;
    }

    internal static async Task<TestPeer> CreateAsync(string fault)
    {
        var name = "director-test-" + Guid.NewGuid().ToString("N");
        var peer = new TestPeer(new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly), fault);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            peer.worker = peer.ServeAsync();
            await client.ConnectAsync(peer.lifetime.Token);
            peer.Session = await RuntimeSession.ConnectTestStreamAsync(client, "rig-test", peer.lifetime.Token);
            return peer;
        }
        catch
        {
            client.Dispose();
            await peer.DisposeAsync();
            throw;
        }
    }

    private async Task ServeAsync()
    {
        await server.WaitForConnectionAsync(lifetime.Token);
        var hello = await PipeProtocol.ReadAsync(server, lifetime.Token);
        var ready = new JsonObject
        {
            ["type"] = "ready",
            ["runtime_version"] = RuntimeContract.RuntimeVersion,
            ["engine_version"] = fault == "handshake" ? "0.0.0" : RuntimeContract.EngineVersion,
            ["contract_version"] = RuntimeContract.ContractVersion,
            ["rig_id"] = "rig-test"
        };
        await PipeProtocol.WriteAsync(server, Reply(hello, ready), lifetime.Token);
        while (!lifetime.IsCancellationRequested)
        {
            var request = await PipeProtocol.ReadAsync(server, lifetime.Token);
            if (request.GetProperty("payload").GetProperty("type").GetString() == "shutdown")
            {
                await PipeProtocol.WriteAsync(server, Reply(request, new JsonObject { ["type"] = "stopped" }), lifetime.Token);
                return;
            }
            PingSeen.TrySetResult();
            if (fault == "stall") { await Task.Delay(Timeout.Infinite, lifetime.Token); return; }
            var reply = Reply(request, new JsonObject { ["type"] = "pong" });
            switch (fault)
            {
                case "session": reply["session_id"] = "old-session"; break;
                case "request": reply["request_id"] = 0; break;
                case "protocol": reply["protocol_version"] = 999; break;
                case "type": reply["payload"]!["type"] = "ready"; break;
                case "extra": reply["payload"]!["extra"] = true; break;
                case "length": await server.WriteAsync(new byte[] { 255, 255, 255, 255 }, lifetime.Token); continue;
                case "truncated": await server.WriteAsync(new byte[] { 10, 0, 0, 0, 123 }, lifetime.Token); server.Dispose(); return;
            }
            await PipeProtocol.WriteAsync(server, reply, lifetime.Token);
        }
    }

    private static JsonObject Reply(JsonElement message, JsonObject payload) => new()
    {
        ["protocol_version"] = RuntimeContract.ProtocolVersion,
        ["session_id"] = message.GetProperty("session_id").GetString(),
        ["request_id"] = message.GetProperty("request_id").GetUInt64(),
        ["payload"] = payload
    };

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (Session is not null) await Session.DisposeAsync();
        server.Dispose();
        if (worker is not null)
        {
            try { await worker.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
        lifetime.Dispose();
    }
}
