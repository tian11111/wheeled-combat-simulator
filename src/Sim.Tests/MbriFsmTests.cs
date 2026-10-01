using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 批1 MbriFsm 骨架单测：仲裁入口（未发令/START_REVERSE 独占/patrol 兜底）、
/// 快照 State 映射、事件流格式，以及双跑确定性（同输入快照指纹逐位一致，
/// implement.md 批1 第 5/6 条）。零 IO/零时钟/零随机：tick 由测试显式注入。
/// </summary>
public sealed class MbriFsmTests
{
    private static RobotRuntime NewRobot(string role = RoleNames.Us) => new()
    {
        Role = role,
        Name = role == RoleNames.Us ? "我方" : "对手",
    };

    /// <summary>设置仿真灰度逻辑别名（0-1000 官方语义；Sensors.cs ClampSensorValue 域）。</summary>
    private static void SetGray(RobotRuntime r, double gFront, double gRear, double gLeft, double gRight)
    {
        r.Sens["gF"] = gFront;
        r.Sens["gB"] = gRear;
        r.Sens["gL"] = gLeft;
        r.Sens["gR"] = gRight;
    }

    // ---------- 仲裁骨架 ----------

    [Fact]
    public void NotArmed_IdlesAtWaitStart()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetGray(robot, 1000, 1000, 1000, 1000);
        fsm.TickFor(robot, 0);
        Assert.Equal("IDLE", fsm.MbriState);
        Assert.Equal(FsmState.WaitStart, robot.Fsm.State);
        Assert.Equal(0.0, robot.V, 12);
        Assert.Equal(0.0, robot.W, 12);
        Assert.Equal("等待发令", robot.Fsm.Action);
    }

    [Fact]
    public void StartReverse_Exclusive36Ticks_ThenPatrol()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetGray(robot, 1000, 1000, 1000, 1000);
        fsm.Arm();
        // tick 0..35：-1000×1.8s=36 tick 独占（main.py:131-147；config.py:163-166）。
        for (long t = 0; t <= 35; t++)
        {
            fsm.TickFor(robot, t);
            Assert.Equal("START_REVERSE", fsm.MbriState);
            Assert.Equal(FsmState.MountRing, robot.Fsm.State);
            Assert.Equal(-0.896, robot.V, 9); // -1000×k
            Assert.Equal(0.0, robot.W, 12);
            Assert.Equal("开局后退上台", robot.Fsm.Action);
        }
        // tick 36：独占结束 → 巡台兜底。期间 patrol 一直被喂帧（滤波已预热），
        // 故首拍即 MEDIUM_CRUISE 而非 WARMUP（main.py:135-137 预热语义）。
        fsm.TickFor(robot, 36);
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState);
        Assert.Equal(FsmState.Search, robot.Fsm.State);
        Assert.Equal(0.3584, robot.V, 9); // 400×k
        Assert.Equal(0.0, robot.W, 12);
        Assert.Equal("按区域分级巡航", robot.Fsm.Action);
    }

    [Fact]
    public void StartReverse_UntilTickFollowsTickSeconds()
    {
        // tickSeconds=0.1 → 1.8s=18 tick 独占（时长→tick 按 tickSeconds 换算）；
        // 独占窗锚定首个 armed tick（main.py: _start_reverse_until = now + seconds）。
        var events = new EventBus();
        var fsm = new MbriFsmController(events, 0.1);
        var robot = NewRobot();
        SetGray(robot, 1000, 1000, 1000, 1000);
        fsm.Arm();
        for (long t = 0; t <= 17; t++)
        {
            fsm.TickFor(robot, t);
            Assert.Equal("START_REVERSE", fsm.MbriState);
        }
        fsm.TickFor(robot, 18);
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState);

        // 锚点=首个 armed tick：从 tick 100 起发令 → 独占到 117，118 交出。
        var events2 = new EventBus();
        var fsm2 = new MbriFsmController(events2, 0.1);
        var robot2 = NewRobot();
        SetGray(robot2, 1000, 1000, 1000, 1000);
        fsm2.Arm();
        fsm2.TickFor(robot2, 100);
        Assert.Equal("START_REVERSE", fsm2.MbriState);
        fsm2.TickFor(robot2, 117);
        Assert.Equal("START_REVERSE", fsm2.MbriState);
        fsm2.TickFor(robot2, 118);
        Assert.Equal("MEDIUM_CRUISE", fsm2.MbriState);
    }

    [Fact]
    public void PatrolWiring_SimGrayToAdc_DrivesEdgeAvoidRecoverMapping()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetGray(robot, 1000, 1000, 1000, 1000);
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState);
        // 全场走道灰度 0 → 仿射后 zone=0；filtered 一帧滞后（中值窗口）→
        // 早期前向风险在 tick 38 触发 → EDGE_AVOID 后退。
        SetGray(robot, 0, 0, 0, 0);
        fsm.TickFor(robot, 37);
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState); // 滞后帧
        fsm.TickFor(robot, 38);
        Assert.Equal("EDGE_AVOID", fsm.MbriState);
        Assert.Equal(FsmState.Recover, robot.Fsm.State); // 快照语义近似映射
        Assert.Equal(-0.3584, robot.V, 9);               // -400×k
        Assert.Equal(0.0, robot.W, 12);
        Assert.Equal("前向灰度趋势变暗，提前离边", robot.Fsm.Action);
    }

    [Fact]
    public void SnapshotStateMapping_SemanticApproximationTable()
    {
        Assert.Equal(FsmState.MountRing, MbriFsmController.SnapshotState("START_REVERSE"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("WARMUP"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("CRUISE"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("MEDIUM_CRUISE"));
        Assert.Equal(FsmState.Recover, MbriFsmController.SnapshotState("EDGE_AVOID"));
        Assert.Equal(FsmState.Recover, MbriFsmController.SnapshotState("EDGE_TURN"));
        Assert.Equal(FsmState.Recover, MbriFsmController.SnapshotState("WHITE_ESCAPE"));
        Assert.Equal(FsmState.Recover, MbriFsmController.SnapshotState("RECOVER_FORWARD"));
        Assert.Equal(FsmState.Recover, MbriFsmController.SnapshotState("RECOVER_BACKWARD"));
        Assert.Equal(FsmState.Incapacitated, MbriFsmController.SnapshotState("SENSOR_STOP"));
        Assert.Equal(FsmState.WaitStart, MbriFsmController.SnapshotState("IDLE"));
    }

    [Fact]
    public void Events_AlignBuiltinFsmLogFormat()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetGray(robot, 1000, 1000, 1000, 1000);
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        var messages = events.Events.Select(e => e.Msg).ToList();
        // 与内置 FSM 同格式（[前缀] 状态: 理由），前缀 [mbri] 以区分来源。
        Assert.Contains(messages, m => m.StartsWith("[mbri] START_REVERSE:") && m.Contains("开局后退上台"));
        Assert.Contains(messages, m => m.StartsWith("[mbri] START_REVERSE 完成"));
        Assert.Contains(messages, m => m == "[mbri] MEDIUM_CRUISE: 按区域分级巡航");
        var fsmEvents = events.Events.Where(e => e.Kind == EventKind.Fsm).ToList();
        Assert.NotEmpty(fsmEvents);
        // 结构化 data 携带状态/理由/命令/换算结果（可观测性对齐）。
        var cruise = events.Events.First(e => e.Msg == "[mbri] MEDIUM_CRUISE: 按区域分级巡航");
        Assert.NotNull(cruise.Data);
    }

    // ---------- 双跑确定性（同输入快照指纹逐位） ----------

    /// <summary>确定性灰度脚本（g 域，显式相位表，无随机源）。</summary>
    private static double ScriptGray(long tick) => tick switch
    {
        < 37 => 1000,                                   // START_REVERSE 期间台面中心
        < 40 => 1000,
        < 55 => 0,                                      // 掉入全暗 → early → EDGE_AVOID → EDGE_TURN
        < 80 => 1000,                                   // 回中心 → 转向完成 → RECOVER → 巡航
        < 90 => 350,                                    // 近边带（g=350 → zone 0.35 边界附近抖动）
        < 120 => 760,                                   // 中间带（滞回区间）
        < 150 => 0,
        < 200 => 1000,
        < 230 => 300,                                   // 黑带常驻
        _ => 650,                                       // 中央红区
    };

    private static string Fingerprint(MbriFsmController fsm, RobotRuntime robot, long tick)
        => $"{tick}|{fsm.MbriState}|{fsm.Patrol.State}|{fsm.Patrol.Reason}|{fsm.Patrol.Command.Left}," +
           $"{fsm.Patrol.Command.Right}|{robot.V:R}|{robot.W:R}|{robot.Fsm.State}|{robot.Fsm.Action}";

    [Fact]
    public void DualRun_MbriFsm_SnapshotFingerprintsIdentical()
    {
        var runs = new List<(List<string> Prints, List<string> Messages)>();
        for (var run = 0; run < 2; run++)
        {
            var events = new EventBus();
            var fsm = new MbriFsmController(events);
            var robot = NewRobot();
            var prints = new List<string>();
            fsm.Arm();
            for (long t = 0; t < 300; t++)
            {
                var g = ScriptGray(t);
                SetGray(robot, g, g, g, g);
                fsm.TickFor(robot, t);
                prints.Add(Fingerprint(fsm, robot, t));
            }
            runs.Add((prints, events.Events.Select(e => e.Msg).ToList()));
        }
        Assert.Equal(runs[0].Prints, runs[1].Prints);
        Assert.Equal(runs[0].Messages, runs[1].Messages);
        // 冒烟：脚本确实驱动了多个状态（非恒一状态空转）。
        var states = runs[0].Prints.Select(p => p.Split('|')[1]).Distinct().ToList();
        Assert.Contains("START_REVERSE", states);
        Assert.Contains("MEDIUM_CRUISE", states);
        Assert.Contains("EDGE_AVOID", states);
        Assert.Contains("EDGE_TURN", states);
        Assert.Contains("RECOVER_FORWARD", states);
    }

    [Fact]
    public void DualRun_MbriPatrol_ADCDomainResultSequencesIdentical()
    {
        var runs = new List<List<string>>();
        for (var run = 0; run < 2; run++)
        {
            var p = new MbriPatrol(TickSeconds());
            var prints = new List<string>();
            for (long t = 0; t < 400; t++)
            {
                var sample = ScriptAdc(t);
                var r = p.Update(sample, t, healthy: (t / 97) % 3 != 1); // 周期性 healthy 抖动相位
                prints.Add($"{t}|{r.State}|{r.Reason}|{r.Left},{r.Right}|{r.RiskSensor}|" +
                           $"{r.TurnDirection}|{r.TurnAngle:R}|{r.TurnDuration:R}|{r.SpeedLevel}|" +
                           $"{r.Observation.ZoneScore:R}|{r.ShovelPreheat}");
            }
            runs.Add(prints);
        }
        Assert.Equal(runs[0], runs[1]);
        var states = runs[0].Select(p => p.Split('|')[1]).Distinct().ToList();
        Assert.Contains("WARMUP", states);
        Assert.Contains("MEDIUM_CRUISE", states);
        Assert.Contains("EDGE_AVOID", states);
        Assert.Contains("EDGE_TURN", states);
        Assert.Contains("WHITE_ESCAPE", states);
        Assert.Contains("RECOVER_BACKWARD", states);
        Assert.Contains("SENSOR_STOP", states);
    }

    private static double TickSeconds() => 0.05;

    /// <summary>ADC 域确定性脚本（含白边相位；显式相位表，无随机源）。</summary>
    private static MbriGraySample ScriptAdc(long tick)
    {
        var center = MbriGrayCalibration.SimSampleToAdc(1000, 1000, 1000, 1000);
        var dark = MbriGrayCalibration.SimSampleToAdc(0, 0, 0, 0);
        return (tick / 20) switch
        {
            0 or 1 => center,
            2 or 3 => MbriGrayCalibration.SimSampleToAdc(0, 0, 0, 0),                    // 前暗 → early
            4 or 5 => center,
            6 => FrontWhiteSample(),                                                     // 白边循环
            7 => FrontWhiteSample(),
            8 => FrontWhiteSample(),
            9 => FrontWhiteSample(),
            10 or 11 => FrontWhiteSample(),
            12 or 13 or 14 => center,
            15 => dark,
            16 => dark,
            17 => dark,
            18 => center,
            _ => center,
        };

        static MbriGraySample FrontWhiteSample()
            => new(1560.0, MbriGrayCalibration.EdgeReference["rear"],
                   MbriGrayCalibration.EdgeReference["left"], MbriGrayCalibration.EdgeReference["right"]);
    }
}
