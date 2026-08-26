using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// Stable, versioned JSON serialization for all protocol messages.
///
/// The options intentionally mirror the legacy wire format:
/// <list type="bullet">
///   <item>camelCase property naming (matching the old protocol field names),</item>
///   <item>null members are omitted,</item>
///   <item>numbers may be read from strings for tolerance (request ids etc.),</item>
///   <item>enums serialize as legacy strings (snake_case sensor types, uppercase
///   match phases, snake_case event kinds).</item>
/// </list>
/// These options must never change shape for existing fields; wire-format
/// evolution goes through new <see cref="ProtocolVersion"/> values.
/// </summary>
public static class ProtocolJson
{
    static ProtocolJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            IncludeFields = false,
            WriteIndented = false,
        };

        // Enum converters are attached via [JsonConverter] attributes on the enum
        // declarations so each enum keeps its own legacy spelling.

        Options = options;
    }

    /// <summary>The canonical serializer options for all protocol messages.</summary>
    public static JsonSerializerOptions Options { get; }

    /// <summary>Serializes a message to its canonical compact JSON form.</summary>
    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, Options);
    }

    /// <summary>
    /// Deserializes a message from JSON. Malformed JSON, wrong token types and
    /// unknown enum values throw <see cref="JsonException"/>.
    /// </summary>
    public static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("JSON payload must not be empty.");
        }
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException("JSON payload deserialized to null.");
    }

    /// <summary>Attempts to deserialize a message; returns false on any JSON error.</summary>
    public static bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            value = Deserialize<T>(json);
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Round-trip check helper: serializes <paramref name="value"/>, deserializes
    /// it back and re-serializes. Returns the re-serialized JSON, which callers
    /// can compare with the first serialization to verify lossless round-trips.
    /// </summary>
    public static string RoundTripJson<T>(T value)
        => Serialize(Deserialize<T>(Serialize(value)));

    /// <summary>
    /// Parses one stdout line from an external controller, applying the legacy
    /// bridge acceptance rules (CONTRACT.md section 2): the line must be a JSON
    /// object containing finite numeric <c>v</c> and <c>w</c>; an optional
    /// <c>requestId</c> (number or string) is echoed verbatim; any other members
    /// are ignored. Lines that do not qualify are rejected with an error message —
    /// the caller treats them as a zero action, never as a partial action.
    /// </summary>
    public static bool TryParseActionLine(string? line, out RobotAction? action, out string? error)
    {
        action = null;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "empty line.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "action line must be a JSON object.";
                return false;
            }

            if (!TryReadFiniteNumber(document.RootElement, "v", out var v))
            {
                error = "action line must contain a finite numeric 'v'.";
                return false;
            }
            if (!TryReadFiniteNumber(document.RootElement, "w", out var w))
            {
                error = "action line must contain a finite numeric 'w'.";
                return false;
            }

            string? requestId = null;
            if (document.RootElement.TryGetProperty("requestId", out var requestIdElement))
            {
                requestId = requestIdElement.ValueKind switch
                {
                    JsonValueKind.Number => requestIdElement.GetRawText(),
                    JsonValueKind.String => requestIdElement.GetString(),
                    JsonValueKind.Null => null,
                    _ => throw new JsonException("requestId must be a number or string."),
                };
            }

            action = new RobotAction { V = v, W = w, RequestId = requestId };
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryReadFiniteNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var element))
        {
            return false;
        }
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out value) || !double.IsFinite(value))
        {
            return false;
        }
        return true;
    }
}
