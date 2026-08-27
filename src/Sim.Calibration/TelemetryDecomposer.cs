using Sim.Protocol;

namespace Sim.Calibration;

/// <summary>
/// Decodes validated telemetry-v1 trials into the sample lists the fitters
/// consume. Assumes <see cref="TelemetryFile.Validate"/> already passed, so all
/// required fields are present and finite; anything still unusable is dropped
/// silently by the same numeric filters the legacy tool used (speed floors,
/// dt caps, sign checks) so it can never poison a fit.
/// </summary>
public static class TelemetryDecomposer
{
    /// <summary>Coast pairs: |speed|&gt;0.02 both samples, same sign, 0&lt;dt≤0.5.</summary>
    public static List<ExponentialPair> CoastPairs(TelemetryTrial trial)
    {
        var intervals = PoseIntervals(trial, "robot");
        var angular = trial.Kind == TelemetryTrialKind.AngularCoast;
        var speeds = new double?[intervals.Count];
        for (var i = 0; i < intervals.Count; i++)
        {
            var interval = intervals[i];
            if (!CommandIsIdle(trial.Frames[interval.Index], angular))
            {
                continue;
            }
            speeds[i] = angular
                ? interval.Omega
                : -interval.Vx * Math.Sin(interval.Th) + interval.Vy * Math.Cos(interval.Th);
        }
        var pairs = new List<ExponentialPair>();
        for (var i = 0; i < intervals.Count - 1; i++)
        {
            if (speeds[i] is not { } speedA || speeds[i + 1] is not { } speedB)
            {
                continue;
            }
            if (Math.Abs(speedA) < 0.02 || Math.Abs(speedB) < 0.02 || speedA * speedB <= 0)
            {
                continue;
            }
            var dt = intervals[i + 1].MidT - intervals[i].MidT;
            if (!(dt > 0) || dt > 0.5)
            {
                continue;
            }
            pairs.Add(new ExponentialPair(dt, Math.Log(Math.Abs(speedB) / Math.Abs(speedA))));
        }
        return pairs;
    }

    /// <summary>Block glide pairs: floor speed≥0.04, no acceleration, 0&lt;dt≤0.5.</summary>
    public static List<BlockPair> BlockPairs(TelemetryTrial trial)
    {
        var intervals = PoseIntervals(trial, "block");
        var speeds = intervals.Select(i => Math.Sqrt(i.Vx * i.Vx + i.Vy * i.Vy)).ToList();
        var pairs = new List<BlockPair>();
        for (var i = 0; i < speeds.Count - 1; i++)
        {
            if (speeds[i] < 0.04 || speeds[i + 1] > speeds[i] + 0.01)
            {
                continue;
            }
            var dt = intervals[i + 1].MidT - intervals[i].MidT;
            if (!(dt > 0) || dt > 0.5)
            {
                continue;
            }
            pairs.Add(new BlockPair(speeds[i], speeds[i + 1], dt));
        }
        return pairs;
    }

    /// <summary>
    /// Collision before/after relative normal velocity. Explicit impact form wins;
    /// otherwise velocities are derived from frames around the impact index.
    /// </summary>
    public static CollisionSample? CollisionSampleOf(TelemetryTrial trial)
    {
        var normal = ResolveNormal(trial);
        if (normal is not { } n)
        {
            return null;
        }
        if (trial.Impact?.Pre is { } pre && trial.Impact.Post is { } post)
        {
            var before = RelativeNormalVelocity(pre, n);
            var after = RelativeNormalVelocity(post, n);
            return before is null || after is null ? null : new CollisionSample(before.Value, after.Value);
        }
        var frames = trial.Frames;
        var impactIndex = trial.ImpactIndex ?? -1;
        if (impactIndex < 1 || impactIndex > frames.Count - 2)
        {
            impactIndex = NearestApproachIndex(trial);
        }
        if (impactIndex < 1 || impactIndex > frames.Count - 2)
        {
            return null;
        }
        var robotIntervals = PoseIntervals(trial, "robot");
        var opponentIntervals = PoseIntervals(trial, "opponent");
        var preRobot = robotIntervals.FirstOrDefault(i => i.Index == impactIndex - 1);
        var postRobot = robotIntervals.FirstOrDefault(i => i.Index == impactIndex);
        var preOther = opponentIntervals.FirstOrDefault(i => i.Index == impactIndex - 1);
        var postOther = opponentIntervals.FirstOrDefault(i => i.Index == impactIndex);
        if (preRobot.Index != impactIndex - 1 || postRobot.Index != impactIndex)
        {
            return null;
        }
        var beforeV = (preRobot.Vx - preOther.Vx) * n.X + (preRobot.Vy - preOther.Vy) * n.Y;
        var afterV = (postRobot.Vx - postOther.Vx) * n.X + (postRobot.Vy - postOther.Vy) * n.Y;
        return new CollisionSample(beforeV, afterV);
    }

