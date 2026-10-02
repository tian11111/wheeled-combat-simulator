namespace Sim.Core;

using Sim.Protocol;

/// <summary>
/// P2 视觉追击：仿真 ObjectSet 真值 → 真车 YOLO 视觉语义投影 + hunt.py 状态机移植。
///
/// ⚠️ 特权观测披露（本文件最核心的语义声明）：
/// 真车视觉 = 相机 + YOLO26n(320) 检测流（rpi-yolo-pi4-int8-lto-8fps，~8 FPS，
/// hunt.py 只消费注入帧）。仿真没有相机/推理，本投影直接读取引擎已知真值
/// （<see cref="BlockRuntime"/> 位置/类别/<see cref="FieldModel.OnPlatform"/>），
/// 是<strong>特权观测</strong>：无遮挡、无噪声、无漏检、无误检建模，检测置信度为
/// 常数（真车 69 个 good 样本实测 0.607~0.971，config.py:208）。事件流以
/// vision="privileged-truth" 字段与首次接管时的显式披露事件标注（MbriFsm）。
/// 对局解读时必须把 hunt 的得分能力视为"上帝视觉版真车追击"。
///
/// 投影规则（真车视觉契约 = hunt.py 消费的 frame dict，字段语义与
/// tools/yolo-bridge/mbri_yolo_bridge.py 契约一致）：
///   - 类别映射：buff→"good"、debuff→"bad"（CLASS_LABELS 0/1，mbri_yolo_bridge.py:46）；
///   - 可见性：仅"在台上且未出台"的块可见——已出台(out)块走道可见性不建模，且
///     仿真无铲子守卫（推击期 no-op，见 MbriFsm 注释），投影已出台块会让 CRUISE
///     级 hunt 追出台沿；此处过滤是"无铲子守卫简化的补偿语义"，照实披露；
///   - 视场：真车相机水平视场无实测锚点，取水平半视场 45°（本投影唯一自造
///     几何常数，照实披露）；锥外块不出现在帧内；
///   - offset_x：目标横向偏移归一化 [-1,1]，正=画面右侧（vision_tracker.py:119
///     "error > 0 → right"语义）；由方位角 β 投影 offset_x=−sin(β)/sin(半视场)
///     （仿真 heading 逆时针为正，与真车画面 x 轴反向，故取负号）；
///   - target：帧内最近块（真车 _select 按最大 bbox 面积≈最近目标，hunt.py:94-104）；
///   - sequence：帧时钟——真车 YOLO ~8 FPS（config.py:192 注释），按既定
///     时长→tick 换算（AwayFromZero）得 3 tick/帧（0.05s tick ⇒ ≈6.7 FPS），
///     同帧周期内复用同一 sequence（hunt 的"不同视觉帧"确认逻辑据此去重）；
///     帧周期内目标移动以最新真值呈现（帧号语义近似，照实披露）；
///   - status：有检测 → "target"，否则 "no_target"；真车另有 error/stale 态，
///     仿真投影恒新鲜，不建模帧龄/断流（VISION_MAX_AGE_MS 语义不适用，披露）。
///
/// 移植范围：vision_tracker.py:18-127（MbriVisionTracker：死区/大转滞回/小弧线/
/// 直线接近）与 hunt.py:23-479（MbriHuntController：good 追击确认/推块 400/
/// 近距 bad 避让/丢帧保留/再布防）。参数 config.py:191-215 逐字。
/// 铁律：零 IO/零时钟/零随机——真车 time.monotonic 全部由调用方按 tick 注入，
/// wall-clock 时长经 MbriUnits.SecondsToTicks 换算为 tick 阈值。
/// </summary>
public sealed record MbriVisionDetection(string Type, double OffsetX, double Confidence);

/// <summary>真车 YOLO 视觉帧的 C# 投影（hunt.py 消费的 raw dict；只保留被消费字段）。</summary>
public sealed record MbriVisionFrame(
    long? Sequence,
    string Status,
    IReadOnlyList<MbriVisionDetection> Detections,
    MbriVisionDetection? Target)
{
    public const string StatusTarget = "target";
    public const string StatusNoTarget = "no_target";
    public const string StatusStale = "stale";

    /// <summary>帧内是否出现 good（含 target；main.py:153-161 vision_good 语义）。</summary>
    public bool HasGood => (Target is not null && Target.Type == "good")
                           || Detections.Any(d => d.Type == "good");

    /// <summary>帧内是否出现 bad（含 target；main.py:162-165 vision_bad 语义）。</summary>
    public bool HasBad => (Target is not null && Target.Type == "bad")
                          || Detections.Any(d => d.Type == "bad");
}

