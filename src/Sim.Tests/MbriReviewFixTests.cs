using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 批2 评审修复单测（FixReceipt 自验）：
/// finding 1（high）——开场走道合法赛前位姿在 START_REVERSE 预热窗内误触发 reentry
/// 并冻结至终场：修复=预热仅喂灰度滤波不推进状态机（main.py:136 意图）+ SAFE_STOP/
/// IR_WAIT 在仿真无人对局的有界重新武装（2s）避免吸收态（reentry.py:22 等人工出口保留）。
/// finding 2（medium）——分派被方块/对手决定 + 对准模型 diff≡0 矫正分支集成不可达：
/// 修复=四路对角不进入分派（仿真对角是 target 模式非墙感）+ 模拟红外对每侧取
/// max(f, 对角)，信号由 f 保证、diff 由对角不对称量驱动（矫正分支集成可执行）。
/// </summary>
public sealed class MbriReviewFixTests
{
    // ---------- finding 1：预热窗误接管 ----------

    [Fact]
    public void Finding1_StartReversePreheat_DoesNotAdvanceReentry_NoFalseTakeover()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方" };
        // 评审复现场景：开场即在走道（合法赛前位姿，随后由 START_REVERSE 倒车上台），
        // 面向擂台墙（f 亮，诱导旧实现的分派）。
        robot.Sens["gF"] = 0;
        robot.Sens["gB"] = 0;
        robot.Sens["gL"] = 0;
        robot.Sens["gR"] = 0;
        robot.Sens["f"] = 0.9;
        robot.Sens["r"] = 0;
        robot.Sens["dLF"] = 0;
        robot.Sens["dRF"] = 0;
        robot.Sens["dLB"] = 0;
        robot.Sens["dRB"] = 0;
        fsm.Arm();
        for (long t = 0; t <= 35; t++) // 独占窗 = 首个 armed tick + 36（tick 36 起交巡台）
        {
            fsm.TickFor(robot, t);
            Assert.Equal("START_REVERSE", fsm.MbriState);   // 预热窗内不被 reentry 接管
            Assert.Equal("WAIT", fsm.Reentry.State);        // 状态机不推进（仅滤波预热）
            Assert.False(fsm.ReentryActive);
            Assert.Equal(-0.896, robot.V, 9);
        }
        Assert.DoesNotContain(events.Events, e => e.Msg.StartsWith("[mbri-reentry]"));
        // 窗后仍掉台（走道）→ 合法触发分派（修复只去掉预热门内的误触发）。
        for (long t = 37; t <= 40; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("ADC_CORRECT", fsm.MbriState);
        Assert.True(fsm.ReentryActive);
        Assert.Contains(events.Events, e => e.Msg.StartsWith("[mbri-reentry]"));
    }

