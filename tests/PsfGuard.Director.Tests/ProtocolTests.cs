using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(266241u)]
    public async Task RejectsInvalidLengthBeforeReadingBody(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync(new MemoryStream(header), default));
    }

    [Theory]
    [InlineData("ff")]
    [InlineData("7b")]
    public async Task RejectsInvalidJson(string hex)
    {
        using var stream = Frame(Convert.FromHexString(hex));
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync(stream, default));
    }

    [Fact]
    public async Task RejectsTruncatedBody()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeProtocol.ReadAsync(new MemoryStream([10, 0, 0, 0, 123]), default));
    }

    [Theory]
    [InlineData("{\"type\":\"pong\",\"type\":\"pong\"}")]
    [InlineData("{\"type\":\"pong\",\"extra\":1}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void RejectsUnknownDuplicateAndMissingFields(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Throws<InvalidDataException>(() => PipeProtocol.RequireFields(document.RootElement, "type"));
    }

    internal static MemoryStream Frame(byte[] bytes)
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        stream.Write(header);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }
}
