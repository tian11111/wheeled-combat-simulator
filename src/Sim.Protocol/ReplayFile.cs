namespace Sim.Protocol;

/// <summary>
/// A recorded match in a self-contained, versioned JSON file: the scenario it
/// ran under, the accepted per-tick action/command stream (ReplayHeader), and
/// the outcome fingerprints that a replay must reproduce bit-for-bit.
/// Lives in the protocol layer so any consumer (CLI, Godot shell, tests) can
/// load CLI-recorded replays without depending on the CLI executable.
/// </summary>
public sealed record ReplayFile
{
    /// <summary>Replay container format version.</summary>
    public string Format { get; init; } = "sim-replay-v1";

    /// <summary>The fully specified match setup (field, vehicles, block layout).</summary>
    public Scenario Scenario { get; init; } = new();

    /// <summary>Accepted actions/commands by tick, produced by the core itself.</summary>
    public ReplayHeader Header { get; init; } = new();

    /// <summary>Total committed ticks in the original run.</summary>
    public long Ticks { get; init; }

    /// <summary>Final score of the original run.</summary>
    public Scores FinalScores { get; init; } = new();

    /// <summary>Terminal reason of the original run (null when unfinished).</summary>
    public string? DoneReason { get; init; }

    /// <summary>Fingerprints (seq|tick|kind|cls|msg) of every event in order.</summary>
    public List<string> EventFingerprints { get; init; } = [];
}