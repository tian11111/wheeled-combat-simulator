using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Regression for the engine-independent view adapter (godot/src/SnapshotView.cs):
/// snapshot → render-frame projection and interpolation must stay faithful to the
/// authoritative state so the desktop shell never drifts from Sim.Cli.
/// </summary>
public class SnapshotViewTests
{
    private static Scenario Scenario() => new()
    {
        Seed = 42,
        Blocks = OfficialLayout.Blocks,
    };

    [Fact]
    public void From_ProjectsSnapshotFaithfully()
    {
        var engine = new MatchEngine(Scenario());
        engine.Arm();
        var snapshot = engine.Tick();

        var frame = SnapshotView.From(snapshot);

        // Sim (x, y, zG) → Godot (x, up, z).
        Assert.Equal(snapshot.Robots[RoleNames.Us].X, frame.Us.Position.X, precision: 9);
        Assert.Equal(snapshot.Robots[RoleNames.Us].Y, frame.Us.Position.Z, precision: 9);
        Assert.Equal(snapshot.Robots[RoleNames.Us].ZG, frame.Us.Position.Up, precision: 9);
        Assert.Equal(snapshot.Robots[RoleNames.Us].Th, frame.Us.Yaw, precision: 9);
        Assert.Equal(snapshot.Robots[RoleNames.Them].X, frame.Them.Position.X, precision: 9);

        Assert.Equal(3, frame.Blocks.Count);
        Assert.Equal(2, frame.Blocks.Count(b => b.Kind == "buff"));
        Assert.Single(frame.Blocks, b => b.Kind == "debuff");

        // Camera focus is the robot midpoint.
        Assert.Equal((frame.Us.Position.X + frame.Them.Position.X) / 2, frame.CameraFocus.X, precision: 9);

        Assert.Equal(snapshot.Scores.Us, frame.Hud.ScoreUs);
        Assert.Equal(snapshot.Tick, frame.Hud.Tick);
        Assert.Equal(snapshot.Phase, frame.Hud.Phase);
    }

    [Fact]
    public void From_IncludesRecentEventMessages()
    {
        var engine = new MatchEngine(Scenario());
        engine.Arm();
        Snapshot last = null!;
        for (var i = 0; i < 50; i++)
        {
            last = engine.Tick();
        }
        var withEvents = engine.Events.Events.Count > 0 ? engine.CommitSnapshot() : last;
        var frame = SnapshotView.From(withEvents, maxRecentEvents: 6);
        Assert.True(frame.Hud.RecentEvents.Count <= 6);
        Assert.All(frame.Hud.RecentEvents, m => Assert.False(string.IsNullOrEmpty(m)));
    }

    [Fact]
    public void Lerp_InterpolatesPositionAndShortestArcYaw()
    {
        var engine = new MatchEngine(Scenario());
        var frameA = SnapshotView.From(engine.CommitSnapshot());
        engine.Arm();
        engine.Tick();
        var frameB = SnapshotView.From(engine.CommitSnapshot());

        var mid = SnapshotView.Lerp(frameA, frameB, 0.5);
        Assert.Equal((frameA.Us.Position.X + frameB.Us.Position.X) / 2, mid.Us.Position.X, precision: 9);

        // Endpoints are returned unchanged.
        Assert.Same(frameA, SnapshotView.Lerp(frameA, frameB, 0));
        Assert.Same(frameB, SnapshotView.Lerp(frameA, frameB, 1));

        // Yaw wrap-around: 3.0 → -3.0 must go through ±π, not through 0.
        var a = frameA with { Us = frameA.Us with { Yaw = 3.0 } };
        var b = frameB with { Us = frameB.Us with { Yaw = -3.0 } };
        var half = SnapshotView.Lerp(a, b, 0.5);
        Assert.True(Math.Abs(Math.Abs(half.Us.Yaw) - Math.PI) < 0.2,
            $"expected interpolated yaw near ±π, got {half.Us.Yaw}");
    }
}