/// <summary>
/// ObjectSet 真值 → 视觉帧投影（纯函数；特权观测语义见文件头披露）。
/// </summary>
public static class MbriVisionProjector
{
    /// <summary>真车 YOLO 检测流频率目标（config.py:192 "YOLO 实际约 8 FPS"）。</summary>
    public const double RealFrameHz = 8.0;

    /// <summary>
    /// 水平半视场（rad，45°）——真车相机视场无实测锚点的工程默认，
    /// 本投影唯一自造几何常数（见文件头披露）。
    /// </summary>
    public const double HalfFovRadians = Math.PI / 4.0;

    /// <summary>
    /// 检测置信度常数（真车 69 个 good 样本 0.607~0.971 区间内；09-25 桥先例
    /// MBri/sim_bridge.py:72-76 即以常数 0.9 投影真值检测）。
    /// ≥ HUNT_GOOD_HIGH_CONFIDENCE(0.80) ⇒ 集成路径 good 直接触发锁定，
    /// GOOD_ACQUIRE（中置信确认）分支仅在注入更低置信度时可达（单测覆盖）。
    /// </summary>
    public const double DetectionConfidence = 0.9;

    /// <summary>帧周期（tick）＝1/8s → tick（既定换算 AwayFromZero；0.05s tick ⇒ 3）。</summary>
    public static long FrameIntervalTicks(double tickSeconds = MbriUnits.DefaultTickSeconds)
        => Math.Max(1, MbriUnits.SecondsToTicks(1.0 / RealFrameHz, tickSeconds));

    /// <summary>
    /// 投影一帧。<paramref name="tick"/> 为仿真 tick（帧时钟与"当前时刻"同源）；
    /// blocks 顺序 = 引擎 _blocks 列表序（确定性）。target = 帧内最近块（并列取先）。
    /// </summary>
    public static MbriVisionFrame Project(
        RobotRuntime observer, IReadOnlyList<BlockRuntime> blocks, FieldModel field,
        long tick, double tickSeconds = MbriUnits.DefaultTickSeconds)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(field);
        var detections = new List<MbriVisionDetection>();
        MbriVisionDetection? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        foreach (var block in blocks)
        {
            // 已出台块不投影（文件头"补偿语义"披露）；台外块（在走道未判 out 前
            // 亦不投影——真车比赛语义中台上块才可推击得分）。
            if (block.Out || !field.OnPlatform(block.X, block.Y))
            {
                continue;
            }
            var dx = block.X - observer.X;
            var dy = block.Y - observer.Y;
            var distance = Js.Hypot(dx, dy);
            var bearing = Js.Norm(Math.Atan2(dy, dx) - observer.Th);
            if (Math.Abs(bearing) > HalfFovRadians)
            {
                continue;   // 相机锥外：真车不可见
            }
            // offset_x 正=画面右侧；仿真 heading 逆时针为正 ⇒ 取负号（文件头披露）。
            var offsetX = Js.Clamp(-Math.Sin(bearing) / Math.Sin(HalfFovRadians), -1.0, 1.0);
            var type = block.Kind == BlockKind.Buff ? "good" : "bad";
            var detection = new MbriVisionDetection(type, offsetX, DetectionConfidence);
            detections.Add(detection);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = detection;
            }
        }
        var sequence = tick / FrameIntervalTicks(tickSeconds);
        var status = detections.Count > 0 ? MbriVisionFrame.StatusTarget : MbriVisionFrame.StatusNoTarget;
        return new MbriVisionFrame(sequence, status, detections, nearest);
    }
}

/// <summary>vision_tracker.py update 返回 dict 的 C# 投影。</summary>
public sealed record MbriTrackerResult
{
    public int Left { get; init; }

    public int Right { get; init; }

