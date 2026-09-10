using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finare.Core;

public static class UnifiedJson
{
    public static JsonSerializerOptions CreateOptions(bool writeIndented = true) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = writeIndented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new SemanticIdJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };
}

public sealed class SemanticIdJsonConverter : JsonConverter<SemanticId>
{
    public override SemanticId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        if (!SemanticId.TryParse(value, out SemanticId id))
        {
            throw new JsonException($"Invalid semantic ID '{value}'.");
        }

        return id;
    }

    public override void Write(Utf8JsonWriter writer, SemanticId value, JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new JsonException("A default semantic ID cannot be serialized.");
        }

        writer.WriteStringValue(value.Value);
    }
}
