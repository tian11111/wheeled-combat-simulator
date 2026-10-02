using System.Text.Json;
using Sim.Cli;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// `match --start-at score_block` 展演装配: 共享缝预推进 → 交接 → 我方外部策略
/// （obs 注入 11 维 rlObservation）/对手内置 FSM; 未进入口 = no_score_block 且不调策略;
/// 默认关不带任何展演元数据。
/// 与 CLI/控制器进程 fixture 同属串行集合: 本类会起子进程并跑整场 MuJoCo 展演,
/// 不得与墙钟敏感的测试(如 TrainingResetPerformanceTests)并行抢占 CPU。
/// </summary>
[Collection("cli-console")]
public class MatchRunnerExhibitionTests
{
    private static string EchoControllerExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EchoController.exe");
        Assert.True(File.Exists(exe), $"EchoController fixture missing: {exe}");
        return exe;
    }

    private static Scenario MujocoScenario(int seed, double duration = 120.0)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "scenarios", "wushu-ring-2026-mujoco.json"));
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path),
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new InvalidOperationException($"scenario '{path}' failed to deserialize");
        return scenario with { Seed = seed, Field = scenario.Field with { MatchDuration = duration } };
    }

    [Fact]
    public void ExhibitionRunner_PrerollsThenHandsOffToTheExternalPolicy()
    {
        if (!OperatingSystem.IsWindows()) return;

        // rlguard: 控制器只在帧真的携带 11 维有限 rlObservation 时才应答, 否则退出
        // ⇒ 下面的 faults==0 同时钉住 "每帧都注入了 rlObservation" 这条数据流
        // (删掉 BuildExhibitionObservation 的注入赋值, 本用例即 red)。
        var result = MatchRunner.Run(MujocoScenario(seed: 42), new MatchRunner.Options
        {
            StartAtScoreBlock = true,
            ControllerUs = $"\"{EchoControllerExe()}\" rlguard",
            TimeoutMs = 500,
        });

        Assert.NotNull(result.Exhibition);
        Assert.True(result.Exhibition!.Handoff);
        Assert.False(result.Exhibition.GateEvidenceEligible);
        // 交接帧与 rl-env 训练入口同源(同一共享缝), 且能到达该 tick 本身就证明预推进期间
        // 我方没有任何外部动作(任何非 null 动作都会把角色切 Manual, 永远进不了 SCORE_BLOCK)。
        // 域标定轮(10-02)重录: 281 → 369(摩擦/补偿标定后 SEARCH 轨迹变化)。
        Assert.Equal(369, result.Exhibition.EntryTick);
        Assert.Equal("增益块", result.Exhibition.TargetName);
        Assert.True(result.Exhibition.TargetIndex >= 0);
        Assert.True(result.Ticks > result.Exhibition.EntryTick);
        Assert.Equal(0, result.UsFaults);
        Assert.Equal(0, result.ThemFaults);
    }

    [Fact]
    public void ExhibitionRunner_WithoutScoreBlock_ReturnsSummaryWithoutCallingThePolicy()
    {
        // 三块全部落在台下: FSM 永远不会进入 SCORE_BLOCK; 2 s 赛时让比赛在 guard 内结束。
        var scenario = Samples.Scenario() with
        {
            Field = Samples.Scenario().Field with { MatchDuration = 2.0 },
            Blocks =
            [
                new BlockSpec { Kind = BlockKind.Buff, X = 0.2, Y = 0.2 },
                new BlockSpec { Kind = BlockKind.Buff, X = 0.2, Y = 3.6 },
                new BlockSpec { Kind = BlockKind.Debuff, X = 3.6, Y = 3.6 },
            ],
        };

        var result = MatchRunner.Run(scenario, new MatchRunner.Options
        {
            StartAtScoreBlock = true,
            // die 模式: 任何一次 Decide 都会计 fault, 用来证明失败路径没有调用策略。
            ControllerUs = $"\"{EchoControllerExe()}\" die",
            TimeoutMs = 100,
        });

        Assert.NotNull(result.Exhibition);
        Assert.False(result.Exhibition!.Handoff);
        Assert.Equal(ScoreBlockExhibition.NoScoreBlockReason, result.Exhibition.Reason);
        Assert.Null(result.Exhibition.TargetName);
        Assert.Equal(0, result.UsFaults);
        Assert.Equal(result.Exhibition.EntryTick, result.Ticks);
        Assert.Equal(ScoreBlockExhibition.NoScoreBlockReason, result.DoneReason);
        Assert.Empty(result.EventFingerprints);
    }

    [Fact]
    public void ExhibitionInjectionGuard_IsNotVacuous_ProbeFaultsWithoutTheField()
    {
        // 反向对照: 普通(非展演)路径的 obs 不带 rlObservation, 同一个守卫进程必须
        // 退出 → 桥计 fault。若去掉这个对照, 展演用例里的 faults==0 可能只是"守
        // 卫从不失败"的空转断言。
        var scenario = Samples.Scenario() with
        {
            Field = Samples.Scenario().Field with { MatchDuration = 0.5 },
        };

        var result = MatchRunner.Run(scenario, new MatchRunner.Options
        {
            ControllerUs = $"\"{EchoControllerExe()}\" rlguard",
            TimeoutMs = 100,
        });

        Assert.Null(result.Exhibition);
        Assert.True(result.UsFaults > 0, "rlguard 必须能检出缺少 rlObservation 的帧");
    }

    [Fact]
    public void DefaultRunner_ReportsNoExhibitionMetadata()
    {
        var scenario = Samples.Scenario() with
        {
            Field = Samples.Scenario().Field with { MatchDuration = 1.0 },
        };

        var result = MatchRunner.Run(scenario, new MatchRunner.Options());

        Assert.Null(result.Exhibition);
        Assert.True(result.Ticks > 0);
    }
}
