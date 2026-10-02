namespace Sim.Core;

/// <summary>巡台单次决策输出（ring_patrol.py _result dict 的 C# 投影）。</summary>
public sealed record MbriPatrolResult
{
    /// <summary>左右轮速命令（车端单位，可带符号；真车语义）。</summary>
    public int Left { get; init; }

    public int Right { get; init; }

    /// <summary>状态名（与真车字符串逐字一致）。</summary>
    public string State { get; init; } = "";

    /// <summary>迁移/驻留理由（真车中文理由逐字保留）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>speed_level（真车 _result 映射表逐字）。</summary>
    public string SpeedLevel { get; init; } = "stop";

    public double TurnAngle { get; init; }

    public string TurnDirection { get; init; } = "";

    /// <summary>转向标定时长（s，真车 wall-clock 语义；执行按秒→tick 换算）。</summary>
    public double TurnDuration { get; init; }

    public string RiskSensor { get; init; } = "";

    public double LinearRisk { get; init; }

    public double TurnRisk { get; init; }

    public bool ShovelPreheat { get; init; }

    public MbriGrayObservation Observation { get; init; } = new();
}

/// <summary>
/// 真车 RingPatrolController 全状态机逐行移植（ring_patrol.py:11-400，
/// 纯状态机，无 IO/无时钟/无随机——真车 time.monotonic 由调用方按 tick 注入，
/// wall-clock 时长经 MbriUnits.SecondsToTicks 换算为 tick 阈值）。
/// 参数取 config.py:92-148（ring_patrol.py 经 import config 注入）。
/// 状态：WARMUP/CRUISE/MEDIUM_CRUISE/EDGE_AVOID/EDGE_TURN/WHITE_ESCAPE/
/// RECOVER_FORWARD/RECOVER_BACKWARD/SENSOR_STOP；确认帧防抖（white 4/near 3/
/// early 1/diagonal 3）与对角风险拦截齐全。
/// 输入输出均为真车域：输入 ADC 采样，输出左右轮速命令（0-1023 带符号）。
/// </summary>
public sealed class MbriPatrol
{
    // ---------- config.py 参数（ring_patrol 注入值，逐字） ----------
    public const int MinActiveSpeed = 400;            // PATROL_MIN_ACTIVE_SPEED
    public const int CruiseLinear = 450;              // PATROL_CRUISE_LINEAR
    public const int CruiseTurn = 0;                  // PATROL_CRUISE_TURN
    public const int MediumLinear = 400;              // PATROL_MEDIUM_LINEAR
    public const int MediumTurn = 0;                  // PATROL_MEDIUM_TURN
    public const double EdgeRetreatSeconds = 0.60;    // PATROL_EDGE_RETREAT_SECONDS
    public const double EdgeTurnAngle = 135.0;        // PATROL_EDGE_TURN_ANGLE
    public const double DeepZone = -0.45;             // PATROL_DEEP_ZONE
    public const double TurnSignalEpsilon = 0.05;     // PATROL_TURN_SIGNAL_EPSILON
    public const double DiagonalSideZone = 0.52;      // PATROL_DIAGONAL_SIDE_ZONE
    public const double DiagonalTurnDelta = 1.00;     // PATROL_DIAGONAL_TURN_DELTA
    public const int DiagonalConfirm = 3;             // PATROL_DIAGONAL_CONFIRM
    public const double RearRetreatDelta = 0.15;      // PATROL_REAR_RETREAT_DELTA
    public const double FastZoneScore = 1.10;         // PATROL_FAST_ZONE_SCORE
    public const double EarlyFrontZone = 0.88;        // PATROL_EARLY_FRONT_ZONE
    public const double ShovelPreheatFrontZone = 0.76; // PATROL_SHOVEL_PREHEAT_FRONT_ZONE

