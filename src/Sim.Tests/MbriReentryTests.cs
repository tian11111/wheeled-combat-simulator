using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 批2 掉台回归单测：MbriReentry 迁移矩阵（真车 ADC/红外注入域，reentry.py 逐行对照）+
/// MbriFsm 仲裁链（reentry 接管 &gt; 巡台、unhealthy→reentry、回归完成重置巡台）+
/// 掉台判定域映射（SimToAdcFallDomain 解决批1 zone&lt;0 域差）。
/// </summary>
public sealed class MbriReentryTests
{
    private const double TickSeconds = 0.05;

    // ---------- 注入样本工厂（真车 ADC/布尔域） ----------

    private static MbriGraySample CenterGray() => MbriGrayCalibration.SimSampleToAdc(1000, 1000, 1000, 1000);

    /// <summary>四路 ADC=0 → zone 全负（front −1.53 / rear −1.25 / left −1.81 / right −2.27）。</summary>
    private static MbriGraySample DarkGray() => new(0.0, 0.0, 0.0, 0.0);

    private static MbriDigiIr IrNone() => new(false, false, false, false, false, false, Valid: true);

    private static MbriAnalogIr AnalogCenter() => new(1000.0, 1000.0, Valid: true);      // diff=0, signal=1000
    private static MbriAnalogIr AnalogWeak() => new(100.0, 100.0, Valid: true);          // signal&lt;296
    private static MbriAnalogIr AnalogLeftBias() => new(900.0, 1100.0, Valid: true);     // diff=-200
    private static MbriAnalogIr AnalogRightBias() => new(1100.0, 900.0, Valid: true);    // diff=+200
    private static MbriAnalogIr AnalogTouch() => new(5000.0, 5000.0, Valid: true);       // ≥1060 贴墙
    private static MbriAnalogIr AnalogInvalid() => new(0.0, 0.0, Valid: false);

    /// <summary>预热：3 帧台面 ADC（reentry 灰度窗口就绪 → SENSOR_STOP 退出到 WAIT）。</summary>
    private static void Warmup(MbriReentry re)
    {
        for (long t = 0; t < 3; t++)
        {
            re.Update(CenterGray(), IrNone(), AnalogCenter(), t);
        }
    }

    /// <summary>从 WAIT 喂全暗帧并断言触发边沿，返回触发时刻。
    /// 含 reentry 灰度模型 3 帧中值滞后：暗帧 1 时 filtered 仍亮（计数 0）、
    /// 暗帧 2/3/4 计数 1/2/3 → 边沿在第 4 个暗帧（reentry.py:177-183）。</summary>
    private static long TriggerFall(MbriReentry re, MbriDigiIr ir, long t0)
    {
        var r1 = re.Update(DarkGray(), ir, AnalogCenter(), t0);
        Assert.False(r1.Fall); // 中值滞后：filtered 仍亮
        Assert.Equal("WAIT", r1.State);
        var r2 = re.Update(DarkGray(), ir, AnalogCenter(), t0 + 1);
        Assert.True(r2.Fall && !r2.FallEdge);
        var r3 = re.Update(DarkGray(), ir, AnalogCenter(), t0 + 2);
        Assert.True(r3.Fall && !r3.FallEdge);
        var r4 = re.Update(DarkGray(), ir, AnalogCenter(), t0 + 3);
        Assert.True(r4.FallEdge); // 连续计数刚达 FALL_CONFIRM=3
        return t0 + 3;
    }

    // ---------- 传感器无效语义（reentry.py:171-175） ----------

    [Fact]
    public void Matrix_SensorStop_OnUnhealthyOrInvalidInputs()
    {
        var re = new MbriReentry(TickSeconds);
        var r = re.Update(CenterGray(), IrNone(), AnalogCenter(), 0, healthy: false);
        Assert.Equal("SENSOR_STOP", r.State);
        Assert.False(r.Fall);
        Assert.Equal((0, 0), (r.Left, r.Right));
        // 灰度坏值（越出 [0,10000]）→ obs.valid=false → SENSOR_STOP。
        var r2 = re.Update(new MbriGraySample(20000, 0, 0, 0), IrNone(), AnalogCenter(), 1);
        Assert.Equal("SENSOR_STOP", r2.State);
        // 数字红外无效 → SENSOR_STOP（reentry.py:171 ir_valid）。
        var r3 = re.Update(CenterGray(), IrNone() with { Valid = false }, AnalogCenter(), 2);
        Assert.Equal("SENSOR_STOP", r3.State);
        // 恢复有效后（窗口就绪）→ WAIT。
        var r4 = re.Update(CenterGray(), IrNone(), AnalogCenter(), 3);
        Assert.Equal("WAIT", r4.State);
    }

