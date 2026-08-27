using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Focused unit tests for the pure field-local ↔ world transform used by the
/// layout extension: exactness of the identity pass-through, rotation/
/// translation behavior, inverse consistency and heading mapping.
/// </summary>
public class FieldTransformTests
{
    [Fact]
    public void Identity_IsBitForBitPassThrough()
    {
        var t = FieldTransform.Identity;
        Assert.True(t.IsIdentity);

        foreach (var (x, y) in new[] { (0.0, 0.0), (0.95, 0.3), (-2.5, 3.1), (1e300, -1e-300) })
        {
            Assert.Equal((x, y), t.LocalToWorldPoint(x, y));
            Assert.Equal((x, y), t.WorldToLocalPoint(x, y));
            Assert.Equal((x, y), t.LocalToWorldVector(x, y));
            Assert.Equal((x, y), t.WorldToLocalVector(x, y));
        }
        Assert.Equal(Math.PI / 3, t.LocalToWorldHeading(Math.PI / 3));
        Assert.Equal(-1.234, t.WorldToLocalHeading(-1.234));
    }

    [Fact]
    public void FromPose_NullIsIdentity_ExplicitZerosAlsoIdentity()
    {
        Assert.True(FieldTransform.FromPose(null).IsIdentity);
        Assert.True(FieldTransform.FromPose(new Pose2 { X = 0, Y = 0, Th = 0 }).IsIdentity);
        var t = FieldTransform.FromPose(new Pose2 { X = 1, Y = 2, Th = 0.5 });
        Assert.False(t.IsIdentity);
        Assert.Equal(1, t.X);
        Assert.Equal(2, t.Y);
        Assert.Equal(0.5, t.Th);
    }

    [Fact]
    public void PureTranslation_ShiftsPointsNotVectors()
    {
        var t = new FieldTransform(10, -5, 0);
        Assert.Equal((11.0, -6.0), t.LocalToWorldPoint(1, -1));
        Assert.Equal((1, -1), t.LocalToWorldVector(1, -1));
        Assert.Equal(0.3, t.LocalToWorldHeading(0.3));
    }

    [Fact]
    public void QuarterTurnRotation_MapsAxes()
    {
        var t = new FieldTransform(0, 0, Math.PI / 2);
        var (x, y) = t.LocalToWorldPoint(1, 0);
        Assert.Equal(0.0, x, 9);
        Assert.Equal(1.0, y, 9);

        var (fx, fy) = t.LocalToWorldPoint(0, 1);
        Assert.Equal(-1.0, fx, 9);
        Assert.Equal(0.0, fy, 9);

        Assert.Equal(Math.PI / 2 + 0.25, t.LocalToWorldHeading(0.25), 12);
    }

    [Fact]
    public void CombinedTransform_RoundTripsPointsVectorsAndHeadings()
    {
        var t = new FieldTransform(0.4, -1.2, 0.7853981633974483);
        foreach (var (x, y) in new[] { (0.7, 0.7), (1.35, 1.35), (2.95, 1.9), (0.0, 3.8) })
        {
            var (wx, wy) = t.LocalToWorldPoint(x, y);
            var (lx, ly) = t.WorldToLocalPoint(wx, wy);
            Assert.Equal(x, lx, 9);
            Assert.Equal(y, ly, 9);
        }

        foreach (var (vx, vy) in new[] { (1.0, 0.0), (-0.5, 2.0), (0.0, -3.0) })
        {
            var origin = t.LocalToWorldPoint(0, 0);
            var tip = t.LocalToWorldPoint(vx, vy);
            var (wx, wy) = t.LocalToWorldVector(vx, vy);
            Assert.Equal(tip.X - origin.X, wx, 9);
            Assert.Equal(tip.Y - origin.Y, wy, 9);
        }

        Assert.Equal(-0.4, t.WorldToLocalHeading(t.LocalToWorldHeading(-0.4)), 12);
    }

    [Fact]
    public void VectorTransform_MatchesPointDelta()
    {
        var t = new FieldTransform(2.0, 1.5, -0.3);
        var a = t.LocalToWorldPoint(1.0, 2.0);
        var b = t.LocalToWorldPoint(1.4, 2.05);
        var v = t.LocalToWorldVector(0.4, 0.05);
        Assert.Equal(b.X - a.X, v.X, 9);
        Assert.Equal(b.Y - a.Y, v.Y, 9);
    }
}
