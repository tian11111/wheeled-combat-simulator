using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

// 09-25 SEARCH 索敌闭环: 分类分流行为验证(AC3)。
// 增益块 → SEARCH → classify → SCORE_BLOCK → 接触/位移/BlockScore;
// 对手 → SEARCH → classify → ATTACK → 追击/接触; 减益块不得走 SCORE_BLOCK。
public class SearchClassificationFlowTests
{
    private static Scenario FlowScenario((double X, double Y) target, bool targetIsBlock)
    {
        // 目标置于车前方 0.8 m(方位 0), 其余方块移出探针范围(>1.675 m)。
        var blocks = new List<BlockSpec>();
        if (targetIsBlock)
        {
            blocks.Add(OfficialLayout.Blocks[0] with { X = target.X, Y = target.Y });
        }
        blocks.Add(OfficialLayout.Blocks[1] with { X = 0.2, Y = 0.2 });
        blocks.Add(OfficialLayout.Blocks[2] with { X = 0.2, Y = 3.6 });
        if (!targetIsBlock)
        {
            // 模型要求恰三块: 对手场景再补一块远置方块。
            blocks.Add(OfficialLayout.Blocks[0] with { X = 3.6, Y = 0.2 });
        }
        return new Scenario
        {
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
            Blocks = blocks,
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.9, Y = 1.9, Th = 0 },
                    // 对手放在远离目标/探针的角落, 零动作保持。
                    [RoleNames.Them] = new() { X = 3.5, Y = 0.3, Th = Math.PI },
                },
            },
        };
    }

    [Fact]
    public void BuffTarget_ClassifiesToScoreBlock_AndScores()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var engine = MatchEngineHost.Create(FlowScenario((2.7, 1.9), targetIsBlock: true));
        engine.Arm();
        var sawClassify = false;
        var sawScoreBlock = false;
        var sawBlockScore = false;
        BlockRuntime? tracked = null;
        (double X, double Y) start = default;
        var current = engine.CommitSnapshot();
        for (var i = 1; i <= 2400; i++)
        {
            current = engine.Tick(null, RobotAction.Zero);
            if (!sawClassify && engine.Events.Events.Any(e => e.Msg is not null && e.Msg.Contains("已对准")))
            {
                sawClassify = true;
            }
            if (!sawScoreBlock && current.Robots[RoleNames.Us].State == "SCORE_BLOCK")
            {
                sawScoreBlock = true;
                tracked = engine.Blocks.FirstOrDefault(b => current.Objects!.Buffs.Any(v => Math.Abs(v.X - b.X) < 0.01 && Math.Abs(v.Y - b.Y) < 0.01))
                    ?? engine.Blocks[0];
                start = (tracked.X, tracked.Y);
            }
            if (engine.Events.Events.Any(e => e.Kind == EventKind.BlockScore))
            {
                sawBlockScore = true;
                break;
            }
        }
        Assert.True(sawClassify, "never aligned to classify");
        Assert.True(sawScoreBlock, $"never entered SCORE_BLOCK; final state={current.Robots[RoleNames.Us].State}");
        Assert.True(sawBlockScore, "no BlockScore event after SCORE_BLOCK");
        Assert.NotNull(tracked);
        var moved = Math.Sqrt(Math.Pow(tracked!.X - start.X, 2) + Math.Pow(tracked.Y - start.Y, 2));
        Assert.True(moved > 0.05, $"block never moved after SCORE_BLOCK: {moved:0.000} m");
    }

    [Fact]
    public void OpponentTarget_ClassifiesToAttack_AndChases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // 对手场景: 唯一探针内目标是静止对手; 方块全部移出探针范围。
        using var engine = MatchEngineHost.Create(FlowScenario((2.7, 1.9), targetIsBlock: false));
        engine.Arm();
        var sawClassify = false;
        var sawAttack = false;
        var current = engine.CommitSnapshot();
        for (var i = 1; i <= 1200; i++)
        {
            current = engine.Tick(null, RobotAction.Zero);
            if (!sawClassify && engine.Events.Events.Any(e => e.Msg is not null && e.Msg.Contains("已对准")))
            {
                sawClassify = true;
            }
            if (!sawAttack && current.Robots[RoleNames.Us].State == "ATTACK")
            {
                sawAttack = true;
                break;
            }
        }
        Assert.True(sawClassify, "never aligned to classify");
        Assert.True(sawAttack, $"never entered ATTACK; final state={current.Robots[RoleNames.Us].State}");
    }

    [Fact]
    public void DebuffTarget_DoesNotEnterScoreBlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // 减益块作为唯一近距目标: 分类必须走规避/绕行, 不得进入 SCORE_BLOCK 得分。
        var blocks = new List<BlockSpec>
        {
            OfficialLayout.Blocks[0] with { X = 0.2, Y = 0.2 },
            OfficialLayout.Blocks[1] with { X = 0.2, Y = 3.6 },
            OfficialLayout.Blocks[2] with { X = 2.7, Y = 1.9, Kind = BlockKind.Debuff },
        };
        var scenario = FlowScenario((2.7, 1.9), targetIsBlock: true) with { Blocks = blocks };
        using var engine = MatchEngineHost.Create(scenario);
        engine.Arm();
        var sawClassify = false;
        var current = engine.CommitSnapshot();
        for (var i = 1; i <= 1200; i++)
        {
            current = engine.Tick(null, RobotAction.Zero);
            if (!sawClassify && engine.Events.Events.Any(e => e.Msg is not null && e.Msg.Contains("已对准")))
            {
                sawClassify = true;
            }
            var enteredScoreBlock = current.Robots[RoleNames.Us].State == "SCORE_BLOCK"
                && engine.Events.Events.Any(e => e.Msg is not null && e.Msg.Contains("减益块") && e.Msg.Contains("SCORE"));
            Assert.False(enteredScoreBlock, "debuff block must not route to SCORE_BLOCK");
        }
        Assert.True(sawClassify, "debuff scenario never aligned to classify");
    }
}