    // ---------- 掉台电平/边沿（reentry.py:177-183） ----------

    [Fact]
    public void Matrix_FallLevelAndEdge_ExactlyAtThirdConsecutiveDarkFrame()
    {
        var re = new MbriReentry(TickSeconds);
        Warmup(re);
        var triggerTick = TriggerFall(re, IrNone() with { Front = true }, 3);
        var r = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), triggerTick + 1);
        Assert.True(r.Fall);
        Assert.False(r.FallEdge); // 第 4 帧起仅电平，无边沿
    }

    [Fact]
    public void Matrix_FallCounterResetsOnBrightFrame()
    {
        var re = new MbriReentry(TickSeconds);
        Warmup(re);
        // 暗 t3（滞后，filtered 仍亮→计数 0）、t4（计数 1）、t5 亮帧（filtered 仍暗→计数 2）。
        _ = re.Update(DarkGray(), IrNone(), AnalogCenter(), 3);
        _ = re.Update(DarkGray(), IrNone(), AnalogCenter(), 4);
        _ = re.Update(CenterGray(), IrNone(), AnalogCenter(), 5);
        // t6 亮帧：filtered 转亮 → 计数清零（reentry.py:178 else 0）。
        var r6 = re.Update(CenterGray(), IrNone(), AnalogCenter(), 6);
        Assert.False(r6.Fall);
        Assert.Equal("WAIT", r6.State);
        // 重新计数：暗 t7（滞后 0）、t8（1）、t9（2）、t10（3 → 边沿）。
        // 若计数未在 t6 清零，边沿会提前到 t9。
        _ = re.Update(DarkGray(), IrNone(), AnalogCenter(), 7);
        var r8 = re.Update(DarkGray(), IrNone(), AnalogCenter(), 8);
        Assert.True(r8.Fall && !r8.FallEdge);
        Assert.Equal("WAIT", r8.State);
        var r9 = re.Update(DarkGray(), IrNone(), AnalogCenter(), 9);
        Assert.True(r9.Fall && !r9.FallEdge);
        Assert.Equal("WAIT", r9.State);
        var r10 = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), 10);
        Assert.True(r10.FallEdge);
        Assert.Equal("ADC_CORRECT", r10.State);
    }

    // ---------- 分派优先级（reentry.py:109-120） ----------

    [Fact]
    public void Matrix_DispatchPriority_FrontCorrectRearTurn180()
    {
        var re = new MbriReentry(TickSeconds);
        Warmup(re);
        var t = TriggerFall(re, IrNone() with { Front = true }, 3);
        // 分派发生在触发边沿那帧（Reason/Command 持续到下一状态迁移）。
        Assert.Equal("ADC_CORRECT", re.State);
        Assert.Equal("掉台触发且前头红外亮，直接矫正", re.Reason);
        Assert.Equal((0, 0), re.Command);
        // 下一帧起 ADC_CORRECT 对齐窗口充填（准备中）。
        var r = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t + 1);
        Assert.Equal("ADC_CORRECT", r.State);
        Assert.Equal("ADC 中值滤波准备中", r.Reason);

        var re2 = new MbriReentry(TickSeconds);
        Warmup(re2);
        var t2 = TriggerFall(re2, IrNone() with { Rear = true }, 3);
        Assert.Equal("TURN_180", re2.State);
        Assert.Equal("正后红外亮，转 180 度", re2.Reason);
        Assert.Equal((560, -560), re2.Command); // Mix(0, +560)
    }

    [Fact]
    public void Matrix_DispatchPriority_SideTurns90AndNoneWaits()
    {
        // 右前/右后 → 右转 90；左前/左后 → 左转 90；全无 → IR_WAIT。
        foreach (var (ir, state, reason) in new[]
                 {
                     (IrNone() with { RightFront = true }, "TURN_RIGHT_90", "右侧红外亮，右转 90 度"),
                     (IrNone() with { RightRear = true }, "TURN_RIGHT_90", "右侧红外亮，右转 90 度"),
                     (IrNone() with { LeftFront = true }, "TURN_LEFT_90", "左侧红外亮，左转 90 度"),
                     (IrNone() with { LeftRear = true }, "TURN_LEFT_90", "左侧红外亮，左转 90 度"),
                 })
        {
            var re = new MbriReentry(TickSeconds);
            Warmup(re);
            _ = TriggerFall(re, ir, 3);
            Assert.Equal(state, re.State);
            Assert.Equal(reason, re.Reason);
        }

        var none = new MbriReentry(TickSeconds);
        Warmup(none);
        _ = TriggerFall(none, IrNone(), 3);
        Assert.Equal("IR_WAIT", none.State);
        Assert.Equal("掉台但六路红外暂时无值，停车等待", none.Reason);
        Assert.Equal((0, 0), none.Command);
    }

    // ---------- 转向定时 → 大力前冲（reentry.py:203-206） ----------

    [Fact]
    public void Matrix_TurnDurationThenApproach()
    {
        // TURN_180：1.2s=24 tick；TURN_RIGHT_90：0.65s=13 tick。
        var re = new MbriReentry(TickSeconds);
        Warmup(re);
        var t = TriggerFall(re, IrNone() with { Rear = true }, 3);
        Assert.Equal("TURN_180", re.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t + 1).State);
        Assert.Equal("TURN_180", re.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t + 23).State);
        var r = re.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t + 24);
        Assert.Equal("ADC_APPROACH", r.State);
        Assert.Equal("定时转向完成，大力前冲撞墙", r.Reason);
        Assert.Equal((700, 700), (r.Left, r.Right));

        var re2 = new MbriReentry(TickSeconds);
        Warmup(re2);
        var t2 = TriggerFall(re2, IrNone() with { RightFront = true }, 3);
        Assert.Equal("TURN_RIGHT_90", re2.Update(DarkGray(), IrNone() with { RightFront = true }, AnalogCenter(), t2 + 12).State);
        var r2 = re2.Update(DarkGray(), IrNone() with { RightFront = true }, AnalogCenter(), t2 + 13);
        Assert.Equal("ADC_APPROACH", r2.State);
    }

    // ---------- ADC_CORRECT（reentry.py:208-245） ----------

    /// <summary>从 WAIT 直接前头分派进 ADC_CORRECT，返回进入时刻（对齐窗口自此刻起充填）。</summary>
    private static long EnterCorrect(MbriReentry re)
    {
        Warmup(re);
        return TriggerFall(re, IrNone() with { Front = true }, 3);
    }

    [Fact]
    public void Matrix_AdcCorrect_WarmupThenCenterConfirm3ThenReverse()
    {
        var re = new MbriReentry(TickSeconds);
        var t0 = EnterCorrect(re);
        // 9 帧中值窗口：前 8 帧 not ready → 命令 (0,0) "ADC 中值滤波准备中"。
        for (long i = 1; i <= 8; i++)
        {
            var rw = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t0 + i);
            Assert.Equal("ADC_CORRECT", rw.State);
            Assert.Equal("ADC 中值滤波准备中", rw.Reason);
            Assert.Equal((0, 0), (rw.Left, rw.Right));
        }
        // 第 9 帧 ready+strong+center → 确认 1/3；第 10/11 帧 → 2/3、3/3 → REVERSE。
        var r9 = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t0 + 9);
        Assert.Equal("ADC 正对确认 1/3，diff=0", r9.Reason);
        var r10 = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t0 + 10);
        Assert.Equal("ADC 正对确认 2/3，diff=0", r10.Reason);
        var r11 = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t0 + 11);
        Assert.Equal("REVERSE", r11.State);
        Assert.Equal("ADC 矫正完成，倒车", r11.Reason);
        Assert.Equal((-900, -900), (r11.Left, r11.Right));
    }

    [Fact]
    public void Matrix_AdcCorrect_BiasTurnsAndResetsConfirmCount()
    {
        var re = new MbriReentry(TickSeconds);
        var t0 = EnterCorrect(re);
        for (long i = 1; i <= 9; i++)
        {
            _ = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogLeftBias(), t0 + i);
        }
        var r = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogLeftBias(), t0 + 10);
        Assert.Equal("ADC 左偏，向右矫正，diff=-200", r.Reason);
        Assert.Equal((400, -400), (r.Left, r.Right));

        var re2 = new MbriReentry(TickSeconds);
        var t1 = EnterCorrect(re2);
        for (long i = 1; i <= 9; i++)
        {
            _ = re2.Update(DarkGray(), IrNone() with { Front = true }, AnalogRightBias(), t1 + i);
        }
        var r2 = re2.Update(DarkGray(), IrNone() with { Front = true }, AnalogRightBias(), t1 + 10);
        Assert.Equal("ADC 右偏，向左矫正，diff=200", r2.Reason);
        Assert.Equal((-400, 400), (r2.Left, r2.Right));
    }

    [Fact]
    public void Matrix_AdcCorrect_WeakSignalOneShotApproach()
    {
        var re = new MbriReentry(TickSeconds);
        var t0 = EnterCorrect(re);
        for (long i = 1; i <= 9; i++)
        {
            _ = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogWeak(), t0 + i);
        }
        var r = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogWeak(), t0 + 10);
        Assert.Equal("ADC_APPROACH", r.State);
        Assert.Equal("ADC 信号弱 100，大力前冲撞墙", r.Reason);
    }

    [Fact]
    public void Matrix_AdcCorrect_InvalidAnalogAndTimeout()
    {
        var re = new MbriReentry(TickSeconds);
        var t0 = EnterCorrect(re);
        var r = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogInvalid(), t0 + 1);
        Assert.Equal("SAFE_STOP", r.State);
        Assert.Equal("模拟红外无效，无法矫正", r.Reason);

        // 矫正超时：恒偏置（永不 center）→ 3s=60 tick → SAFE_STOP。
        var re2 = new MbriReentry(TickSeconds);
        var t1 = EnterCorrect(re2);
        for (long i = 1; i <= 60; i++)
        {
            _ = re2.Update(DarkGray(), IrNone() with { Front = true }, AnalogLeftBias(), t1 + i);
        }
        var r2 = re2.Update(DarkGray(), IrNone() with { Front = true }, AnalogLeftBias(), t1 + 61);
        Assert.Equal("SAFE_STOP", r2.State);
        Assert.Equal("矫正超时未正对", r2.Reason);
    }

    // ---------- ADC_APPROACH（reentry.py:247-254） ----------

    [Fact]
    public void Matrix_AdcApproach_TouchStopsAndTimeoutSafeStops()
    {
        // 贴墙：max(左右) ≥ 1060 → 停车回矫正（对齐窗口重置）。
        var re = new MbriReentry(TickSeconds);
        var t0 = EnterCorrect(re);
        for (long i = 1; i <= 9; i++)
        {
            _ = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogWeak(), t0 + i);
        }
        _ = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogWeak(), t0 + 10); // 弱信号 → APPROACH
        var touch = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogTouch(), t0 + 11);
        Assert.Equal("ADC_CORRECT", touch.State);
        Assert.Equal("大力冲撞贴墙，停车矫正", touch.Reason);
        // 重置后窗口重新充填 → 准备中。
        Assert.Equal("ADC 中值滤波准备中", re.Update(DarkGray(), IrNone() with { Front = true }, AnalogTouch(), t0 + 12).Reason);

        // 超时：2s=40 tick 未贴墙 → SAFE_STOP。
        var re2 = new MbriReentry(TickSeconds);
        Warmup(re2);
        var t1 = TriggerFall(re2, IrNone() with { Rear = true }, 3);
        _ = re2.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t1 + 1); // TURN_180
        _ = re2.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t1 + 25); // → APPROACH
        _ = re2.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t1 + 64); // elapsed 39
        var r = re2.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t1 + 65); // elapsed 40
        Assert.Equal("SAFE_STOP", r.State);
        Assert.Equal("大力冲撞超时未贴墙，停车", r.Reason);
    }

    // ---------- REVERSE / SAFE_STOP（reentry.py:256-265, 194-201） ----------

    [Fact]
    public void Matrix_Reverse_CompletesOnFrontIrLoss_TimesOutOtherwise()
    {
        var re = new MbriReentry(TickSeconds);
        var t0 = EnterCorrect(re);
        for (long i = 1; i <= 11; i++)
        {
            _ = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t0 + i);
        }
        // 前头红外无值 → 倒车完成。
        var r = re.Update(DarkGray(), IrNone(), AnalogCenter(), t0 + 12);
        Assert.Equal("SAFE_STOP", r.State);
        Assert.Equal("倒车完成（前头红外无值）", r.Reason);

        // 前头红外持续有值 → 3s=60 tick 倒车超时（REVERSE 于 t1+11 进入，t1+71 超时）。
        var re2 = new MbriReentry(TickSeconds);
        var t1 = EnterCorrect(re2);
        for (long i = 1; i <= 70; i++)
        {
            _ = re2.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t1 + i);
        }
        var r2 = re2.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), t1 + 71);
        Assert.Equal("SAFE_STOP", r2.State);
        Assert.Equal("倒车超时", r2.Reason);
    }

    [Fact]
    public void Matrix_SafeStop_LatchesWhileFallen_ReleasesToWaitOnGrayRecovery()
    {
        var re = new MbriReentry(TickSeconds);
        Warmup(re);
        var t = TriggerFall(re, IrNone() with { Rear = true }, 3);
        _ = re.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t + 1); // TURN_180
        _ = re.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t + 25); // APPROACH
        _ = re.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t + 65); // SAFE_STOP 超时
        // 仍掉台：保持 SAFE_STOP 不重复触发。
        var hold = re.Update(DarkGray(), IrNone(), AnalogCenter(), t + 66);
        Assert.Equal("SAFE_STOP", hold.State);
        // 灰度恢复（人工/上台）：中值滞后需 2 帧亮 → SAFE_STOP → WAIT。
        _ = re.Update(CenterGray(), IrNone(), AnalogCenter(), t + 67);
        var recovered = re.Update(CenterGray(), IrNone(), AnalogCenter(), t + 68);
        Assert.Equal("WAIT", recovered.State);
        Assert.Equal("等待掉台触发", recovered.Reason);
        Assert.False(recovered.Fall);
    }

    [Fact]
    public void Matrix_IrWait_ReDispatchesWhenIrAppears_ReleasesWhenFallClears()
    {
        var re = new MbriReentry(TickSeconds);
        Warmup(re);
        var t = TriggerFall(re, IrNone(), 3);
        Assert.Equal("IR_WAIT", re.Update(DarkGray(), IrNone(), AnalogCenter(), t + 1).State);
        // 有值后重新分派。
        var r = re.Update(DarkGray(), IrNone() with { Rear = true }, AnalogCenter(), t + 2);
        Assert.Equal("TURN_180", r.State);
        // IR_WAIT 期间掉台解除 → WAIT。
        var re2 = new MbriReentry(TickSeconds);
        Warmup(re2);
        var t2 = TriggerFall(re2, IrNone(), 3);
        _ = re2.Update(DarkGray(), IrNone(), AnalogCenter(), t2 + 1);
        _ = re2.Update(CenterGray(), IrNone(), AnalogCenter(), t2 + 2);
        var r2 = re2.Update(CenterGray(), IrNone(), AnalogCenter(), t2 + 3);
        Assert.Equal("WAIT", r2.State);
    }

    [Fact]
    public void Matrix_SensorStopRecovery_RetriggersOnFallEdge()
    {
        // SENSOR_STOP 恢复后重新达到连续帧数：重走流程（reentry.py:196）。
        var re = new MbriReentry(TickSeconds);
        Warmup(re);
        _ = re.Update(DarkGray(), IrNone(), AnalogCenter(), 3); // 掉台中传感器故障
        var stop = re.Update(DarkGray(), IrNone(), AnalogCenter(), 4, healthy: false);
        Assert.Equal("SENSOR_STOP", stop.State);
        // 恢复后重新计数 3 帧 → 触发。
        _ = re.Update(DarkGray(), IrNone(), AnalogCenter(), 5);
        _ = re.Update(DarkGray(), IrNone(), AnalogCenter(), 6);
        var r = re.Update(DarkGray(), IrNone() with { Front = true }, AnalogCenter(), 7);
        Assert.True(r.FallEdge);
        Assert.Equal("ADC_CORRECT", r.State);
    }

    // ---------- 掉台判定域映射（批1 zone<0 域差的批2 解法） ----------

    [Fact]
    public void FallDomain_WalkwayMapsToDarkAdc_PlatformUnchanged()
    {
        // 走道段（g<150）→ ADC 0 → zone<0 → 掉台可达。
        Assert.Equal(0.0, MbriGrayCalibration.SimToAdcFallDomain("front", 0), 9);
        Assert.Equal(0.0, MbriGrayCalibration.SimToAdcFallDomain("rear", 30), 9);
        Assert.Equal(0.0, MbriGrayCalibration.SimToAdcFallDomain("left", 149), 9);
        // 台面段与批1 仿射一致。
        Assert.Equal(MbriGrayCalibration.SimToAdc("front", 150), MbriGrayCalibration.SimToAdcFallDomain("front", 150), 9);
        Assert.Equal(MbriGrayCalibration.SimToAdc("rear", 1000), MbriGrayCalibration.SimToAdcFallDomain("rear", 1000), 9);
        // 四路走道采样 → reentry 模型 zone 全负 → all-dark 可达（批1 openIssue 解除）。
        var sample = MbriGrayCalibration.SimSampleToAdcFallDomain(0, 0, 0, 0);
        var model = new MbriRiskModel(3);
        _ = model.Update(sample);
        _ = model.Update(sample);
        var obs = model.Update(sample);
        Assert.True(obs.Zone.Front < 0 && obs.Zone.Rear < 0 && obs.Zone.Left < 0 && obs.Zone.Right < 0);
    }
}

