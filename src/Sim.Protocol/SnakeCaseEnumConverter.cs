using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// Writes enum values as lower snake_case strings (<c>BlockOff</c> ↔ "block_off",
/// <c>IrGround</c> ↔ "ir_ground"). Reading accepts the exact snake_case form or the
/// plain member name, case-insensitively.
/// </summary>
public sealed class SnakeCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException($"Expected a non-empty string for enum {typeof(TEnum).Name}.");
        }

        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(ToSnakeCase(name), text, StringComparison.Ordinal))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new JsonException($"Unknown {typeof(TEnum).Name} value '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToSnakeCase(value.ToString()));

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
