using Sim.Core;

namespace Sim.Tests;

/// <summary>
/// 批1 巡台状态机迁移矩阵 + 灰度风险模型单测（implement.md 批1 第 3/4/6 条）。
/// 输入为真车 ADC 域采样（MbriPatrol 的原生输入域，如同真车 CSV 回放）；
/// 仿真灰度→ADC 由 MbriGrayCalibration 承担（另有端点单测）。
/// 期望迁移与理由逐字对照 ring_patrol.py / config.py（引用见各断言）。
/// 计时含 3 点中值滤波的 2 帧滞后（gray.py 窗口语径：输入切换后 filtered 需
/// 2 帧才跟上——真车同源行为，非移植偏差）。
/// </summary>
public sealed class MbriPatrolTests
{
    private const double TickSeconds = 0.05;

    // ---------- ADC 样本工厂 ----------

    private static double ZoneToAdc(string channel, double zone)
        => MbriGrayCalibration.EdgeReference[channel]
           + zone * (MbriGrayCalibration.CenterReference[channel] - MbriGrayCalibration.EdgeReference[channel]);

    /// <summary>
    /// 台心红区：官方场实测台心灰度（A1 重标锚点 CenterGrayReference）经标定层
    /// → zone=1.0，与真车"中心参考读数"同域（批1 原写 g=1000；A1 重标后 g=1000
    /// 是台心锚以上的外推亮值 → zone≈2.1 → CRUISE 档，另见 Matrix_CruiseFastTier）。
    /// </summary>
    private static MbriGraySample Center()
        => MbriGrayCalibration.SimSampleToAdc(
            MbriGrayCalibration.CenterGrayReference["front"],
            MbriGrayCalibration.CenterGrayReference["rear"],
            MbriGrayCalibration.CenterGrayReference["left"],
            MbriGrayCalibration.CenterGrayReference["right"]);

    /// <summary>
    /// 全场均匀暗外圈：ADC 域四路同 zone=0（＝边缘参考亮度，A1 重标前
    /// SimSampleToAdc(0,0,0,0) 的旧语义）。A1 重标后 g=0 是走道（zone≈−1.0 落深暗域，
    /// 会在 EDGE_TURN 触发"单路深暗"分支），故本 craft 改用 ADC 域均匀 zone 表达
    /// "均匀暗外圈"，保持 early confirm=1 触发与 linear=0 后退分支的测试意图。
    /// </summary>
    private static MbriGraySample UniformDark()
        => new(ZoneToAdc("front", 0.0), ZoneToAdc("rear", 0.0), ZoneToAdc("left", 0.0), ZoneToAdc("right", 0.0));

    /// <summary>
    /// 近边 craft：zone front=0.9（亮，压住 early 判据）、其余 0.2 →
    /// zone_score=0.2&lt;0.35（near_edge ✓）且 zone.front≥0.35（early ✗，A3 前路线），
    /// linear_signal=+0.7&gt;REAR_RETREAT_DELTA → EDGE_AVOID 前进（真车"车尾贴边"分支）。
    /// </summary>
    private static MbriGraySample NearCraft()
        => new(ZoneToAdc("front", 0.9), ZoneToAdc("rear", 0.2), ZoneToAdc("left", 0.2), ZoneToAdc("right", 0.2));

    /// <summary>
    /// 回中 craft：zone front=0.8 / 其余 0.45 → 无 near(≥0.35)、无 early(front≥0.35,
    /// A3 前路线)、无 diagonal(左右同值)、score=0.45&lt;0.55（RECOVER 超时走"转后前进"分支）。
    /// </summary>
    private static MbriGraySample RecoverCraft()
        => new(ZoneToAdc("front", 0.8), ZoneToAdc("rear", 0.45), ZoneToAdc("left", 0.45), ZoneToAdc("right", 0.45));

    /// <summary>前白边 craft：front=white_enter(1560)，其余外圈值 → 白命中+中位数暗。</summary>
    private static MbriGraySample FrontWhiteCraft()
        => new(1560.0, ZoneToAdc("rear", 0.0), ZoneToAdc("left", 0.0), ZoneToAdc("right", 0.0));

