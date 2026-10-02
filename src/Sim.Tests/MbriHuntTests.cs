using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// P2 视觉追击单测：视觉投影（可见性/符号/视场/过滤/帧时钟/确定性）+ tracker
/// （死区/大转滞回/小弧线）+ hunt 状态机（确认帧/推块/近距 bad 避让/丢帧保留）
/// + probe 状态机（刹车/定角转向/视觉确认/推敌/中断）+ MbriFsm 仲裁优先级
/// （CRUISE 门禁/推击 latch/reentry 覆盖/特权披露事件/交还重置）+ 双跑确定性。
/// 零 IO/零时钟/零随机：tick 与视觉帧由测试显式注入。
/// </summary>
public sealed class MbriHuntTests
{
    private static RobotRuntime NewRobot(string role = RoleNames.Us) => new()
    {
        Role = role,
        Name = role == RoleNames.Us ? "我方" : "对手",
    };

    private static void SetGray(RobotRuntime r, double gFront, double gRear, double gLeft, double gRight)
    {
        r.Sens["gF"] = gFront;
        r.Sens["gB"] = gRear;
        r.Sens["gL"] = gLeft;
        r.Sens["gR"] = gRight;
    }

    /// <summary>测试用视觉帧（帧时钟同投影器：seq = tick / 3）。</summary>
    private static MbriVisionFrame FrameAt(long tick, params MbriVisionDetection[] detections)
        => new(
            tick / MbriVisionProjector.FrameIntervalTicks(),
            detections.Length > 0 ? MbriVisionFrame.StatusTarget : MbriVisionFrame.StatusNoTarget,
            detections,
            detections.FirstOrDefault());

    private static MbriVisionDetection Det(string type, double offset, double confidence
        = MbriVisionProjector.DetectionConfidence) => new(type, offset, confidence);

    private static readonly FieldModel Field = new(FieldParams.Default);

    private static BlockRuntime Block(BlockKind kind, double x, double y, bool onPlatform = true, bool isOut = false)
        => new() { Kind = kind, Name = kind == BlockKind.Buff ? "增益块" : "减益块", X = x, Y = y, Out = isOut };

    // ---------- MbriVisionProjector ----------

    [Fact]
    public void Projector_AheadBlockVisible_CenterOffset()
    {
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.0;
        var blocks = new List<BlockRuntime> { Block(BlockKind.Buff, 2.0, 1.0) };
        var frame = MbriVisionProjector.Project(observer, blocks, Field, tick: 0);
        Assert.Equal(MbriVisionFrame.StatusTarget, frame.Status);
        var detection = Assert.Single(frame.Detections);
        Assert.Equal("good", detection.Type);
        Assert.Equal(0.0, detection.OffsetX, 12);
        Assert.Equal(MbriVisionProjector.DetectionConfidence, detection.Confidence, 12);
        Assert.Equal(detection, frame.Target);
    }

    [Fact]
    public void Projector_OffsetSign_PositiveIsRightSide()
    {
        // 仿真 heading 逆时针为正：+Y 侧=画面左 → offset 负；−Y 侧=画面右 → offset 正。
        // （避开恰在 ±45° 视场边界的块——边界浮点 1ulp 差异不算可见性语义。）
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.0;
        var left = MbriVisionProjector.Project(observer, [Block(BlockKind.Buff, 3.0, 2.0)], Field, 0);
        var right = MbriVisionProjector.Project(observer, [Block(BlockKind.Buff, 3.0, 0.8)], Field, 0);
        Assert.True(left.Detections[0].OffsetX < 0.0, "左侧块 offset 应为负（vision_tracker error>0→right 语义）");
        Assert.True(right.Detections[0].OffsetX > 0.0, "右侧块 offset 应为正");
    }

    [Fact]
    public void Projector_FovConeClipsBehindAndSideways()
    {
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.0;
        // ±45° 半视场：正侧 (1,2) 方位 90° 不可见；正后 (0,1) 不可见；45° 斜前 (2,2) 可见。
        Assert.Empty(MbriVisionProjector.Project(observer, [Block(BlockKind.Buff, 1.0, 2.0)], Field, 0).Detections);
        Assert.Empty(MbriVisionProjector.Project(observer, [Block(BlockKind.Buff, 0.0, 1.0)], Field, 0).Detections);
        var diagonal = MbriVisionProjector.Project(observer, [Block(BlockKind.Buff, 2.0, 2.0)], Field, 0);
        Assert.Single(diagonal.Detections);
        Assert.Equal(-1.0, diagonal.Detections[0].OffsetX, 9); // 45° 恰在视场边界 → 满偏
    }

    [Fact]
    public void Projector_FiltersOutBlocksAndOffPlatform()
    {
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.0;
        var blocks = new List<BlockRuntime>
        {
            Block(BlockKind.Buff, 2.0, 1.0, isOut: true),   // 已出台（补偿语义过滤；锥内但不投影）
            Block(BlockKind.Buff, 3.5, 1.0),                // 台外块（3.5 > 台沿 3.1，锥内但不投影）
            Block(BlockKind.Debuff, 1.5, 1.0),              // 在台减益块 → bad
        };
        var frame = MbriVisionProjector.Project(observer, blocks, Field, 0);
        var detection = Assert.Single(frame.Detections);
        Assert.Equal("bad", detection.Type);
        Assert.False(frame.HasGood);
        Assert.True(frame.HasBad);
    }

    [Fact]
    public void Projector_NoTarget_WhenNothingVisible()
    {
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.0;
        var frame = MbriVisionProjector.Project(observer, [], Field, 7);
        Assert.Equal(MbriVisionFrame.StatusNoTarget, frame.Status);
        Assert.Equal(2, frame.Sequence); // 帧时钟 tick/3
        Assert.False(frame.HasGood);
        Assert.False(frame.HasBad);
    }

