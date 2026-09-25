using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

public class MujocoScoreEdgeGuardTests
{
    private static Scenario EdgeScoreScenario(string backend, double usX) => new()
    {
        Seed = 42,
        Physics = new PhysicsSpec
        {
            Backend = backend,
            ModelVersion = backend == PhysicsSpec.Mujoco ? PhysicsSpec.MujocoModelV1 : null,
        },
        Blocks =
        [
            OfficialLayout.Blocks[0] with { X = 3.05, Y = 1.9 },
            OfficialLayout.Blocks[1] with { X = 0.2, Y = 0.2 },
            OfficialLayout.Blocks[2] with { X = 0.2, Y = 3.6 },
        ],
        Field = FieldParams.Default with
        {
            Starts = new Dictionary<string, Pose2>
            {
                [RoleNames.Us] = new() { X = usX, Y = 1.9, Th = 0 },
                [RoleNames.Them] = new() { X = 3.5, Y = 3.5, Th = Math.PI },
            },
        },
    };

    private static BlockRuntime EnterScoreBlock(MatchEngine engine)
    {
        engine.Arm();
        var robot = engine.Us;
        var target = engine.Blocks[0];
        robot.Fsm.State = FsmState.ScoreBlock;
        robot.Fsm.ScoreTarget = target;
        robot.Fsm.ScoreLastX = target.X;
        robot.Fsm.ScoreLastY = target.Y;
        robot.Fsm.ScoreProgressT = 1.9;
        return target;
    }

    [Fact]
    public void MujocoScoreBlock_NearEdgeTurnsTowardCenterBeforeDriving()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var engine = MatchEngineHost.Create(EdgeScoreScenario(PhysicsSpec.Mujoco, usX: 2.85));
        var target = EnterScoreBlock(engine);
        engine.Tick();

        Assert.Equal(FsmState.ScoreBlock, engine.Us.Fsm.State);
        Assert.Equal("SCORE_BLOCK: 近台沿回中", engine.Us.Fsm.Action);
        Assert.Equal(-0.4, engine.Us.V, 9);
        Assert.Equal(0, engine.Us.W, 9);
        Assert.Same(target, engine.Us.Fsm.ScoreTarget);
        Assert.True(engine.Us.Fsm.ScoreProgressT > 1.9);
    }

    [Fact]
    public void MujocoScoreBlock_ClearanceFromEdgeContinuesPushingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var engine = MatchEngineHost.Create(EdgeScoreScenario(PhysicsSpec.Mujoco, usX: 2.3));
        EnterScoreBlock(engine);
        engine.Tick();

        Assert.Equal("推增益块接近边缘, 减速", engine.Us.Fsm.Action);
        Assert.Equal(0.35, engine.Us.V, 9);
    }

    [Fact]
    public void LegacyScoreBlock_NearEdgeBehaviorIsUnchanged()
    {
        using var engine = MatchEngineHost.Create(EdgeScoreScenario(PhysicsSpec.Legacy, usX: 2.85));
        EnterScoreBlock(engine);
        engine.Tick();

        Assert.Equal("推增益块接近边缘, 减速", engine.Us.Fsm.Action);
        Assert.Equal(0.35, engine.Us.V, 9);
    }

    [Fact]
    public void OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var engine = MatchEngineHost.Create(new Scenario
        {
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
            Blocks = OfficialLayout.Blocks,
        });
        var starts = engine.Blocks.Where(b => b.Kind == BlockKind.Buff)
            .Select(b => (Block: b, X: b.X, Y: b.Y)).ToArray();
        engine.Arm();
        for (var i = 0; i < 2400 && !engine.Done; i++)
        {
            engine.Tick();
        }

        Assert.Contains(engine.Events.Events, e => e.Kind == EventKind.BlockScore && e.Robot.IsUs);
        Assert.Contains(starts, s => s.Block.Out
            && Math.Sqrt(Math.Pow(s.Block.X - s.X, 2) + Math.Pow(s.Block.Y - s.Y, 2)) > 0.05);
        Assert.DoesNotContain(engine.Events.Events, e => e.Kind == EventKind.Drop && e.Robot.IsUs);
        Assert.Equal(2400, engine.TickIndex);
    }
}