    private static MbriGraySample RearWhiteCraft()
        => new(ZoneToAdc("front", 0.0), 1946.0, ZoneToAdc("left", 0.0), ZoneToAdc("right", 0.0));

    private static MbriGraySample LeftWhiteCraft()
        => new(ZoneToAdc("front", 0.0), ZoneToAdc("rear", 0.0), 1790.0, ZoneToAdc("right", 0.0));

    private static MbriGraySample RightWhiteCraft()
        => new(ZoneToAdc("front", 0.0), ZoneToAdc("rear", 0.0), ZoneToAdc("left", 0.0), 1720.0);

    /// <summary>转向期对角 craft（turn_direction=right）：toward(right)=0.3、away(left)=1.4；
    /// front/rear 与台面中心同值（zone 1.0），保证对角判定独立于前向信号。</summary>
    private static MbriGraySample DiagonalTurnCraft()
        => new(ZoneToAdc("front", 1.0), ZoneToAdc("rear", 1.0), ZoneToAdc("left", 1.4), ZoneToAdc("right", 0.3));

    private static MbriPatrol NewPatrol() => new(TickSeconds);

    // ---------- 风险模型（gray.py 逐行移植的行为钉板） ----------

    [Fact]
    public void RiskModel_WindowMustBePositiveOdd()
    {
        Assert.Throws<ArgumentException>(() => new MbriRiskModel(0));
        Assert.Throws<ArgumentException>(() => new MbriRiskModel(2));
        Assert.Throws<ArgumentException>(() => new MbriRiskModel(-3));
        _ = new MbriRiskModel(3);
    }

    [Fact]
    public void RiskModel_ReadyAfterExactlyWindowFrames()
    {
        var model = new MbriRiskModel(3);
        var obs1 = model.Update(Center());
        Assert.False(obs1.Ready);
        var obs2 = model.Update(Center());
        Assert.False(obs2.Ready);
        var obs3 = model.Update(Center());
        Assert.True(obs3.Ready);
    }

    [Fact]
    public void RiskModel_ThreePointMedianFilter()
    {
        var model = new MbriRiskModel(3);
        var edge = UniformDark();
        _ = model.Update(new MbriGraySample(500, edge.Rear, edge.Left, edge.Right));
        _ = model.Update(new MbriGraySample(900, edge.Rear, edge.Left, edge.Right));
        var obs = model.Update(new MbriGraySample(200, edge.Rear, edge.Left, edge.Right));
        Assert.True(obs.Ready);
        Assert.Equal(500.0, obs.Filtered.Front, 9); // median(500,900,200)=500
        obs = model.Update(new MbriGraySample(800, edge.Rear, edge.Left, edge.Right));
        Assert.Equal(800.0, obs.Filtered.Front, 9); // 滑窗 median(900,200,800)=800
    }

    [Fact]
    public void RiskModel_InvalidValuesCleanedToFailingValid()
    {
        var model = new MbriRiskModel(3);
        var bad = new MbriGraySample(double.NaN, 20000.0, -1.0, MbriGrayCalibration.CenterReference["right"]);
        var obs = model.Update(bad);
        Assert.False(obs.Valid);              // gray.py _clean: 坏值→0 且 valid=false
        Assert.Equal(0.0, obs.Raw.Front, 9);
        Assert.Equal(0.0, obs.Raw.Rear, 9);
        Assert.Equal(0.0, obs.Raw.Left, 9);
        Assert.Equal(MbriGrayCalibration.CenterReference["right"], obs.Raw.Right, 9);
    }

