using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// MBri 比赛逻辑内置可选控制器（批2 骨架）：仲裁入口 + START_REVERSE 独占 +
/// 掉台回归接管 + 巡台兜底 + 快照 State 映射 + 事件流。与 <see cref="FsmController"/>
/// 平行的加法实现（design.md 第 1/3 节），不共享状态、不触碰既有 Fsm/MatchEngine/
/// 物理/裁判——未接线时对既有基线零影响（接线在批3）。
///
/// 仲裁结构（main.py RobotController.update 优先级）：
///   0. START_REVERSE 独占（main.py:131-147：-1000×1.8s，期间不看灰度、
///      独占控制权，patrol+reentry 仍被喂帧预热——main.py:135-137）；
///   1. 巡台每 tick 必跑（main.py:174，观测与滤波预热）；
///   2. unhealthy → reentry 以 healthy=false 接管（SENSOR_STOP，main.py:176-188）；
///   3. reentry.update（main.py:268-277）：状态 ≠ WAIT 即接管（掉台回归全流程）；
///   4. 回归完成（回 WAIT）→ 重置巡台为新实例（main.py:279-281 _reset_patrol）
///      并交还巡台。
///
/// 传感器桥接（仿真无真车硬件；批2 评审 finding 2 修复后逐项披露）：
///   - 灰度：巡台用批1 仿射 SimSampleToAdc；reentry 用掉台判定域
///     SimSampleToAdcFallDomain（走道 g&lt;150→ADC 0→zone&lt;0，掉台判定可达——
///     解决批1 openIssue；台面段与批1 仿射一致）。
///   - 数字红外 6 路：只有墙信息进分派——front←f（edge_target，唯一前向墙感）、
///     rear←r（fence，legacy14 专属；wheeledCombat11 r 未映射恒 0）；四路对角
///     是 target 模式（只探机器人/方块，不探墙），不得决定"墙在哪"，恒 false
///     （侧向 90° 分支保持移植完整，直接注入单测覆盖，集成不可达并披露）；
///     位阈值 0.35 与内核 IrTrigger 约定同值（SimParameters.cs:15）。
///   - 前向模拟红外对：仿真缺失，每侧取 max(f, 对角)×(10000/1.2)（09-25 桥文档
///     比例）：信号由 f 保证（贴墙/strong 可用），diff 由对角不对称量驱动 →
///     左偏/右偏矫正分支集成路径可执行；残余近似（对角探目标非墙）照实披露。
///   - START_REVERSE 窗口：patrol 喂帧预热；reentry 仅 Preheat 灰度滤波不推进
///     状态机（评审 finding 1：开场走道是合法赛前位姿，逐字推进会在窗内误判
///     掉台并在窗后接管冻结；main.py:136 意图即"预热灰度滤波"）。
///   - 回归流程的仿真无人复位（评审 finding 1）：reentry SAFE_STOP/IR_WAIT
///     仍掉台超 2s（MbriReentry.RearmWaitSeconds）→ 重新武装重走流程，
///     避免 headless 对局吸收态；真车"等人工"出口（灰度恢复→WAIT）保留。
///
/// 铁律：零 IO/零时钟/零随机——真车 wall-clock 全部按 tick 计数驱动。
///
/// 快照 State 映射（语义近似，design.md 第 3 节"对外枚举用既有 FsmState"）：
///   START_REVERSE → MountRing；WARMUP/CRUISE/MEDIUM_CRUISE → Search；
///   EDGE_AVOID/EDGE_TURN/WHITE_ESCAPE/RECOVER_FORWARD/RECOVER_BACKWARD → Recover；
///   reentry 流程态（IR_WAIT/TURN_*/ADC_CORRECT/ADC_APPROACH/REVERSE/SAFE_STOP）→ Recover；
///   SENSOR_STOP（巡台与 reentry 同）→ Incapacitated（失去感知停车）。
/// </summary>
public sealed class MbriFsmController
{
    /// <summary>开局后退速度（车端单位，config.py START_REVERSE_SPEED=1000，≈0.896 m/s）。</summary>
    public const int StartReverseSpeed = 1000;

