using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// Maps a legacy logical sensor name to the real profile channel(s) that back it.
///
/// CONTRACT.md / SIMULATOR.md legacy shapes:
/// <code>
/// "gF": "gray_front"                                   // single channel
/// "r":  null                                           // unconfigured → compatibility stub only
/// "f":  { "channels": ["a","b"], "reducer": "max", "virtual": true }
/// </code>
/// </summary>
[JsonConverter(typeof(LogicalSensorMapConverter))]
public sealed record LogicalSensorMap
{
    /// <summary>Single backing raw channel id (serialized as a plain string).</summary>
    public string? Channel { get; init; }

    /// <summary>Multiple backing raw channel ids for derived signals.</summary>
    public IReadOnlyList<string>? Channels { get; init; }

    /// <summary>Reducer applied to <see cref="Channels"/>, e.g. "max".</summary>
    public string? Reducer { get; init; }

    /// <summary>True when the logical signal has no dedicated physical channel.</summary>
    public bool Virtual { get; init; }

    /// <summary>True when the logical signal is explicitly unmapped (JSON null).</summary>
    public bool IsNull { get; init; }

    /// <summary>A logical signal that is explicitly not configured (legacy "r": null).</summary>
    public static LogicalSensorMap Unmapped { get; } = new() { IsNull = true };

    /// <summary>Creates a single-channel mapping.</summary>
    public static LogicalSensorMap FromChannel(string channel) => new() { Channel = channel };
}

/// <summary>Reads/writes <see cref="LogicalSensorMap"/> in the legacy JSON shape.</summary>
public sealed class LogicalSensorMapConverter : JsonConverter<LogicalSensorMap>
{
    /// <summary>JSON null is meaningful here ("r": null = explicitly unmapped).</summary>
    public override bool HandleNull => true;

    public override LogicalSensorMap? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return LogicalSensorMap.Unmapped;

            case JsonTokenType.String:
                return LogicalSensorMap.FromChannel(reader.GetString()!);

            case JsonTokenType.StartObject:
            {
                var map = new LogicalSensorMap();
                string? channel = null;
                IReadOnlyList<string>? channels = null;
                string? reducer = null;
                var isVirtual = false;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new JsonException("Unexpected token in logical sensor map.");
                    }

                    var property = reader.GetString();
                    reader.Read();
                    switch (property)
                    {
                        case "channel":
                            channel = reader.GetString();
                            break;
                        case "channels":
                            channels = ReadStringArray(ref reader);
                            break;
                        case "reducer":
                            reducer = reader.GetString();
                            break;
                        case "virtual":
                            isVirtual = reader.GetBoolean();
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }

                return new LogicalSensorMap
                {
                    Channel = channel,
                    Channels = channels,
                    Reducer = reducer,
                    Virtual = isVirtual,
                };
            }

            default:
                throw new JsonException("Logical sensor map must be a string, null or object.");
        }
    }

    public override void Write(Utf8JsonWriter writer, LogicalSensorMap value, JsonSerializerOptions options)
    {
        if (value is null || value.IsNull)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Channel is not null)
        {
            writer.WriteStringValue(value.Channel);
            return;
        }

        writer.WriteStartObject();
        if (value.Channels is not null)
        {
            writer.WritePropertyName("channels");
            writer.WriteStartArray();
            foreach (var channel in value.Channels)
            {
                writer.WriteStringValue(channel);
            }
            writer.WriteEndArray();
        }
        if (value.Reducer is not null)
        {
            writer.WriteString("reducer", value.Reducer);
        }
        if (value.Virtual)
        {
            writer.WriteBoolean("virtual", true);
        }
        writer.WriteEndObject();
    }

    private static IReadOnlyList<string> ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of channel ids.");
        }

        var result = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return result;
            }
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Channel ids must be strings.");
            }
            result.Add(reader.GetString()!);
        }
        throw new JsonException("Unterminated channel array.");
    }
}
