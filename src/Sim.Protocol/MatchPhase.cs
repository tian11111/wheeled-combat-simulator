using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>Match phases as used by the referee (legacy strings "PREP"/"RUN"/"DONE").</summary>
[JsonConverter(typeof(MatchPhaseConverter))]
public enum MatchPhase
{
    Prep,
    Run,
    Done,
}

/// <summary>Serializes <see cref="MatchPhase"/> as the legacy uppercase referee strings.</summary>
public sealed class MatchPhaseConverter : JsonConverter<MatchPhase>
{
    public override MatchPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (string.Equals(text, "PREP", StringComparison.OrdinalIgnoreCase))
        {
            return MatchPhase.Prep;
        }
        if (string.Equals(text, "RUN", StringComparison.OrdinalIgnoreCase))
        {
            return MatchPhase.Run;
        }
        if (string.Equals(text, "DONE", StringComparison.OrdinalIgnoreCase))
        {
            return MatchPhase.Done;
        }
        throw new JsonException($"Unknown match phase '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, MatchPhase value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            MatchPhase.Prep => "PREP",
            MatchPhase.Run => "RUN",
            MatchPhase.Done => "DONE",
            _ => throw new JsonException($"Unknown match phase {value}."),
        });
}
