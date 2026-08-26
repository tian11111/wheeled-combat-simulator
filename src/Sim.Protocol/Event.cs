using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>Structured domain event kinds (JSON: lower snake_case, e.g. "block_off").</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<EventKind>))]
public enum EventKind
{
    /// <summary>Match armed (WAIT_START released).</summary>
    Arm,

    /// <summary>Robot mounted the platform.</summary>
    Mount,

    /// <summary>Robot fell off the platform.</summary>
    Drop,

    /// <summary>Fallen robot recovered (back on / re-mounting).</summary>
    Recover,

    /// <summary>Robot-robot contact resolved.</summary>
    Contact,

    /// <summary>Energy block pushed off the platform (out for the rest of the match).</summary>
    BlockOff,

    /// <summary>Energy block scoring decision (last-toucher attribution).</summary>
    BlockScore,

    /// <summary>Referee penalty applied.</summary>
    Penalty,

    /// <summary>Restart penalty (debug +3 / restart +4, points to the opponent).</summary>
    RestartPenalty,

    /// <summary>Both robots dropped in the same frame (no points awarded).</summary>
    SimultaneousDrop,

    /// <summary>Inactivity: both robots stationary beyond the limit (消极比赛 +1).</summary>
    Inactivity,

    /// <summary>Match time exhausted.</summary>
    Timeout,

    /// <summary>Match reached a terminal state.</summary>
    End,

    /// <summary>Referee paused the match.</summary>
    Pause,

    /// <summary>Referee resumed the match.</summary>
    Resume,

    /// <summary>Match restarted.</summary>
    Restart,

    /// <summary>FSM state/action transition (built-in controller; legacy log lines like "[fsm] SEARCH: …").</summary>
    Fsm,

    /// <summary>Stage-clock score tick: the only robot on stage earns +1 per 10 s (登台/掉台读秒).</summary>
    ScoreClock,
}

/// <summary>
/// A structured domain event. Events carry a monotonically increasing
/// <see cref="Seq"/> (1-based, matching the legacy log sequence used for
/// incremental de-duplication when only the last 500 entries are kept).
/// </summary>
public sealed record Event : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Monotonic 1-based sequence number across the whole match.</summary>
    public long Seq { get; init; }

    /// <summary>Tick index at which the event was committed.</summary>
    public long Tick { get; init; }

    /// <summary>Simulation time in seconds.</summary>
    public double T { get; init; }

    /// <summary>Structured event kind.</summary>
    public EventKind Type { get; init; }

    /// <summary>Affected role ("us"/"them"), null for neutral events.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// Legacy log class compatibility ("us"/"them") — the old log entries carried
    /// a cls field for coloring; kept so old trace tooling keeps working.
    /// </summary>
    public string? Cls { get; init; }

    /// <summary>Human-readable message (legacy log text semantics).</summary>
    public string? Msg { get; init; }

    /// <summary>Kind-specific structured payload (e.g. drop height, block id, penalty reason).</summary>
    public Dictionary<string, JsonElement>? Data { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "event: protocolVersion must not be empty.";
        }
        if (Seq < 1)
        {
            yield return $"event: seq must be >= 1, got {Seq}.";
        }
        if (Tick < 0)
        {
            yield return "event: tick must be >= 0.";
        }
        if (!(T >= 0) || !double.IsFinite(T))
        {
            yield return "event: t must be a non-negative finite number.";
        }
        if (!Enum.IsDefined(Type))
        {
            yield return $"event: unknown event type {Type}.";
        }
        if (Role is not null && !RoleNames.IsKnownRole(Role))
        {
            yield return $"event: role must be 'us', 'them' or null, got '{Role}'.";
        }
        if (Cls is not null && !IsKnownLegacyClass(Cls))
        {
            yield return $"event: cls must be a legacy log class ('us', 'them', 'score', 'warn', 'sim') or null, got '{Cls}'.";
        }
    }

    /// <summary>Legacy log-line coloring classes (CONTRACT.md trace format).</summary>
    public static bool IsKnownLegacyClass(string? cls)
        => cls is null or "us" or "them" or "score" or "warn" or "sim";
}
