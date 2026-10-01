using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 批2 行为冒烟（implement.md 批2："官方场景 3-seed：掉台后能走完回归流程"）：
/// 官方 legacy 场景（wushu-ring-2026.json，2D PhysicsWorld，无 MuJoCo 依赖）+
/// 迷你引擎循环（采样→MbriFsm→物理步进，镜像 MatchEngine 主干；正式接线在批3）。
/// 人为掉台（搬运出台面）后断言 reentry 走完 回归流程
/// （fall×3 → 前头分派 ADC_CORRECT → REVERSE → SAFE_STOP），再断言灰度恢复
/// （人工上台等价物，对应真车 SAFE_STOP 等待"灰度恢复（人工/后续上台）"，
/// reentry.py:22）后仲裁回到重置后的巡台。
/// 忠实性边界（reentry.py:8 "当前流程到倒车脱离为止，主动上台动作尚未实现"）：
/// 真车回归流程不含主动上台动作，冒烟断言的是流程走完与仲裁闭环。
/// </summary>
public sealed class MbriReentrySmokeTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, parts[0], parts[1])))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static Scenario LoadScenario(string fileName)
        => ProtocolJson.Deserialize<Scenario>(File.ReadAllText(RepoFile("scenarios", fileName)))
           ?? throw new InvalidOperationException($"{fileName} did deserialize to null");

    [Theory]
    [InlineData(42)]
    [InlineData(7)]
    [InlineData(2026)]
    public void Smoke_OfficialField_ForcedFall_CompletesReentryFlow_ThenRecoversToPatrol(long seed)
    {
        var scenario = LoadScenario("wushu-ring-2026.json");
        var field = new FieldModel(scenario.Field);
        var parameters = SimParameters.FromDictionary(scenario.Parameters);
        var us = new RobotRuntime { Role = RoleNames.Us, Name = "我方" };
        var them = new RobotRuntime { Role = RoleNames.Them, Name = "对手" };
        var blocks = new List<BlockRuntime>();
        var events = new EventBus();
        var physics = new PhysicsWorld(field, parameters, us, them, blocks, events, 0.0, 0.0);
        long step = 0;
        var sampler = new SensorSampler(field, parameters, us, them, blocks, seed, () => step, physics);
        var fsm = new MbriFsmController(events, scenario.Field.TickSeconds);
        // 迷你引擎与内置一致的前提：物理只积分 Armed/Manual 的机器人（Physics.cs:1108）。
        us.Fsm.Armed = true;
        them.Fsm.Armed = true;

        // 初始位姿：北走道背对擂台（开局后退=向擂台倒车，真车 START_REVERSE 台面语义）。
        us.X = 1.9;
        us.Y = 3.3;
        us.Th = Math.PI / 2;
        them.X = 0.5;
        them.Y = 0.5;
        them.Th = 0;
        fsm.Arm();
        for (long t = 0; t < 120; t++)
        {
            step++;
            sampler.SampleSensorsFor(us);
            sampler.SampleSensorsFor(them);
            fsm.TickFor(us, t);
            physics.Step(scenario.Field.TickSeconds);
            us.Fsm.SimT += scenario.Field.TickSeconds;
            them.Fsm.SimT += scenario.Field.TickSeconds;
        }

        // 人为掉台：搬到北走道、面向擂台墙（f 前向可见台沿，四路灰度全在走道）。
        us.X = 1.9;
        us.Y = 3.35;
        us.Th = -Math.PI / 2;
        us.Vx = 0;
        us.Vy = 0;
        var sawAdcCorrect = false;
        var sawReverse = false;
        var sawSafeStop = false;
        var reentryOwned = false;
        for (long t = 120; t < 620; t++)
        {
            step++;
            sampler.SampleSensorsFor(us);
            sampler.SampleSensorsFor(them);
            fsm.TickFor(us, t);
            physics.Step(scenario.Field.TickSeconds);
            us.Fsm.SimT += scenario.Field.TickSeconds;
            them.Fsm.SimT += scenario.Field.TickSeconds;
            sawAdcCorrect |= fsm.MbriState == "ADC_CORRECT";
            sawReverse |= fsm.MbriState == "REVERSE";
            sawSafeStop |= fsm.MbriState == "SAFE_STOP";
            reentryOwned |= fsm.ReentryActive;
        }
        Assert.True(reentryOwned, "掉台后 reentry 应接管");
        Assert.True(sawAdcCorrect, "应进入 ADC_CORRECT（前头红外分派）");
        Assert.True(sawReverse, "应进入 REVERSE（矫正完成倒车）");
        Assert.True(sawSafeStop, "应到达 SAFE_STOP（流程终点）");
        // 无人复位有界重武装（评审 finding 1 修复）后流程会重试：500 tick 窗口末
        // 仍由 reentry 接管（可能处于重试途中的任一状态），但不被吸收为静止。
        Assert.NotEqual("WAIT", fsm.Reentry.State);
        Assert.True(fsm.ReentryActive);

        // 人工恢复等价物：搬回台面（真车 SAFE_STOP 等待"灰度恢复（人工/后续上台）"，
        // reentry.py:22）。上台动作本身不属于回归流程（reentry.py:8）。
        // 面向北、位于台面中北段：若重试正处 REVERSE，倒车向南且 f（北沿）1.5m 内
        // 丢失 → "倒车完成" 停在台上，不会倒出台。
        us.X = 1.9;
        us.Y = 2.4;
        us.Th = Math.PI / 2;
        us.Vx = 0;
        us.Vy = 0;
        Assert.True(physics.OnStage(us), "恢复位姿应在台上");
        var sawFreshPatrolWarmup = false;
        var patrolResumed = false;
        var sawReentryWait = false;
        var sawReentryReleased = false;
        for (long t = 620; t < 780; t++)
        {
            step++;
            sampler.SampleSensorsFor(us);
            sampler.SampleSensorsFor(them);
            fsm.TickFor(us, t);
            physics.Step(scenario.Field.TickSeconds);
            us.Fsm.SimT += scenario.Field.TickSeconds;
            them.Fsm.SimT += scenario.Field.TickSeconds;
            sawFreshPatrolWarmup |= fsm.MbriState == "WARMUP"; // 重置后新巡台的预热签名
            patrolResumed |= fsm.MbriState is "CRUISE" or "MEDIUM_CRUISE"
                or "EDGE_AVOID" or "EDGE_TURN" or "WHITE_ESCAPE"
                or "RECOVER_FORWARD" or "RECOVER_BACKWARD";
            sawReentryWait |= fsm.Reentry.State == "WAIT";
            sawReentryReleased |= !fsm.ReentryActive;
        }
        // 断言窗口内观察到的仲裁闭环（此后巡逻若再掉台由有界重武装循环接管，
        // 属街机式再触发而非本冒烟断言的仲裁交还）。
        Assert.True(sawReentryWait, "灰度恢复后 reentry 应回 WAIT");
        Assert.True(sawReentryReleased, "接管电平应交还");
        Assert.True(sawFreshPatrolWarmup, "回归完成后巡台应重置（新实例 WARMUP 预热）");
        Assert.True(patrolResumed, "控制权应交还巡台状态机");
        Assert.Contains(events.Events, e => e.Msg.StartsWith("[mbri-reentry]"));
    }
}