    [Fact]
    public void Finding1_OnStageStartReverse_ThenPatrol_NoFreeze()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方" };
        robot.Sens["gF"] = 1000;
        robot.Sens["gB"] = 1000;
        robot.Sens["gL"] = 1000;
        robot.Sens["gR"] = 1000;
        robot.Sens["f"] = 0;
        fsm.Arm();
        for (long t = 0; t <= 40; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState); // 窗后交还巡台，控制器持续决策
        Assert.False(fsm.ReentryActive);
        for (long t = 41; t <= 200; t++)
        {
            fsm.TickFor(robot, t);
            Assert.NotEqual(0.0, Math.Max(Math.Abs(robot.V), Math.Abs(robot.W)));
        }
    }

    // ---------- finding 1：SAFE_STOP / IR_WAIT 有界重新武装 ----------

    [Fact]
    public void Finding1_SafeStop_HeadlessRearm_WaitsThenRetriesFlow()
    {
        var re = new MbriReentry(0.05);
        var center = MbriGrayCalibration.SimSampleToAdc(1000, 1000, 1000, 1000);
        var dark = new MbriGraySample(0, 0, 0, 0);
        for (long t = 0; t < 3; t++)
        {
            _ = re.Update(center, new MbriDigiIr(false, false, false, false, false, false, true), new MbriAnalogIr(1000, 1000, true), t);
        }
        // 触发 → TURN_180(1.2s=24) → APPROACH 超时(2s=40) → SAFE_STOP。
        for (long t = 3; t <= 6; t++)
        {
            _ = re.Update(dark, new MbriDigiIr(false, true, false, false, false, false, true), new MbriAnalogIr(1000, 1000, true), t);
        }
        Assert.Equal("TURN_180", re.State);
        _ = re.Update(dark, new MbriDigiIr(false, true, false, false, false, false, true), new MbriAnalogIr(1000, 1000, true), 31);
        Assert.Equal("ADC_APPROACH", re.State);
        _ = re.Update(dark, new MbriDigiIr(false, true, false, false, false, false, true), new MbriAnalogIr(1000, 1000, true), 71);
        Assert.Equal("SAFE_STOP", re.State);
        // 仍掉台：2s=40 tick 内保持（真车等人工语义），39 tick 仍未武装。
        _ = re.Update(dark, new MbriDigiIr(false, false, false, false, false, false, true), new MbriAnalogIr(0, 0, true), 110);
        Assert.Equal("SAFE_STOP", re.State);
        // 第 40 tick → 仿真无人复位：重新武装回 WAIT。
        _ = re.Update(dark, new MbriDigiIr(false, false, false, false, false, false, true), new MbriAnalogIr(0, 0, true), 111);
        Assert.Equal("WAIT", re.State);
        Assert.Contains("重新武装回归", re.Reason);
        // 仍暗 → 重新长出 fall 边沿 → 重走分派（重试而非吸收）。
        _ = re.Update(dark, new MbriDigiIr(true, false, false, false, false, false, true), new MbriAnalogIr(0, 0, true), 112);
        _ = re.Update(dark, new MbriDigiIr(true, false, false, false, false, false, true), new MbriAnalogIr(0, 0, true), 113);
        _ = re.Update(dark, new MbriDigiIr(true, false, false, false, false, false, true), new MbriAnalogIr(0, 0, true), 114);
        Assert.Equal("ADC_CORRECT", re.State);
    }

    [Fact]
    public void Finding1_IrWait_HeadlessRearm_WhenNoIrAppears()
    {
        var re = new MbriReentry(0.05);
        var center = MbriGrayCalibration.SimSampleToAdc(1000, 1000, 1000, 1000);
        var dark = new MbriGraySample(0, 0, 0, 0);
        var irNone = new MbriDigiIr(false, false, false, false, false, false, true);
        var analog = new MbriAnalogIr(1000, 1000, true);
        for (long t = 0; t < 3; t++)
        {
            _ = re.Update(center, irNone, analog, t);
        }
        for (long t = 3; t <= 6; t++)
        {
            _ = re.Update(dark, irNone, analog, t);
        }
        Assert.Equal("IR_WAIT", re.State); // 触发于 t=6
        _ = re.Update(dark, irNone, analog, 45);
        Assert.Equal("IR_WAIT", re.State); // elapsed 39 < 40
        _ = re.Update(dark, irNone, analog, 46);
        Assert.Equal("WAIT", re.State);    // elapsed 40 → 重新武装
        Assert.Contains("重新武装回归", re.Reason);
    }

    // ---------- finding 2：分派只认墙感 ----------

    [Fact]
    public void Finding2_DispatchIgnoresDiagonalTargets()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方" };
        // 走道 + 左前对角亮（方块/对手，非墙）→ 旧实现按"右侧红外亮"分派右转 90°；
        // 修复后对角不进分派 → 六路无墙感 → IR_WAIT。
        robot.Sens["gF"] = 0;
        robot.Sens["gB"] = 0;
        robot.Sens["gL"] = 0;
        robot.Sens["gR"] = 0;
        robot.Sens["f"] = 0;
        robot.Sens["r"] = 0;
        robot.Sens["dLF"] = 1.0;
        robot.Sens["dRF"] = 0;
        robot.Sens["dLB"] = 0;
        robot.Sens["dRB"] = 0;
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        for (long t = 37; t <= 40; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("IR_WAIT", fsm.MbriState);
        Assert.NotEqual("TURN_RIGHT_90", fsm.MbriState);
    }

    // ---------- finding 2：对准模型 diff 在集成路径可动 ----------

    [Fact]
    public void Finding2_AnalogPairIsLive_BiasCorrectionExecutesInIntegration()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方" };
        // 走道、面向擂台墙（f=0.9 → 前头分派进 ADC_CORRECT），左前对角亮（dLF=1.0）、
        // 右前对角暗 → 模拟对 left=8333 / right=7500 → diff=+833 > 63.5
        // → right_bias → "向右偏，向左矫正" command=(−400,+400)（旧桥接 diff≡0 永不执行）。
        robot.Sens["gF"] = 0;
        robot.Sens["gB"] = 0;
        robot.Sens["gL"] = 0;
        robot.Sens["gR"] = 0;
        robot.Sens["f"] = 0.9;
        robot.Sens["r"] = 0;
        robot.Sens["dLF"] = 1.0;
        robot.Sens["dRF"] = 0;
        robot.Sens["dLB"] = 0;
        robot.Sens["dRB"] = 0;
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        for (long t = 37; t <= 40; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("ADC_CORRECT", fsm.MbriState);
        // 9 帧窗口（41-49）→ 第 9 帧 ready → right_bias 矫正。
        for (long t = 41; t <= 48; t++)
        {
            fsm.TickFor(robot, t);
        }
        fsm.TickFor(robot, 49);
        Assert.Equal("ADC 右偏，向左矫正，diff=833", robot.Fsm.Action);
        Assert.Equal(0.0, robot.V, 6);          // 原地转
        Assert.Equal(1.5651, robot.W, 3);        // (400−(−400))/(2×0.229)×k
        Assert.True(fsm.ReentryActive); // 仍由 reentry 接管
    }
}
