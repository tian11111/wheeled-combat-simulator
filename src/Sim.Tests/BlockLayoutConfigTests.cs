using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 2026-10-04 能量块数量/类型可配置（用户: "加上能调整能量块数量，增益减益都要有"）：
/// 协议允许 0..MaxBlocks 任意增益/减益组合；观测 <c>Debuffs</c> 列表按块索引顺序
/// 暴露全部减益块，<c>Debuff</c> 保持"第一个减益块"向后兼容（旧控制器/RL 环境不感知）；
/// 多个减益块各自独立计分（上台 +6 给对手）。
/// </summary>
public class BlockLayoutConfigTests
{
    [Fact]
    public void Scenario_ZeroBlocks_IsValid_AndEngineRuns()
    {
        var scenario = new Scenario { Seed = 7, Blocks = [] };
        Assert.Empty(scenario.Validate());

        using var engine = new MatchEngine(scenario);
        engine.Arm();
        for (var i = 0; i < 10 && !engine.Done; i++)
        {
            var snapshot = engine.Tick();
            Assert.NotNull(snapshot.Objects);
            Assert.Empty(snapshot.Objects.Buffs);
            Assert.Empty(snapshot.Objects.Debuffs);
            Assert.Null(snapshot.Objects.Debuff);
        }
    }

    [Fact]
    public void Scenario_OverMaxBlocks_FailsValidation()
    {
        var scenario = new Scenario
        {
            Seed = 1,
            Blocks = Enumerable.Range(0, Scenario.MaxBlocks + 1)
                .Select(i => new BlockSpec { Kind = BlockKind.Buff, X = 0.4 + i * 0.25, Y = 0.4 })
                .ToList(),
        };
        var errors = scenario.Validate().ToList();
        Assert.Contains(errors, e => e.Contains($"at most {Scenario.MaxBlocks}"));

        var atLimit = scenario with { Blocks = scenario.Blocks.Take(Scenario.MaxBlocks).ToList() };
        Assert.Empty(atLimit.Validate());
    }

    [Fact]
    public void MultipleDebuffs_ObservationListsAll_AndDebuffAliasIsFirst()
    {
        var scenario = new Scenario
        {
            Seed = 7,
            Blocks =
            [
                new() { Kind = BlockKind.Buff, X = 1.35, Y = 1.35 },
                new() { Kind = BlockKind.Debuff, X = 1.6, Y = 2.4 },
                new() { Kind = BlockKind.Buff, X = 2.5, Y = 2.6 },
                new() { Kind = BlockKind.Debuff, X = 2.9, Y = 1.1 },
                new() { Kind = BlockKind.Debuff, X = 3.2, Y = 2.0 },
            ],
        };
        Assert.Empty(scenario.Validate());

        using var engine = new MatchEngine(scenario);
        var obs = engine.BuildObservation(engine.Us);
        Assert.NotNull(obs.Objects);
        Assert.Equal(2, obs.Objects.Buffs.Count);
        Assert.Equal(3, obs.Objects.Debuffs.Count);
        Assert.Equal(obs.Objects.Debuffs[0], obs.Objects.Debuff);
        Assert.Equal(1.6, obs.Objects.Debuff!.X, 9);
        Assert.Equal(3.2, obs.Objects.Debuffs[2].X, 9);
    }

    [Fact]
    public void MultipleDebuffs_EachScoresSixForOpponent()
    {
        var scenario = new Scenario
        {
            Seed = 7,
            Blocks =
            [
                new() { Kind = BlockKind.Debuff, X = 2.95, Y = 1.9 },
                new() { Kind = BlockKind.Debuff, X = 2.95, Y = 2.6 },
                new() { Kind = BlockKind.Buff, X = 1.35, Y = 1.35 },
            ],
        };
        var engine = new MatchEngine(scenario);
        var drive = new RobotAction { V = 1.5 };

        engine.Us.X = 2.3;
        engine.Us.Y = 1.9;
        engine.Us.Th = 0;
        for (var i = 0; i < 150 && engine.Scores.Them < 6; i++)
        {
            engine.Tick(drive);
        }
        Assert.Equal(6, engine.Scores.Them);
        Assert.Equal(0, engine.Scores.Us);
        Assert.True(engine.Blocks[0].Out);

        engine.Us.X = 2.3;
        engine.Us.Y = 2.6;
        engine.Us.Th = 0;
        for (var i = 0; i < 150 && engine.Scores.Them < 12; i++)
        {
            engine.Tick(drive);
        }
        Assert.Equal(12, engine.Scores.Them);
        Assert.Equal(0, engine.Scores.Us);
        Assert.True(engine.Blocks[1].Out);
        var scored = engine.Events.Events.Where(e => e.Kind == EventKind.BlockScore).ToList();
        Assert.Equal(2, scored.Count);
    }
}