    /// <summary>
    /// 前路提前离边线（A3 行为重校，2026-10-01）。真车 PATROL_EARLY_FRONT_ABS=0.76
    /// 是真车场"内环安全前路 zone 最小 0.817 / 出界最大 0.714"的中点（config.py:110-113
    /// 注释），其成立前提——安全/出界两分布不重叠——在官方场陡线性渐变
    /// （g=300+700(1−t)，zone(d)≈1.81d−0.09，d=探点距沿距离）下不存在：0.76 ↔
    /// 探点距沿 0.46m，而 RECOVER_FORWARD 全程 ≈0.72m，1 帧确认每轮恢复刚起步即
    /// 触发 → 巡台锁死在沿带 EDGE_AVOID↔EDGE_TURN 循环、CRUISE 不可达、hunt 门禁
    /// （CRUISE/MEDIUM_CRUISE）永不打开（head-us-mbri 事件日志实证，见任务
    /// 10-01-mbri-hunt-engagement prd.md §1）。重推：取官方场 danger 边界＝A1 锚下
    /// near-edge 线 zone=0.35（↔ 探点距沿 0.24m，与 MbriRiskModel.NearEdgeEnter 同源
    /// 同值）；early-front 语义由"真车内环保护线"变为"官方场台沿危险线"
    /// （front 单通道过线 + zone_score&lt;0.88 双确认）。防掉台裕度：巡航 0.40m/s
    /// 下 1 帧确认+反应 ≈2cm ≪ 0.24m。
    /// 与真车 ALIAS 的偏离（披露）：真车两常数同值双用途（=SHOVEL_PREHEAT 0.76）；
    /// 仿真铲子为 no-op，<see cref="ShovelPreheatFrontZone"/> 保持真车透传 0.76，
    /// 本常数独立为 0.35。
    /// </summary>
    public const double EarlyFrontAbs = 0.35;
    public const int EarlyConfirm = 1;                // PATROL_EARLY_CONFIRM
    public const double RecoverDeepZone = -0.35;      // PATROL_RECOVER_DEEP_ZONE
    public const int CommandLimit = 1023;             // PATROL_COMMAND_LIMIT
    public const int WhiteEscapeSpeed = 400;          // PATROL_WHITE_ESCAPE_SPEED
    public const double WhiteEscapeSeconds = 0.6;     // PATROL_WHITE_ESCAPE_SECONDS
    public const int RecoverForwardSpeed = 400;       // PATROL_RECOVER_FORWARD_SPEED
    public const int RecoverBackwardSpeed = 400;      // PATROL_RECOVER_BACKWARD_SPEED
    public const double RecoverSeconds = 2;           // PATROL_RECOVER_SECONDS
    public const double RecoverReleaseZone = 0.55;    // PATROL_RECOVER_RELEASE_ZONE
    public const int WhiteConfirm = 4;                // PATROL_WHITE_CONFIRM
    public const int NearConfirm = 3;                 // PATROL_NEAR_CONFIRM

    /// <summary>
    /// MOTOR_TURN_CALIBRATION（config.py:121-138，新车 2026-08-18 重采 45/90/135/180；
    /// 左右表数值相同仍分表保留——真车查表语义）。角度 → (速度, 时长s)。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<double, (int Speed, double Duration)>>
        MotorTurnCalibration = new Dictionary<string, IReadOnlyDictionary<double, (int, double)>>
        {
            ["left"] = new Dictionary<double, (int, double)>
            {
                [10.0] = (400, 0.5),
                [22.5] = (400, 1.0),
                [45.0] = (600, 0.55),
                [90.0] = (600, 0.65),
                [135.0] = (625, 1.0),
                [180.0] = (560, 1.2),
            },
            ["right"] = new Dictionary<double, (int, double)>
            {
                [10.0] = (400, 0.5),
                [22.5] = (400, 1.0),
                [45.0] = (600, 0.55),
                [90.0] = (600, 0.65),
                [135.0] = (625, 1.0),
                [180.0] = (560, 1.2),
            },
        };

    private readonly MbriRiskModel _model;
    private readonly double _tickSeconds;

    public string State { get; private set; } = "WARMUP";
    public long StateStartedTick { get; private set; }
    public (int Left, int Right) Command { get; private set; } = (0, 0);
    public string Reason { get; private set; } = "等待滤波窗口";
    public double TurnAngle { get; private set; }
    public string TurnDirection { get; private set; } = "";
    public double TurnDuration { get; private set; }
    public string RiskSensor { get; private set; } = "";

