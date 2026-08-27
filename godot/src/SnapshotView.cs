// Typed view adapter: projects an immutable Sim.Protocol.Snapshot into a
// render-ready frame. This file is intentionally free of any Godot namespace so
// it can be unit-tested without the Godot editor/SDK; the Godot shell
// (ArenaVisualizer) consumes the RenderFrame it produces.
//
// Coordinate convention: the authoritative 2-D model lives in the (x, y) plane.
// Godot's ground plane is (x, z) with +y up, so a sim point (x, y) at height h
// maps to world (x, h, y). Yaw is the sim heading th, measured about the up axis.

using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>A point in Godot world space (x, up, z).</summary>
public readonly record struct Vec3(double X, double Up, double Z);

/// <summary>Render state for one robot.</summary>
public sealed record RobotVisual
{
    public required string Role { get; init; }
    public Vec3 Position { get; init; }
    /// <summary>Heading in radians about the up axis.</summary>
    public double Yaw { get; init; }
    public bool OnPlatform { get; init; }
    public string? State { get; init; }
    public bool Armed { get; init; }
    public bool Manual { get; init; }
    public string? Action { get; init; }
}

/// <summary>Render state for one energy block.</summary>
public sealed record BlockVisual
{
    /// <summary>"buff" or "debuff".</summary>
    public required string Kind { get; init; }
    public Vec3 Position { get; init; }
    public bool OnPlatform { get; init; }
    /// <summary>True once pushed off the platform (stays for the rest of the match).</summary>
    public bool Out { get; init; }
}

/// <summary>HUD / status panel content.</summary>
public sealed record HudState
{
    public long Tick { get; init; }
    public double T { get; init; }
    public double Timer { get; init; }
    public MatchPhase Phase { get; init; }
    public bool Paused { get; init; }
    public bool Done { get; init; }
    public string? DoneReason { get; init; }
    public double ScoreUs { get; init; }
    public double ScoreThem { get; init; }
    public double RestartPenaltyUs { get; init; }
    public double RestartPenaltyThem { get; init; }
    /// <summary>Most recent event messages, newest last.</summary>
    public IReadOnlyList<string> RecentEvents { get; init; } = [];
}

/// <summary>Everything the renderer needs for one frame.</summary>
public sealed record RenderFrame
{
    public required RobotVisual Us { get; init; }
    public required RobotVisual Them { get; init; }
    public IReadOnlyList<BlockVisual> Blocks { get; init; } = [];
    /// <summary>Suggested camera look-at target: midpoint of the two robots.</summary>
    public Vec3 CameraFocus { get; init; }
    public HudState Hud { get; init; } = new();
}

/// <summary>Pure mapping + interpolation helpers over snapshots.</summary>
public static class SnapshotView
{
    /// <summary>Projects a snapshot into a render frame.</summary>
    public static RenderFrame From(Snapshot snapshot, int maxRecentEvents = 6)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var us = ToRobotVisual(RoleNames.Us, snapshot);
        var them = ToRobotVisual(RoleNames.Them, snapshot);

        var blocks = new List<BlockVisual>();
        if (snapshot.Objects is { } objects)
        {
            foreach (var buff in objects.Buffs)
            {
                blocks.Add(ToBlockVisual("buff", buff));
            }
            if (objects.Debuff is { } debuff)
            {
                blocks.Add(ToBlockVisual("debuff", debuff));
            }
        }

        var events = snapshot.Events is { Count: > 0 }
            ? snapshot.Events.Select(e => e.Msg ?? "").Where(m => m.Length > 0).ToList()
            : new List<string>();

        return new RenderFrame
        {
            Us = us,
            Them = them,
            Blocks = blocks,
            CameraFocus = Midpoint(us.Position, them.Position),
            Hud = new HudState
            {
                Tick = snapshot.Tick,
                T = snapshot.T,
                Timer = snapshot.Timer,
                Phase = snapshot.Phase,
                Paused = snapshot.Paused,
                Done = snapshot.Done,
                DoneReason = snapshot.DoneReason,
                ScoreUs = snapshot.Scores.Us,
                ScoreThem = snapshot.Scores.Them,
                RestartPenaltyUs = snapshot.RestartPenalties.Us,
                RestartPenaltyThem = snapshot.RestartPenalties.Them,
                RecentEvents = events.TakeLast(maxRecentEvents).ToList(),
            },
        };
    }

    /// <summary>
    /// Linear interpolation between two consecutive frames for smooth rendering.
    /// <paramref name="alpha"/> is clamped to [0, 1]; yaw takes the shortest arc.
    /// </summary>
    public static RenderFrame Lerp(RenderFrame a, RenderFrame b, double alpha)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        alpha = Math.Clamp(alpha, 0, 1);
        if (alpha <= 0)
        {
            return a;
        }
        if (alpha >= 1)
        {
            return b;
        }

        return b with
        {
            Us = b.Us with
            {
                Position = LerpVec(a.Us.Position, b.Us.Position, alpha),
                Yaw = LerpYaw(a.Us.Yaw, b.Us.Yaw, alpha),
            },
            Them = b.Them with
            {
                Position = LerpVec(a.Them.Position, b.Them.Position, alpha),
                Yaw = LerpYaw(a.Them.Yaw, b.Them.Yaw, alpha),
            },
            Blocks = LerpBlocks(a.Blocks, b.Blocks, alpha),
            CameraFocus = LerpVec(a.CameraFocus, b.CameraFocus, alpha),
        };
    }

    private static RobotVisual ToRobotVisual(string role, Snapshot snapshot)
    {
        if (!snapshot.Robots.TryGetValue(role, out var robot) || robot is null)
        {
            return new RobotVisual { Role = role, Position = new Vec3(0, 0, 0) };
        }
        return new RobotVisual
        {
            Role = role,
            Position = new Vec3(robot.X, robot.ZG, robot.Y),
            Yaw = robot.Th,
            OnPlatform = robot.OnPlatform,
            State = robot.State,
            Armed = robot.Armed,
            Manual = robot.Manual,
            Action = robot.Action,
        };
    }

    private static BlockVisual ToBlockVisual(string kind, EnergyBlockView block) => new()
    {
        Kind = kind,
        // Blocks sit on the ground / platform surface; height is purely visual.
        Position = new Vec3(block.X, block.OnPlatform ? 0.06 : 0.0, block.Y),
        OnPlatform = block.OnPlatform,
        Out = block.Out == true,
    };

    private static Vec3 Midpoint(Vec3 a, Vec3 b)
        => new((a.X + b.X) / 2, (a.Up + b.Up) / 2, (a.Z + b.Z) / 2);

    private static Vec3 LerpVec(Vec3 a, Vec3 b, double alpha)
        => new(a.X + (b.X - a.X) * alpha, a.Up + (b.Up - a.Up) * alpha, a.Z + (b.Z - a.Z) * alpha);

    private static double LerpYaw(double a, double b, double alpha)
    {
        var delta = (b - a + Math.PI) % (2 * Math.PI) - Math.PI;
        if (delta < -Math.PI)
        {
            delta += 2 * Math.PI;
        }
        return a + delta * alpha;
    }

    private static IReadOnlyList<BlockVisual> LerpBlocks(IReadOnlyList<BlockVisual> a, IReadOnlyList<BlockVisual> b, double alpha)
    {
        if (a.Count != b.Count)
        {
            return b; // layout changed (respawn); snap to the newer frame
        }
        var result = new List<BlockVisual>(b.Count);
        for (var i = 0; i < b.Count; i++)
        {
            result.Add(b[i] with { Position = LerpVec(a[i].Position, b[i].Position, alpha) });
        }
        return result;
    }
}
