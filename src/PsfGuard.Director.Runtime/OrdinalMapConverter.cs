using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Director.Runtime;

// Program fingerprints must not depend on randomized string hashes or locale.
internal sealed class OrdinalMapConverter<T> : JsonConverter<ImmutableDictionary<string, T>>
{
    public override ImmutableDictionary<string, T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, T>>(ref reader, options) ?? throw new JsonException();
        if (values.Values.Any(value => value is null)) throw new JsonException();
        return values.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public override void Write(Utf8JsonWriter writer, ImmutableDictionary<string, T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var entry in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(entry.Key);
            JsonSerializer.Serialize(writer, entry.Value, options);
        }
        writer.WriteEndObject();
    }
}
