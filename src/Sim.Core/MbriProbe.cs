namespace Sim.Core;

/// <summary>main.py:168-173 probe_vision dict 的 C# 投影（probe 只消费帧摘要，不消费检测明细）。</summary>
public sealed record MbriProbeVision(long? Sequence, string Status, bool HasGood, bool HasBad)
{
    /// <summary>vision 为 null（仿真未接线视觉源）时的语义（main.py:171 else 分支 "stale"）。</summary>
    public static MbriProbeVision Stale { get; } = new(null, MbriVisionFrame.StatusStale, false, false);

    /// <summary>从投影帧构造摘要（HasGood/HasBad 覆盖 target + detections，main.py:153-165）。</summary>
    public static MbriProbeVision From(MbriVisionFrame? frame)
        => frame is null
            ? Stale
            : new(frame.Sequence, frame.Status, frame.HasGood, frame.HasBad);
}

/// <summary>proximity_probe.py update/_result dict 的 C# 投影。</summary>
public sealed record MbriProbeResult
{
    public int Left { get; init; }

    public int Right { get; init; }

    public bool OwnsControl { get; init; }

    public string State { get; init; } = "";

    public string Reason { get; init; } = "";

    public string? SourceDirection { get; init; }

    public string? TurnDirection { get; init; }

    public bool Confirmed { get; init; }

    public bool Slow { get; init; }

    public int VisionCount { get; init; }

    /// <summary>idle/waiting/no_target_N/enemy_confirmed/good/bad/timeout/…（真车 verdict 字符串逐字）。</summary>
    public string VisionVerdict { get; init; } = "idle";

    public int BadInterruptCount { get; init; }
}

/// <summary>
/// 敌人搜索与推动状态机逐行移植（proximity_probe.py:25-436，纯状态机）：
/// 数字红外发现近物候选 → 刹车归 0 → 定角转向（查 MOTOR_TURN_CALIBRATION）→
/// 停车等视觉分类；连续新视觉帧均无 good/bad 才确认敌并推动（700，近台沿降速 550，
/// 后路近台沿中断，连续 2 帧识别 bad 打断）。状态：IDLE/PROBE_BRAKE/PROBE_TURN/
/// PROBE_VISION_WAIT/ENEMY_PUSH/COOLDOWN。参数 config.py:217-246 逐字。
///
/// 仿真传感器裁剪披露（不硬造传感器，批2 同款处理）：
///   - 六路数字红外中，仿真只有四路对角（dLF/dRF/dLB/dRB，±45°/±135°，target
///     模式=只探机器人/方块）具备"近物体"语义 → 桥接为 left_front/right_front/
///     left_rear/right_rear（MbriFsm.ReadNearIr）；
///   - 正前专用与正后专用通道仿真缺失：f 是 edge_target（墙+台沿语义，在台上
///     恒高电平）、r 是 fence（围栏语义），均非近物体语义，不得充当敌人探测
///     → Front/Rear 恒 false：正前近物由 (left_front && right_front) 分支承载
///     （proximity_probe.py:91-94 原生支持），正后分支（rear→180°）集成不可达，
///     保持移植完整（单测注入覆盖）；
///   - 几何后果照实披露：对角半视场 0.55rad≈31.5°，正前 ±13.5° 锥内近物不触发
///     任何对角 → 该锥内敌人 probe 不可达（真车由专用正前通道覆盖）；
///   - 视觉 good/bad 排除依赖特权投影帧（MbriVisionProjector 披露同源）。
/// 铁律：零 IO/零时钟/零随机——wall-clock 时长经 MbriUnits.SecondsToTicks 换算。
/// </summary>
public sealed class MbriProbeController
{
    public const int PushSpeed = 700;                 // ENEMY_PUSH_SPEED
    public const int SlowSpeed = 550;                 // ENEMY_SLOW_SPEED
    public const double SlowZone = 1.3;               // ENEMY_SLOW_ZONE
    public const int SlowConfirm = 6;                 // ENEMY_SLOW_CONFIRM
    public const int BadInterruptFrames = 2;          // ENEMY_BAD_INTERRUPT_FRAMES
    public const int VisionConfirmFrames = 3;         // PROBE_VISION_CONFIRM_FRAMES
    public const double VisionWaitTimeout = 0.6;      // PROBE_VISION_WAIT_TIMEOUT
    public const int IrRearmClearFrames = 3;          // PROBE_IR_REARM_CLEAR_FRAMES
    public const double BrakeSeconds = 0.2;           // PROBE_BRAKE_SECONDS
    public const double CooldownSeconds = 3.0;        // ENEMY_COOLDOWN_SECONDS
    public const double RearAbortZone = -0.45;        // ENEMY_REAR_ABORT_ZONE
    public const string RearFirstTurn = "right";      // PROBE_REAR_FIRST_TURN

