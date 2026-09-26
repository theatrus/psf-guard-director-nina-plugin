using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsfGuard.Director.Runtime;

internal static class PipeProtocol
{
    internal static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length is 0 or > RuntimeContract.MaxFrameBytes)
            throw new InvalidDataException("Invalid runtime request length.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > RuntimeContract.MaxFrameBytes)
            throw new InvalidDataException("Invalid runtime response length.");
        var body = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid runtime JSON.", exception);
        }
    }

    internal static JsonElement ValidateReply(JsonElement reply, string sessionId, ulong requestId, string type)
    {
        RequireFields(reply, "protocol_version", "session_id", "request_id", "payload");
        if (!reply.GetProperty("protocol_version").TryGetInt32(out var version) || version != RuntimeContract.ProtocolVersion ||
            reply.GetProperty("session_id").GetString() != sessionId ||
            !reply.GetProperty("request_id").TryGetUInt64(out var id) || id != requestId)
            throw new InvalidDataException("Runtime response belongs to a different session or request.");
        var payload = reply.GetProperty("payload");
        if (type == "ready")
            RequireFields(payload, "type", "runtime_version", "engine_version", "contract_version", "rig_id");
        else
            RequireFields(payload, "type");
        if (payload.GetProperty("type").GetString() != type)
            throw new InvalidDataException("Unexpected runtime response.");
        return payload;
    }

    internal static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Runtime response must be an object.");
        var remaining = fields.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name))
                throw new InvalidDataException("Runtime response contains duplicate or unknown fields.");
        if (remaining.Count != 0)
            throw new InvalidDataException("Runtime response is incomplete.");
    }
}
