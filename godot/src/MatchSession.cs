// Godot-free session facade (no Godot namespace): owns the authoritative
// MatchEngine, applies the fixed-step clock in live mode, and caches a replay
// by re-running the deterministic core with the recorded action/command stream.
// Replay playback therefore never re-implements rules — it reproduces the core.
// Keeping this file free of Godot lets Sim.Tests regress it headlessly.

using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>Playback mode of the desktop shell.</summary>
public enum SessionMode
{
    Live,
    Replay,
}

/// <summary>
/// Shell-side match state: one authoritative engine, the fixed-step clock, and
/// (in replay mode) the cached snapshot stream reconstructed from a ReplayFile.
/// </summary>
public sealed class MatchSession
{
    // Live scenario the session resets to. Loading a replay re-points it at the
    // replay's own scenario so F5 (ResetToLive) rebuilds the same match the
    // replay ended with — the reset scenario always represents the current
    // live match.
    private Scenario _scenario;
    private readonly Queue<Snapshot> _pending = new();
    private double _accumulator;

    public MatchSession(Scenario scenario)
    {
        _scenario = scenario;
        Engine = new MatchEngine(scenario);
    }

    public MatchEngine Engine { get; private set; }

    public Scenario Scenario => Engine.Scenario;

    /// <summary>
    /// Same scenario with every block frozen at its current field-local
    /// position (seeded placements resolved). The layout editor drafts on this
    /// so its preview matches what the engine actually spawned.
    /// </summary>
    public Scenario ScenarioWithResolvedBlocks()
    {
        var blocks = Engine.Blocks
            .Select(b =>
            {
                var (lx, ly) = Engine.Field.Transform.WorldToLocalPoint(b.X, b.Y);
                return new BlockSpec { Kind = b.Kind, X = lx, Y = ly, Radius = b.R };
            })
            .ToList();
        return Scenario with { Blocks = blocks };
    }

    public SessionMode Mode { get; private set; } = SessionMode.Live;

    public Snapshot? LatestSnapshot { get; private set; }

    /// <summary>Cached snapshots of the loaded replay (index 0 == first tick).</summary>
    public IReadOnlyList<Snapshot> ReplayCache { get; private set; } = [];

    /// <summary>Current replay cursor (index into <see cref="ReplayCache"/>).</summary>
    public int ReplayIndex { get; private set; }

    public bool ReplayPlaying { get; set; }

    /// <summary>True when the replay cursor reached the final cached frame.</summary>
    public bool ReplayAtEnd => ReplayCache.Count == 0 || ReplayIndex >= ReplayCache.Count - 1;

    // ---------- live mode ----------

    /// <summary>Fast-forwards the clock; returns true when a tick was committed.</summary>
    public bool StepLive(double delta, out Snapshot? snapshot)
    {
        snapshot = null;
        if (Mode != SessionMode.Live || Engine.Done)
        {
            return false;
        }
        _accumulator += delta;
        var tickSeconds = Engine.Scenario.Field.TickSeconds;
        var stepped = false;
        while (_accumulator >= tickSeconds)
        {
            _accumulator -= tickSeconds;
            _pending.Enqueue(Engine.Tick());
            stepped = true;
        }
        if (_pending.Count > 0)
        {
            // 渲染掉帧不追赶: 每渲染帧只消费最新一帧快照。
            Snapshot? latest = null;
            while (_pending.Count > 0)
            {
                latest = _pending.Dequeue();
            }
            LatestSnapshot = latest;
            snapshot = latest;
        }
        return stepped;
    }

    /// <summary>Rebuilds a fresh engine for the same scenario (reset same seed).</summary>
    public void ResetToLive()
    {
        Engine = new MatchEngine(_scenario);
        Mode = SessionMode.Live;
        LatestSnapshot = null;
        ReplayCache = [];
        ReplayIndex = 0;
        ReplayPlaying = false;
        _pending.Clear();
        _accumulator = 0;
    }

    // ---------- replay mode ----------

    /// <summary>
    /// Reconstructs the recorded match from the replay file's own scenario and
    /// accepted action/command stream, caching every committed snapshot. The
    /// cache is bounded by the recorded tick count so seek/step are O(1).
    /// </summary>
    public void LoadReplay(ReplayFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var errors = file.Header.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid replay header: {string.Join(" ", errors)}");
        }

        var engine = new MatchEngine(file.Scenario);
        var actionsByTick = file.Header.Ticks.ToDictionary(t => t.Tick, t => t.Actions);
        var commandsByTick = file.Header.Ticks
            .Where(t => t.Commands is { Count: > 0 })
            .ToDictionary(t => t.Tick, t => t.Commands!);

        var cache = new List<Snapshot>();
        engine.Arm();
        var lastTick = Math.Max(file.Ticks, file.Header.Ticks.Count > 0 ? file.Header.Ticks[^1].Tick : 0);
        for (var tick = 1; tick <= lastTick && !engine.Done; tick++)
        {
            if (commandsByTick.TryGetValue(tick, out var commands))
            {
                ApplyCommands(engine, commands);
            }
            actionsByTick.TryGetValue(tick, out var actions);
            cache.Add(engine.Tick(
                actions?.GetValueOrDefault(RoleNames.Us),
                actions?.GetValueOrDefault(RoleNames.Them)));
        }

        Engine = engine;
        _scenario = file.Scenario; // F5 (ResetToLive) 从回放场景重建, 与录制端同一场地
        Mode = SessionMode.Replay;
        LatestSnapshot = null;
        ReplayCache = cache;
        ReplayIndex = 0;
        ReplayPlaying = false;
    }

    /// <summary>Steps the replay cursor; returns false when already at an end.</summary>
    public bool ReplayStep(int delta)
    {
        if (ReplayCache.Count == 0)
        {
            return false;
        }
        var next = Math.Clamp(ReplayIndex + delta, 0, ReplayCache.Count - 1);
        if (next == ReplayIndex)
        {
            return false;
        }
        ReplayIndex = next;
        return true;
    }

    /// <summary>Seeks to a 1-based tick number, clamped to the cache.</summary>
    public void ReplaySeekTick(long tick)
    {
        if (ReplayCache.Count == 0)
        {
            return;
        }
        ReplayIndex = (int)Math.Clamp(tick - 1, 0, ReplayCache.Count - 1);
    }

    public long ReplayTickForIndex(int index)
        => index >= 0 && index < ReplayCache.Count ? ReplayCache[index].Tick : 0;

    /// <summary>
    /// Frame for the current cursor with optional alpha interpolation toward the
    /// next cached snapshot (snaps on the final frame).
    /// </summary>
    public RenderFrame ReplayFrame(double alpha)
    {
        if (ReplayCache.Count == 0)
        {
            return new RenderFrame
            {
                Us = new RobotVisual { Role = RoleNames.Us },
                Them = new RobotVisual { Role = RoleNames.Them },
            };
        }
        var platformHeight = Engine.Scenario.Field.PlatformHeight;
        var current = ReplayCache[ReplayIndex];
        if (ReplayIndex >= ReplayCache.Count - 1 || alpha >= 0.999)
        {
            return SnapshotView.From(current, platformHeight);
        }
        var next = ReplayCache[ReplayIndex + 1];
        return SnapshotView.Lerp(
            SnapshotView.From(current, platformHeight),
            SnapshotView.From(next, platformHeight),
            alpha);
    }

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
            // future command kind never breaks old replay playback.
        }
    }
}