    public string State { get; init; } = "";

    public string Reason { get; init; } = "";

    public double? ErrorX { get; init; }

    public int TurnCommand { get; init; }
}

/// <summary>
/// YOLO good 能量块追踪控制逐行移植（vision_tracker.py:18-127，纯状态机）：
/// 按归一化横向误差选择原地大转（400）、小弧线（500/400 差速）或直线接近（450）；
/// 大转方向滞回（enter 0.55 / clear 0.35）防止大小转反复切换。
/// 参数 config.py:191-200。
/// </summary>
public sealed class MbriVisionTracker
{
    public const double DeadZone = 0.08;        // VISION_DEAD_ZONE
    public const double BigTurnEnter = 0.55;    // VISION_BIG_TURN_ENTER
    public const double BigTurnClear = 0.35;    // VISION_BIG_TURN_CLEAR
    public const int BigTurnSpeed = 400;        // VISION_BIG_TURN_SPEED
    public const int ArcInnerSpeed = 400;       // VISION_ARC_INNER_SPEED
    public const int ArcOuterSpeed = 500;       // VISION_ARC_OUTER_SPEED
    public const int ApproachSpeed = 450;       // VISION_APPROACH_SPEED

    /// <summary>大转方向滞回记忆（真车公开属性：hunt.cancel 会重置，vision_tracker.py:44）。</summary>
    public string? BigTurnDirection { get; set; }

    public MbriVisionTracker()
    {
        // vision_tracker.py:29-30 阈值顺序校验（常数组合编译期固定，构造期断言防御）。
        if (!(0.0 <= DeadZone && DeadZone < BigTurnClear && BigTurnClear < BigTurnEnter && BigTurnEnter <= 1.0))
        {
            throw new InvalidOperationException("视觉死区和大小转阈值顺序无效");
        }
    }

    private static MbriTrackerResult Stop(string state, string reason)
    {
        return new MbriTrackerResult { Left = 0, Right = 0, State = state, Reason = reason };
    }

    private MbriTrackerResult BigTurn(string direction, double? error)
    {
        var speed = BigTurnSpeed;
        var (left, right, turn) = direction == "right" ? (speed, -speed, speed) : (-speed, speed, -speed);
        BigTurnDirection = direction;
        return new MbriTrackerResult
        {
            Left = left,
            Right = right,
            State = direction == "right" ? "BIG_TURN_RIGHT" : "BIG_TURN_LEFT",
            Reason = "目标偏差较大，原地大转",
            ErrorX = error,
            TurnCommand = turn,
        };
    }

    private static MbriTrackerResult Arc(string direction, double? error)
    {
        var (left, right, turn) = direction == "right"
            ? (ArcOuterSpeed, ArcInnerSpeed, ArcOuterSpeed - ArcInnerSpeed)
            : (ArcInnerSpeed, ArcOuterSpeed, ArcInnerSpeed - ArcOuterSpeed);
        return new MbriTrackerResult
        {
            Left = left,
            Right = right,
            State = direction == "right" ? "ARC_RIGHT" : "ARC_LEFT",
            Reason = "持续差速前进并小幅对准",
            ErrorX = error,
            TurnCommand = turn,
        };
    }

    /// <summary>vision_tracker.py:94-127 update 逐行移植（control 字段展开为参数）。</summary>
    public MbriTrackerResult Update(bool valid, string action, string? targetType, double? offsetX,
        string reason = "vision_invalid")
    {
        if (!valid)
        {
            BigTurnDirection = null;
            return Stop("VISION_STOP", reason);
        }
        if (action != "push")
        {
            BigTurnDirection = null;
            return Stop("SEARCH", "当前没有 good 能量块");
        }
        if (targetType is not (null or "good"))
        {
            BigTurnDirection = null;
            return Stop("SEARCH", "当前目标不是 good 能量块");
        }
        if (offsetX is not { } error || !double.IsFinite(error))
        {
            BigTurnDirection = null;
            return Stop("VISION_STOP", "good 目标缺少有效 offset_x");
        }

        var magnitude = Math.Abs(error);
        if (magnitude <= DeadZone)
        {
            BigTurnDirection = null;
            return new MbriTrackerResult
            {
                Left = ApproachSpeed,
                Right = ApproachSpeed,
                State = "APPROACH",
                Reason = "good 能量块已居中，直线接近",
                ErrorX = error,
            };
        }

        var direction = error > 0.0 ? "right" : "left";
        if (BigTurnDirection == direction && magnitude > BigTurnClear)
        {
            return BigTurn(direction, error);   // 滞回保持：已在同向大转且未退到 clear 线
        }

        BigTurnDirection = null;
        if (magnitude >= BigTurnEnter)
        {
            return BigTurn(direction, error);
        }
        return Arc(direction, error);
    }
}

