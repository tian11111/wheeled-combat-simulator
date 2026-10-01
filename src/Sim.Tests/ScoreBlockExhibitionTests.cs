using System.Text.Json;
using Sim.Cli;
using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// SCORE_BLOCK 展演共享缝的契约测试: 预推进/目标锁定/11 维投影与训练入口
/// <c>rl-env</c> 同语义, 以及"交接前我方仍走内置 FSM、注入外部动作即切 Manual"的
/// 门控依据 (docs/CONTROLLER_PROTOCOL.md)。
/// </summary>
[Collection("cli-console")]
public class ScoreBlockExhibitionTests
{
    /// <summary>与 RlEnvCommandTests 相同的场景定位/反序列化口径（rl-env 默认场景）。</summary>
    private static Scenario MujocoScenario(int seed = 42, double duration = 120.0)
    {
        var path = ScenarioPath();
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path),
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new InvalidOperationException($"scenario '{path}' failed to deserialize");
        return scenario with { Seed = seed, Field = scenario.Field with { MatchDuration = duration } };
    }

    private static string ScenarioPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "scenarios", "wushu-ring-2026-mujoco.json"));

    [Fact]
    public void Preroll_Seed42_StopsAtScoreBlockWithTheDocumentedEntryTickAndLockedTarget()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var engine = MatchEngineHost.Create(MujocoScenario());
        engine.Arm();
        var result = ScoreBlockExhibition.ArmAndPreroll(engine);

        Assert.False(result.NoScoreBlock);
        Assert.Null(result.Reason);
        Assert.True(result.HasTarget);
        // implement.md 批 1 冒烟对照: seed 42 → entry_tick=281, 目标=增益块。
        Assert.Equal(281, result.EntryTick);
        Assert.Equal(result.EntryTick, result.PrerollTicks);
        Assert.Equal(engine.TickIndex, result.PrerollTicks);
        Assert.Equal("增益块", engine.Blocks[result.TargetIndex].Name);
        Assert.Equal(FsmState.ScoreBlock, engine.Us.Fsm.State);
    }

    [Fact]
    public void HandoffObservation_IsBitIdenticalToRlEnvResetObs()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (rlEnvObs, rlEnvEntryTick, rlEnvTargetName) = RunRlEnvReset(seed: 42);
        using var engine = MatchEngineHost.Create(MujocoScenario());
        engine.Arm();
        var handoff = ScoreBlockExhibition.ArmAndPreroll(engine);
        Assert.True(handoff.HasTarget);

        var observation = ScoreBlockExhibition.BuildObservation(engine, handoff.TargetIndex,
            engine.Scenario.Field.Platform, engine.Scenario.Field.MatchDuration,
            handoff.EntrySnapshot.Robots[RoleNames.Us].OnPlatform, handoff.EntrySnapshot.Timer);

        Assert.Equal(rlEnvEntryTick, handoff.EntryTick);
        Assert.Equal(rlEnvTargetName, engine.Blocks[handoff.TargetIndex].Name);
        Assert.Equal(11, observation.Length);
        // 同一实现 ⇒ 逐位一致; 这里同时覆盖 9+2 顺序与 clamp 口径。
        Assert.Equal(rlEnvObs, observation);
        Assert.All(observation, value => Assert.InRange(value, -1.0, 1.0));
        // facts.md §2 实测样本（±1e-4）: 固定训练入口的数值口径。
        double[] documented = [0.173, 0.0865, 0.5625, 0.5625, 0.0, -0.0003, 1.0, 1.0, 0.8829, -0.7476, -0.7151];
        for (var i = 0; i < documented.Length; i++)
        {
            Assert.InRange(observation[i], documented[i] - 1e-4, documented[i] + 1e-4);
        }
    }

    [Fact]
    public void Preroll_GuardExhausted_ReportsNoScoreBlockWithoutRunningTheStrategy()
    {
        // legacy 物理: 5 tick 内不可能进入 SCORE_BLOCK, 用于钉 guard 边界语义。
        using var engine = MatchEngineHost.Create(Samples.Scenario());
        engine.Arm();
        var result = ScoreBlockExhibition.ArmAndPreroll(engine, maxTicks: 5);

        Assert.True(result.NoScoreBlock);
        Assert.Equal(ScoreBlockExhibition.NoScoreBlockReason, result.Reason);
        Assert.False(result.HasTarget);
        Assert.Equal(5, result.PrerollTicks);
        Assert.Equal(5, engine.TickIndex);
        Assert.NotEqual(FsmState.ScoreBlock, engine.Us.Fsm.State);
    }

    [Fact]
    public void Preroll_ScoreBlockWithoutValidTarget_ReportsReasonAndReachedTick()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var engine = MatchEngineHost.Create(MujocoScenario());
        engine.Arm();
        Assert.True(ScoreBlockExhibition.ArmAndPreroll(engine).HasTarget);

        // 兜底分支: 已进入 SCORE_BLOCK, 但 FSM 未选中块且没有任何合法台上增益块。
        engine.Us.Fsm.ScoreTarget = null;
        foreach (var block in engine.Blocks.Where(b => b.Kind == BlockKind.Buff))
        {
            block.Out = true;
        }

        var result = ScoreBlockExhibition.ArmAndPreroll(engine);

        Assert.True(result.NoScoreBlock);
        Assert.Equal(ScoreBlockExhibition.NoValidTargetReason, result.Reason);
        Assert.False(result.HasTarget);
        Assert.Equal(0, result.PrerollTicks);
        Assert.Equal((int)engine.TickIndex, result.EntryTick);
    }

    [Fact]
    public void Preroll_ZeroTicks_FailsFastWithoutTickingTheEngine()
    {
        using var engine = MatchEngineHost.Create(Samples.Scenario());
        engine.Arm();
        var result = ScoreBlockExhibition.ArmAndPreroll(engine, maxTicks: 0);

        Assert.True(result.NoScoreBlock);
        Assert.Equal(ScoreBlockExhibition.NoScoreBlockReason, result.Reason);
        Assert.Equal(0, result.PrerollTicks);
        Assert.Equal(0, engine.TickIndex);
    }

    [Fact]
    public void LockTarget_PrefersTheFsmChosenBlock_ThenFallsBackToTheFirstValidBuffBlock()
    {
        using var engine = MatchEngineHost.Create(Samples.Scenario());

        // FSM 已选中: 直通索引 (即使该块不在台上/已出界也不改选)。
        engine.Us.Fsm.ScoreTarget = engine.Blocks[1];
        Assert.Equal(1, ScoreBlockExhibition.LockTargetIndex(engine));
        engine.Blocks[1].Out = true;
        Assert.Equal(1, ScoreBlockExhibition.LockTargetIndex(engine));

        // ScoreTarget 为空: 取第一个"台上未出界的增益块"。
        engine.Us.Fsm.ScoreTarget = null;
        Assert.Equal(0, ScoreBlockExhibition.LockTargetIndex(engine));

        // 所有增益块都出界/不在台上且 FSM 未锁定 ⇒ 无可交接目标。
        foreach (var block in engine.Blocks.Where(b => b.Kind == BlockKind.Buff))
        {
            block.Out = true;
        }
        Assert.Equal(-1, ScoreBlockExhibition.LockTargetIndex(engine));

        // FSM 选中的块已不在 Blocks 列表: 回退到台上增益块规则。
        var orphan = new BlockRuntime { Kind = BlockKind.Buff, Name = "外块", X = 1.9, Y = 1.9 };
        engine.Us.Fsm.ScoreTarget = orphan;
        foreach (var block in engine.Blocks.Where(b => b.Kind == BlockKind.Buff))
        {
            block.Out = false;
        }
        Assert.Equal(0, ScoreBlockExhibition.LockTargetIndex(engine));
    }

    [Fact]
    public void AppendOwnPosition_AppendsNormalizedPositionAfterTheExistingNineValues()
    {
        var baseObservation = Enumerable.Range(1, 9).Select(value => value / 10.0).ToArray();
        var platform = new Region { MinX = 1.0, MinY = 2.0, MaxX = 5.0, MaxY = 6.0 };

        var observation = ScoreBlockExhibition.AppendOwnPositionObservation(baseObservation,
            ownX: 5.0, ownY: 2.0, platform: platform);

        Assert.Equal(11, observation.Length);
        Assert.Equal(baseObservation, observation[..9]);
        Assert.Equal(1.0, observation[9], 12);
        Assert.Equal(-1.0, observation[10], 12);
    }

    [Fact]
    public void AppendOwnPosition_ClampsToPlatformSideRange_AndRejectsWrongBaseLength()
    {
        var platform = new Region { MinX = 1.0, MinY = 2.0, MaxX = 5.0, MaxY = 6.0 };

        var observation = ScoreBlockExhibition.AppendOwnPositionObservation(new double[9],
            ownX: 100.0, ownY: -100.0, platform: platform);

        Assert.Equal(1.0, observation[9]);
        Assert.Equal(-1.0, observation[10]);
        Assert.Throws<ArgumentException>(() => ScoreBlockExhibition.AppendOwnPositionObservation(
            new double[8], ownX: 0.0, ownY: 0.0, platform: platform));
    }

    [Fact]
    public void AfterHandoff_UsStaysOnTheFsmUntilAnExternalActionIsInjected()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var engine = MatchEngineHost.Create(MujocoScenario());
        engine.Arm();
        var handoff = ScoreBlockExhibition.ArmAndPreroll(engine);
        Assert.True(handoff.HasTarget);

        // 交接前: us 参数为 null ⇒ 仍由内置 FSM 决策, 不切 Manual。
        engine.Tick(null, null);
        Assert.False(engine.Us.Fsm.Manual);
        Assert.NotEqual(FsmState.Manual, engine.Us.Fsm.State);

        // 注入外部动作 ⇒ 内核把该角色切 Manual (MatchEngine.StepSimExt 契约)。
        engine.Tick(RobotAction.Zero, null);
        Assert.True(engine.Us.Fsm.Manual);
        Assert.Equal(FsmState.Manual, engine.Us.Fsm.State);
    }

    /// <summary>跑一次 rl-env reset, 返回其 obs/entry_tick/target_name（Console 重定向口径同 RlEnvCommandTests）。</summary>
    private static (double[] Obs, int EntryTick, string TargetName) RunRlEnvReset(int seed)
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader($"{{\"op\":\"reset\",\"seed\":{seed}}}\n{{\"op\":\"close\"}}\n");
        using var output = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            Assert.Equal(0, RlEnvCommand.Run(["--scenario", ScenarioPath()]));
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        var line = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(l => l.Contains("\"type\":\"reset\""));
        using var response = JsonDocument.Parse(line);
        var root = response.RootElement;
        var obs = root.GetProperty("obs").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var info = root.GetProperty("info");
        return (obs, info.GetProperty("entry_tick").GetInt32(), info.GetProperty("target_name").GetString()!);
    }
}