    /// <summary>
    /// PROBE_TURN_PLAN（config.py:235-241）：近物来源 → (转向方向, 定角)；
    /// rear 方向为 (None, 180)：运行时按 _fallback_turn 左右交替（真车语义）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string? Direction, double Angle)> TurnPlan =
        new Dictionary<string, (string?, double)>
        {
            ["left_front"] = ("left", 45.0),
            ["right_front"] = ("right", 45.0),
            ["left_rear"] = ("left", 135.0),
            ["right_rear"] = ("right", 135.0),
            ["rear"] = (null, 180.0),
        };

    private readonly double _tickSeconds;

    public string State { get; private set; } = "IDLE";

    public (int Left, int Right) Command { get; private set; } = (0, 0);

    public string Reason { get; private set; } = "等待摄像头和红外空闲";

    public string? SourceDirection { get; private set; }

    public string? TurnDirection { get; private set; }

    public bool Confirmed { get; private set; }

    public bool Slow { get; private set; }

    public int VisionCount { get; private set; }

    public string VisionVerdict { get; private set; } = "idle";

    public int BadInterruptCount => _badInterruptCount;

    /// <summary>proximity_probe.py:81-83 active 属性：state != IDLE。</summary>
    public bool Active => State != "IDLE";

    private long _stateStartedTick;
    private int _slowCount;
    private int _badInterruptCount;
    private long? _lastBadInterruptSequence;
    private bool _sideArmed = true;
    private bool _frontArmed = true;
    private int _sideClearCount;
    private int _frontClearCount;
    private string _fallbackTurn = RearFirstTurn;
    private string? _brakeSource;
    private long _brakeUntilTick;
    private long _turnUntilTick;
    private int _turnSpeed;
    private long? _lastVisionSequence;
    private bool _visionWaitRequiresFront = true;

