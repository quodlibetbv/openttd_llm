using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenTtd.ModelArena.Contracts;

/// <summary>
/// Produces the small, deterministic JSON representation used for persisted
/// observations and replay hashes. Object keys are ordinally sorted while
/// array order remains meaningful and is therefore preserved.
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonElement value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        });

        WriteValue(writer, value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static string SerializeToString(JsonElement value) =>
        Encoding.UTF8.GetString(Serialize(value));

    public static string ComputeSha256(JsonElement value) =>
        Convert.ToHexString(SHA256.HashData(Serialize(value))).ToLowerInvariant();

    public static string ComputeSha256(ReadOnlySpan<byte> utf8Json) =>
        Convert.ToHexString(SHA256.HashData(utf8Json)).ToLowerInvariant();

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new JsonException("The JSON value kind is not supported by canonical serialization.");
        }
    }
}