/// <summary>hunt.py update 返回 dict 的 C# 投影（_result，hunt.py:167-189）。</summary>
public sealed record MbriHuntResult
{
    public int Left { get; init; }

    public int Right { get; init; }

    public bool OwnsControl { get; init; }

    public string Mode { get; init; } = "";

    public string State { get; init; } = "";

    public string Reason { get; init; } = "";

    /// <summary>选定目标类型（good/bad/null）。</summary>
    public string? TargetType { get; init; }

    public double? GoodOffsetX { get; init; }

    public double? BadOffsetX { get; init; }

    public string? NearDirection { get; init; }

    public string? TurnDirection { get; init; }

    public double? GoodConfidence { get; init; }

    public int GoodAcquireCount { get; init; }

    public int GoodMissCount { get; init; }

    public bool GoodLocked { get; init; }
}

/// <summary>
/// 能量块追踪与近距 bad 避让逐行移植（hunt.py:23-479，纯状态机）。
/// 追踪任意距离 good；只在数字红外近距确认时避开 bad（远处 bad 不触发避让）。
/// 状态：IDLE/BAD_CONFIRM/AVOID_TURN/AVOID_DONE/GOOD_REARM_WAIT/GOOD_ACQUIRE/
/// GOOD_CONFIRM/GOOD_PUSH/GOOD_LOST_HOLD/IR_UNCLASSIFIED/SENSOR_STOP。
/// 参数 config.py:202-215；90° 原地转向查 MbriPatrol.MotorTurnCalibration
/// （真车 hunt.py:31 turn_calibration=MOTOR_TURN_CALIBRATION）。
/// 时间域：真车 wall-clock → tick（0.35s 丢帧保留 → 7 tick；0.65s 转向 → 13 tick）。
/// </summary>
public sealed class MbriHuntController
{
    public const double BadCenterZone = 0.15;        // HUNT_BAD_CENTER_ZONE
    public const int BadConfirmFrames = 2;           // HUNT_BAD_CONFIRM_FRAMES
    public const double GoodMinConfidence = 0.55;    // HUNT_GOOD_MIN_CONFIDENCE
    public const double GoodHighConfidence = 0.80;   // HUNT_GOOD_HIGH_CONFIDENCE
    public const int GoodAcquireFrames = 2;          // HUNT_GOOD_ACQUIRE_FRAMES
    public const int GoodLostHoldFrames = 2;         // HUNT_GOOD_LOST_HOLD_FRAMES
    public const double GoodLostHoldSeconds = 0.35;  // HUNT_GOOD_LOST_HOLD_SECONDS
    public const int GoodConfirmFrames = 2;          // HUNT_GOOD_CONFIRM_FRAMES
    public const int GoodPushSpeed = 400;            // HUNT_GOOD_PUSH_SPEED
    private const double AvoidTurnAngle = 90.0;      // _start_turn 查表角度（hunt.py:231）

    private readonly double _tickSeconds;
    private readonly MbriVisionTracker _tracker;

    public string State { get; private set; } = "IDLE";

    public (int Left, int Right) Command { get; private set; } = (0, 0);

    public string? TurnDirection { get; private set; }

    private long _turnUntilTick;
    private int _nearBadCount;
    private long? _lastBadSequence;
    private bool _avoidArmed = true;
    private string _fallbackDirection = "right";
    private int _goodConfirmCount;
    private long? _lastGoodSequence;
    private bool _goodArmed = true;
    private int _goodAcquireCount;
    private long? _lastGoodAcquireSequence;
    private bool _goodLocked;
    private int _goodMissCount;
    private long? _lastGoodMissSequence;
    private long? _goodLastSeenTick;
    private long? _lastGoodSeenSequence;

