// Godot-free cross-end parity verifier. Re-runs a recorded ReplayFile through
// the authoritative core and compares final score, done reason, final tick and
// event fingerprints, mirroring Sim.Cli replay-check semantics exactly.
// No Godot namespace so Sim.Tests and the Godot headless runner share one code
// path, keeping the acceptance evidence honest.

using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>Outcome of a parity verification run.</summary>
public sealed record ParityReport
{
    public required bool Pass { get; init; }
    public long Ticks { get; init; }
    public Scores Scores { get; init; } = new();
    public string? DoneReason { get; init; }
    public long EventCount { get; init; }
    public string? FirstDivergence { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Verifies a recorded replay against a fresh core run: identical final score,
/// done reason, final tick, and event fingerprints. Rule logic stays in the
/// core; this only replays inputs and compares outputs.
/// </summary>
public static class ParityCheck
{
    /// <summary>Verifies the replay file; never throws for a mismatched match.</summary>
    public static ParityReport Verify(ReplayFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var errors = file.Header.Validate().ToList();
        if (errors.Count > 0)
        {
            return new ParityReport
            {
                Pass = false,
                Error = $"invalid replay header: {string.Join(" ", errors)}",
            };
        }

        var engine = new MatchEngine(file.Scenario);
        var actionsByTick = file.Header.Ticks.ToDictionary(t => t.Tick, t => t.Actions);
        var commandsByTick = file.Header.Ticks
            .Where(t => t.Commands is { Count: > 0 })
            .ToDictionary(t => t.Tick, t => t.Commands!);

        var fingerprints = new List<string>();
        engine.Arm();
        var lastTick = Math.Max(file.Ticks, file.Header.Ticks.Count > 0 ? file.Header.Ticks[^1].Tick : 0);
        for (var tick = 1; tick <= lastTick && !engine.Done; tick++)
        {
            if (commandsByTick.TryGetValue(tick, out var commands))
            {
                ApplyCommands(engine, commands);
            }
            actionsByTick.TryGetValue(tick, out var actions);
            var snapshot = engine.Tick(
                actions?.GetValueOrDefault(RoleNames.Us),
                actions?.GetValueOrDefault(RoleNames.Them));
            if (snapshot.Events is { Count: > 0 })
            {
                foreach (var evt in snapshot.Events)
                {
                    fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                }
            }
        }

        var scoreOk = engine.Scores.Us == file.FinalScores.Us
            && engine.Scores.Them == file.FinalScores.Them;
        var eventsOk = fingerprints.SequenceEqual(file.EventFingerprints);
        var doneOk = (engine.Done ? engine.Us.Fsm.DoneReason.Length > 0 ? engine.Us.Fsm.DoneReason : engine.Them.Fsm.DoneReason : null)
            == file.DoneReason;
        var tickCountOk = engine.TickIndex == file.Ticks;

        var firstDiff = eventDivergence(fingerprints, file.EventFingerprints);
        var pass = scoreOk && eventsOk && doneOk && tickCountOk;

        return new ParityReport
        {
            Pass = pass,
            Ticks = engine.TickIndex,
            Scores = engine.Scores,
            DoneReason = engine.Done ? (engine.Us.Fsm.DoneReason.Length > 0 ? engine.Us.Fsm.DoneReason : engine.Them.Fsm.DoneReason) : null,
            EventCount = fingerprints.Count,
            FirstDivergence = firstDiff,
        };
    }

    /// <summary>
    /// Applies recorded core commands with exactly the same semantics as
    /// Sim.Cli replay-check: legacy <c>restart:&lt;role&gt;:&lt;kind&gt;</c>
    /// stays penalty-only, additive <c>restart_robot:&lt;role&gt;</c> performs
    /// the real restart, and unknown commands are ignored so a future command
    /// kind never breaks old replay verification.
    /// </summary>
    private static void ApplyCommands(MatchEngine engine, List<string> commands)
    {
        foreach (var command in commands)
        {
            var parts = command.Split(':', 3);
            if (parts.Length == 3 && parts[0] == "restart")
            {
                engine.RestartPenalty(parts[1], parts[2]);
            }
            else if (parts.Length == 2 && parts[0] == "restart_robot"
                && RoleNames.IsKnownRole(parts[1]))
            {
                engine.RestartRobot(parts[1]);
            }
            // Unknown recorded commands are ignored exactly like Sim.Cli, so a
            // future command kind never breaks old replay verification.
        }
    }

    private static string? eventDivergence(List<string> actual, List<string> expected)
    {
        var n = Math.Min(actual.Count, expected.Count);
        for (var i = 0; i < n; i++)
        {
            if (actual[i] != expected[i])
            {
                return $"event {i + 1}:\n  replay: {actual[i]}\n  record: {expected[i]}";
            }
        }
        if (actual.Count != expected.Count)
        {
            return $"event count {actual.Count} != recorded {expected.Count}";
        }
        return null;
    }
}