    public MbriProbeController(double tickSeconds = MbriUnits.DefaultTickSeconds)
    {
        if (!(tickSeconds > 0) || !double.IsFinite(tickSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), "tickSeconds must be a positive finite number.");
        }
        // proximity_probe.py:38-47 参数校验（常数组合编译期固定，构造期断言防御）。
        if (VisionConfirmFrames < 1 || BadInterruptFrames < 1 || IrRearmClearFrames < 1 || BrakeSeconds < 0.0
            || VisionWaitTimeout <= 0.0)
        {
            throw new ArgumentException("probe 参数校验失败");
        }
        _tickSeconds = tickSeconds;
    }

    private long DurationTicks(double seconds) => MbriUnits.SecondsToTicks(seconds, _tickSeconds);

    /// <summary>proximity_probe.py:90-94 _front_detected。</summary>
    private static bool FrontDetected(MbriDigiIr ir)
        => ir.Front || (ir.LeftFront && ir.RightFront);

    /// <summary>proximity_probe.py:97-109 _side_direction。</summary>
    private static string? SideDirection(MbriDigiIr ir)
    {
        if (ir.LeftFront)
        {
            return "left_front";
        }
        if (ir.RightFront)
        {
            return "right_front";
        }
        if (ir.Rear || (ir.LeftRear && ir.RightRear))
        {
            return "rear";
        }
        if (ir.LeftRear)
        {
            return "left_rear";
        }
        if (ir.RightRear)
        {
            return "right_rear";
        }
        return null;
    }

    /// <summary>proximity_probe.py:111-113 _turn_for_source（rear 方向用 _fallback_turn 交替）。</summary>
    private (string Direction, double Angle) TurnForSource(string source)
    {
        var (direction, angle) = TurnPlan[source];
        return (direction is null ? _fallbackTurn : direction, angle);
    }

    /// <summary>proximity_probe.py:115-125 _enter。</summary>
    private void Enter(string state, long tick, string reason)
    {
        State = state;
        _stateStartedTick = tick;
        Command = (0, 0);
        Reason = reason;
        if (state == "ENEMY_PUSH")
        {
            Confirmed = false;
            Slow = false;
            _slowCount = 0;
            _badInterruptCount = 0;
            _lastBadInterruptSequence = null;
        }
    }

    /// <summary>proximity_probe.py:127-134 _start_brake：先刹车归 0，停稳再按原地标定转向。</summary>
    private void StartBrake(string source, long tick)
    {
        _brakeSource = source;
        SourceDirection = source;
        _brakeUntilTick = tick + DurationTicks(BrakeSeconds);
        _sideArmed = false;
        _sideClearCount = 0;
        Enter("PROBE_BRAKE", tick, $"{source} 发现近物候选，先刹车归0再转向");
    }

    /// <summary>proximity_probe.py:136-152 _start_turn：定角原地转向。</summary>
    private void StartTurn(string source, long tick)
    {
        var (direction, angle) = TurnForSource(source);
        if (source == "rear")
        {
            _fallbackTurn = _fallbackTurn == "right" ? "left" : "right";
        }
        var table = MbriPatrol.MotorTurnCalibration[direction];
        if (!table.TryGetValue(angle, out var entry))
        {
            throw new InvalidOperationException($"转向标定表缺少角度 {angle}");
        }
        SourceDirection = source;
        TurnDirection = direction;
        _turnUntilTick = tick + DurationTicks(entry.Duration);
        _turnSpeed = entry.Speed;
        Enter("PROBE_TURN", tick, $"{source} 发现近物候选，原地转向");
        Command = direction == "left" ? (-_turnSpeed, _turnSpeed) : (_turnSpeed, -_turnSpeed);
    }

    /// <summary>proximity_probe.py:154-158 _start_push。</summary>
    private void StartPush(long tick, string reason)
    {
        SourceDirection = "front";
        TurnDirection = null;
        VisionVerdict = "enemy_confirmed";
        Enter("ENEMY_PUSH", tick, reason);
    }

    /// <summary>proximity_probe.py:160-170 _start_vision_wait。</summary>
    private void StartVisionWait(long tick, long? visionSequence, bool requireFront = true)
    {
        SourceDirection = "front";
        TurnDirection = null;
        VisionCount = 0;
        VisionVerdict = "waiting";
        _lastVisionSequence = visionSequence;
        _visionWaitRequiresFront = requireFront;
        Enter("PROBE_VISION_WAIT", tick, "近物体已停车，等待新视觉帧排除 good/bad");
    }

    /// <summary>proximity_probe.py:172-189 cancel。</summary>
    public void Cancel()
    {
        State = "IDLE";
        Command = (0, 0);
        Reason = "控制权被更高优先级状态收回";
        SourceDirection = null;
        TurnDirection = null;
        _brakeSource = null;
        _brakeUntilTick = 0;
        _turnUntilTick = 0;
        Confirmed = false;
        Slow = false;
        _slowCount = 0;
        _badInterruptCount = 0;
        _lastBadInterruptSequence = null;
        VisionCount = 0;
        VisionVerdict = "idle";
        _lastVisionSequence = null;
        _visionWaitRequiresFront = true;
    }

    /// <summary>proximity_probe.py:191-199 finish_push：前方红外解除前禁止再次推动同一目标。</summary>
    public void FinishPush()
    {
        var wasPush = State == "ENEMY_PUSH";
        Cancel();
        _frontArmed = false;
        _frontClearCount = 0;
        Confirmed = wasPush;
        VisionVerdict = "push_finished";
        Reason = "推动结束，等待前方目标离开后重新布防";
    }

    /// <summary>proximity_probe.py:201-202 _abort。</summary>
    private void Abort(long tick, string reason) => Enter("COOLDOWN", tick, reason);

    /// <summary>proximity_probe.py:204-219 _update_rearm：红外解除后 N 帧重新布防。</summary>
    private void UpdateRearm(bool front, string? side)
    {
        if (front)
        {
            _frontClearCount = 0;
        }
        else if (!_frontArmed)
        {
            _frontClearCount += 1;
            if (_frontClearCount >= IrRearmClearFrames)
            {
                _frontArmed = true;
                _frontClearCount = 0;
            }
        }

        if (side is not null)
        {
            _sideClearCount = 0;
        }
        else if (!_sideArmed)
        {
            _sideClearCount += 1;
            if (_sideClearCount >= IrRearmClearFrames)
            {
                _sideArmed = true;
                _sideClearCount = 0;
            }
        }
    }

    private MbriProbeResult Result(bool? ownsControl = null)
        => new()
        {
            Left = Command.Left,
            Right = Command.Right,
            OwnsControl = ownsControl ?? Active,
            State = State,
            Reason = Reason,
            SourceDirection = SourceDirection,
            TurnDirection = TurnDirection,
            Confirmed = Confirmed,
            Slow = Slow,
            VisionCount = VisionCount,
            VisionVerdict = VisionVerdict,
            BadInterruptCount = BadInterruptCount,
        };

    /// <summary>
    /// proximity_probe.py:239-436 update 逐行移植（observation = 巡台观测，ENEMY_PUSH
    /// 台沿中断用；vision = 投影帧摘要；allowStart 对应 main.py:338 的 not vision_good）。
    /// </summary>
    public MbriProbeResult Update(MbriDigiIr ir, MbriGrayObservation? observation, MbriProbeVision vision,
        long tick, bool healthy = true, bool allowStart = true)
    {
        ArgumentNullException.ThrowIfNull(vision);
        var irValid = ir.Valid;
        if (!healthy || !irValid)
        {
            if (Active)
            {
                Abort(tick, "传感器无效或数据过期，停止敌人动作");
                return Result();
            }
            return Result(false);
        }

        var front = FrontDetected(ir);
        var side = SideDirection(ir);
        UpdateRearm(front, side);

        if (State == "IDLE")
        {
            if (!allowStart)
            {
                Reason = "摄像头正在处理能量块";
                return Result(false);
            }
            if (front && _frontArmed)
            {
                StartVisionWait(tick, vision.Sequence);
                return Result();
            }
            if (front)
            {
                Reason = "等待前方目标离开后重新允许推动";
                return Result(false);
            }
            if (side is not null && _sideArmed)
            {
                StartBrake(side, tick);
                return Result();
            }
            Reason = "没有近物候选";
            return Result(false);
        }

        if (State == "PROBE_BRAKE")
        {
            if (vision.HasGood)
            {
                Cancel();
                _sideArmed = false;
                _sideClearCount = 0;
                Reason = "刹车期间视觉识别为 good，取消近物候选";
                VisionVerdict = "good";
                return Result(false);
            }
            if (front)
            {
                StartVisionWait(tick, vision.Sequence);
                return Result();
            }
            if (tick >= _brakeUntilTick)
            {
                StartTurn(_brakeSource!, tick);
                return Result();
            }
            return Result();
        }

        if (State == "PROBE_TURN")
        {
            if (vision.HasGood)
            {
                Cancel();
                _sideArmed = false;
                _sideClearCount = 0;
                Reason = "转向期间视觉识别为 good，取消近物候选";
                VisionVerdict = "good";
                return Result(false);
            }
            if (front)
            {
                StartVisionWait(tick, vision.Sequence);
                return Result();
            }
            if (tick >= _turnUntilTick)
            {
                StartVisionWait(tick, vision.Sequence, requireFront: false);
                Reason = "定角转向完成，停车等待转后视觉结果";
                return Result();
            }
            Command = TurnDirection == "left" ? (-_turnSpeed, _turnSpeed) : (_turnSpeed, -_turnSpeed);
            return Result();
        }

        if (State == "PROBE_VISION_WAIT")
        {
            if (vision.HasGood || vision.HasBad)
            {
                var verdict = vision.HasGood ? "good" : "bad";
                Cancel();
                _frontArmed = false;
                _frontClearCount = 0;
                _sideArmed = false;
                _sideClearCount = 0;
                Reason = $"视觉识别为 {verdict}，取消近物候选";
                VisionVerdict = verdict;
                return Result(false);
            }
            if (tick - _stateStartedTick >= DurationTicks(VisionWaitTimeout))
            {
                Cancel();
                _frontArmed = false;
                _frontClearCount = 0;
                _sideArmed = false;
                _sideClearCount = 0;
                Reason = "等待新视觉结果超时，取消近物候选";
                VisionVerdict = "timeout";
                return Result(false);
            }
            if (_visionWaitRequiresFront && !front)
            {
                Cancel();
                _frontArmed = false;
                _frontClearCount = 0;
                Reason = "等待视觉期间前方红外消失，取消敌人候选";
                VisionVerdict = "target_lost";
                return Result(false);
            }
            if (vision.Sequence is null
                || vision.Status is null or "stale" or "error")
            {
                Reason = "视觉无新结果，停车等待";
                VisionVerdict = "waiting";
                return Result();
            }
            if (vision.Sequence == _lastVisionSequence)
            {
                Reason = "等待下一个不同视觉帧";
                return Result();
            }

            _lastVisionSequence = vision.Sequence;
            if (vision.Status != MbriVisionFrame.StatusNoTarget)
            {
                Cancel();
                _frontArmed = false;
                _frontClearCount = 0;
                _sideArmed = false;
                _sideClearCount = 0;
                Reason = "视觉存在未分类目标，取消近物候选";
                VisionVerdict = "visual_target";
                return Result(false);
            }

            if (!_visionWaitRequiresFront && !front)
            {
                Cancel();
                _sideArmed = false;
                _sideClearCount = 0;
                Reason = "转向后正前红外未发现目标，恢复巡台";
                VisionVerdict = "post_turn_no_front";
                return Result(false);
            }

            _visionWaitRequiresFront = true;

            VisionCount += 1;
            VisionVerdict = $"no_target_{VisionCount}";
            if (VisionCount >= VisionConfirmFrames)
            {
                StartPush(tick, "连续新视觉帧均无 good/bad，确认近物体为敌人并开始推动");
            }
            else
            {
                Reason = $"视觉无 good/bad，确认 {VisionCount}/{VisionConfirmFrames}";
            }
            return Result();
        }

        var zone = observation?.Zone;
        // 与真车一致：zone 需含 front/rear/left/right 四路（proximity_probe.py:384
        // all(name in zone ...)）；MbriGrayObservation.Zone 为四通道 record 恒齐备，
        // 检查保留为移植完整性——集成路径恒 valid。
        var zoneValid = zone is not null;

        if (State == "ENEMY_PUSH")
        {
            if (vision.HasGood)
            {
                Cancel();
                _frontArmed = false;
                _frontClearCount = 0;
                Reason = "推动期间视觉识别为 good，取消敌人攻击";
                VisionVerdict = "good";
                return Result(false);
            }
            if (vision.HasBad)
            {
                if (vision.Sequence != _lastBadInterruptSequence)
                {
                    _badInterruptCount += 1;
                    _lastBadInterruptSequence = vision.Sequence;
                }
                if (_badInterruptCount >= BadInterruptFrames)
                {
                    var badInterruptCount = _badInterruptCount;
                    Cancel();
                    _frontArmed = false;
                    _frontClearCount = 0;
                    Reason = $"推动期间连续 {BadInterruptFrames} 帧识别为 bad，取消敌人攻击";
                    VisionVerdict = "bad_confirmed";
                    var interrupted = Result(false);
                    return interrupted with { BadInterruptCount = badInterruptCount };
                }
            }
            else
            {
                _badInterruptCount = 0;
                _lastBadInterruptSequence = null;
            }
            if (!zoneValid)
            {
                Abort(tick, "灰度观测无效，中断推动");
                return Result();
            }
            if (zone!.Value.Rear <= RearAbortZone)
            {
                Abort(tick, "后路接近台外，中断推动");
                return Result();
            }
            if (zone.Value.Front < SlowZone)
            {
                _slowCount += 1;
            }
            else
            {
                _slowCount = 0;
            }
            if (_slowCount >= SlowConfirm)
            {
                Slow = true;
            }
            var speed = Slow ? SlowSpeed : PushSpeed;
            Command = (speed, speed);
            return Result();
        }

        if (State == "COOLDOWN")
        {
            if (tick - _stateStartedTick >= DurationTicks(CooldownSeconds))
            {
                Enter("IDLE", tick, "安全冷却结束，释放控制权");
                return Result();
            }
            return Result();
        }

        Cancel();
        return Result(false);
    }
}