    [Fact]
    public void RiskModel_ZoneWhiteAndHysteresisFields()
    {
        var model = new MbriRiskModel(3);
        _ = model.Update(Center());
        _ = model.Update(Center());
        var obs = model.Update(Center());
        Assert.Equal(1.0, obs.Zone.Front, 9);
        Assert.Equal(0.0, obs.White.Front, 9);
        Assert.Equal(1.0, obs.ZoneScore, 9);
        Assert.False(obs.NearEdge);           // 1.0 ≥ enter(0.35)
        Assert.True(obs.NearClear);           // 1.0 > clear(0.65)
        Assert.Empty(obs.WhiteHits);
        Assert.True(obs.WhiteClear);          // 全部 < white_clear
        // 中间带：0.35~0.65 双侧均不触发（滞回语义）。
        var model2 = new MbriRiskModel(3);
        var mid = new MbriGraySample(ZoneToAdc("front", 0.5), ZoneToAdc("rear", 0.5),
            ZoneToAdc("left", 0.5), ZoneToAdc("right", 0.5));
        _ = model2.Update(mid);
        _ = model2.Update(mid);
        var obsMid = model2.Update(mid);
        Assert.Equal(0.5, obsMid.ZoneScore, 9);
        Assert.False(obsMid.NearEdge);
        Assert.False(obsMid.NearClear);
    }

    [Fact]
    public void RiskModel_WhiteHitsNeedFilteredAtLeastEnter()
    {
        var model = new MbriRiskModel(3);
        var craft = FrontWhiteCraft();
        _ = model.Update(craft);
        _ = model.Update(craft);
        var obs = model.Update(craft);
        Assert.Equal(["front"], obs.WhiteHits); // filtered ≥ white_enter (>=, gray.py:154-157)
        Assert.False(obs.WhiteClear);           // front 1560 ≥ clear 1479
        Assert.True(obs.NearEdge);              // median(3.30,0,0,0)=0 < 0.35
    }

    // ---------- 巡台迁移矩阵 ----------

    [Fact]
    public void Matrix_WarmupThenMediumCruise()
    {
        var p = NewPatrol();
        // 窗口未满 → WARMUP（ring_patrol.py:225-227），指令 (0,0)。
        var r0 = p.Update(Center(), 0);
        Assert.Equal("WARMUP", r0.State);
        Assert.Equal((0, 0), (r0.Left, r0.Right));
        Assert.Equal("等待滤波窗口", r0.Reason);
        var r1 = p.Update(Center(), 1);
        Assert.Equal("WARMUP", r1.State);
        // 第 3 帧 ready → zone_score=1.0 < 1.10 → MEDIUM_CRUISE（400,400）
        // （ring_patrol.py:177-185 + config PATROL_FAST_ZONE_SCORE）。
        var r2 = p.Update(Center(), 2);
        Assert.Equal("MEDIUM_CRUISE", r2.State);
        Assert.Equal((400, 400), (r2.Left, r2.Right));
        Assert.Equal("按区域分级巡航", r2.Reason);
        Assert.Equal("medium", r2.SpeedLevel);
    }

    [Fact]
    public void Matrix_CruiseFastTier_OnlyAboveFastZoneScore()
    {
        var p = NewPatrol();
        // ADC 高于 center_ref（真车可能读到比参考亮的值）→ zone_score≈1.36 ≥ 1.10 → CRUISE。
        var bright = new MbriGraySample(
            ZoneToAdc("front", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["front"] - MbriGrayCalibration.CenterReference["front"]),
            ZoneToAdc("rear", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["rear"] - MbriGrayCalibration.CenterReference["rear"]),
            ZoneToAdc("left", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["left"] - MbriGrayCalibration.CenterReference["left"]),
            ZoneToAdc("right", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["right"] - MbriGrayCalibration.CenterReference["right"]));
        _ = p.Update(bright, 0);
        _ = p.Update(bright, 1);
        var r = p.Update(bright, 2);
        Assert.Equal("CRUISE", r.State);
        Assert.Equal((450, 450), (r.Left, r.Right));
        Assert.Equal("fast", r.SpeedLevel);
    }

