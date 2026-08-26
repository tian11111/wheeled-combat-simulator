using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// Tolerant reader/writer for request ids. The legacy bridge compares ids with
/// string coercion (controllers may echo the id as either a JSON number or a
/// string), so both forms are accepted on read. Integral ids are written back
/// as JSON numbers to match the observation wire format.
/// </summary>
public sealed class RequestIdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var numeric) ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
            case JsonTokenType.String:
                return reader.GetString();
            default:
                throw new JsonException($"Request id must be a number or string, got {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            writer.WriteNumberValue(numeric);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