    /// <summary>Stall samples: frames with a measured speed, boolean label and a commanded v.</summary>
    public static List<StallSample> StallSamples(TelemetryTrial trial)
    {
        var samples = new List<StallSample>();
        foreach (var frame in trial.Frames)
        {
            if (frame.Speed is not { } speed || frame.Stalled is not { } stalled)
            {
                continue;
            }
            var commanded = frame.Command?.V is not { } v || Math.Abs(v) > 0.05;
            if (commanded)
            {
                samples.Add(new StallSample(speed, stalled));
            }
        }
        return samples;
    }

    /// <summary>Mount sample from measured approach + outcome.</summary>
    public static MountSample? MountSampleOf(TelemetryTrial trial)
    {
        if (trial.Approach?.Vn is not { } vn || trial.Approach?.Vt is not { } vt || trial.Outcome is not { } outcome)
        {
            return null;
        }
        return new MountSample(vn, vt, outcome);
    }

    // ---------- internals ----------

    private readonly record struct Interval(int Index, double MidT, double Dt, double Vx, double Vy, double Omega, double Th);

    private static List<Interval> PoseIntervals(TelemetryTrial trial, string key)
    {
        var intervals = new List<Interval>();
        var frames = trial.Frames;
        for (var i = 0; i < frames.Count - 1; i++)
        {
            var a = PoseOf(frames[i], key);
            var b = PoseOf(frames[i + 1], key);
            var t0 = frames[i].T;
            var t1 = frames[i + 1].T;
            if (a is null || b is null || t0 is null || t1 is null)
            {
                continue;
            }
            var dt = t1.Value - t0.Value;
            if (!(dt > 0))
            {
                continue;
            }
            var dth = CalibrationMath.AngleDelta(a.Th, b.Th);
            intervals.Add(new Interval(
                i,
                (t0.Value + t1.Value) / 2,
                dt,
                (b.X - a.X) / dt,
                (b.Y - a.Y) / dt,
                dth / dt,
                a.Th + dth / 2));
        }
        return intervals;
    }

    private sealed record Pose(double X, double Y, double Th);

    private static Pose? PoseOf(TelemetryFrame? frame, string key)
    {
        var pose = key switch
        {
            "robot" => frame?.Robot,
            "block" => frame?.Block,
            _ => frame?.Opponent,
        };
        if (pose?.X is not { } x || pose?.Y is not { } y)
        {
            return null;
        }
        return new Pose(x, y, pose.Th ?? 0);
    }

    private static bool CommandIsIdle(TelemetryFrame? frame, bool angular)
    {
        var command = frame?.Command;
        if (command is null)
        {
            return true;
        }
        var value = angular ? command.W : command.V;
        return value is null || Math.Abs(value.Value) <= 0.05;
    }

    private static (double X, double Y)? ResolveNormal(TelemetryTrial trial)
    {
        if (trial.Normal is { X: { } nx, Y: { } ny } && Math.Sqrt(nx * nx + ny * ny) > 1e-9)
        {
            var length = Math.Sqrt(nx * nx + ny * ny);
            return (nx / length, ny / length);
        }
        if (trial.Wall?.ToLowerInvariant() is { } wall)
        {
            return wall switch
            {
                "east" => (1, 0),
                "west" => (-1, 0),
                "north" => (0, 1),
                "south" => (0, -1),
                _ => null,
            };
        }
        var frames = trial.Frames;
        if (frames.Count == 0)
        {
            return null;
        }
        var index = trial.ImpactIndex ?? NearestApproachIndex(trial);
        if (index < 1 || index > frames.Count - 2)
        {
            index = Math.Max(1, Math.Min(frames.Count - 2, frames.Count / 2));
        }
        var robot = PoseOf(frames[index], "robot");
        var opponent = PoseOf(frames[index], "opponent");
        if (robot is null || opponent is null)
        {
            return null;
        }
        var length2 = Math.Sqrt(
            Math.Pow(opponent.X - robot.X, 2) + Math.Pow(opponent.Y - robot.Y, 2));
        return length2 > 1e-9
            ? ((opponent.X - robot.X) / length2, (opponent.Y - robot.Y) / length2)
            : null;
    }

    private static double? RelativeNormalVelocity(TelemetryImpactVelocities pair, (double X, double Y) normal)
    {
        if (pair.Robot is not { Vx: { } rvx, Vy: { } rvy })
        {
            return null;
        }
        var ox = pair.Opponent?.Vx ?? 0;
        var oy = pair.Opponent?.Vy ?? 0;
        return (rvx - ox) * normal.X + (rvy - oy) * normal.Y;
    }

    private static int NearestApproachIndex(TelemetryTrial trial)
    {
        var frames = trial.Frames;
        var nearest = double.PositiveInfinity;
        var best = -1;
        for (var i = 1; i < frames.Count - 1; i++)
        {
            var robot = PoseOf(frames[i], "robot");
            var opponent = PoseOf(frames[i], "opponent");
            if (robot is null || opponent is null)
            {
                continue;
            }
            var distance = Math.Sqrt(
                Math.Pow(robot.X - opponent.X, 2) + Math.Pow(robot.Y - opponent.Y, 2));
            if (distance < nearest)
            {
                nearest = distance;
                best = i;
            }
        }
        return best;
    }
}