    [Fact]
    public void Projector_FrameClock_EightFpsTarget()
    {
        Assert.Equal(3, MbriVisionProjector.FrameIntervalTicks(0.05)); // 1/8s → 2.5 → AwayFromZero 3
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.0;
        var blocks = new List<BlockRuntime> { Block(BlockKind.Buff, 2.0, 1.0) };
        Assert.Equal(0, MbriVisionProjector.Project(observer, blocks, Field, 0).Sequence);
        Assert.Equal(0, MbriVisionProjector.Project(observer, blocks, Field, 2).Sequence);
        Assert.Equal(1, MbriVisionProjector.Project(observer, blocks, Field, 3).Sequence);
    }

    [Fact]
    public void Projector_DebuffMapsToBad_NearestBecomesTarget()
    {
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.0;
        var blocks = new List<BlockRuntime>
        {
            Block(BlockKind.Buff, 2.0, 1.0),
            Block(BlockKind.Debuff, 1.5, 1.0), // 更近
        };
        var frame = MbriVisionProjector.Project(observer, blocks, Field, 0);
        Assert.Equal(2, frame.Detections.Count);
        Assert.Equal("bad", frame.Target!.Type); // 最近者为目标（真车最大 bbox≈最近语义）
    }

    [Fact]
    public void Projector_DeterministicSameInputSameFrame()
    {
        var observer = NewRobot();
        observer.X = 1.0;
        observer.Y = 1.0;
        observer.Th = 0.7;
        var blocks = new List<BlockRuntime>
        {
            Block(BlockKind.Buff, 2.0, 1.0),
            Block(BlockKind.Buff, 1.5, 1.4),
            Block(BlockKind.Debuff, 0.9, 1.0),
        };
        var a = MbriVisionProjector.Project(observer, blocks, Field, 11);
        var b = MbriVisionProjector.Project(observer, blocks, Field, 11);
        // record 对 List 成员是引用相等——逐字段结构比较（确定性断言）。
        Assert.Equal(a.Sequence, b.Sequence);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Detections.Count, b.Detections.Count);
        for (var i = 0; i < a.Detections.Count; i++)
        {
            Assert.Equal(a.Detections[i], b.Detections[i]);
        }
        Assert.Equal(a.Target, b.Target);
    }

    // ---------- MbriVisionTracker ----------

    [Fact]
    public void Tracker_DeadZone_Approach()
    {
        var tracker = new MbriVisionTracker();
        var result = tracker.Update(true, "push", "good", 0.05);
        Assert.Equal("APPROACH", result.State);
        Assert.Equal((MbriVisionTracker.ApproachSpeed, MbriVisionTracker.ApproachSpeed), (result.Left, result.Right));
        Assert.Null(tracker.BigTurnDirection);
    }

    [Fact]
    public void Tracker_ArcDirections()
    {
        var tracker = new MbriVisionTracker();
        var right = tracker.Update(true, "push", "good", 0.2);
        Assert.Equal("ARC_RIGHT", right.State);
        Assert.Equal((MbriVisionTracker.ArcOuterSpeed, MbriVisionTracker.ArcInnerSpeed), (right.Left, right.Right));
        var left = tracker.Update(true, "push", "good", -0.2);
        Assert.Equal("ARC_LEFT", left.State);
        Assert.Equal((MbriVisionTracker.ArcInnerSpeed, MbriVisionTracker.ArcOuterSpeed), (left.Left, left.Right));
    }

    [Fact]
    public void Tracker_BigTurnEnter_AndHysteresis()
    {
        var tracker = new MbriVisionTracker();
        var enter = tracker.Update(true, "push", "good", 0.6);
        Assert.Equal("BIG_TURN_RIGHT", enter.State);
        Assert.Equal((MbriVisionTracker.BigTurnSpeed, -MbriVisionTracker.BigTurnSpeed), (enter.Left, enter.Right));
        Assert.Equal("right", tracker.BigTurnDirection);
        // 滞回保持：同向且 > clear(0.35) → 维持大转。
        var hold = tracker.Update(true, "push", "good", 0.4);
        Assert.Equal("BIG_TURN_RIGHT", hold.State);
        // 退到 clear 之内 → 退出大转（小弧线）。
        var clear = tracker.Update(true, "push", "good", 0.3);
        Assert.Equal("ARC_RIGHT", clear.State);
        Assert.Null(tracker.BigTurnDirection);
    }

    [Fact]
    public void Tracker_InvalidAndNonPush_Stop()
    {
        var tracker = new MbriVisionTracker();
        Assert.Equal("VISION_STOP", tracker.Update(false, "push", "good", 0.0).State);
        Assert.Equal("SEARCH", tracker.Update(true, "idle", "good", 0.0).State);
        Assert.Equal("SEARCH", tracker.Update(true, "push", "bad", 0.0).State);
        Assert.Equal("VISION_STOP", tracker.Update(true, "push", "good", null).State);
    }

    // ---------- MbriHuntController ----------

    private static MbriDigiIr NoNearIr => new(false, false, false, false, false, false, true);

    [Fact]
    public void Hunt_ConfirmFrames_ThenPush()
    {
        var hunt = new MbriHuntController();
        // 帧序列 0/1（不同视觉帧）：居中 good 置信 0.9 ≥ 0.80 → 直接锁定，
        // APPROACH 后等 2 个不同帧确认 → GOOD_PUSH。
        var first = hunt.Update(FrameAt(0, Det("good", 0.0)), NoNearIr, 0);
        Assert.Equal("GOOD_CONFIRM", first.State);
        Assert.True(first.OwnsControl);
        Assert.Equal((0, 0), (first.Left, first.Right));
        Assert.True(first.GoodLocked);
        var sameFrame = hunt.Update(FrameAt(1, Det("good", 0.0)), NoNearIr, 1); // 同 seq → 去重
        Assert.Equal("GOOD_CONFIRM", sameFrame.State);
        var second = hunt.Update(FrameAt(3, Det("good", 0.0)), NoNearIr, 3);
        Assert.Equal("GOOD_PUSH", second.State);
        Assert.Equal((MbriHuntController.GoodPushSpeed, MbriHuntController.GoodPushSpeed),
            (second.Left, second.Right));
        // latch：之后无论视觉是否消失，自持前推（真车出口=铲子守卫，仿真=仲裁层掉台接管）。
        var latched = hunt.Update(null, NoNearIr, 4);
        Assert.Equal("GOOD_PUSH", latched.State);
        Assert.Equal((MbriHuntController.GoodPushSpeed, MbriHuntController.GoodPushSpeed),
            (latched.Left, latched.Right));
    }

    [Fact]
    public void Hunt_BigTurnThenLostHold_ThenRelease()
    {
        var hunt = new MbriHuntController();
        var acquire = hunt.Update(FrameAt(0, Det("good", 0.6)), NoNearIr, 0);
        Assert.Equal("BIG_TURN_RIGHT", acquire.State); // 0.6 ≥ enter → 原地大转（已锁定）
        Assert.True(acquire.GoodLocked);
        // 丢帧保留：2 帧内停车保留目标身份。
        var hold1 = hunt.Update(FrameAt(3), NoNearIr, 3);
        Assert.Equal("GOOD_LOST_HOLD", hold1.State);
        var hold2 = hunt.Update(FrameAt(6), NoNearIr, 6);
        Assert.Equal("GOOD_LOST_HOLD", hold2.State);
        Assert.Equal(2, hold2.GoodMissCount);
        // 第 3 帧仍无目标 → 解锁释放控制权。
        var release = hunt.Update(FrameAt(9), NoNearIr, 9);
        Assert.Equal("NO_TARGET", release.State);
        Assert.False(release.OwnsControl);
        Assert.False(release.GoodLocked);
    }

    [Fact]
    public void Hunt_LostHoldExpiresByTime()
    {
        var hunt = new MbriHuntController();
        _ = hunt.Update(FrameAt(0, Det("good", 0.0)), NoNearIr, 0);  // GOOD_CONFIRM（锁定）
        _ = hunt.Update(FrameAt(3, Det("good", 0.0)), NoNearIr, 3);  // GOOD_PUSH
        hunt.Cancel();
        // 锁定后目标消失：丢帧保留时长 0.35s=7 tick，超时即使帧数未满也解锁。
        _ = hunt.Update(FrameAt(0, Det("good", 0.3)), NoNearIr, 100); // 重新锁定（大转）
        var late = hunt.Update(FrameAt(36), NoNearIr, 111);          // 11 tick 无帧间隔 → elapsed>7
        Assert.Equal("NO_TARGET", late.State);
    }

    [Fact]
    public void Hunt_MinConfidenceFiltered()
    {
        var hunt = new MbriHuntController();
        var result = hunt.Update(FrameAt(0, Det("good", 0.0, confidence: 0.5)), NoNearIr, 0);
        Assert.Equal("NO_TARGET", result.State); // 0.5 < 0.55 → 视为无 good
        Assert.Null(result.GoodConfidence);
    }

    [Fact]
    public void Hunt_NearBadAvoidTurn90()
    {
        var hunt = new MbriHuntController();
        // 左前数字红外 + 画面左侧 bad（|offset| 在近物候选域）→ 近距 bad。
        var nearIr = new MbriDigiIr(false, false, LeftFront: true, LeftRear: false, RightFront: false, RightRear: false, true);
        var confirm1 = hunt.Update(FrameAt(0, Det("bad", -0.1)), nearIr, 0);
        Assert.Equal("BAD_CONFIRM", confirm1.State);
        Assert.True(confirm1.OwnsControl);
        // 第 2 个不同视觉帧即达确认帧数 → 原地转 90°（2 帧语义，真车 ~0.25s）。
        var confirm2 = hunt.Update(FrameAt(3, Det("bad", -0.1)), nearIr, 3);
        Assert.Equal("AVOID_TURN", confirm2.State);
        Assert.Equal("right", confirm2.TurnDirection); // bad 在左 → 右转避开（hunt.py:215-228）
        Assert.Equal((600, -600), (confirm2.Left, confirm2.Right));
        // 90°×0.65s=13 tick：期间保持转向，随后释放一帧。
        var holding = hunt.Update(FrameAt(9, Det("bad", -0.1)), nearIr, 9);
        Assert.Equal("AVOID_TURN", holding.State);
        var release = hunt.Update(FrameAt(39, Det("bad", -0.1)), nearIr, 19);
        Assert.Equal("AVOID_RELEASE", release.State);
        Assert.Equal((0, 0), (release.Left, release.Right));
        // 已避让未重新布防：近距 bad 仍在 → 交还控制权（不反复转）。
        var done = hunt.Update(FrameAt(42, Det("bad", -0.1)), nearIr, 20);
        Assert.Equal("AVOID_DONE", done.State);
        Assert.False(done.OwnsControl);
        // 目标离开后重新布防。
        var cleared = hunt.Update(FrameAt(45), NoNearIr, 21);
        Assert.False(cleared.OwnsControl);
        var rearmed = hunt.Update(FrameAt(48, Det("bad", -0.1)), nearIr, 22);
        Assert.Equal("BAD_CONFIRM", rearmed.State);
    }

    [Fact]
    public void Hunt_IrUnclassified_ReleasesToProbe()
    {
        var hunt = new MbriHuntController();
        var nearIr = new MbriDigiIr(false, false, LeftFront: true, LeftRear: false, RightFront: false, RightRear: false, true);
        // 近物方向存在但视觉无 good/bad → 交给敌人模块（probe）。
        var result = hunt.Update(FrameAt(0), nearIr, 0);
        Assert.Equal("IR_UNCLASSIFIED", result.State);
        Assert.False(result.OwnsControl);
    }

    [Fact]
    public void Hunt_Unhealthy_SafetyStop_AndCancel()
    {
        var hunt = new MbriHuntController();
        _ = hunt.Update(FrameAt(0, Det("good", 0.0)), NoNearIr, 0);
        var stopped = hunt.Update(FrameAt(3, Det("good", 0.0)), NoNearIr, 3, healthy: false);
        Assert.Equal("SENSOR_STOP", stopped.State);
        Assert.Equal("IDLE", hunt.State);
    }

    [Fact]
    public void Hunt_FinishPush_DisarmsUntilGoodDisappears()
    {
        var hunt = new MbriHuntController();
        _ = hunt.Update(FrameAt(0, Det("good", 0.0)), NoNearIr, 0);
        _ = hunt.Update(FrameAt(3, Det("good", 0.0)), NoNearIr, 3);
        Assert.Equal("GOOD_PUSH", hunt.State);
        hunt.FinishPush();
        Assert.Equal("IDLE", hunt.State);
        // 同一 good 仍在 → 等待消失（防止立即重推）。
        var wait = hunt.Update(FrameAt(6, Det("good", 0.0)), NoNearIr, 6);
        Assert.Equal("GOOD_REARM_WAIT", wait.State);
        Assert.False(wait.OwnsControl);
        // good 消失 → 重新布防。
        var gone = hunt.Update(FrameAt(9), NoNearIr, 9);
        Assert.Equal("NO_TARGET", gone.State);
        var fresh = hunt.Update(FrameAt(12, Det("good", 0.0)), NoNearIr, 12);
        Assert.Equal("GOOD_CONFIRM", fresh.State);
    }

    [Fact]
    public void Hunt_ConstructorRejectsInvalidTickSeconds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MbriHuntController(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MbriHuntController(double.NaN));
    }

    // ---------- MbriProbeController ----------

    private static MbriProbeVision ProbeVision(long? seq, string status, bool good = false, bool bad = false)
        => new(seq, status, good, bad);

    [Fact]
    public void Probe_BrakeTurnVisionWait_ThenEnemyPush()
    {
        var probe = new MbriProbeController();
        var obs = new MbriGrayObservation { Zone = new MbriGraySample(2.0, 0.5, 2.0, 2.0) };
        var leftIr = new MbriDigiIr(false, false, LeftFront: true, LeftRear: false, RightFront: false, RightRear: false, true);
        var frontIr = new MbriDigiIr(false, false, LeftFront: true, LeftRear: false, RightFront: true, RightRear: false, true);
        // t=0：左前候选 → 刹车归 0（0.2s=4 tick）。
        var brake = probe.Update(leftIr, obs, ProbeVision(0, MbriVisionFrame.StatusNoTarget), 0);
        Assert.Equal("PROBE_BRAKE", brake.State);
        Assert.Equal((0, 0), (brake.Left, brake.Right));
        Assert.Equal("left_front", brake.SourceDirection);
        // t=1：视觉出现 good → 取消候选（侧向解除布防）。
        var goodSeen = probe.Update(leftIr, obs, ProbeVision(3, MbriVisionFrame.StatusTarget, good: true), 1);
        Assert.Equal("IDLE", goodSeen.State);
        Assert.Equal("good", goodSeen.VisionVerdict);
        // t=2..4：红外清空 3 帧 → 侧向重新布防。
        var clearIr = new MbriDigiIr(false, false, false, false, false, false, true);
        for (var t = 2; t <= 4; t++)
        {
            var idle = probe.Update(clearIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), t);
            Assert.Equal("IDLE", idle.State);
        }
        // t=5：候选重现 → 刹车至 t=9。
        Assert.Equal("PROBE_BRAKE", probe.Update(leftIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), 5).State);
        for (var t = 6; t <= 8; t++)
        {
            Assert.Equal("PROBE_BRAKE", probe.Update(leftIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), t).State);
        }
        // t=9：定角转向 left 45°（600×0.55s=11 tick → 至 t=20）。
        var turning = probe.Update(leftIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), 9);
        Assert.Equal("PROBE_TURN", turning.State);
        Assert.Equal("left", turning.TurnDirection);
        Assert.Equal((-600, 600), (turning.Left, turning.Right));
        for (var t = 10; t <= 19; t++)
        {
            Assert.Equal("PROBE_TURN", probe.Update(leftIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), t).State);
        }
        // t=20：转向完成 → 停车等视觉；此后目标已转正（front=左前&&右前）。
        var wait = probe.Update(frontIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), 20);
        Assert.Equal("PROBE_VISION_WAIT", wait.State);
        Assert.Equal("waiting", wait.VisionVerdict);
        // 3 个不同 no_target 帧逐帧确认 → ENEMY_PUSH。
        var c1 = probe.Update(frontIr, obs, ProbeVision(9, MbriVisionFrame.StatusNoTarget), 21);
        Assert.Contains("no_target_1", c1.VisionVerdict);
        var c2 = probe.Update(frontIr, obs, ProbeVision(12, MbriVisionFrame.StatusNoTarget), 22);
        Assert.Contains("no_target_2", c2.VisionVerdict);
        var push = probe.Update(frontIr, obs, ProbeVision(15, MbriVisionFrame.StatusNoTarget), 23);
        Assert.Equal("ENEMY_PUSH", push.State);
        Assert.Equal((0, 0), (push.Left, push.Right)); // _enter 置 (0,0)，推速下一拍生效（真车语义）
        Assert.False(push.Confirmed); // _enter 重置（Confirmed 仅 finish_push 回写）
        // 推敌中：zone front 远离台沿 → 保持 700。
        var pushing = probe.Update(frontIr, obs, ProbeVision(18, MbriVisionFrame.StatusNoTarget), 24);
        Assert.Equal((700, 700), (pushing.Left, pushing.Right));
    }

    [Fact]
    public void Probe_EnemyPush_SlowsNearEdge_AndAbortsRearEdge()
    {
        var probe = new MbriProbeController();
        var obs = new MbriGrayObservation { Zone = new MbriGraySample(2.0, 0.5, 2.0, 2.0) };
        var frontIr = new MbriDigiIr(false, false, LeftFront: true, LeftRear: false, RightFront: true, RightRear: false, true);
        // 压缩注入：正前候选 → 视觉等待 → 3 个不同 no_target 帧 → ENEMY_PUSH。
        Assert.Equal("PROBE_VISION_WAIT", probe.Update(frontIr, obs, ProbeVision(0, MbriVisionFrame.StatusNoTarget), 0).State);
        _ = probe.Update(frontIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), 1);
        _ = probe.Update(frontIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), 2);
        var push = probe.Update(frontIr, obs, ProbeVision(9, MbriVisionFrame.StatusNoTarget), 3);
        Assert.Equal("ENEMY_PUSH", push.State);
        // front zone < 1.3 连续 6 帧 → slow 550（第 6 帧起）。
        var nearEdge = new MbriGrayObservation { Zone = new MbriGraySample(1.0, 0.5, 2.0, 2.0) };
        for (var t = 4; t <= 8; t++)
        {
            var fast = probe.Update(frontIr, nearEdge, ProbeVision(9, MbriVisionFrame.StatusNoTarget), t);
            Assert.Equal((700, 700), (fast.Left, fast.Right));
        }
        var slow = probe.Update(frontIr, nearEdge, ProbeVision(12, MbriVisionFrame.StatusNoTarget), 9);
        Assert.True(slow.Slow);
        Assert.Equal((MbriProbeController.SlowSpeed, MbriProbeController.SlowSpeed), (slow.Left, slow.Right));
        // 后路 zone ≤ −0.45 → 中断进冷却。
        var rearEdge = new MbriGrayObservation { Zone = new MbriGraySample(1.0, -0.5, 2.0, 2.0) };
        var aborted = probe.Update(frontIr, rearEdge, ProbeVision(15, MbriVisionFrame.StatusNoTarget), 10);
        Assert.Equal("COOLDOWN", aborted.State);
        Assert.Equal("后路接近台外，中断推动", aborted.Reason);
        // 冷却 3s=60 tick → 释放。
        var cooling = probe.Update(frontIr, obs, ProbeVision(15, MbriVisionFrame.StatusNoTarget), 40);
        Assert.Equal("COOLDOWN", cooling.State);
        var released = probe.Update(frontIr, obs, ProbeVision(15, MbriVisionFrame.StatusNoTarget), 70);
        Assert.Equal("IDLE", released.State);
        Assert.False(released.OwnsControl);
    }

    [Fact]
    public void Probe_VisionWaitRejectsGoodBadAndTimesOut()
    {
        var probe = new MbriProbeController();
        var obs = new MbriGrayObservation { Zone = new MbriGraySample(2.0, 0.5, 2.0, 2.0) };
        var frontIr = new MbriDigiIr(false, false, LeftFront: true, LeftRear: false, RightFront: true, RightRear: false, true);
        // t=0：正前候选 → 视觉等待。
        var wait = probe.Update(frontIr, obs, ProbeVision(0, MbriVisionFrame.StatusNoTarget), 0);
        Assert.Equal("PROBE_VISION_WAIT", wait.State);
        // t=1：bad 出现 → 取消（前向解除布防）。
        var bad = probe.Update(frontIr, obs, ProbeVision(3, MbriVisionFrame.StatusTarget, bad: true), 1);
        Assert.Equal("IDLE", bad.State);
        Assert.Equal("bad", bad.VisionVerdict);
        // t=2..4：红外清空 3 帧 → 前向重新布防。
        var clearIr = new MbriDigiIr(false, false, false, false, false, false, true);
        for (var t = 2; t <= 4; t++)
        {
            _ = probe.Update(clearIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), t);
        }
        // t=5：重新候选 → 等待；同 seq 帧不推进确认，0.6s=12 tick 超时取消。
        Assert.Equal("PROBE_VISION_WAIT", probe.Update(frontIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), 5).State);
        for (var t = 6; t <= 16; t++)
        {
            Assert.Equal("PROBE_VISION_WAIT", probe.Update(frontIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), t).State);
        }
        var timedOut = probe.Update(frontIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), 17);
        Assert.Equal("IDLE", timedOut.State);
        Assert.Equal("timeout", timedOut.VisionVerdict);
    }

    [Fact]
    public void Probe_BadInterruptDuringPush()
    {
        var probe = new MbriProbeController();
        var obs = new MbriGrayObservation { Zone = new MbriGraySample(2.0, 0.5, 2.0, 2.0) };
        var frontIr = new MbriDigiIr(false, false, LeftFront: true, LeftRear: false, RightFront: true, RightRear: false, true);
        Assert.Equal("PROBE_VISION_WAIT", probe.Update(frontIr, obs, ProbeVision(0, MbriVisionFrame.StatusNoTarget), 0).State);
        _ = probe.Update(frontIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), 1);
        _ = probe.Update(frontIr, obs, ProbeVision(6, MbriVisionFrame.StatusNoTarget), 2);
        var push = probe.Update(frontIr, obs, ProbeVision(9, MbriVisionFrame.StatusNoTarget), 3);
        Assert.Equal("ENEMY_PUSH", push.State);
        // 单帧 bad 不打断；连续 2 个不同帧 bad → 取消。
        var first = probe.Update(frontIr, obs, ProbeVision(12, MbriVisionFrame.StatusTarget, bad: true), 4);
        Assert.Equal("ENEMY_PUSH", first.State);
        Assert.Equal(1, first.BadInterruptCount);
        var second = probe.Update(frontIr, obs, ProbeVision(15, MbriVisionFrame.StatusTarget, bad: true), 5);
        Assert.Equal("IDLE", second.State);
        Assert.Equal("bad_confirmed", second.VisionVerdict);
        Assert.Equal(2, second.BadInterruptCount);
    }

    [Fact]
    public void Probe_RearCandidate_TurnPlanAlternates()
    {
        // rear 分支集成不可达（仿真无正后近物通道），注入验证移植完整性。
        var probe = new MbriProbeController();
        var obs = new MbriGrayObservation { Zone = new MbriGraySample(2.0, 0.5, 2.0, 2.0) };
        var rearIr = new MbriDigiIr(false, Rear: true, false, false, false, false, true);
        var brake = probe.Update(rearIr, obs, ProbeVision(0, MbriVisionFrame.StatusNoTarget), 0);
        Assert.Equal("PROBE_BRAKE", brake.State);
        Assert.Equal("rear", brake.SourceDirection);
        var turn = probe.Update(rearIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), 4);
        Assert.Equal("PROBE_TURN", turn.State);
        Assert.Equal("right", turn.TurnDirection); // PROBE_REAR_FIRST_TURN=right
        var holding = probe.Update(rearIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), 5);
        Assert.Equal("PROBE_TURN", holding.State);
        Assert.Equal("right", holding.TurnDirection);
        // 第二次 rear 候选 → 交替为 left（fallback 翻转不受 cancel 重置，真车语义）。
        // 侧向布防需 3 帧红外清空（StartBrake 解除布防，_update_rearm 语义）。
        probe.Cancel();
        var clearIr = new MbriDigiIr(false, false, false, false, false, false, true);
        for (var t = 197; t <= 199; t++)
        {
            _ = probe.Update(clearIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), t);
        }
        _ = probe.Update(rearIr, obs, ProbeVision(0, MbriVisionFrame.StatusNoTarget), 200);
        var turn3 = probe.Update(rearIr, obs, ProbeVision(3, MbriVisionFrame.StatusNoTarget), 204);
        Assert.Equal("PROBE_TURN", turn3.State);
        Assert.Equal("left", turn3.TurnDirection);
    }

    // ---------- MbriFsm P2 仲裁 ----------

    /// <summary>构造已发令、巡台在 MEDIUM_CRUISE（台心灰度）的仲裁环境。</summary>
    private static (MbriFsmController Fsm, RobotRuntime Robot, EventBus Events) ArmedFsm()
    {
        var events = new EventBus();
        var fsm = new MbriFsmController(events);
        var robot = NewRobot();
        SetGray(robot, 650, 650, 650, 650);
        fsm.Arm();
        for (long t = 0; t <= 36; t++)
        {
            fsm.TickFor(robot, t);
        }
        return (fsm, robot, events);
    }

    [Fact]
    public void Arbitration_HuntEngagesInCruise_ConfirmThenPush()
    {
        var (fsm, robot, events) = ArmedFsm();
        fsm.VisionSource = (_, tick) => FrameAt(tick, Det("good", 0.0));
        // 帧时钟：tick 37/38 同帧（seq 12）→ GOOD_CONFIRM；tick 39 新帧（seq 13）→ GOOD_PUSH。
        fsm.TickFor(robot, 37);
        Assert.Equal("GOOD_CONFIRM", fsm.MbriState);
        Assert.Equal(FsmState.Search, robot.Fsm.State);
        Assert.Equal(0.0, robot.V, 9);
        fsm.TickFor(robot, 38);
        Assert.Equal("GOOD_CONFIRM", fsm.MbriState); // 同帧去重
        fsm.TickFor(robot, 39);
        Assert.Equal("GOOD_PUSH", fsm.MbriState);
        Assert.Equal(FsmState.Attack, robot.Fsm.State);
        Assert.Equal(0.3584, robot.V, 9); // 400×k
        Assert.Equal(0.0, robot.W, 12);
        // 特权观测披露事件（事件流必须披露）。
        Assert.Contains(events.Events, e => e.Msg.Contains("视觉输入披露") && e.Msg.Contains("特权观测"));
        var huntEvent = events.Events.First(e => e.Msg.StartsWith("[mbri-hunt] GOOD_PUSH"));
        Assert.Contains("privileged-truth", System.Text.Json.JsonSerializer.Serialize(huntEvent.Data));
    }

    [Fact]
    public void Arbitration_PushLatchSurvivesVisionLoss_ReentryCancelsOnFall()
    {
        var (fsm, robot, _) = ArmedFsm();
        fsm.VisionSource = (_, tick) => FrameAt(tick, Det("good", 0.0));
        for (var t = 37; t <= 39; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("GOOD_PUSH", fsm.MbriState);
        // 视觉消失：推击 latch 不依赖视觉，自持（真车出口=铲子守卫，仿真=掉台接管）。
        fsm.VisionSource = null;
        fsm.TickFor(robot, 40);
        Assert.Equal("GOOD_PUSH", fsm.MbriState);
        // 掉台（走道灰度 → fall 域全暗 3 帧）→ reentry 接管并 cancel 推击。
        SetGray(robot, 0, 0, 0, 0);
        long takeoverTick = -1;
        for (var t = 41; t <= 60; t++)
        {
            fsm.TickFor(robot, t);
            if (fsm.ReentryActive && takeoverTick < 0)
            {
                takeoverTick = t;
            }
        }
        Assert.True(takeoverTick > 0, "推击出台沿后应由 reentry 接管（守卫 no-op 的有界出口）");
        Assert.Equal("IDLE", fsm.Hunt.State);
        Assert.Equal(FsmState.Recover, robot.Fsm.State);
    }

    [Fact]
    public void Arbitration_PatrolGate_BlocksHuntOutsideCruise()
    {
        var (fsm, robot, _) = ArmedFsm();
        fsm.VisionSource = (_, tick) => FrameAt(tick, Det("good", 0.0));
        // 走道灰度 → 前向风险 → EDGE_AVOID（非 CRUISE 级）→ 双 cancel 回巡台。
        SetGray(robot, 0, 0, 0, 0);
        fsm.TickFor(robot, 37);
        fsm.TickFor(robot, 38);
        Assert.Equal("EDGE_AVOID", fsm.MbriState);
        Assert.Equal("IDLE", fsm.Hunt.State);
        Assert.Equal("IDLE", fsm.Probe.State);
        // 回到台心 → 巡台避险流程走完回 CRUISE 级 → hunt 重新接洽。
        SetGray(robot, 650, 650, 650, 650);
        var reengaged = false;
        for (var t = 39; t <= 220 && !reengaged; t++)
        {
            fsm.TickFor(robot, t);
            reengaged = fsm.MbriState is "GOOD_CONFIRM" or "GOOD_PUSH";
        }
        Assert.True(reengaged, "回到 CRUISE 级后 hunt 应重新接洽");
    }

    [Fact]
    public void Arbitration_HuntRelease_ResetsPatrolHandoff()
    {
        var (fsm, robot, _) = ArmedFsm();
        // good 在视场边缘（大转态，永不居中）→ 目标消失后超时解锁释放。
        fsm.VisionSource = (_, tick) => FrameAt(tick, Det("good", 0.6));
        fsm.TickFor(robot, 37);
        Assert.Equal("BIG_TURN_RIGHT", fsm.MbriState);
        Assert.Equal(FsmState.Search, robot.Fsm.State);
        fsm.VisionSource = null;
        var sawHold = false;
        string releasedState = "";
        for (var t = 38; t <= 90; t++)
        {
            fsm.TickFor(robot, t);
            sawHold |= fsm.MbriState == "GOOD_LOST_HOLD";
            if (sawHold && fsm.MbriState is "WARMUP" or "MEDIUM_CRUISE" or "CRUISE")
            {
                releasedState = fsm.MbriState;
                break;
            }
        }
        Assert.True(sawHold, "释放前应先进入 GOOD_LOST_HOLD 保留目标身份");
        Assert.Equal("WARMUP", releasedState); // 交还帧=新巡台实例（main.py:353-356 滤波重填）
        Assert.False(fsm.Probe.Active);
    }

    [Fact]
    public void Arbitration_ProbeTakesOverWhenNoGoodVisible()
    {
        var (fsm, robot, _) = ArmedFsm();
        // 无 good 视觉 + 左前数字红外 → probe 刹车/转向接管（hunt 无目标不拥有控制权）。
        fsm.VisionSource = (_, tick) => FrameAt(tick);
        robot.Sens["dLF"] = 1.0;
        fsm.TickFor(robot, 37);
        Assert.Equal("PROBE_BRAKE", fsm.MbriState);
        Assert.Equal(FsmState.Search, robot.Fsm.State);
        Assert.True(fsm.Probe.Active);
        // 转向（至 t=51）完成后目标应转正（右前对角也亮）→ 3 个不同 no_target 帧 → 推敌。
        robot.Sens["dRF"] = 1.0;
        for (var t = 38; t <= 60; t++)
        {
            fsm.TickFor(robot, t);
        }
        Assert.Equal("ENEMY_PUSH", fsm.Probe.State);
        Assert.Equal(FsmState.Attack, robot.Fsm.State);
        // 巡台观测 zone front≈1.0 < ENEMY_SLOW_ZONE(1.3) → 近台沿降速 550（真车语义）。
        Assert.Equal(0.4928, robot.V, 9); // 550×k
    }

    [Fact]
    public void SnapshotMapping_P2States()
    {
        Assert.Equal(FsmState.Attack, MbriFsmController.SnapshotState("GOOD_PUSH"));
        Assert.Equal(FsmState.Attack, MbriFsmController.SnapshotState("ENEMY_PUSH"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("BAD_CONFIRM"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("GOOD_ACQUIRE"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("GOOD_CONFIRM"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("GOOD_LOST_HOLD"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("GOOD_REARM_WAIT"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("IR_UNCLASSIFIED"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("AVOID_TURN"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("BIG_TURN_LEFT"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("ARC_RIGHT"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("PROBE_BRAKE"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("PROBE_TURN"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("PROBE_VISION_WAIT"));
        Assert.Equal(FsmState.Search, MbriFsmController.SnapshotState("COOLDOWN"));
    }

    // ---------- 双跑确定性 ----------

    /// <summary>确定性视觉/灰度/红外脚本：相位表显式，无随机源。</summary>
    private static void ScriptInputs(RobotRuntime robot, long tick, out MbriVisionFrame? frame)
    {
        var phase = (tick / 24) % 6;
        frame = phase switch
        {
            0 or 1 => FrameAt(tick, Det("good", phase == 0 ? 0.0 : 0.3)),
            2 => FrameAt(tick, Det("bad", -0.1)),
            3 => FrameAt(tick),
            4 => FrameAt(tick, Det("good", 0.6)),
            _ => null, // 视觉断流相位
        };
        var g = phase is 0 or 1 or 4 ? 650.0 : 1000.0;
        SetGray(robot, g, g, g, g);
        robot.Sens["dLF"] = phase == 2 ? 1.0 : 0.0;
        robot.Sens["dRF"] = phase == 2 ? 1.0 : 0.0;
    }

    [Fact]
    public void DualRun_HuntArbitration_FingerprintsIdentical()
    {
        var runs = new List<(List<string> Prints, List<string> Messages)>();
        for (var run = 0; run < 2; run++)
        {
            var events = new EventBus();
            var fsm = new MbriFsmController(events);
            var robot = NewRobot();
            var prints = new List<string>();
            fsm.Arm();
            for (long t = 0; t < 600; t++)
            {
                ScriptInputs(robot, t, out var frame);
                fsm.VisionSource = (_, _) => frame;
                fsm.TickFor(robot, t);
                prints.Add($"{t}|{fsm.MbriState}|{fsm.Hunt.State}|{fsm.Probe.State}|{fsm.Patrol.State}|" +
                           $"{robot.V:R}|{robot.W:R}|{robot.Fsm.State}|{robot.Fsm.Action}");
            }
            runs.Add((prints, events.Events.Select(e => e.Msg).ToList()));
        }
        Assert.Equal(runs[0].Prints, runs[1].Prints);
        Assert.Equal(runs[0].Messages, runs[1].Messages);
        // 冒烟：脚本确实驱动了 hunt/probe/patrol 多状态（非恒一状态空转）。
        // 状态集 = MbriState ∪ hunt 内部态 ∪ probe 内部态（hunt 不拥有控制权时
        // MbriState 显示接管者——AVOID_DONE/NO_TARGET 等释放态被同拍接管者覆盖，
        // 由 Hunt_NearBadAvoidTurn90 直接断言，不进本冒烟）。
        // （脚本相位下巡台只在 g=1000 相位露出 → CRUISE；MEDIUM_CRUISE 不出现。）
        var states = runs[0].Prints
            .SelectMany(p => p.Split('|')[1..4])
            .Distinct()
            .ToList();
        Assert.Contains("START_REVERSE", states);
        Assert.Contains("CRUISE", states);
        Assert.Contains("ARC_RIGHT", states);
        Assert.Contains("BIG_TURN_RIGHT", states);
        Assert.Contains("BAD_CONFIRM", states);
        Assert.Contains("AVOID_TURN", states);
        Assert.Contains("AVOID_RELEASE", states);
        Assert.Contains("GOOD_LOST_HOLD", states);
        Assert.Contains("PROBE_VISION_WAIT", states);
        Assert.Contains("WARMUP", states);
    }

    // ---------- 端到端接线（MatchEngine 视觉源 + 得分路径冒烟） ----------

    [Fact]
    public void MatchEngine_MbriScenario_WiresTruthVisionSource_AndScores()
    {
        // mbri 场景 → CreateMbriController 接好 ObjectSet 真值视觉源（特权观测）；
        // 场景校验要求双角色 vehicles 齐备，them 保持默认 builtin。
        // 起始位姿朝向场内（开局后退上台后块在相机锥内）——官方默认起始位姿
        // （车尾朝台沿）下巡台避险循环占主导、CRUISE 窗与块可见不重合，hunt
        // 不接洽（行为对照披露见 BatchReceipt；门禁语义=HUNT_ALLOWED_PATROL_STATES
        // 逐字，不放水）。
        var scenario = new Scenario
        {
            Seed = 42,
            Field = new FieldParams
            {
                MatchDuration = 40,
                Starts = new Dictionary<string, Pose2>(StringComparer.Ordinal)
                {
                    [RoleNames.Us] = new Pose2 { X = 1.9, Y = 1.2, Th = -Math.PI / 2 },
                    [RoleNames.Them] = new Pose2 { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
                },
            },
            Blocks =
            [
                new BlockSpec { Kind = BlockKind.Buff, X = 1.9, Y = 2.2 },
                new BlockSpec { Kind = BlockKind.Buff, X = 2.5, Y = 2.6 },
                new BlockSpec { Kind = BlockKind.Debuff, X = 1.2, Y = 1.0 },
            ],
            Vehicles = new Dictionary<string, VehicleProfile>(StringComparer.Ordinal)
            {
                [RoleNames.Us] = new VehicleProfile { Controller = VehicleControllers.Mbri },
                [RoleNames.Them] = new VehicleProfile(),
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        engine.Arm();
        var messages = new List<string>();
        for (var i = 0; i < 800 && !engine.Done; i++)
        {
            var snapshot = engine.Tick();
            if (snapshot.Events is { Count: > 0 } events)
            {
                messages.AddRange(events.Select(e => e.Msg ?? ""));
            }
        }
        // 特权观测披露事件进了事件流（要求：头注释与事件流必须披露）。
        Assert.Contains(messages, m => m.Contains("视觉输入披露") && m.Contains("特权观测"));
        // 视觉追击全链路在真实引擎里被驱动：大转/弧线对准 → 居中确认 → 推块
        // （协议 Msg 带 "[我方] " 前缀，用 Contains）。
        Assert.Contains(messages, m => m.Contains("[mbri-hunt]", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("[mbri-hunt] GOOD_PUSH", StringComparison.Ordinal));
        // 得分路径打通：推增益块下台 → 我方 +3（BlockScore 判分与控制器解耦）。
        Assert.Contains(messages, m => m.Contains("增益块被推下擂台! 我方 +3", StringComparison.Ordinal));
    }
}