    /// <summary>开局后退时长（config.py START_REVERSE_SECONDS=1.8s）。</summary>
    public const double StartReverseSeconds = 1.8;

    /// <summary>数字红外位阈值（与内核 IrTrigger 约定同值，SimParameters.cs:15）。</summary>
    public const double IrBitThreshold = 0.35;

    /// <summary>仿真红外值 → 真车模拟红外 ADC 的桥接比例（09-25 桥文档 ×(10000/1.2)）。</summary>
    public const double AnalogIrAdcScale = 10000.0 / 1.2;

    private readonly EventBus _events;
    private readonly double _tickSeconds;
    private MbriPatrol _patrol;
    private readonly MbriReentry _reentry;

    private bool _armed;
    private bool _startReverseDone;
    private long _startReverseUntilTick = -1;
    private bool _reentryActive;
    private string _loggedState = "";
    private string _loggedReason = "";

    /// <summary>Mbri 内部状态名（IDLE/START_REVERSE/巡台九态/reentry 十态；真车字符串语义）。</summary>
    public string MbriState { get; private set; } = "IDLE";

    /// <summary>巡台子状态机（测试与后续批次可检视）。</summary>
    public MbriPatrol Patrol => _patrol;

    /// <summary>掉台回归子状态机。</summary>
    public MbriReentry Reentry => _reentry;

    /// <summary>回归接管电平（reentry 状态 ≠ WAIT 期间为 true；main.py _reentry_active）。</summary>
    public bool ReentryActive => _reentryActive;

