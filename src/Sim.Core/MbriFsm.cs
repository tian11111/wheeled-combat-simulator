using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// MBri 比赛逻辑内置可选控制器（批2 骨架 + P2 视觉追击）：仲裁入口 + START_REVERSE
/// 独占 + 掉台回归接管 + 视觉追击/近物推敌仲裁 + 巡台兜底 + 快照 State 映射 + 事件流。
/// 与 <see cref="FsmController"/> 平行的加法实现（design.md 第 1/3 节），不共享状态、
/// 不触碰既有 Fsm/MatchEngine/物理/裁判——未接线时对既有基线零影响（接线在批3）。
///
/// 仲裁结构（main.py RobotController.update 优先级）：
///   0. START_REVERSE 独占（main.py:131-147：-1000×1.8s，期间不看灰度、
///      独占控制权，patrol+reentry 仍被喂帧预热——main.py:135-137）；
///   1. 巡台每 tick 必跑（main.py:174，观测与滤波预热）；
///   2. unhealthy → reentry 以 healthy=false 接管（SENSOR_STOP，main.py:176-188）；
///      同时 hunt/probe cancel（main.py:180-181）；
///   3. reentry.update（main.py:268-277）：状态 ≠ WAIT 即接管（掉台回归全流程），
///      hunt/probe cancel（main.py:272-273）；
///   4. 回归完成（回 WAIT）→ 重置巡台为新实例（main.py:279-281 _reset_patrol）
///      并交还巡台；
///   5. P2 视觉追击仲裁（main.py:237-356，见下节）；
///   6. 巡台兜底。
///
/// P2 视觉追击仲裁（main.py 顺序，推击守卫 no-op 简化，照实披露）：
///   a. GOOD_PUSH latch（main.py:237-242）→ hunt 自持前推 400；
///   b. ENEMY_PUSH latch（main.py:244-266）→ probe 前推 700/近台沿 550；
///   c. probe 转向/刹车维持（main.py:283-295）；PROBE_TURN/PROBE_BRAKE 期间巡台
///      脱离 CRUISE 级（HUNT_ALLOWED_PATROL_STATES，main.py:42）→ 双 cancel 回巡台；
///   d. 巡台分级门禁（main.py:310-314）：非 CRUISE/MEDIUM_CRUISE → 双 cancel 回巡台；
///   e. hunt.update（main.py:316-332）→ owns 即接管；
///   f. probe.update（main.py:334-352，allow_start=not vision_good）→ owns 即接管；
///   g. 策略交还时重置巡台（main.py:353-356 _reset_patrol：交接帧走新巡台实例，
///      滤波窗重新填充 → 短暂 WARMUP 停车，真车语义逐字）。
///
/// 与 main.py 的顺序差异（披露，非逐字）：真车把 GOOD_PUSH/ENEMY_PUSH latch 放在
/// reentry.update 之前（main.py:237/244 vs 268）——真车推击中的坠台由铲子守卫
/// （REVERSE 收回）兜底；仿真铲子守卫 no-op（无铲子机构/红外，prd R3），若维持
/// 真车顺序，推击 latch 会在掉台后永久吞掉 reentry → 无界冲出。故仿真把 reentry
/// 检查保持在推击 latch 之上：推击期间仍逐 tick 检查掉台，掉台即 reentry 接管并
/// cancel 推击（与 main.py:272-273 的 cancel 语义一致）。守卫 no-op 的推击出口
/// 由此变为：块下台（得分达成）→ 车随推出台沿 → reentry 接管回归（A2 有界回台）。
///
/// 视觉输入（特权观测，全链路披露）：
///   - 视觉源 <see cref="VisionSource"/> 由 MatchEngine 接线（仅 mbri 场景）：
///     ObjectSet 真值 → 真车 YOLO 语义投影（MbriVisionProjector，特权观测语义、
///     自造常数与补偿过滤全部在其头注释披露）；未接线（null）时帧为 null，
///     hunt 语义=视觉无效（NO_TARGET 释放），probe 语义=has_good/has_bad=false。
///   - 事件流披露：首次 hunt/probe 取得控制权时发出显式披露事件；后续每次
///     hunt/probe 状态迁移事件携带 vision="privileged-truth" 字段。
///
/// 传感器桥接（仿真无真车硬件；批2 评审 finding 2 修复后逐项披露）：
///   - 灰度：巡台用重标仿射 SimSampleToAdc（A1：zone 0/1 锚定官方场台沿黑带/
///     台心红区实测灰度，台心≈1、接近边沿→0，治 early-fire）；reentry 用掉台
///     判定域 SimSampleToAdcFallDomain（走道 g&lt;150→ADC 0→zone&lt;0，掉台判定可达
///     ——解决批1 openIssue；台面段与巡台仿射一致）。
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
///   - 有界回台（A2+A4）：reentry 完成对准并倒车脱离后仍在场外 → REMOUNT 冲台
///     （START_REVERSE 同款命令 −1000×1.8s），fall-domain 灰度恢复台面值即回台
///     成功（回 WAIT 交还巡台），1+2 次尝试耗尽仍失败 → SAFE_STOP 如实停车
///     （真车 reentry.py:8 主动上台未实现的仿真补全，见 MbriReentry 注释）。
///
/// 铁律：零 IO/零时钟/零随机——真车 wall-clock 全部按 tick 计数驱动。
///
/// 快照 State 映射（语义近似，design.md 第 3 节"对外枚举用既有 FsmState"）：
///   START_REVERSE → MountRing；WARMUP/CRUISE/MEDIUM_CRUISE → Search；
///   EDGE_AVOID/EDGE_TURN/WHITE_ESCAPE/RECOVER_FORWARD/RECOVER_BACKWARD → Recover；
///   reentry 流程态（IR_WAIT/TURN_*/ADC_CORRECT/ADC_APPROACH/REVERSE/REMOUNT/SAFE_STOP）→ Recover；
///   P2 hunt 追踪/确认/避让态与 probe 转向/等待/冷却态 → Search；
///   P2 推击态（GOOD_PUSH/ENEMY_PUSH）→ Attack；
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

    /// <summary>
    /// 巡台分级门禁（main.py:42 HUNT_ALLOWED_PATROL_STATES 逐字）：巡台处于这两档
    /// 才允许 hunt 追击/probe 转向启动；推击 latch（GOOD_PUSH/ENEMY_PUSH）不受此门禁
    /// （main.py:237/244 先于门禁）。
    /// </summary>
    public static readonly IReadOnlySet<string> HuntAllowedPatrolStates =
        new HashSet<string> { "CRUISE", "MEDIUM_CRUISE" };

    private readonly EventBus _events;
    private readonly double _tickSeconds;
    private MbriPatrol _patrol;
    private readonly MbriReentry _reentry;
    private readonly MbriHuntController _hunt;
    private readonly MbriProbeController _probe;

    private bool _armed;
    private bool _startReverseDone;
    private long _startReverseUntilTick = -1;
    private bool _reentryActive;
    private bool _strategyOwned;
    private bool _visionPrivilegeDisclosed;
    private string _loggedState = "";
    private string _loggedReason = "";

    /// <summary>
    /// P2 视觉源（由 MatchEngine 对 mbri 场景接线）：ObjectSet 真值 → 投影帧
    /// （特权观测，披露见 <see cref="MbriVisionProjector"/>）。null = 视觉未接线
    /// （hunt 视为视觉无效帧，probe 视为 stale 摘要）。
    /// </summary>
    public Func<RobotRuntime, long, MbriVisionFrame?>? VisionSource { get; set; }

    /// <summary>Mbri 内部状态名（IDLE/START_REVERSE/巡台九态/reentry 十态；真车字符串语义）。</summary>
    public string MbriState { get; private set; } = "IDLE";

    /// <summary>巡台子状态机（测试与后续批次可检视）。</summary>
    public MbriPatrol Patrol => _patrol;

    /// <summary>掉台回归子状态机。</summary>
    public MbriReentry Reentry => _reentry;

    /// <summary>视觉追击子状态机（P2；测试与事件流可检视）。</summary>
    public MbriHuntController Hunt => _hunt;

    /// <summary>近物推敌子状态机（P2）。</summary>
    public MbriProbeController Probe => _probe;

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
        _hunt = new MbriHuntController(tickSeconds);
        _probe = new MbriProbeController(tickSeconds);
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

        // P1: unhealthy → reentry 接管（main.py:176-188，SENSOR_STOP 语义）；
        // hunt/probe cancel（main.py:180-181）。
        if (!healthy)
        {
            _hunt.Cancel();
            _probe.Cancel();
            _reentryActive = true;
            var unhealthyResult = _reentry.Update(ReadReentryGray(r), ir, analog, tick, healthy: false);
            ApplyReentry(r, unhealthyResult, tick);
            return;
        }

        // P2: 掉台回归接管（main.py:268-277：状态 ≠ WAIT 即接管）；
        // hunt/probe cancel（main.py:272-273）。顺序对 main.py 的差异（reentry 高于
        // 推击 latch）见类型头注释披露——守卫 no-op 下这是推击的有界出口。
        var reentryResult = _reentry.Update(ReadReentryGray(r), ir, analog, tick, healthy: true);
        if (reentryResult.State != "WAIT")
        {
            _hunt.Cancel();
            _probe.Cancel();
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

        // ---------- P2 视觉追击仲裁（main.py:237-356；推击守卫 no-op 简化） ----------
        var vision = VisionSource?.Invoke(r, tick);
        var probeVision = MbriProbeVision.From(vision);
        var nearIr = ReadNearIr(r);

        // a. GOOD_PUSH latch（main.py:237-242）：hunt 自持前推 400，不读视觉、
        //    不受巡台门禁；真车经 _run_push_guard 监控铲子红外才退出，仿真守卫
        //    no-op → 出口=块下台得分 + 车随出台沿 → 上方 reentry 逐 tick 检查接管
        //    （顺序差异披露见类型头注释）。
        if (_hunt.State == "GOOD_PUSH")
        {
            _strategyOwned = true;
            ApplyHunt(r, _hunt.Update(vision, nearIr, tick, healthy), tick);
            return;
        }

        // b. ENEMY_PUSH latch（main.py:244-266）：前推 700/近台沿 550；被视觉
        //    good/bad 或台沿中断 → 交还（COOLDOWN 拥有控制权则继续持有）。
        if (_probe.State == "ENEMY_PUSH")
        {
            _strategyOwned = true;
            var pushed = _probe.Update(nearIr, patrolResult.Observation, probeVision, tick, healthy, allowStart: false);
            if (pushed.State == "ENEMY_PUSH" || pushed.OwnsControl)
            {
                ApplyProbe(r, pushed, tick);
                return;
            }
        }

        // c. probe 转向/刹车维持（main.py:283-295）；PROBE_TURN/PROBE_BRAKE 期间
        //    巡台脱离 CRUISE 级 → 双 cancel 回巡台；活动 probe 抑制 hunt。
        if (_probe.Active)
        {
            if ((_probe.State == "PROBE_TURN" || _probe.State == "PROBE_BRAKE")
                && !HuntAllowedPatrolStates.Contains(patrolResult.State))
            {
                _probe.Cancel();
                _hunt.Cancel();
                ApplyPatrol(r, patrolResult);
                return;
            }
            _hunt.Cancel();
            var probeHold = _probe.Update(nearIr, patrolResult.Observation, probeVision, tick, healthy, allowStart: false);
            if (probeHold.State == "ENEMY_PUSH" || probeHold.OwnsControl)
            {
                _strategyOwned = true;
                ApplyProbe(r, probeHold, tick);
                return;
            }
        }

        // d. 巡台分级门禁（main.py:310-314 HUNT_ALLOWED_PATROL_STATES）。
        if (!HuntAllowedPatrolStates.Contains(patrolResult.State))
        {
            _hunt.Cancel();
            _probe.Cancel();
            ApplyPatrol(r, patrolResult);
            return;
        }

        // e. hunt 追击（main.py:316-332）：owns 即接管（GOOD_PUSH 由下一 tick 的
        //    latch 持续；真车经推击守卫，此处 no-op 直通）。
        var huntResult = _hunt.Update(vision, nearIr, tick, healthy);
        if (huntResult.OwnsControl)
        {
            _strategyOwned = true;
            ApplyHunt(r, huntResult, tick);
            return;
        }

        // f. probe 启动（main.py:334-352）：视觉正处理 good 时不启动
        //    （allow_start=not vision_good）。
        var probeStart = _probe.Update(nearIr, patrolResult.Observation, probeVision, tick, healthy,
            allowStart: !probeVision.HasGood);
        if (probeStart.State == "ENEMY_PUSH" || probeStart.OwnsControl)
        {
            _strategyOwned = true;
            ApplyProbe(r, probeStart, tick);
            return;
        }

        // g. 策略交还 → 重置巡台（main.py:353-356 _reset_patrol：交接帧即用新实例
        //    的结果，滤波窗重新填充 → 短暂 WARMUP 停车，真车语义逐字）。
        if (_strategyOwned)
        {
            _strategyOwned = false;
            _patrol = new MbriPatrol(_tickSeconds);
            patrolResult = _patrol.Update(ReadPatrolGray(r), tick, healthy);
        }

        // 巡台兜底（RingPatrol 全状态机）。
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
    /// P2 hunt 结果落地（v/w 换算 + 快照映射 + 事件流；事件携带特权观测披露字段）。
    /// </summary>
    private void ApplyHunt(RobotRuntime r, MbriHuntResult result, long tick)
    {
        var (hv, hw) = MbriUnits.DifferentialToVW(result.Left, result.Right);
        r.V = hv;
        r.W = hw;
        MbriState = result.State;
        MapSnapshot(r, result.State);
        SetAction(r, result.Reason);
        DiscloseVisionPrivilege(r, tick);
        if (result.State != _loggedState || result.Reason != _loggedReason)
        {
            Log(EventKind.Fsm, r, $"[mbri-hunt] {result.State}: {result.Reason}",
                new
                {
                    state = result.State,
                    reason = result.Reason,
                    mode = result.Mode,
                    ownsControl = result.OwnsControl,
                    left = result.Left,
                    right = result.Right,
                    v = hv.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    w = hw.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    targetType = result.TargetType,
                    goodOffsetX = result.GoodOffsetX,
                    badOffsetX = result.BadOffsetX,
                    nearDirection = result.NearDirection,
                    goodLocked = result.GoodLocked,
                    vision = "privileged-truth",
                    tick,
                });
            _loggedState = result.State;
            _loggedReason = result.Reason;
        }
    }

    /// <summary>P2 probe 结果落地（同 <see cref="ApplyHunt"/> 的事件流披露约定）。</summary>
    private void ApplyProbe(RobotRuntime r, MbriProbeResult result, long tick)
    {
        var (pv, pw) = MbriUnits.DifferentialToVW(result.Left, result.Right);
        r.V = pv;
        r.W = pw;
        MbriState = result.State;
        MapSnapshot(r, result.State);
        SetAction(r, result.Reason);
        DiscloseVisionPrivilege(r, tick);
        if (result.State != _loggedState || result.Reason != _loggedReason)
        {
            Log(EventKind.Fsm, r, $"[mbri-probe] {result.State}: {result.Reason}",
                new
                {
                    state = result.State,
                    reason = result.Reason,
                    ownsControl = result.OwnsControl,
                    left = result.Left,
                    right = result.Right,
                    v = pv.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    w = pw.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    sourceDirection = result.SourceDirection,
                    turnDirection = result.TurnDirection,
                    confirmed = result.Confirmed,
                    slow = result.Slow,
                    visionCount = result.VisionCount,
                    visionVerdict = result.VisionVerdict,
                    vision = "privileged-truth",
                    tick,
                });
            _loggedState = result.State;
            _loggedReason = result.Reason;
        }
    }

    /// <summary>
    /// 特权观测披露事件（每控制器一次）：hunt/probe 首次取得控制权时发出，
    /// 把"视觉=ObjectSet 真值投影"语义写进事件流（要求：头注释与事件流必须披露）。
    /// </summary>
    private void DiscloseVisionPrivilege(RobotRuntime r, long tick)
    {
        if (_visionPrivilegeDisclosed)
        {
            return;
        }
        _visionPrivilegeDisclosed = true;
        Log(EventKind.Fsm, r,
            "[mbri] 视觉输入披露: hunt/probe 视觉=ObjectSet 真值投影（特权观测：引擎已知块位/类别/在台状态；" +
            "无遮挡/噪声/漏检建模；置信度常数 0.9；详见 MbriVisionProjector 头注释）",
            new { vision = "privileged-truth", disclosure = true, tick });
    }

    /// <summary>
    /// P2 六路数字红外桥接（probe/hunt 近物语义，批2 ReadDigiIr 的姊妹投影）：
    /// 仿真四路对角（dLF/dRF/dLB/dRB，target 模式=只探机器人/方块，Profiles.cs:471-474）
    /// 具备"近物体"语义 → 桥接为 left_front/right_front/left_rear/right_rear；
    /// 正前/正后专用近物通道仿真缺失（f=edge_target 含墙/台沿语义、r=fence 围栏语义，
    /// 均非近物体语义，不得充当敌人探测）→ Front/Rear 恒 false：正前由
    /// (left_front && right_front) 原生分支承载（proximity_probe.py:91-94），正后
    /// （rear→180°）分支集成不可达，保持移植完整（单测注入覆盖），全量披露见
    /// MbriProbe 文件头。位阈值与 ReadDigiIr 同值（IrBitThreshold）；wheeledCombat11
    /// 对角为数字位（0/1），legacy14 对角为连续值（≥0.35 ⇒ 距离 ≤0.65×1.6m），
    /// 两种档案下同阈值语义一致。
    /// </summary>
    private static MbriDigiIr ReadNearIr(RobotRuntime r)
        => new(
            Front: false,
            Rear: false,
            LeftFront: r.Sens.GetValueOrDefault("dLF") >= IrBitThreshold,
            LeftRear: r.Sens.GetValueOrDefault("dLB") >= IrBitThreshold,
            RightFront: r.Sens.GetValueOrDefault("dRF") >= IrBitThreshold,
            RightRear: r.Sens.GetValueOrDefault("dRB") >= IrBitThreshold,
            Valid: true);

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
    /// 前向模拟红外对桥接（批2 评审 finding 2 修复后；A6 有效性门）：
    /// 仿真无此硬件对。原实现 f×scale 左右同值 ⇒ diff≡0 恒判 center，左偏/右偏矫正
    /// 分支集成路径永不执行。改为每侧取 max(f, 对角)：
    /// - 信号 = 前向最近反射源强度：f（墙，居中对称）保证贴墙判定/strong 判定可用；
    /// - diff = 对角不对称量：前向对角（最近反射源，与真车模拟红外的"最近障碍"
    ///   语义一致）偏离时矫正分支可执行，集成路径不再恒 center。
    /// A6 有效性门（10-01-mbri-hunt-engagement）：Valid = f ≥ IrBitThreshold。
    /// 动机：对角是 target 模式（探机器人/方块），mirror 对局中掉台位姿旁常有
    /// 对手/块——f 暗时 analog 桥让 ADC_CORRECT 把对手当墙"正对确认"，REVERSE/
    /// REMOUNT 全链沿错误方向走道翻滚（mirror seed1 实证：双方 108s 不回台）。
    /// f 是唯一墙感（edge_target），门在 f 上即"矫正只在真的看到墙时进行"；
    /// f 暗时 analog 无效 → reentry 走既有 SAFE_STOP（"模拟红外无效"）→ A5b
    /// 有界扫描直到 f 捕到台沿。f 亮时对角不对称量仍驱动矫正（finding-2 可执行性
    /// 不变）。残余近似照实披露：对角探的是机器人/方块而非墙，矫正量为"对最近
    /// 反射源的对齐"而非真车"对墙垂直度"；官方场空场直线接近时两对角同值仍判 center。
    /// </summary>
    private static MbriAnalogIr ReadAnalogIr(RobotRuntime r)
    {
        var f = r.Sens.GetValueOrDefault("f");
        var frontLit = f >= IrBitThreshold;
        var left = Math.Max(f, r.Sens.GetValueOrDefault("dLF")) * AnalogIrAdcScale;
        var right = Math.Max(f, r.Sens.GetValueOrDefault("dRF")) * AnalogIrAdcScale;
        return new MbriAnalogIr(left, right, Valid: frontLit);
    }

    /// <summary>对外快照 State 映射（语义近似，见类型注释映射表）。internal 供映射表单测钉死。</summary>
    internal static FsmState SnapshotState(string mbriState) => mbriState switch
    {
        "START_REVERSE" => FsmState.MountRing,
        "WARMUP" or "CRUISE" or "MEDIUM_CRUISE" => FsmState.Search,
        "EDGE_AVOID" or "EDGE_TURN" or "WHITE_ESCAPE" or "RECOVER_FORWARD" or "RECOVER_BACKWARD" => FsmState.Recover,
        "IR_WAIT" or "TURN_180" or "TURN_LEFT_90" or "TURN_RIGHT_90"
            or "ADC_CORRECT" or "ADC_APPROACH" or "REVERSE" or "REMOUNT" or "SAFE_STOP" => FsmState.Recover,
        // P2 hunt/probe（语义近似）：追踪/确认/避让/转向/等待 → Search；推击 → Attack。
        // hunt 的 tracker 联动态（BIG_TURN_*/ARC_*）经 hunt.State 透出（hunt.py:429）。
        "BAD_CONFIRM" or "AVOID_TURN" or "AVOID_RELEASE" or "GOOD_REARM_WAIT"
            or "GOOD_ACQUIRE" or "GOOD_CONFIRM" or "GOOD_LOST_HOLD" or "IR_UNCLASSIFIED"
            or "BIG_TURN_LEFT" or "BIG_TURN_RIGHT" or "ARC_LEFT" or "ARC_RIGHT"
            or "PROBE_BRAKE" or "PROBE_TURN" or "PROBE_VISION_WAIT" or "COOLDOWN" => FsmState.Search,
        "GOOD_PUSH" or "ENEMY_PUSH" => FsmState.Attack,
        "SENSOR_STOP" => FsmState.Incapacitated,
        _ => FsmState.WaitStart, // IDLE（未发令）
    };

    private static void MapSnapshot(RobotRuntime r, string mbriState) => r.Fsm.State = SnapshotState(mbriState);

    private static void SetAction(RobotRuntime r, string text) => r.Fsm.Action = text;

    private void Log(EventKind kind, RobotRuntime r, string msg, object data)
        => _events.Emit(kind, r, msg, null, data);
}
