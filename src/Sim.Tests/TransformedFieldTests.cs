using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Rotation/translation equivariance of the transformed field: driving the
/// same seeded scenario with a field pose must produce exactly the same
/// match in field-local coordinates and only a rotated/translated world
/// frame — spawns, stage-wall contacts, fences and snapshots included.
/// </summary>
public class TransformedFieldTests
{
    private static Scenario OfficialWithPose(Pose2? pose) => new()
    {
        Seed = 42,
        Field = FieldParams.Default with { Pose = pose },
        Blocks = OfficialLayout.Blocks,
    };

    private static (RobotState Us, RobotState Them, List<string> Events) Run(
        Pose2? pose, RobotAction action, int ticks)
    {
        var engine = new MatchEngine(OfficialWithPose(pose));
        engine.Arm();
        var events = new List<string>();
        Snapshot last = null!;
        for (var i = 0; i < ticks && !engine.Done; i++)
        {
            last = engine.Tick(action, action);
            events.AddRange(last.Events?.Select(e => $"{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}") ?? []);
        }
        return (last.Robots[RoleNames.Us], last.Robots[RoleNames.Them], events);
    }

    [Fact]
    public void TranslatedLayout_ShiftsWorldPositionsOnly()
    {
        var action = new RobotAction { V = 0.5, W = 0 };
        var identity = Run(null, action, 200);
        var translated = Run(new Pose2 { X = 1.25, Y = -2.5, Th = 0 }, action, 200);

        Assert.Equal(identity.Events, translated.Events);
        Assert.Equal(identity.Us.X + 1.25, translated.Us.X, 12);
        Assert.Equal(identity.Us.Y - 2.5, translated.Us.Y, 12);
        Assert.Equal(identity.Us.Th, translated.Us.Th, 12);
    }

    [Fact]
    public void RotatedLayout_IsRotationEquivariant()
    {
        const double th = Math.PI / 6;
        var t = new FieldTransform(0.3, -0.2, th);
        var action = new RobotAction { V = 0.4, W = 0.35 };
        var identity = Run(null, action, 200);
        var rotated = Run(new Pose2 { X = 0.3, Y = -0.2, Th = th }, action, 200);

        // Same scripted outcome in field terms: identical events/scores.
        Assert.Equal(identity.Events, rotated.Events);

        foreach (var (a, b) in new[] { (identity.Us, rotated.Us), (identity.Them, rotated.Them) })
        {
            var (wx, wy) = t.LocalToWorldPoint(a.X, a.Y);
            Assert.Equal(wx, b.X, 9);
            Assert.Equal(wy, b.Y, 9);
            Assert.Equal(t.LocalToWorldHeading(a.Th), b.Th, 9);
        }
    }

    [Fact]
    public void SpawnsAndBlocks_UseTransformedStartGeometry()
    {
        var pose = new Pose2 { X = -1.0, Y = 0.5, Th = Math.PI / 4 };
        var engine = new MatchEngine(OfficialWithPose(pose));
        var snapshot = engine.Tick();

        var t = engine.Field.Transform;
        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            var start = engine.Scenario.Field.Starts[role];
            var robot = snapshot.Robots[role];
            var (sx, sy) = t.LocalToWorldPoint(start.X, start.Y);
            Assert.Equal(sx, robot.X, 12);
            Assert.Equal(sy, robot.Y, 12);
            Assert.Equal(t.LocalToWorldHeading(start.Th), robot.Th, 12);
            // Start zones are on the walkway: not on stage, zero step height.
            Assert.False(robot.OnPlatform);
        }

        for (var i = 0; i < engine.Scenario.Blocks.Count; i++)
        {
            var spec = engine.Scenario.Blocks[i];
            if (spec.X is null || spec.Y is null)
            {
                continue;
            }
            var block = snapshot.Objects!.Buffs.Concat([snapshot.Objects.Debuff!])
                .First(b => Math.Abs(b.X - t.LocalToWorldPoint(spec.X.Value, spec.Y.Value).X) < 1e-9);
            var (bx, by) = t.LocalToWorldPoint(spec.X.Value, spec.Y.Value);
            Assert.Equal(bx, block.X, 12);
            Assert.Equal(by, block.Y, 12);
        }
    }

    [Fact]
    public void RotatedStageWall_StopsRobotAtSameLocalEdge()
    {
        // Drive north (local +y) into the stage wall; the rotated field must
        // stop the robot at the same local edge as the identity layout does.
        var pose = new Pose2 { X = 2.0, Y = -1.0, Th = -Math.PI / 3 };
        var t = new FieldTransform(pose.X, pose.Y, pose.Th);

        var scenario = OfficialWithPose(pose) with
        {
            Field = FieldParams.Default with
            {
                Pose = pose,
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.9, Y = 0.55, Th = Math.PI / 2 },
                    [RoleNames.Them] = new() { X = 1.9, Y = 3.25, Th = -Math.PI / 2 },
                },
            },
        };
        var engine = new MatchEngine(scenario);
        engine.Arm();
        // V=0.15 is below MountVMin, so the stage wall must block the push.
        var push = new RobotAction { V = 0.15, W = 0 };
        Snapshot last = null!;
        for (var i = 0; i < 120; i++)
        {
            last = engine.Tick(push, push);
        }
        var usWorld = last.Robots[RoleNames.Us];
        var (usx, usy) = t.WorldToLocalPoint(usWorld.X, usWorld.Y);
        // Local y must stay on the walkway side of the south platform edge (0.7)
        // minus the support margin, exactly like the identity geometry does.
        Assert.InRange(usy, 0.4, 0.7);
        var (themx, themy) = t.WorldToLocalPoint(
            last.Robots[RoleNames.Them].X, last.Robots[RoleNames.Them].Y);
        Assert.InRange(themy, 3.1, 3.4);

        // World bounding sanity: field is inside its rotated square, so world
        // coordinates deviate from the local ones by at most FieldSize.
        Assert.True(Math.Abs(usWorld.X - usx) < 3.8);
    }
}
