namespace PsfGuard.Director.Runtime;

public static class HashEncoding
{
    public static string Lower(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();
}