    public MbriFsmController(EventBus events, double tickSeconds = MbriUnits.DefaultTickSeconds)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
        if (!(tickSeconds > 0) || !double.IsFinite(tickSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), "tickSeconds must be a positive finite number.");
        }
        _tickSeconds = tickSeconds;
        _patrol = new MbriPatrol(tickSeconds);
        _reentry = new MbriReentry(tickSeconds);
    }

    /// <summary>发令（对应真车 run() 启动；批3 接线时由控制器选择层调用）。</summary>
    public void Arm() => _armed = true;

    /// <summary>单 tick 决策入口（与 FsmController.FsmTickFor 同风格：调用方逐 tick 传入机器人）。
    /// healthy 对应真车 RobotController.update(healthy)——传感器失效语义（仿真常态为 true）。</summary>
    public void TickFor(RobotRuntime r, long tick, bool healthy = true)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!_armed)
        {
            MbriState = "IDLE";
            MapSnapshot(r, "IDLE");
            r.V = 0;
            r.W = 0;
            SetAction(r, "等待发令");
            return;
        }

        var ir = ReadDigiIr(r);
        var analog = ReadAnalogIr(r);

        // P0-0: 开局后退上台（独占；main.py:131-147 逐行语义）。
        if (!_startReverseDone)
        {
            if (_startReverseUntilTick < 0)
            {
                _startReverseUntilTick = tick + MbriUnits.SecondsToTicks(StartReverseSeconds, _tickSeconds);
                Log(EventKind.Fsm, r, $"[mbri] START_REVERSE: 开局后退上台 ({StartReverseSpeed}×{StartReverseSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s=" +
                       $"{MbriUnits.SecondsToTicks(StartReverseSeconds, _tickSeconds)} tick 独占)",
                    new { state = "START_REVERSE", reason = "开局后退上台", tick });
            }
            if (tick < _startReverseUntilTick)
            {
                // 真车期间仍喂帧 patrol 预热（main.py:135-137），输出丢弃；
                // reentry 只预热灰度滤波不推进状态机（批2 评审 finding 1：
                // 仿真开场机器人在走道上是合法赛前位姿，逐字推进会在预热窗内
                // 误判掉台并接管，窗后冻结至终场；main.py:136 的显式意图是滤波预热）。
                _ = _patrol.Update(ReadPatrolGray(r), tick, healthy);
                _reentry.Preheat(ReadReentryGray(r));
                MbriState = "START_REVERSE";
                MapSnapshot(r, "START_REVERSE");
                var (v, w) = MbriUnits.DifferentialToVW(-StartReverseSpeed, -StartReverseSpeed);
                r.V = v;
                r.W = w;
                SetAction(r, "开局后退上台");
                return;
            }
            _startReverseDone = true;
            Log(EventKind.Fsm, r, "[mbri] START_REVERSE 完成 → 巡台仲裁",
                new { state = _patrol.State, reason = "开局后退上台完成", tick });
        }

        // 巡台每 tick 必跑（main.py:174：观测与滤波预热；所有权由 reentry 决定）。
        var patrolResult = _patrol.Update(ReadPatrolGray(r), tick, healthy);

        // P1: unhealthy → reentry 接管（main.py:176-188，SENSOR_STOP 语义）。
        if (!healthy)
        {
            _reentryActive = true;
            var unhealthyResult = _reentry.Update(ReadReentryGray(r), ir, analog, tick, healthy: false);
            ApplyReentry(r, unhealthyResult, tick);
            return;
        }

        // P2: 掉台回归接管（main.py:268-277：状态 ≠ WAIT 即接管）。
        var reentryResult = _reentry.Update(ReadReentryGray(r), ir, analog, tick, healthy: true);
        if (reentryResult.State != "WAIT")
        {
            _reentryActive = true;
            ApplyReentry(r, reentryResult, tick);
            return;
        }

        // 回归完成 → 重置巡台为新实例（main.py:279-281 _reset_patrol）并交还巡台。
        if (_reentryActive)
        {
            _patrol = new MbriPatrol(_tickSeconds);
            _reentryActive = false;
            patrolResult = _patrol.Update(ReadPatrolGray(r), tick, healthy);
        }

        // P3: 巡台兜底（RingPatrol 全状态机）。
        ApplyPatrol(r, patrolResult);
    }

    private void ApplyPatrol(RobotRuntime r, MbriPatrolResult result)
    {
        var (pv, pw) = MbriUnits.DifferentialToVW(result.Left, result.Right);
        r.V = pv;
        r.W = pw;
        MbriState = result.State;
        MapSnapshot(r, result.State);
        SetAction(r, result.Reason);
        if (result.State != _loggedState || result.Reason != _loggedReason)
        {
            Log(EventKind.Fsm, r, $"[mbri] {result.State}: {result.Reason}",
                new
                {
                    state = result.State,
                    reason = result.Reason,
                    left = result.Left,
                    right = result.Right,
                    v = pv.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    w = pw.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    zoneScore = result.Observation.ZoneScore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    riskSensor = result.RiskSensor,
                });
            _loggedState = result.State;
            _loggedReason = result.Reason;
        }
    }

    private void ApplyReentry(RobotRuntime r, MbriReentryResult result, long tick)
    {
        var (rv, rw) = MbriUnits.DifferentialToVW(result.Left, result.Right);
        r.V = rv;
        r.W = rw;
        MbriState = result.State;
        MapSnapshot(r, result.State);
        SetAction(r, result.Reason);
        if (result.State != _loggedState || result.Reason != _loggedReason)
        {
            Log(EventKind.Recover, r, $"[mbri-reentry] {result.State}: {result.Reason}",
                new
                {
                    state = result.State,
                    reason = result.Reason,
                    left = result.Left,
                    right = result.Right,
                    v = rv.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    w = rw.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    fall = result.Fall,
                    fallEdge = result.FallEdge,
                    tick,
                });
            _loggedState = result.State;
            _loggedReason = result.Reason;
        }
    }

    /// <summary>
    /// 仿真灰度逻辑别名（gF/gB/gL/gR，0-1000）→ 真车 ADC 域采样（巡台域，批1 合同）。
    /// </summary>
    private static MbriGraySample ReadPatrolGray(RobotRuntime r)
        => MbriGrayCalibration.SimSampleToAdc(
            r.Sens.GetValueOrDefault("gF"),
            r.Sens.GetValueOrDefault("gB"),
            r.Sens.GetValueOrDefault("gL"),
            r.Sens.GetValueOrDefault("gR"));

    /// <summary>
    /// 仿真灰度 → 掉台判定域 ADC 采样（走道→ADC 0→zone&lt;0，见
    /// MbriGrayCalibration.SimToAdcFallDomain——批2 解决批1 zone&lt;0 域差）。
    /// </summary>
    private static MbriGraySample ReadReentryGray(RobotRuntime r)
        => MbriGrayCalibration.SimSampleToAdcFallDomain(
            r.Sens.GetValueOrDefault("gF"),
            r.Sens.GetValueOrDefault("gB"),
            r.Sens.GetValueOrDefault("gL"),
            r.Sens.GetValueOrDefault("gR"));

    /// <summary>
    /// 六路数字红外桥接（批2 评审 finding 2 修复后）：只有 f（edge_target，唯一
    /// 前向墙感）与 r（fence）是墙信息；仿真对角通道是 target 模式（只探机器人/
    /// 方块，不探墙——Profiles.cs legacy14/wheeledCombat11），不得当"侧向墙感"用，
    /// 否则方块/对手会决定"墙在哪"分派。四路对角位恒 false；侧向 90°/对角分派分支
    /// 保持移植完整（直接注入单测覆盖），集成路径不可达并披露。
    /// 位阈值 IrBitThreshold(0.35)；仿真传感器恒有效（valid=true）。
    /// </summary>
    private static MbriDigiIr ReadDigiIr(RobotRuntime r)
        => new(
            Front: r.Sens.GetValueOrDefault("f") >= IrBitThreshold,
            Rear: r.Sens.GetValueOrDefault("r") >= IrBitThreshold,
            LeftFront: false,
            LeftRear: false,
            RightFront: false,
            RightRear: false,
            Valid: true);

    /// <summary>
    /// 前向模拟红外对桥接（批2 评审 finding 2 修复后）：仿真无此硬件对。
    /// 原实现 f×scale 左右同值 ⇒ diff≡0 恒判 center，左偏/右偏矫正分支集成路径
    /// 永不执行。改为每侧取 max(f, 对角)：
    /// - 信号 = 前向最近反射源强度：f（墙，居中对称）保证贴墙判定/strong 判定可用；
    /// - diff = 对角不对称量：前向对角（最近反射源，与真车模拟红外的"最近障碍"
    ///   语义一致）偏离时矫正分支可执行，集成路径不再恒 center。
    /// 残余近似照实披露：对角探的是机器人/方块而非墙，故矫正量为"对最近反射源
    /// 的对齐"而非真车"对墙垂直度"；官方场空场直线接近时两对角同值仍判 center。
    /// </summary>
    private static MbriAnalogIr ReadAnalogIr(RobotRuntime r)
    {
        var f = r.Sens.GetValueOrDefault("f");
        var left = Math.Max(f, r.Sens.GetValueOrDefault("dLF")) * AnalogIrAdcScale;
        var right = Math.Max(f, r.Sens.GetValueOrDefault("dRF")) * AnalogIrAdcScale;
        return new MbriAnalogIr(left, right, Valid: true);
    }

    /// <summary>对外快照 State 映射（语义近似，见类型注释映射表）。internal 供映射表单测钉死。</summary>
    internal static FsmState SnapshotState(string mbriState) => mbriState switch
    {
        "START_REVERSE" => FsmState.MountRing,
        "WARMUP" or "CRUISE" or "MEDIUM_CRUISE" => FsmState.Search,
        "EDGE_AVOID" or "EDGE_TURN" or "WHITE_ESCAPE" or "RECOVER_FORWARD" or "RECOVER_BACKWARD" => FsmState.Recover,
        "IR_WAIT" or "TURN_180" or "TURN_LEFT_90" or "TURN_RIGHT_90"
            or "ADC_CORRECT" or "ADC_APPROACH" or "REVERSE" or "SAFE_STOP" => FsmState.Recover,
        "SENSOR_STOP" => FsmState.Incapacitated,
        _ => FsmState.WaitStart, // IDLE（未发令）
    };

    private static void MapSnapshot(RobotRuntime r, string mbriState) => r.Fsm.State = SnapshotState(mbriState);

    private static void SetAction(RobotRuntime r, string text) => r.Fsm.Action = text;

    private void Log(EventKind kind, RobotRuntime r, string msg, object data)
        => _events.Emit(kind, r, msg, null, data);
}