    public MbriHuntController(double tickSeconds = MbriUnits.DefaultTickSeconds,
        MbriVisionTracker? tracker = null)
    {
        if (!(tickSeconds > 0) || !double.IsFinite(tickSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), "tickSeconds must be a positive finite number.");
        }
        // hunt.py:37-50 参数校验逐条移植（常数组合 + 可注入参数）。
        if (BadCenterZone < 0.0 || BadCenterZone >= 1.0)
        {
            throw new ArgumentException("bad 居中区间无效");
        }
        if (BadConfirmFrames < 1 || GoodAcquireFrames < 1 || GoodConfirmFrames < 1 || GoodLostHoldFrames < 0)
        {
            throw new ArgumentException("good/bad 确认帧数无效");
        }
        if (!(0.0 <= GoodMinConfidence && GoodMinConfidence <= GoodHighConfidence && GoodHighConfidence <= 1.0))
        {
            throw new ArgumentException("good 置信度阈值顺序无效");
        }
        if (GoodLostHoldSeconds <= 0.0)
        {
            throw new ArgumentException("good 丢帧保留时间必须为正");
        }
        _tickSeconds = tickSeconds;
        _tracker = tracker ?? new MbriVisionTracker();
    }

    /// <summary>丢帧保留时长（s）→ tick 阈值。</summary>
    private long LostHoldTicks => MbriUnits.SecondsToTicks(GoodLostHoldSeconds, _tickSeconds);

    /// <summary>90° 避让转向时长（s）→ tick 阈值。</summary>
    private long AvoidTurnTicks => MbriUnits.SecondsToTicks(AvoidTurnDuration, _tickSeconds);

    private static double AvoidTurnDuration
        => MbriPatrol.MotorTurnCalibration["left"][AvoidTurnAngle].Duration;

    /// <summary>视觉帧有效性（hunt.py:278-282：sequence 非空且 status ∈ {target,no_target}）。</summary>
    private static bool VisionValid(MbriVisionFrame? frame)
        => frame is { Sequence: not null, Status: MbriVisionFrame.StatusTarget or MbriVisionFrame.StatusNoTarget };

    /// <summary>
    /// hunt.py:94-104 _select：target 类型匹配优先，否则在 detections 中选同类型。
    /// 仿真检测无 bbox → 面积并列 0 → Python max 语义取首个同类型检测
    /// （帧 target 已按最近距离给出，此 fallback 仅在 target 类型不匹配时走）。
    /// </summary>
    private static MbriVisionDetection? Select(MbriVisionFrame? frame, string targetType)
    {
        if (frame is null)
        {
            return null;
        }
        if (frame.Target is { } preferred && preferred.Type == targetType)
        {
            return preferred;
        }
        return frame.Detections.FirstOrDefault(d => d.Type == targetType);
    }

    private static double? Offset(MbriVisionDetection? target) => target?.OffsetX;

    private static double? Confidence(MbriVisionDetection? target) => target?.Confidence;

    /// <summary>hunt.py:125-140 _near_direction：六路数字红外 → 近物方向。</summary>
    private static string? NearDirection(MbriDigiIr ir)
    {
        if (!ir.Valid)
        {
            return null;
        }
        if (ir.Front || (ir.LeftFront && ir.RightFront))
        {
            return "front";
        }
        if (ir.Rear || (ir.LeftRear && ir.RightRear))
        {
            return "rear";
        }
        if (ir.LeftFront || ir.LeftRear)
        {
            return "left";
        }
        if (ir.RightFront || ir.RightRear)
        {
            return "right";
        }
        return null;
    }

    /// <summary>hunt.py:142-165 _near_target：近物方向侧的视觉候选（good 按 min_confidence 过滤）。</summary>
    private MbriVisionDetection? NearTarget(MbriVisionFrame? frame, string? nearDirection)
    {
        if (nearDirection is null or "rear" || frame is null)
        {
            return null;
        }
        MbriVisionDetection? best = null;
        var bestKey = double.MaxValue;
        var candidates = frame.Detections.ToList();
        if (frame.Target is { } preferred && !candidates.Contains(preferred))
        {
            candidates.Add(preferred);
        }
        foreach (var detection in candidates)
        {
            if (detection.Type == "good"
                && (detection.Confidence < GoodMinConfidence))
            {
                continue;
            }
            var offset = detection.OffsetX;
            double key;
            if (nearDirection == "left" && offset <= BadCenterZone)
            {
                key = offset;
            }
            else if (nearDirection == "right" && offset >= -BadCenterZone)
            {
                key = -offset;
            }
            else if (nearDirection == "front")
            {
                key = Math.Abs(offset);
            }
            else
            {
                continue;
            }
            if (key < bestKey)
            {
                bestKey = key;
                best = detection;
            }
        }
        return best;
    }

    private MbriHuntResult Result(bool ownsControl, string mode, string state, string reason,
        MbriVisionDetection? target, double? goodOffset, double? badOffset, string? nearDirection)
        => new()
        {
            Left = Command.Left,
            Right = Command.Right,
            OwnsControl = ownsControl,
            Mode = mode,
            State = state,
            Reason = reason,
            TargetType = target?.Type,
            GoodOffsetX = goodOffset,
            BadOffsetX = badOffset,
            NearDirection = nearDirection,
            TurnDirection = TurnDirection,
            GoodConfidence = target is { Type: "good" } ? target.Confidence : null,
            GoodAcquireCount = _goodAcquireCount,
            GoodMissCount = _goodMissCount,
            GoodLocked = _goodLocked,
        };

    /// <summary>hunt.py:191-207 cancel：清空全部状态与计数（含 tracker 大转记忆）。</summary>
    public void Cancel()
    {
        State = "IDLE";
        Command = (0, 0);
        TurnDirection = null;
        _turnUntilTick = 0;
        _nearBadCount = 0;
        _lastBadSequence = null;
        _avoidArmed = true;
        _goodConfirmCount = 0;
        _lastGoodSequence = null;
        _goodAcquireCount = 0;
        _lastGoodAcquireSequence = null;
        _goodLocked = false;
        _goodMissCount = 0;
        _lastGoodMissSequence = null;
        _goodLastSeenTick = null;
        _lastGoodSeenSequence = null;
        _tracker.BigTurnDirection = null;
    }

    /// <summary>hunt.py:210-213 finish_push：结束本次推动；当前 good 消失前禁止立即重新推动。</summary>
    public void FinishPush()
    {
        Cancel();
        _goodArmed = false;
    }

    /// <summary>hunt.py:215-228 _choose_turn：转向方向选择（bad 偏离 > good 对准 > 红外侧 > 交替兜底）。</summary>
    private string ChooseTurn(double badOffset, double? goodOffset, string? nearDirection)
    {
        if (badOffset < -BadCenterZone)
        {
            return "right";
        }
        if (badOffset > BadCenterZone)
        {
            return "left";
        }
        if (goodOffset is { } g && Math.Abs(g) > MbriVisionTracker.DeadZone)
        {
            return g > 0.0 ? "right" : "left";
        }
        if (nearDirection == "left")
        {
            return "right";
        }
        if (nearDirection == "right")
        {
            return "left";
        }
        var direction = _fallbackDirection;
        _fallbackDirection = direction == "right" ? "left" : "right";
        return direction;
    }

    /// <summary>hunt.py:230-238 _start_turn：锁定方向原地转 90°（查标定表 600×0.65s→13 tick）。</summary>
    private void StartTurn(string direction, long tick)
    {
        var table = MbriPatrol.MotorTurnCalibration[direction];
        if (!table.TryGetValue(AvoidTurnAngle, out var entry))
        {
            throw new InvalidOperationException($"转向标定表缺少角度 {AvoidTurnAngle}");
        }
        var speed = entry.Speed;
        Command = direction == "right" ? (speed, -speed) : (-speed, speed);
        State = "AVOID_TURN";
        TurnDirection = direction;
        _turnUntilTick = tick + AvoidTurnTicks;
        _nearBadCount = 0;
        _lastBadSequence = null;
    }

    /// <summary>
    /// hunt.py:240-479 update 逐行移植。时间域：tick（调用方注入，替代 time.monotonic）；
    /// nearIr 为六路数字红外投影（仿真桥接语义见 MbriFsm.ReadNearIr）。
    /// </summary>
    public MbriHuntResult Update(MbriVisionFrame? frame, MbriDigiIr nearIr, long tick, bool healthy = true)
    {
        var nearDirection = NearDirection(nearIr);

        if (!healthy)
        {
            Cancel();
            return Result(true, "safety_stop", "SENSOR_STOP", "传感器无效或数据过期", null, null, null, nearDirection);
        }

        if (State == "AVOID_TURN")
        {
            if (tick < _turnUntilTick)
            {
                return Result(true, "avoid_bad", "AVOID_TURN", "锁定方向完成 90 度原地转向",
                    null, null, null, nearDirection);
            }
            State = "IDLE";
            Command = (0, 0);
            TurnDirection = null;
            _avoidArmed = false;
            return Result(true, "release", "AVOID_RELEASE", "bad 避让转向完成，停车一帧后释放控制权",
                null, null, null, nearDirection);
        }

        if (State == "GOOD_PUSH")
        {
            Command = (GoodPushSpeed, GoodPushSpeed);
            return Result(true, "push_good", "GOOD_PUSH", "good 已居中，持续前推直到铲子悬空保护触发",
                null, null, null, nearDirection);
        }

        Command = (0, 0);
        var visionValid = VisionValid(frame);
        var good = visionValid ? Select(frame, "good") : null;
        var bad = visionValid ? Select(frame, "bad") : null;
        var nearTarget = visionValid ? NearTarget(frame, nearDirection) : null;
        if (nearTarget is { Type: "good" })
        {
            good = nearTarget;
        }
        var rawGood = good;
        var goodConfidence = Confidence(good);
        if (goodConfidence is null || goodConfidence < GoodMinConfidence)
        {
            good = null;
            goodConfidence = null;
        }
        var goodOffset = Offset(good);
        var badOffset = Offset(bad);
        var nearBad = nearTarget is { Type: "bad" };
        if (nearBad)
        {
            _goodConfirmCount = 0;
            _lastGoodSequence = null;
            _goodAcquireCount = 0;
            _lastGoodAcquireSequence = null;
            _goodLocked = false;
            _goodMissCount = 0;
            _lastGoodMissSequence = null;
            _goodLastSeenTick = null;
            _lastGoodSeenSequence = null;
            bad = nearTarget;
            badOffset = Offset(bad);
        }

        if (!nearBad)
        {
            _nearBadCount = 0;
            _lastBadSequence = null;
            _avoidArmed = true;
        }

        if (nearBad && _avoidArmed)
        {
            var sequence = frame!.Sequence;
            if (sequence != _lastBadSequence)
            {
                _nearBadCount += 1;
                _lastBadSequence = sequence;
            }
            if (_nearBadCount < BadConfirmFrames)
            {
                State = "BAD_CONFIRM";
                return Result(true, "avoid_bad", "BAD_CONFIRM", "红外近距 bad 等待不同视觉帧确认",
                    bad, goodOffset, badOffset, nearDirection);
            }
            StartTurn(ChooseTurn(badOffset!.Value, goodOffset, nearDirection), tick);
            return Result(true, "avoid_bad", "AVOID_TURN", "近距 bad 已确认，锁定方向原地转 90 度",
                bad, goodOffset, badOffset, nearDirection);
        }

        if (nearBad)
        {
            State = "IDLE";
            return Result(false, "release", "AVOID_DONE", "本次近距 bad 已避让，等待目标离开后重新布防",
                bad, goodOffset, badOffset, nearDirection);
        }

        if (!_goodArmed)
        {
            if (visionValid && rawGood is null)
            {
                _goodArmed = true;
            }
            else
            {
                State = "GOOD_REARM_WAIT";
                Command = (0, 0);
                return Result(false, "release", "GOOD_REARM_WAIT", "等待当前 good 消失后重新允许推动",
                    good, goodOffset, badOffset, nearDirection);
            }
        }

        var goodSequence = frame?.Sequence;
        if (good is not null)
        {
            if (goodSequence != _lastGoodSeenSequence)
            {
                if (_goodLastSeenTick is { } lastSeen && tick - lastSeen > LostHoldTicks)
                {
                    _goodLocked = false;
                    _goodAcquireCount = 0;
                    _lastGoodAcquireSequence = null;
                    _goodConfirmCount = 0;
                    _lastGoodSequence = null;
                }
                _lastGoodSeenSequence = goodSequence;
                _goodLastSeenTick = tick;
            }
            else if (_goodLastSeenTick is { } seen && tick - seen > LostHoldTicks)
            {
                good = null;
                goodConfidence = null;
                goodOffset = null;
            }
        }

        if (good is not null && goodOffset is not null)
        {
            _goodMissCount = 0;
            _lastGoodMissSequence = null;
            if (!_goodLocked)
            {
                var sequence = frame!.Sequence;
                if (goodConfidence >= GoodHighConfidence)
                {
                    _goodLocked = true;
                }
                else
                {
                    if (sequence != _lastGoodAcquireSequence)
                    {
                        _goodAcquireCount += 1;
                        _lastGoodAcquireSequence = sequence;
                    }
                    if (_goodAcquireCount < GoodAcquireFrames)
                    {
                        State = "GOOD_ACQUIRE";
                        return Result(true, "track_good", "GOOD_ACQUIRE", "中置信度 good 停车等待不同视觉帧确认",
                            good, goodOffset, badOffset, nearDirection);
                    }
                    _goodLocked = true;
                }
                _goodAcquireCount = 0;
                _lastGoodAcquireSequence = null;
            }
            var tracked = _tracker.Update(true, "push", "good", goodOffset);
            Command = (tracked.Left, tracked.Right);
            if (tracked.State == "APPROACH")
            {
                var sequence = frame!.Sequence;
                if (sequence != _lastGoodSequence)
                {
                    _goodConfirmCount += 1;
                    _lastGoodSequence = sequence;
                }
                if (_goodConfirmCount < GoodConfirmFrames)
                {
                    Command = (0, 0);
                    State = "GOOD_CONFIRM";
                    return Result(true, "track_good", "GOOD_CONFIRM", "good 已居中，等待不同视觉帧确认",
                        good, goodOffset, badOffset, nearDirection);
                }
                Command = (GoodPushSpeed, GoodPushSpeed);
                State = "GOOD_PUSH";
                return Result(true, "push_good", "GOOD_PUSH", "good 已居中，锁定持续前推直到铲子保护触发",
                    good, goodOffset, badOffset, nearDirection);
            }
            _goodConfirmCount = 0;
            _lastGoodSequence = null;
            State = tracked.State;
            return Result(true, "track_good", tracked.State, tracked.Reason,
                good, goodOffset, badOffset, nearDirection);
        }

        _goodConfirmCount = 0;
        _lastGoodSequence = null;

        if (_goodLocked)
        {
            var sequence = frame?.Sequence;
            if (sequence is not null && sequence != _lastGoodMissSequence)
            {
                _goodMissCount += 1;
                _lastGoodMissSequence = sequence;
            }
            var elapsed = _goodLastSeenTick is { } seen ? tick - seen : long.MaxValue;
            if (_goodMissCount <= GoodLostHoldFrames && elapsed <= LostHoldTicks)
            {
                State = "GOOD_LOST_HOLD";
                return Result(true, "track_good", "GOOD_LOST_HOLD", "已锁定 good 临时丢帧，停车保留目标身份",
                    null, goodOffset, badOffset, nearDirection);
            }
            _goodLocked = false;
            _goodMissCount = 0;
            _lastGoodMissSequence = null;
            _goodLastSeenTick = null;
            _lastGoodSeenSequence = null;
        }

        _goodAcquireCount = 0;
        _lastGoodAcquireSequence = null;

        _tracker.BigTurnDirection = null;
        if (nearDirection is not null)
        {
            State = "IR_UNCLASSIFIED";
            return Result(false, "release", "IR_UNCLASSIFIED", "红外近物体未被识别为能量块，交给敌人模块",
                bad, goodOffset, badOffset, nearDirection);
        }
        State = "IDLE";
        return Result(false, "release", "NO_TARGET", "没有可追踪 good；远处 bad 不触发避让",
            bad, goodOffset, badOffset, nearDirection);
    }
}