/// <summary>
/// 批2 MbriFsm 仲裁链单测：unhealthy→reentry 接管、掉台回归接管 &gt; 巡台、
/// 回归完成重置巡台（main.py:174-281 顺序）、reentry 传感器桥接（f/r/对角→
/// 数字红外位，f×(10000/1.2)→模拟红外对）。
/// </summary>
public sealed class MbriFsmBatch2Tests
{
    private static RobotRuntime NewRobot() => new() { Role = RoleNames.Us, Name = "我方" };

    private static void SetStage(RobotRuntime r, double f = 0.0)
    {
        r.Sens["gF"] = 1000;
        r.Sens["gB"] = 1000;
        r.Sens["gL"] = 1000;
        r.Sens["gR"] = 1000;
        r.Sens["f"] = f;
        r.Sens["r"] = 0;
        r.Sens["dLF"] = 0;
        r.Sens["dRF"] = 0;
        r.Sens["dLB"] = 0;
        r.Sens["dRB"] = 0;
    }

    /// <summary>搬下台：走道灰度（FallDomain→ADC 0→zone&lt;0）。</summary>
    private static void SetWalkway(RobotRuntime r, double f)
    {
        r.Sens["gF"] = 0;
        r.Sens["gB"] = 0;
        r.Sens["gL"] = 0;
        r.Sens["gR"] = 0;
        r.Sens["f"] = f;
    }

