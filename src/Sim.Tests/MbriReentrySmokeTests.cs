using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 批2 行为冒烟（implement.md 批2："官方场景 3-seed：掉台后能走完回归流程"；
/// A2 扩展：倒车脱离后仍掉台 → 有界回台冲台；A4 升级：方向裁决后官方场
/// "面向擂台"位姿应<b>自主回台成功</b>，不再落入 A2 时代的"背台倒车 → 重试耗尽
/// → SAFE_STOP"走道死螺旋——失败分支由
/// <see cref="MbriReentryTests.Matrix_Remount_BoundedRetriesExhausted_SafeStop"/>
/// 在注入域钉板，本冒烟钉自主恢复全链）：
/// 官方 legacy 场景（wushu-ring-2026.json，2D PhysicsWorld，无 MuJoCo 依赖）+
/// 迷你引擎循环（采样→MbriFsm→物理步进，镜像 MatchEngine 主干；正式接线在批3）。
/// 人为掉台（搬运出台面、面向擂台）后断言 reentry 走完
/// fall×3 → 前头分派 ADC_CORRECT → REVERSE → REMOUNT（A4：f 前亮=正对台 → 前向冲台）
/// → fall-domain 灰度恢复 = 回台成功回 WAIT → 仲裁重置巡台并交还控制权。
/// 忠实性边界（reentry.py:8 "当前流程到倒车脱离为止，主动上台动作尚未实现"）：
/// REMOUNT 是仿真无人对局的有界补全；A4 方向裁决复用真车红外/灰度语义
/// （f=edge_target 只见台沿不见围栏，Sensors.cs Digital 桥），不发明新传感器。
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
        // A4：此位姿下 REMOUNT 方向裁决=前向冲台 → 预期自主回台成功。
        us.X = 1.9;
        us.Y = 3.35;
        us.Th = -Math.PI / 2;
        us.Vx = 0;
        us.Vy = 0;
        var sawAdcCorrect = false;
        var sawReverse = false;
        var sawRemount = false;
        var sawReentryWait = false;
        var sawReentryReleased = false;
        var sawFreshPatrolWarmup = false;
        var patrolResumed = false;
        var reentryOwned = false;
        for (long t = 120; t < 800; t++)
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
            sawRemount |= fsm.MbriState == "REMOUNT";
            reentryOwned |= fsm.ReentryActive;
            sawReentryWait |= fsm.Reentry.State == "WAIT";
            sawReentryReleased |= sawRemount && !fsm.ReentryActive;
            sawFreshPatrolWarmup |= sawReentryReleased && fsm.MbriState == "WARMUP"; // 重置后新巡台预热签名
            patrolResumed |= sawReentryReleased && fsm.MbriState is "CRUISE" or "MEDIUM_CRUISE"
                or "EDGE_AVOID" or "EDGE_TURN" or "WHITE_ESCAPE"
                or "RECOVER_FORWARD" or "RECOVER_BACKWARD";
        }
        Assert.True(reentryOwned, "掉台后 reentry 应接管");
        Assert.True(sawAdcCorrect, "应进入 ADC_CORRECT（前头红外分派）");
        Assert.True(sawReverse, "应进入 REVERSE（矫正完成倒车）");
        Assert.True(sawRemount, "A2：倒车脱离后仍掉台应进入 REMOUNT 有界回台冲台");
        // A4 断言：面向擂台位姿自主回台（不再需要"人工搬回"救场步骤）。
        Assert.True(sawReentryWait, "A4：REMOUNT 前向冲台应自主回台成功（reentry 回 WAIT）");
        Assert.True(sawReentryReleased, "A4：回台成功后接管电平应交还");
        Assert.True(sawFreshPatrolWarmup, "回归完成后巡台应重置（新实例 WARMUP 预热）");
        Assert.True(patrolResumed, "A4：控制权应自主交还巡台状态机");
        Assert.Contains(events.Events, e => e.Msg.StartsWith("[mbri-reentry]"));
    }
}