    [Fact]
    public void Matrix_NearEdgeConfirm3_EdgeAvoidForwardThenTurnThenRecoverForward()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        Assert.Equal("MEDIUM_CRUISE", p.Update(Center(), 2).State);
        // NearCraft 自 tick 3 起喂入；filtered 自 tick 4 生效（中值滞后）→
        // near_count 1@4、2@5、3@6。第 3 帧 → EDGE_AVOID（ring_patrol.py:248-261）：
        // linear=+0.7>0.15 → 车尾贴边 → 前进 (400,400)，risk_sensor=rear。
        _ = p.Update(NearCraft(), 3);
        _ = p.Update(NearCraft(), 4);
        Assert.Equal("MEDIUM_CRUISE", p.Update(NearCraft(), 5).State); // 防抖未满
        var r6 = p.Update(NearCraft(), 6);
        Assert.Equal("EDGE_AVOID", r6.State);
        Assert.Equal("进入危险区，先直线退离再转向", r6.Reason);
        Assert.Equal((400, 400), (r6.Left, r6.Right));
        Assert.Equal("rear", r6.RiskSensor);
        Assert.Equal("slow", r6.SpeedLevel);
        // 0.60s=12 tick 内保持（ring_patrol.py:351-353 + PATROL_EDGE_RETREAT_SECONDS）。
        Assert.Equal("EDGE_AVOID", p.Update(NearCraft(), 17).State);
        // 第 12 tick → EDGE_TURN：sign 沿用 _last_turn_sign=+1 → 右转 135°（±625），
        // 时长 1.0s=20 tick（ring_patrol.py:117-141 + MOTOR_TURN_CALIBRATION）。
        var r18 = p.Update(NearCraft(), 18);
        Assert.Equal("EDGE_TURN", r18.State);
        Assert.Equal("直线退离完成，开始转向", r18.Reason);
        Assert.Equal((625, -625), (r18.Left, r18.Right));
        Assert.Equal("right", r18.TurnDirection);
        Assert.Equal(135.0, r18.TurnAngle, 9);
        Assert.Equal(1.0, r18.TurnDuration, 9);
        Assert.Equal("danger_turn", r18.SpeedLevel);
        // 转向期间喂中心灰度（无对角/深暗风险）→ 保持。
        _ = p.Update(Center(), 19);
        Assert.Equal("EDGE_TURN", p.Update(Center(), 37).State);
        // 20 tick 后 → RECOVER_FORWARD (400,400)（ring_patrol.py:327-337）。
        // near_count 已在中心帧清零 → RECOVER_FORWARD（不在 near 排除名单）不被抢占
        // —— 同时钉住计数器清零语义。
        var r38 = p.Update(Center(), 38);
        Assert.Equal("RECOVER_FORWARD", r38.State);
        Assert.Equal("转向完成，向前离开边缘", r38.Reason);
        Assert.Equal((400, 400), (r38.Left, r38.Right));
        Assert.Equal("danger", r38.SpeedLevel);
        // RECOVER 2.0s=40 tick 内保持；释放线 zone_score≥0.55 → 回 MEDIUM_CRUISE
        // （ring_patrol.py:339-349 + PATROL_RECOVER_SECONDS/RELEASE_ZONE）。
        Assert.Equal("RECOVER_FORWARD", p.Update(Center(), 77).State);
        var r78 = p.Update(Center(), 78);
        Assert.Equal("MEDIUM_CRUISE", r78.State);
        Assert.Equal("已达到避边释放线，恢复中速穿越渐变区", r78.Reason);
    }

    [Fact]
    public void Matrix_EarlyFrontConfirm1_EdgeAvoidBackward()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        // 均匀暗自 tick 3 喂入：filtered tick 3 仍是中心（中值滞后），tick 4 生效 →
        // early confirm=1 首个生效帧即触发（PATROL_EARLY_CONFIRM=1）；
        // linear=0≤0.15 → 后退 (ring_patrol.py:263-275 + PATROL_REAR_RETREAT_DELTA)。
        Assert.Equal("MEDIUM_CRUISE", p.Update(UniformDark(), 3).State);
        var r4 = p.Update(UniformDark(), 4);
        Assert.Equal("EDGE_AVOID", r4.State);
        Assert.Equal("前向灰度趋势变暗，提前离边", r4.Reason);
        Assert.Equal((-400, -400), (r4.Left, r4.Right));
        Assert.Equal("front", r4.RiskSensor);
        // 12 tick 后 → EDGE_TURN（reason 不以"转后前进"开头 → sign 由信号定：全 0 → 沿用 +1）。
        Assert.Equal("EDGE_AVOID", p.Update(UniformDark(), 15).State);
        var r16 = p.Update(UniformDark(), 16);
        Assert.Equal("EDGE_TURN", r16.State);
        Assert.Equal("直线退离完成，开始转向", r16.Reason);
        Assert.Equal("right", r16.TurnDirection);
    }

    /// <summary>
    /// A3 行为重校钉板（10-01-mbri-hunt-engagement prd.md §1 极限循环机制）：
    /// RECOVER_FORWARD 途中前路 zone=0.5——旧真车透传线 0.76 会判"前向灰度趋势变暗"
    /// （score=0.8&lt;0.88 且 front&lt;0.76，1 帧确认）把恢复打断回 EDGE_AVOID；
    /// A3 前路线 0.35 下 0.5 不触发，恢复走满 2s 后按释放线（0.55）回 MEDIUM_CRUISE。
    /// 该循环曾是 CRUISE/MEDIUM_CRUISE 不可达 → hunt 门禁永不打开的直接原因。
    /// </summary>
    [Fact]
    public void Matrix_EarlyFrontA3_MidFrontRecoverSurvives_ReleasesToCruise()
    {
        // 触发 craft：front 0.2（过 A3 线）+ score=0.8（过 0.88 门）→ early 确认=1 触发。
        var trip = new MbriGraySample(
            ZoneToAdc("front", 0.2), ZoneToAdc("rear", 0.6),
            ZoneToAdc("left", 1.0), ZoneToAdc("right", 1.0));
        // 恢复中 craft：front 0.5（旧线内/新线外）+ score=0.8；无 near/diagonal/deep。
        var mid = new MbriGraySample(
            ZoneToAdc("front", 0.5), ZoneToAdc("rear", 0.6),
            ZoneToAdc("left", 1.0), ZoneToAdc("right", 1.0));
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        Assert.Equal("MEDIUM_CRUISE", p.Update(Center(), 2).State);
        _ = p.Update(trip, 3); // 中值滞后：窗口仍以中心为主，不触发
        var r4 = p.Update(trip, 4);
        Assert.Equal("EDGE_AVOID", r4.State);
        Assert.Equal("前向灰度趋势变暗，提前离边", r4.Reason);
        Assert.Equal((-400, -400), (r4.Left, r4.Right));
        Assert.Equal("front", r4.RiskSensor);
        // 退离 0.6s=12 tick → tick 16 EDGE_TURN；转向尾段（tick 26 起）改喂 mid，
        // 让恢复起 tick 时滤波窗口已是 mid（否则 front=0.2 残影仍过线）。
        Assert.Equal("EDGE_AVOID", p.Update(trip, 15).State);
        Assert.Equal("EDGE_TURN", p.Update(trip, 16).State);
        for (var t = 17; t <= 25; t++)
        {
            _ = p.Update(trip, t);
        }
        for (var t = 26; t <= 35; t++)
        {
            Assert.Equal("EDGE_TURN", p.Update(mid, t).State);
        }
        var r36 = p.Update(mid, 36); // 1.0s=20 tick 转向完成 → RECOVER_FORWARD
        Assert.Equal("RECOVER_FORWARD", r36.State);
        Assert.Equal("转向完成，向前离开边缘", r36.Reason);
        // A3 断言：恢复期间 mid 不再触发 early（旧 0.76 线此处即 EDGE_AVOID）。
        for (var t = 37; t <= 75; t++)
        {
            Assert.Equal("RECOVER_FORWARD", p.Update(mid, t).State);
        }
        var r76 = p.Update(mid, 76); // 2.0s=40 tick 恢复完成；score=0.8≥0.55 释放
        Assert.Equal("MEDIUM_CRUISE", r76.State);
        Assert.Equal("已达到避边释放线，恢复中速穿越渐变区", r76.Reason);
    }

    [Fact]
    public void Matrix_RecoverTimeoutStillDark_RearmTurnReusesDirection()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        // 均匀暗 → early → EDGE_AVOID（后退）。
        _ = p.Update(UniformDark(), 3);
        Assert.Equal("EDGE_AVOID", p.Update(UniformDark(), 4).State);
        _ = p.Update(UniformDark(), 15);
        var turn = p.Update(UniformDark(), 16);
        Assert.Equal("EDGE_TURN", turn.State);
        // 转向期喂回中 craft（filtered 自 18 生效；无深暗/对角/near）。
        _ = p.Update(RecoverCraft(), 17);
        _ = p.Update(RecoverCraft(), 35);
        var recover = p.Update(RecoverCraft(), 36);
        Assert.Equal("RECOVER_FORWARD", recover.State);
        Assert.Equal("转向完成，向前离开边缘", recover.Reason);
        // RECOVER 40 tick 期间 craft 无触发（near/early 均不命中）。
        _ = p.Update(RecoverCraft(), 37);
        _ = p.Update(RecoverCraft(), 75);
        // 超时且 zone_score=0.45<0.55 → EDGE_AVOID "转后前进仍在暗外圈"。
        var r76 = p.Update(RecoverCraft(), 76);
        Assert.Equal("EDGE_AVOID", r76.State);
        Assert.Equal("转后前进仍在暗外圈，重新退离后再转", r76.Reason);
        // 12 tick 后 reuse_direction=true（reason 以"转后前进"开头）→ 沿用上次 +1 右转。
        _ = p.Update(RecoverCraft(), 87);
        var r88 = p.Update(RecoverCraft(), 88);
        Assert.Equal("EDGE_TURN", r88.State);
        Assert.Equal("直线退离完成，开始转向", r88.Reason);
        Assert.Equal("right", r88.TurnDirection);
    }

    [Fact]
    public void Matrix_DiagonalTurnRiskConfirm3_RearmAvoid()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        _ = p.Update(NearCraft(), 3);
        _ = p.Update(NearCraft(), 4);
        _ = p.Update(NearCraft(), 5);
        Assert.Equal("EDGE_AVOID", p.Update(NearCraft(), 6).State);
        _ = p.Update(Center(), 16);
        var turn = p.Update(Center(), 18);
        Assert.Equal("EDGE_TURN", turn.State);
        Assert.Equal("right", turn.TurnDirection);
        // 转向侧斜压（toward=right 0.3<0.52, away−toward=1.1≥1.0）确认 3 帧
        // （filtered 自 20 生效：计数 1@20、2@21、3@22）→ 重新退离
        // （ring_patrol.py:93-105/277-296 + PATROL_DIAGONAL_*）。
        _ = p.Update(DiagonalTurnCraft(), 19);
        _ = p.Update(DiagonalTurnCraft(), 20);
        Assert.Equal("EDGE_TURN", p.Update(DiagonalTurnCraft(), 21).State);
        var r22 = p.Update(DiagonalTurnCraft(), 22);
        Assert.Equal("EDGE_AVOID", r22.State);
        Assert.Equal("转向侧斜压白边，重新退离后再转", r22.Reason);
    }

    [Fact]
    public void Matrix_DiagonalCounterResetsOnSafeFrames()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        _ = p.Update(NearCraft(), 3);
        _ = p.Update(NearCraft(), 4);
        _ = p.Update(NearCraft(), 5);
        Assert.Equal("EDGE_AVOID", p.Update(NearCraft(), 6).State);
        _ = p.Update(Center(), 16);
        Assert.Equal("EDGE_TURN", p.Update(Center(), 18).State);
        // 对角仅 2 个生效帧（filtered：0@19 滞后、1@20、2@21）→ 中心帧把 filtered
        // 拉回（22 起读中心）→ 计数在到达 3 之前清零 → 后续对角须重新数满 3 帧
        // （27 触发而非 25）——钉住"非连续对角帧不累积"语义。
        _ = p.Update(DiagonalTurnCraft(), 19);
        _ = p.Update(DiagonalTurnCraft(), 20);
        Assert.Equal("EDGE_TURN", p.Update(Center(), 21).State);      // 计数 2 未拦截
        _ = p.Update(Center(), 22);                                    // filtered 转中心 → 清零
        _ = p.Update(Center(), 23);
        _ = p.Update(DiagonalTurnCraft(), 24); // 滞后帧, 计数 0
        _ = p.Update(DiagonalTurnCraft(), 25); // 计数 1
        Assert.Equal("EDGE_TURN", p.Update(DiagonalTurnCraft(), 26).State); // 计数 2 未拦截
        var r27 = p.Update(DiagonalTurnCraft(), 27); // 计数 3 → 拦截
        Assert.Equal("EDGE_AVOID", r27.State);
        Assert.Equal("转向侧斜压白边，重新退离后再转", r27.Reason);
    }

    [Fact]
    public void Matrix_FrontWhite_WhiteEscapeBackwardThenRecoverBackward()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        // 前白 craft 自 3 喂入：filtered 自 4 起四路就位（front 白命中 1560、
        // 其余滑入外圈 → score=0 → white_edge_risk ✓）：white/near 同拍计数
        // 1@4、2@5、3@6、4@7；near 拦截先行（3@6 → EDGE_AVOID）；white 4@7 →
        // 状态 EDGE_AVOID 不在白排除名单 → 白响应（ring_patrol.py:229-246 顺序）。
        _ = p.Update(FrontWhiteCraft(), 3);
        _ = p.Update(FrontWhiteCraft(), 4);
        _ = p.Update(FrontWhiteCraft(), 5);
        Assert.Equal("EDGE_AVOID", p.Update(FrontWhiteCraft(), 6).State);
        var r7 = p.Update(FrontWhiteCraft(), 7);
        Assert.Equal("WHITE_ESCAPE", r7.State);
        Assert.Equal("前方白边，先后退", r7.Reason);
        Assert.Equal((-400, -400), (r7.Left, r7.Right));
        Assert.Equal("rear", r7.RiskSensor); // dominant=min zone（rear/left/right 同 0 取 NAMES 序首个）
        Assert.Equal("danger", r7.SpeedLevel);
        // 0.6s=12 tick 内保持。
        Assert.Equal("WHITE_ESCAPE", p.Update(FrontWhiteCraft(), 18).State);
        // 超时且 front 仍 ≥ white_clear(1479) → white_clear=false → 沿原方向继续直线脱离
        // （command[0]<0 → RECOVER_BACKWARD，ring_patrol.py:313-325）。
        var r19 = p.Update(FrontWhiteCraft(), 19);
        Assert.Equal("RECOVER_BACKWARD", r19.State);
        Assert.Equal("白边后继续直线脱离，不在边缘转向", r19.Reason);
        Assert.Equal((-400, -400), (r19.Left, r19.Right));
        // RECOVER_BACKWARD 2.0s=40 tick 超时仍在暗外圈 → 重新退离。
        _ = p.Update(FrontWhiteCraft(), 20);
        _ = p.Update(FrontWhiteCraft(), 58);
        var r59 = p.Update(FrontWhiteCraft(), 59);
        Assert.Equal("EDGE_AVOID", r59.State);
        Assert.Equal("转后前进仍在暗外圈，重新退离后再转", r59.Reason);
    }

    [Fact]
    public void Matrix_RearWhite_WhiteEscapeForward()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        // 后白 craft 自 3 喂入：filtered 4 生效（rear 白命中+前暗）→ early（front=0）
        // 先行触发 EDGE_AVOID；white 计数持续累积，4@7（EDGE_AVOID 不在白排除名单）
        // → 后方白边 → 前进（ring_patrol.py:148-150）。
        _ = p.Update(RearWhiteCraft(), 3);
        Assert.Equal("EDGE_AVOID", p.Update(RearWhiteCraft(), 4).State);
        _ = p.Update(RearWhiteCraft(), 5);
        _ = p.Update(RearWhiteCraft(), 6);
        var r7 = p.Update(RearWhiteCraft(), 7);
        Assert.Equal("WHITE_ESCAPE", r7.State);
        Assert.Equal("后方白边，先前进", r7.Reason);
        Assert.Equal((400, 400), (r7.Left, r7.Right));
    }

    [Fact]
    public void Matrix_SideWhite_ForcedTurnDirection()
    {
        // 左白 → 向右转离（forced_sign=+1）；右白 → 向左转离（forced_sign=-1）。
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        _ = p.Update(LeftWhiteCraft(), 3);
        Assert.Equal("EDGE_AVOID", p.Update(LeftWhiteCraft(), 4).State); // early 先行
        _ = p.Update(LeftWhiteCraft(), 5);
        _ = p.Update(LeftWhiteCraft(), 6);
        var left = p.Update(LeftWhiteCraft(), 7); // white 计数 4
        Assert.Equal("EDGE_TURN", left.State);
        Assert.Equal("左侧白边，向右转离边缘", left.Reason);
        Assert.Equal((625, -625), (left.Left, left.Right));
        Assert.Equal("right", left.TurnDirection);

        var p2 = NewPatrol();
        _ = p2.Update(Center(), 0);
        _ = p2.Update(Center(), 1);
        _ = p2.Update(Center(), 2);
        _ = p2.Update(RightWhiteCraft(), 3);
        Assert.Equal("EDGE_AVOID", p2.Update(RightWhiteCraft(), 4).State);
        _ = p2.Update(RightWhiteCraft(), 5);
        _ = p2.Update(RightWhiteCraft(), 6);
        var right = p2.Update(RightWhiteCraft(), 7);
        Assert.Equal("EDGE_TURN", right.State);
        Assert.Equal("右侧白边，向左转离边缘", right.Reason);
        Assert.Equal((-625, 625), (right.Left, right.Right));
        Assert.Equal("left", right.TurnDirection);
    }

    [Fact]
    public void Matrix_SensorStop_OnUnhealthyOrInvalidAdc()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        var r = p.Update(Center(), 1, healthy: false);
        Assert.Equal("SENSOR_STOP", r.State);
        Assert.Equal((0, 0), (r.Left, r.Right));
        Assert.Equal("传感器无效或数据过期", r.Reason);
        Assert.Equal("stop", r.SpeedLevel);
        // ADC 越出 [0,10000] → valid=false → SENSOR_STOP（healthy=true 亦停）。
        var r2 = p.Update(new MbriGraySample(20000, 0, 0, 0), 2);
        Assert.Equal("SENSOR_STOP", r2.State);
    }

    [Fact]
    public void Matrix_Rearm_ResetsCountersAndRetreats()
    {
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        _ = p.Update(Center(), 2);
        p.Rearm(3);
        Assert.Equal("EDGE_AVOID", p.State);
        Assert.Equal("外部自救后重新退离", p.Reason);
        Assert.Equal((-400, -400), p.Command);
    }

    [Fact]
    public void Matrix_CommandsWithinCommandLimit()
    {
        // 公开输出域钉板：巡航/转向/白边命令绝对值均 ≤ 1023
        // （ring_patrol.py:40-49 _mix 限幅；现行标定表下峰值 625，缩放分支不触发）。
        var p = NewPatrol();
        _ = p.Update(Center(), 0);
        _ = p.Update(Center(), 1);
        var bright = new MbriGraySample(
            ZoneToAdc("front", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["front"] - MbriGrayCalibration.CenterReference["front"]),
            ZoneToAdc("rear", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["rear"] - MbriGrayCalibration.CenterReference["rear"]),
            ZoneToAdc("left", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["left"] - MbriGrayCalibration.CenterReference["left"]),
            ZoneToAdc("right", 1.0) + 0.15 * (MbriGrayCalibration.WhiteReference["right"] - MbriGrayCalibration.CenterReference["right"]));
        var r = p.Update(bright, 2);
        Assert.InRange(Math.Max(Math.Abs(r.Left), Math.Abs(r.Right)), 0, 1023);
    }
}