    [Fact]
    public void Arbitration_UnhealthyHandsOverToReentrySensorStop_ThenRecoversWithFreshPatrol()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetStage(robot);
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState);
        // 传感器失效 → reentry 以 healthy=false 接管（SENSOR_STOP→Incapacitated）。
        fsm.TickFor(robot, 37, healthy: false);
        Assert.Equal("SENSOR_STOP", fsm.MbriState);
        Assert.Equal(FsmState.Incapacitated, robot.Fsm.State);
        Assert.True(fsm.ReentryActive);
        Assert.Equal(0.0, robot.V, 12);
        // 恢复 → reentry 回 WAIT → 巡台重置为新实例（WARMUP）并交还。
        fsm.TickFor(robot, 38);
        Assert.False(fsm.ReentryActive);
        Assert.Equal("WARMUP", fsm.MbriState);
        Assert.Equal(FsmState.Search, robot.Fsm.State);
        // 新实例预热 2 帧 → MEDIUM_CRUISE。
        fsm.TickFor(robot, 39);
        fsm.TickFor(robot, 40);
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState);
    }

    [Fact]
    public void Arbitration_FallTakeover_FullReentryFlowThroughRealBridging()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetStage(robot);
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        // 人为掉台：走道灰度（FallDomain→全暗）+ f 亮（面向擂台墙）。
        // 含 reentry 灰度模型 3 帧中值滞后：filtered 自 t=39 起转暗，计数 1@38?——
        // 实际：t37 滞后计数 0、t38 计数 1、t39 计数 2、t40 计数 3 → 边沿 → 前头分派。
        SetWalkway(robot, f: 0.9);
        fsm.TickFor(robot, 37);
        fsm.TickFor(robot, 38);
        fsm.TickFor(robot, 39);
        fsm.TickFor(robot, 40);
        Assert.Equal("ADC_CORRECT", fsm.MbriState);
        Assert.True(fsm.ReentryActive);
        Assert.Equal(FsmState.Recover, robot.Fsm.State);
        // 对准模型：analog=f×(10000/1.2)=7500 强信号+diff≡0 → 9 帧窗口(41-49)
        // +3 帧确认(49-51) → REVERSE。
        for (long t = 41; t <= 50; t++)
        {
            fsm.TickFor(robot, t);
        }
        fsm.TickFor(robot, 51);
        Assert.Equal("REVERSE", fsm.MbriState);
        Assert.Equal(-0.8064, robot.V, 6); // −900×k
        Assert.Equal(0.0, robot.W, 12);
        // 前头红外丢失 → 倒车完成 → SAFE_STOP（保持接管）。
        SetWalkway(robot, f: 0.0);
        fsm.TickFor(robot, 52);
        Assert.Equal("SAFE_STOP", fsm.MbriState);
        Assert.True(fsm.ReentryActive);
        Assert.Equal(0.0, robot.V, 12);
        // 事件流：reentry 迁移有 [mbri-reentry] 行（EventKind.Recover）。
        Assert.Contains(events.Events, e => e.Msg.StartsWith("[mbri-reentry] ADC_CORRECT"));
        Assert.Contains(events.Events, e => e.Kind == EventKind.Recover && e.Msg.Contains("REVERSE"));
    }

    [Fact]
    public void Arbitration_ReentryCompletion_ResetsPatrolAndReturnsControl()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetStage(robot);
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        SetWalkway(robot, f: 0.9);
        for (long t = 37; t <= 51; t++)
        {
            fsm.TickFor(robot, t); // 滞后+确认 → REVERSE 于 t=51（同上路径）
        }
        Assert.Equal("REVERSE", fsm.MbriState);
        SetWalkway(robot, f: 0.0);
        fsm.TickFor(robot, 52); // → SAFE_STOP
        Assert.Equal("SAFE_STOP", fsm.MbriState);
        // 灰度恢复（人工/上台）：t53 filtered 仍暗（滞后）保持 SAFE_STOP，
        // t54 转亮 → reentry 回 WAIT → 巡台重置（WARMUP）→ 恢复巡航。
        SetStage(robot);
        fsm.TickFor(robot, 53);
        Assert.Equal("SAFE_STOP", fsm.MbriState);
        fsm.TickFor(robot, 54);
        Assert.False(fsm.ReentryActive);
        Assert.Equal("WARMUP", fsm.MbriState); // 新实例预热（旧实例残留状态被丢弃）
        fsm.TickFor(robot, 55);
        fsm.TickFor(robot, 56);
        Assert.Equal("MEDIUM_CRUISE", fsm.MbriState);
        Assert.Equal(0.3584, robot.V, 6); // 400×k
        // 事件流证明巡台重置发生（重置后的 WARMUP 迁移日志）。
        Assert.Contains(events.Events, e => e.Msg == "[mbri] WARMUP: 等待滤波窗口");
    }
}
