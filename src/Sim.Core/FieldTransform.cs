using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// Pure, deterministic mapping between field-local coordinates (where all
/// arena geometry in <see cref="FieldParams"/> lives) and simulation-world
/// coordinates (where robots, blocks and snapshots are expressed).
///
/// The transform is a translation followed by a rotation about the field
/// origin's new position: world = pose.translation + R(pose.th) * local.
/// The identity transform short-circuits to bit-for-bit pass-through so
/// legacy scenarios (no <c>field.pose</c>) reproduce their recorded output
/// exactly.
/// </summary>
public sealed class FieldTransform
{
    /// <summary>The identity (0,0,0) transform: field-local equals world.</summary>
    public static readonly FieldTransform Identity = new(0, 0, 0);

    private readonly double _cos;
    private readonly double _sin;

    public FieldTransform(double x, double y, double th)
    {
        X = x;
        Y = y;
        Th = th;
        _cos = Math.Cos(th);
        _sin = Math.Sin(th);
    }

    /// <summary>Builds the transform for a serializable pose; null means identity.</summary>
    public static FieldTransform FromPose(Pose2? pose)
        => pose is null ? Identity : new FieldTransform(pose.X, pose.Y, pose.Th);

    /// <summary>World position of the field origin (m).</summary>
    public double X { get; }

    /// <summary>World position of the field origin (m).</summary>
    public double Y { get; }

    /// <summary>Field yaw about the vertical axis (rad, counter-clockwise).</summary>
    public double Th { get; }

    /// <summary>True when the transform is exactly (0,0,0): everything passes through unchanged.</summary>
    public bool IsIdentity => X == 0 && Y == 0 && Th == 0;

    /// <summary>Maps a field-local point to simulation-world coordinates.</summary>
    public (double X, double Y) LocalToWorldPoint(double x, double y)
    {
        if (IsIdentity)
        {
            return (x, y);
        }
        return (X + x * _cos - y * _sin, Y + x * _sin + y * _cos);
    }

    /// <summary>Maps a simulation-world point back to field-local coordinates.</summary>
    public (double X, double Y) WorldToLocalPoint(double x, double y)
    {
        if (IsIdentity)
        {
            return (x, y);
        }
        var dx = x - X;
        var dy = y - Y;
        return (dx * _cos + dy * _sin, -dx * _sin + dy * _cos);
    }

    /// <summary>Rotates a field-local direction/velocity into world coordinates (translation-free).</summary>
    public (double X, double Y) LocalToWorldVector(double vx, double vy)
    {
        if (IsIdentity)
        {
            return (vx, vy);
        }
        return (vx * _cos - vy * _sin, vx * _sin + vy * _cos);
    }

    /// <summary>Rotates a world direction/velocity back into field-local coordinates.</summary>
    public (double X, double Y) WorldToLocalVector(double vx, double vy)
    {
        if (IsIdentity)
        {
            return (vx, vy);
        }
        return (vx * _cos + vy * _sin, -vx * _sin + vy * _cos);
    }

    /// <summary>Adds the field yaw to a local heading (e.g. robot facing).</summary>
    public double LocalToWorldHeading(double th) => IsIdentity ? th : th + Th;

    /// <summary>Removes the field yaw from a world heading.</summary>
    public double WorldToLocalHeading(double th) => IsIdentity ? th : th - Th;
}