    private int _whiteCount;
    private int _nearCount;
    private int _earlyFrontCount;
    private int _diagonalCount;
    private double _lastTurnSign = 1.0;

    public MbriPatrol(double tickSeconds = MbriUnits.DefaultTickSeconds, MbriRiskModel? model = null)
    {
        if (!(tickSeconds > 0) || !double.IsFinite(tickSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), "tickSeconds must be a positive finite number.");
        }
        _tickSeconds = tickSeconds;
        _model = model ?? new MbriRiskModel(
            MbriRiskModel.FilterWindow,
            nearEdgeEnter: MbriRiskModel.NearEdgeEnter,
            nearEdgeClear: MbriRiskModel.NearEdgeClear,
            adcMax: MbriRiskModel.AdcMax);
    }

    /// <summary>真车 rearm（ring_patrol.py:204-216）：外部自救结束后重置计数并重新退离。</summary>
    public void Rearm(long tick)
    {
        _whiteCount = 0;
        _nearCount = 0;
        _earlyFrontCount = 0;
        _diagonalCount = 0;
        Enter("EDGE_AVOID", tick, (-RecoverBackwardSpeed, -RecoverBackwardSpeed), "外部自救后重新退离");
    }

    /// <summary>重置滤波窗口（真车进程重启语义；中值滤波窗口清空，design.md 第 4 节）。</summary>
    public void ResetFilter() => _model.Clear();

    // ---------- ring_patrol.py:40-49 _mix ----------
    private static (int Left, int Right) Mix(int linear, int turn)
    {
        var left = (double)(linear + turn);
        var right = (double)(linear - turn);
        var peak = Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), 1.0);
        if (peak > CommandLimit)
        {
            var scale = CommandLimit / peak;
            left *= scale;
            right *= scale;
        }
        // Python int(round(x)) = 银行家舍入，C# Math.Round 默认即 ToEven。
        return ((int)Math.Round(left), (int)Math.Round(right));
    }

    // ---------- ring_patrol.py:51-53 _dominant_risk（min 按 NAMES 序取首最小） ----------
    private static string DominantRisk(MbriGrayObservation obs)
    {
        var best = Names()[0];
        var bestZone = obs.Zone.Get(best);
        for (var i = 1; i < MbriRiskModel.Names.Length; i++)
        {
            var name = MbriRiskModel.Names[i];
            var z = obs.Zone.Get(name);
            if (z < bestZone)
            {
                best = name;
                bestZone = z;
            }
        }
        return best;
    }

    private static string[] Names() => MbriRiskModel.Names;

    // ---------- ring_patrol.py:55-61 _risk_signals ----------
    private static (double Linear, double Turn) RiskSignals(MbriGrayObservation obs)
        => (obs.Zone.Front - obs.Zone.Rear, obs.Zone.Left - obs.Zone.Right);

    // ---------- ring_patrol.py:63-65 _turn_table ----------
    private static IReadOnlyDictionary<double, (int Speed, double Duration)> TurnTable(double sign)
        => MotorTurnCalibration[sign < 0.0 ? "left" : "right"];

    // ---------- ring_patrol.py:67-79 _start_edge_avoid ----------
    private void StartEdgeAvoid(MbriGrayObservation obs, long tick, string reason)
    {
        var (linearSignal, turnSignal) = RiskSignals(obs);
        if (Math.Abs(linearSignal) >= Math.Abs(turnSignal))
        {
            RiskSensor = linearSignal > 0.0 ? "rear" : "front";
        }
        else
        {
            RiskSensor = turnSignal > 0.0 ? "right" : "left";
        }

        // 只有明确的车尾贴边信号才前进，其余情况统一先后退制造转向空间。
        (int, int) command = linearSignal > RearRetreatDelta
            ? (RecoverForwardSpeed, RecoverForwardSpeed)
            : (-RecoverBackwardSpeed, -RecoverBackwardSpeed);
        Enter("EDGE_AVOID", tick, command, reason);
    }

    // ---------- ring_patrol.py:81-91 _early_front_risk ----------
    private static bool EarlyFrontRisk(MbriGrayObservation obs)
        => obs.ZoneScore < EarlyFrontZone && obs.Zone.Front < EarlyFrontAbs;

    // ---------- ring_patrol.py:93-105 _diagonal_turn_risk ----------
    private bool DiagonalTurnRisk(MbriGrayObservation obs)
    {
        double toward, away;
        if (TurnDirection == "left")
        {
            toward = obs.Zone.Left;
            away = obs.Zone.Right;
        }
        else if (TurnDirection == "right")
        {
            toward = obs.Zone.Right;
            away = obs.Zone.Left;
        }
        else
        {
            return false;
        }
        return toward < DiagonalSideZone && away - toward >= DiagonalTurnDelta;
    }

    // ---------- ring_patrol.py:107-115 _diagonal_recover_risk ----------
    private static bool DiagonalRecoverRisk(MbriGrayObservation obs)
        => Math.Min(obs.Zone.Left, obs.Zone.Right) < DiagonalSideZone
           && Math.Abs(obs.Zone.Left - obs.Zone.Right) >= DiagonalTurnDelta;

    // ---------- ring_patrol.py:117-141 _start_edge_turn ----------
    private void StartEdgeTurn(MbriGrayObservation obs, long tick, string reason,
        bool reuseDirection = false, double? forcedSign = null)
    {
        var (linearSignal, turnSignal) = RiskSignals(obs);
        double sign;
        if (forcedSign is { } forced)
        {
            sign = forced;
        }
        else if (reuseDirection)
        {
            sign = _lastTurnSign;
        }
        else if (Math.Abs(turnSignal) > TurnSignalEpsilon)
        {
            // zone 越小越接近暗外圈，始终转离风险更高的一侧。
            sign = turnSignal > 0.0 ? -1.0 : 1.0;
        }
        else
        {
            sign = _lastTurnSign;
        }

        if (Math.Abs(linearSignal) >= Math.Abs(turnSignal))
        {
            RiskSensor = linearSignal > 0.0 ? "rear" : "front";
        }
        else
        {
            RiskSensor = turnSignal > 0.0 ? "right" : "left";
        }

        var table = TurnTable(sign);
        if (!table.TryGetValue(EdgeTurnAngle, out var entry))
        {
            throw new InvalidOperationException($"转向标定表缺少角度 {EdgeTurnAngle}");
        }
        var (speed, duration) = entry;
        _lastTurnSign = sign;
        Enter("EDGE_TURN", tick, Mix(0, (int)Math.Round(sign * speed)), reason);
        TurnAngle = EdgeTurnAngle;
        TurnDirection = sign > 0.0 ? "right" : "left";
        TurnDuration = duration;
    }

    // ---------- ring_patrol.py:143-161 _start_white_response ----------
    private void StartWhiteResponse(MbriGrayObservation obs, long tick)
    {
        var hits = new HashSet<string>(obs.WhiteHits);
        RiskSensor = DominantRisk(obs);
        if (hits.Contains("front") && !hits.Contains("rear"))
        {
            Enter("WHITE_ESCAPE", tick, (-WhiteEscapeSpeed, -WhiteEscapeSpeed), "前方白边，先后退");
        }
        else if (hits.Contains("rear") && !hits.Contains("front"))
        {
            Enter("WHITE_ESCAPE", tick, (WhiteEscapeSpeed, WhiteEscapeSpeed), "后方白边，先前进");
        }
        else if (hits.Contains("left") && !hits.Contains("right"))
        {
            StartEdgeTurn(obs, tick, "左侧白边，向右转离边缘", forcedSign: 1.0);
        }
        else if (hits.Contains("right") && !hits.Contains("left"))
        {
            StartEdgeTurn(obs, tick, "右侧白边，向左转离边缘", forcedSign: -1.0);
        }
        else
        {
            StartEdgeTurn(obs, tick, "侧向或多方向白边，直接转 135 度");
        }
    }

    // ---------- ring_patrol.py:163-167 _white_edge_risk ----------
    private static bool WhiteEdgeRisk(MbriGrayObservation obs)
        => obs.WhiteHits.Length > 0 && obs.NearEdge;

    // ---------- ring_patrol.py:169-175 _start_recover ----------
    private void StartRecover(MbriGrayObservation obs, long tick, bool forward, string reason)
    {
        var speed = forward ? RecoverForwardSpeed : -RecoverBackwardSpeed;
        var state = forward ? "RECOVER_FORWARD" : "RECOVER_BACKWARD";
        Enter(state, tick, (speed, speed), reason);
    }

    // ---------- ring_patrol.py:177-193 _start_cruise_for_zone ----------
    private void StartCruiseForZone(MbriGrayObservation obs, long tick, string reason)
    {
        if (obs.ZoneScore < FastZoneScore)
        {
            RiskSensor = "";
            Enter("MEDIUM_CRUISE", tick, Mix(MediumLinear, MediumTurn), reason);
        }
        else
        {
            RiskSensor = "";
            Enter("CRUISE", tick, Mix(CruiseLinear, CruiseTurn), reason);
        }
    }

    // ---------- ring_patrol.py:195-202 _enter ----------
    private void Enter(string state, long tick, (int Left, int Right) command, string reason)
    {
        State = state;
        StateStartedTick = tick;
        Command = command;
        Reason = reason;
        TurnAngle = 0.0;
        TurnDirection = "";
        TurnDuration = 0.0;
    }

    private long ElapsedTicks(long tick) => tick - StateStartedTick;

    /// <summary>时长阈值（s）→ tick 阈值（四舍五入，design.md "时长→tick 数"）。</summary>
    private long DurationTicks(double seconds) => MbriUnits.SecondsToTicks(seconds, _tickSeconds);

    // ---------- ring_patrol.py:218-367 update ----------
    /// <summary>
    /// 单 tick 决策。tick 由调用方注入（仿真按 0.05s/tick 递增，替代真车
    /// time.monotonic——零时钟铁律）；healthy 对应真车 update(healthy)。
    /// </summary>
    public MbriPatrolResult Update(MbriGraySample raw, long tick, bool healthy = true)
    {
        var observation = _model.Update(raw);

        if (!healthy || !observation.Valid)
        {
            Enter("SENSOR_STOP", tick, (0, 0), "传感器无效或数据过期");
            return Result(observation);
        }
        if (!observation.Ready)
        {
            Enter("WARMUP", tick, (0, 0), "等待滤波窗口");
            return Result(observation);
        }

        _whiteCount = WhiteEdgeRisk(observation) ? _whiteCount + 1 : 0;
        _nearCount = observation.NearEdge ? _nearCount + 1 : 0;
        _earlyFrontCount = EarlyFrontRisk(observation) ? _earlyFrontCount + 1 : 0;

        if (_whiteCount >= WhiteConfirm && State is not ("WHITE_ESCAPE" or "EDGE_TURN" or "RECOVER_BACKWARD"))
        {
            StartWhiteResponse(observation, tick);
        }

        if (_nearCount >= NearConfirm && State is not ("WHITE_ESCAPE" or "EDGE_AVOID" or "EDGE_TURN" or "RECOVER_BACKWARD"))
        {
            StartEdgeAvoid(observation, tick, "进入危险区，先直线退离再转向");
            return Result(observation);
        }

        if (_earlyFrontCount >= EarlyConfirm
            && observation.ZoneScore < EarlyFrontZone
            && State is not ("WHITE_ESCAPE" or "EDGE_AVOID" or "EDGE_TURN" or "RECOVER_BACKWARD"))
        {
            StartEdgeAvoid(observation, tick, "前向灰度趋势变暗，提前离边");
            return Result(observation);
        }

        var diagonalRisk = (State == "EDGE_TURN" && DiagonalTurnRisk(observation))
                           || (State == "RECOVER_FORWARD" && DiagonalRecoverRisk(observation));
        if (diagonalRisk)
        {
            _diagonalCount++;
        }
        else
        {
            _diagonalCount = 0;
        }
        if (_diagonalCount >= DiagonalConfirm)
        {
            var reason = State == "EDGE_TURN"
                ? "转向侧斜压白边，重新退离后再转"
                : "回中时车身斜压白边，重新退离后再转";
            StartEdgeAvoid(observation, tick, reason);
            return Result(observation);
        }

        if (State is "EDGE_TURN" or "RECOVER_FORWARD" or "RECOVER_BACKWARD")
        {
            var deepThreshold = State == "EDGE_TURN" ? DeepZone : RecoverDeepZone;
            var minZone = Math.Min(Math.Min(observation.Zone.Front, observation.Zone.Rear),
                Math.Min(observation.Zone.Left, observation.Zone.Right));
            if (minZone < deepThreshold)
            {
                var reason = State == "EDGE_TURN"
                    ? "转中单路深暗，重新退离后再转"
                    : "回中移动时单路深暗，重新退离后再转";
                StartEdgeAvoid(observation, tick, reason);
                return Result(observation);
            }
        }

        var elapsedTicks = ElapsedTicks(tick);
        if (State == "WHITE_ESCAPE")
        {
            if (elapsedTicks >= DurationTicks(WhiteEscapeSeconds))
            {
                if (observation.WhiteClear && observation.NearClear)
                {
                    StartCruiseForZone(observation, tick, "白边直线脱离完成");
                }
                else
                {
                    StartRecover(observation, tick, Command.Left > 0, "白边后继续直线脱离，不在边缘转向");
                }
            }
            return Result(observation);
        }

        if (State == "EDGE_TURN")
        {
            if (elapsedTicks >= DurationTicks(TurnDuration))
            {
                if (DiagonalRecoverRisk(observation))
                {
                    StartEdgeAvoid(observation, tick, "转向结束时车身斜压白边，重新退离后再转");
                }
                else
                {
                    StartRecover(observation, tick, true, "转向完成，向前离开边缘");
                }
            }
            return Result(observation);
        }

        if (State is "RECOVER_FORWARD" or "RECOVER_BACKWARD")
        {
            if (elapsedTicks >= DurationTicks(RecoverSeconds))
            {
                if (observation.ZoneScore >= RecoverReleaseZone)
                {
                    StartCruiseForZone(observation, tick, "已达到避边释放线，恢复中速穿越渐变区");
                }
                else
                {
                    StartEdgeAvoid(observation, tick, "转后前进仍在暗外圈，重新退离后再转");
                }
            }
            return Result(observation);
        }

        if (State == "EDGE_AVOID" && elapsedTicks >= DurationTicks(EdgeRetreatSeconds))
        {
            StartEdgeTurn(observation, tick, "直线退离完成，开始转向",
                reuseDirection: Reason.StartsWith("转后前进", StringComparison.Ordinal));
            return Result(observation);
        }

        if (State == "EDGE_AVOID")
        {
            return Result(observation);
        }

        StartCruiseForZone(observation, tick, "按区域分级巡航");
        return Result(observation);
    }

    // ---------- ring_patrol.py:369-400 _result ----------
    private MbriPatrolResult Result(MbriGrayObservation observation)
    {
        var (linearRisk, turnRisk) = RiskSignals(observation);
        var speedLevel = State switch
        {
            "CRUISE" => "fast",
            "MEDIUM_CRUISE" => "medium",
            "EDGE_AVOID" => "slow",
            "EDGE_TURN" => "danger_turn",
            "WHITE_ESCAPE" => "danger",
            "RECOVER_FORWARD" => "danger",
            "RECOVER_BACKWARD" => "danger",
            "SENSOR_STOP" => "stop",
            "WARMUP" => "stop",
            _ => "stop",
        };
        return new MbriPatrolResult
        {
            Left = Command.Left,
            Right = Command.Right,
            State = State,
            Reason = Reason,
            SpeedLevel = speedLevel,
            TurnAngle = TurnAngle,
            TurnDirection = TurnDirection,
            TurnDuration = TurnDuration,
            RiskSensor = RiskSensor,
            LinearRisk = linearRisk,
            TurnRisk = turnRisk,
            ShovelPreheat = observation.Ready && observation.Zone.Front < ShovelPreheatFrontZone,
            Observation = observation,
        };
    }
}
