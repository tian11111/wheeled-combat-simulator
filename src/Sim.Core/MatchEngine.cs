using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// External controller seam. The engine builds one observation per robot per
/// tick and asks the adapter for an action; <c>null</c> means "no external
/// action this tick" and the robot falls back to its built-in FSM (legacy
/// <c>stepSimExt</c> semantics). Process lifetime, request-id matching,
/// deadlines and zero-action fallback live in the adapter implementation
/// (Sim.Cli Python bridge), not in the core.
/// </summary>
public interface IControllerAdapter
{
    RobotAction? Decide(Observation observation);
}

/// <summary>Legacy referee control phases (PREP/READY/RUNNING/PAUSED/FINISHED).</summary>
public enum MatchControlPhase
{
    Prep,
    Ready,
    Running,
    Paused,
    Finished,
}

/// <summary>
/// The deterministic match kernel. Ports the legacy CORE <c>resetAll</c> /
/// <c>stepSimExt</c> / referee pipeline onto the Sim.Protocol DTOs with a
/// fixed tick length (scenario <c>tickSeconds</c>, default 0.05 s). Same seed
/// and same accepted action sequence produce bit-identical events and scores.
/// </summary>
public sealed class MatchEngine
{
    /// <summary>Version stamped into replay headers produced by this core.</summary>
    public const string CoreVersion = "sim-core-1.0.0";

    private readonly Scenario _scenario;
    private readonly FieldModel _field;
    private readonly SimParameters _params;
    private readonly DeterministicRandom.Mulberry32 _rng;
    private readonly RobotRuntime _us;
    private readonly RobotRuntime _them;
    private readonly List<BlockRuntime> _blocks;
    private readonly EventBus _events;
    private readonly PhysicsWorld _physics;
    private readonly SensorSampler _sensors;
    private readonly FsmController _fsm;
    private readonly IVisionAdapter _vision;

    private MatchControlPhase _phase = MatchControlPhase.Prep;
    private double _prepRemaining = 60;
    private double _matchTimer;
    private bool _paused;
    private double _scoreUs;
    private double _scoreThem;
    private double _penaltyUs;
    private double _penaltyThem;
    private string _scorePhase = "both_off";
    private double _scorePhaseT;
    private long _lastCommittedSeq;
    private double _lastRewardUs;
    private double _lastRewardThem;
    private readonly List<ReplayTick> _replayTicks = new();
    private readonly List<string> _pendingCommands = new();
    private long _requestIdCounter;

    /// <summary>
    /// Default construction: the classifyRate random stub stays the vision
    /// adapter, preserving the one-rng-draw semantics of every existing trace.
    /// </summary>
    public MatchEngine(Scenario scenario) : this(scenario, null)
    {
    }

    /// <summary>
    /// Explicit vision adapter injection (R3): the deterministic real-vision
    /// replay path replaces the classifyRate stub for this match. A null
    /// adapter is identical to the single-argument constructor — the default
    /// path must stay bit-identical (rng draw order, events, scores).
    /// </summary>
    public MatchEngine(Scenario scenario, IVisionAdapter? visionAdapter)
    {
        var errors = scenario.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid scenario: {string.Join(" ", errors)}", nameof(scenario));
        }
        _scenario = scenario;
        _field = new FieldModel(scenario.Field);
        _params = SimParameters.FromDictionary(scenario.Parameters);
        _rng = new DeterministicRandom.Mulberry32(unchecked((int)scenario.Seed));
        _vision = visionAdapter ?? new ClassifyRateVision(_params);

        var usVehicle = VehicleNormalizer.Normalize(
            scenario.Vehicles.TryGetValue(RoleNames.Us, out var us) ? us : new VehicleProfile());
        var themVehicle = VehicleNormalizer.Normalize(
            scenario.Vehicles.TryGetValue(RoleNames.Them, out var them) ? them : new VehicleProfile());

        _us = CreateRobot(RoleNames.Us, "我方", scenario.Field.Starts[RoleNames.Us], usVehicle);
        _them = CreateRobot(RoleNames.Them, "对手", scenario.Field.Starts[RoleNames.Them], themVehicle);        _blocks = CreateBlocks(scenario, _us, _them);

        // 比赛时长来自场景（官方默认 120s）；全局计时器与双方 FSM 计时器同源，
        // 避免 --duration / 自定义场景被运行时常量覆盖。
        var matchDuration = scenario.Field.MatchDuration;
        _matchTimer = matchDuration;
        _us.Fsm.Timer = matchDuration;
        _them.Fsm.Timer = matchDuration;

        _events = new EventBus();
        _physics = new PhysicsWorld(_field, _params, _us, _them, _blocks, _events);
        _sensors = new SensorSampler(_field, _params, _us, _them, _blocks, scenario.Seed, () => SimStepIndex);
        _fsm = new FsmController(_field, _physics, _params, () => _rng.Next(), _us, _them, _blocks, _events,
            _vision, OnBothDone);

        // resetAll tail: refresh sensors once so PREP-phase views show real data.
        _sensors.SampleSensorsFor(_us);
        _sensors.SampleSensorsFor(_them);
    }

    /// <summary>
    /// Spawns a robot. Scenario start poses are field-local (see
    /// <see cref="FieldModel.Transform"/>); runtime state is world-space.
    /// </summary>
    private RobotRuntime CreateRobot(string role, string name, Pose2 start, VehicleProfile vehicle)
    {
        var t = _field.Transform;
        var (x, y) = t.LocalToWorldPoint(start.X, start.Y);
        return new()
        {
            Role = role,
            Name = name,
            X = x,
            Y = y,
            Th = t.LocalToWorldHeading(start.Th),
            Vehicle = vehicle,
            R = vehicle.CollisionRadius,
            ZG = _field.StageHeightAt(x, y),
            StallAnchorX = x,
            StallAnchorY = y,
        };
    }

    /// <summary>
    /// Block layout: fixed scenario coordinates are field-local and freeze
    /// the layout; null coordinates fall back to the legacy defaults and then
    /// the seeded <c>respawnBlock</c> placement (20 attempts in field-local
    /// coordinates, rejecting positions within 0.8 m of either robot or inside
    /// the central 0.6 m zone). Runtime positions are world-space.
    /// </summary>
    private List<BlockRuntime> CreateBlocks(Scenario scenario, RobotRuntime us, RobotRuntime them)
    {
        var defaults = OfficialLayout.Blocks
            .Select(b => (b.Kind, b.Kind == BlockKind.Buff ? "增益块" : "减益块", b.X!.Value, b.Y!.Value))
            .ToList();
        var blocks = new List<BlockRuntime>();
        for (var i = 0; i < scenario.Blocks.Count; i++)
        {
            var spec = scenario.Blocks[i];
            var (kind, name, fx, fy) = i < defaults.Count
                ? defaults[i]
                : (spec.Kind, spec.Kind == BlockKind.Buff ? "增益块" : "减益块", 1.9, 1.9);
            var block = new BlockRuntime
            {
                Kind = spec.Kind,
                Name = name,
                X = spec.X ?? fx,
                Y = spec.Y ?? fy,
                R = spec.Radius ?? scenario.Field.BlockRadius,
            };
            if (spec.X is null || spec.Y is null)
            {
                RespawnBlock(block, us, them);
            }
            else
            {
                // spec coordinates are field-local; map into the world once.
                (block.X, block.Y) = _field.Transform.LocalToWorldPoint(block.X, block.Y);
                block.WasOn = _field.OnPlatform(block.X, block.Y);
            }
            blocks.Add(block);
        }
        return blocks;
    }

    private void RespawnBlock(BlockRuntime block, RobotRuntime us, RobotRuntime them)
    {
        // Deterministic placement happens in field-local coordinates so the
        // seeded draw order never depends on the field pose.
        var (ux, uy) = _field.Transform.WorldToLocalPoint(us.X, us.Y);
        var (tx, ty) = _field.Transform.WorldToLocalPoint(them.X, them.Y);
        var el = _field.El;
        var span = 2 * _field.Half - 0.7;
        for (var i = 0; i < 20; i++)
        {
            var x = el + 0.35 + _rng.Next() * span;
            var y = el + 0.35 + _rng.Next() * span;
            var distUs = Js.Hypot(ux - x, uy - y);
            var distThem = Js.Hypot(tx - x, ty - y);
            if (Math.Min(distUs, distThem) > 0.8 && !(x > 1.6 && x < 2.2 && y > 1.6 && y < 2.2))
            {
                (block.X, block.Y) = _field.Transform.LocalToWorldPoint(x, y);
                break;
            }
        }
        block.Vx = 0;
        block.Vy = 0;
        block.WasOn = true;
        block.Out = false;
        block.ContactThisStep.Clear();
        block.LastContactRole = null;
    }

    // ---------- public state ----------

    public Scenario Scenario => _scenario;

    public FieldModel Field => _field;

    public RobotRuntime Us => _us;

    public RobotRuntime Them => _them;

    public IReadOnlyList<BlockRuntime> Blocks => _blocks;

    public EventBus Events => _events;

    public MatchControlPhase Phase => _phase;

    public bool Paused => _paused;

    /// <summary>Committed tick count (one snapshot per tick).</summary>
    public long TickIndex { get; private set; }

    /// <summary>Legacy simStepIndex (sensor-noise stream key, 1-based after the first step).</summary>
    public long SimStepIndex { get; private set; }

    public Scores Scores => new() { Us = _scoreUs, Them = _scoreThem };

    public Scores RestartPenalties => new() { Us = _penaltyUs, Them = _penaltyThem };

    public double MatchTimer => _matchTimer;

    public bool Done => _phase == MatchControlPhase.Finished;

    // ---------- referee commands ----------

    /// <summary>Arms both robots (legacy <c>arm()</c>): WAIT_START → MOUNT_RING and PREP/READY → RUNNING.</summary>
    public void Arm()
    {
        ArmFor(_us);
        ArmFor(_them);
    }

    private void ArmFor(RobotRuntime r)
    {
        var st = r.Fsm;
        if (st.Manual)
        {
            _events.Emit(EventKind.Arm, r, "手动模式无需发令", "warn");
            return;
        }
        if (st.Armed)
        {
            if (st.State == FsmState.WaitStart)
            {
                _events.Emit(EventKind.Arm, r, "已在等待/运行中", "warn");
            }
            return;
        }
        st.Armed = true;
        if (_phase is MatchControlPhase.Prep or MatchControlPhase.Ready)
        {
            _phase = MatchControlPhase.Running;
            _paused = false;
            _prepRemaining = 0;
        }
        _scorePhase = ScoreClockPhase();
        _scorePhaseT = 0;
        st.State = FsmState.MountRing;
        st.Mount = new MountState();
        _events.Emit(EventKind.Arm, r, "[fsm] 发令! WAIT_START → MOUNT_RING");
    }

    /// <summary>Pauses a RUNNING match (legacy <c>pauseMatch</c>). Returns the resulting phase.</summary>
    public MatchControlPhase Pause(string? reason = null)
    {
        if (_phase == MatchControlPhase.Running)
        {
            _phase = MatchControlPhase.Paused;
            _paused = true;
            _events.Emit(EventKind.Pause, _us, $"[referee] 比赛暂停{(reason is { Length: > 0 } ? ": " + reason : "")}", "score", neutral: true);
        }
        return _phase;
    }

    /// <summary>Resumes a PAUSED match (legacy <c>resumeMatch</c>). Returns the resulting phase.</summary>
    public MatchControlPhase Resume()
    {
        if (_phase == MatchControlPhase.Paused)
        {
            _phase = MatchControlPhase.Running;
            _paused = false;
            _events.Emit(EventKind.Resume, _us, "[referee] 比赛继续", "score", neutral: true);
        }
        return _phase;
    }

    /// <summary>
    /// Restart/debug penalty (legacy <c>restartFor</c>): "restart"/"reboot" kinds cost the
    /// opponent 4 points, anything else 3. Returns the points awarded.
    /// </summary>
    public double RestartPenalty(string role, string kind = "debug")
    {
        var r = role == RoleNames.Them ? _them : _us;
        var key = r.IsUs ? RoleNames.Us : RoleNames.Them;
        var label = (kind ?? "debug").ToLowerInvariant();
        var pts = label.Contains("restart") || label.Contains("reboot") ? 4 : 3;
        if (r.IsUs)
        {
            _penaltyUs += pts;
        }
        else
        {
            _penaltyThem += pts;
        }
        OppGain(r, pts);
        _events.Emit(EventKind.RestartPenalty, r,
            $"[referee] {(pts == 4 ? "重启" : "调试")}判罚, 对方 +{Js.Num(pts)} ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})", "score");
        RecordCommand($"restart:{key}:{kind}");
        return pts;
    }

    /// <summary>
    /// Real restart (referee R/T): the target robot returns to its scenario
    /// start pose with motion, sensor and FSM transients cleaned; the opponent
    /// is awarded exactly 4 points and the restarted role's penalty total
    /// increments once. The match clock, the other robot and the blocks are
    /// preserved, and the restarted robot re-enters MOUNT_RING (armed) so it
    /// resumes the mount/recovery flow without extending the clock — a robot
    /// that already finished is revivable while the match is still active.
    /// Only legal while the match is live (<see cref="MatchControlPhase.Running"/>
    /// or <see cref="MatchControlPhase.Paused"/>); Prep/Ready/Finished reject
    /// with no score, event or replay changes. Records the additive command
    /// <c>restart_robot:&lt;role&gt;</c>; the legacy penalty-only
    /// <see cref="RestartPenalty"/> path is untouched.
    /// Returns false when the current phase rejects the restart.
    /// </summary>
    public bool RestartRobot(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!RoleNames.IsKnownRole(role))
        {
            throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        }
        if (_phase is not (MatchControlPhase.Running or MatchControlPhase.Paused))
        {
            return false;
        }
        var r = role == RoleNames.Us ? _us : _them;
        ResetRobotToStart(r);
        const double points = 4;
        if (r.IsUs)
        {
            _penaltyUs += points;
        }
        else
        {
            _penaltyThem += points;
        }
        OppGain(r, points);
        _events.Emit(EventKind.Restart, r,
            $"[referee] 真实重启 {r.Name}: 回到出发点并清理瞬态, 对方 +{Js.Num(points)} ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})",
            "score",
            new { role = r.Role, points, scorer = r.IsUs ? RoleNames.Them : RoleNames.Us });
        RecordCommand($"restart_robot:{r.Role}");
        return true;
    }

    /// <summary>
    /// Resets one robot's mutable runtime state to its scenario start pose
    /// (mapped through <see cref="FieldModel.Transform"/>) and replaces the
    /// FSM runtime with clean sub-state objects. Match elapsed time
    /// (<c>Fsm.SimT</c>) is kept and the FSM clock is set to the current match
    /// timer, so the match ends exactly when it would have anyway.
    /// </summary>
    private void ResetRobotToStart(RobotRuntime r)
    {
        var start = _scenario.Field.Starts[r.Role];
        var t = _field.Transform;
        var (x, y) = t.LocalToWorldPoint(start.X, start.Y);
        r.X = x;
        r.Y = y;
        r.Th = t.LocalToWorldHeading(start.Th);
        r.V = 0;
        r.W = 0;
        r.Vx = 0;
        r.Vy = 0;
        r.Omega = 0;
        r.SpinOmega = 0;
        r.ZG = _field.StageHeightAt(x, y);
        r.Pitch = 0;
        r.Roll = 0;
        r.IsStalled = false;
        r.StallT = 0;
        r.StallAnchorX = x;
        r.StallAnchorY = y;
        r.WedgedFront = false;
        r.FrontLoad = 1;
        r.CmdQueue = new Queue<(double V, double W)>();
        r.CmdV = 0;
        r.CmdW = 0;
        r.IrHyst = new Dictionary<string, HysteresisState>();
        r.Probe = new Dictionary<string, SensorProbe>();
        r.Sens = new Dictionary<string, double>();
        r.RawSens = new Dictionary<string, double>();
        r.DropPending = false;
        r.WasOn = _field.OnPlatform(x, y);
        r.Fsm = new FsmRuntime
        {
            SimT = r.Fsm.SimT,      // 比赛已进行时间不回退 (事件时间戳保持单调)
            Timer = _matchTimer,    // 剩余比赛时间: 不延长比赛时钟
            Armed = true,
            State = FsmState.MountRing,
            Mount = new MountState(),
        };
        // resetAll tail: refresh sensors once so paused/pre-commit views show
        // real data at the new pose (pure recomputation, no rng draws).
        _sensors.SampleSensorsFor(r);
    }

    private void OnBothDone(RobotRuntime robot, string reason)
    {
        if (_us.Fsm.State == FsmState.Finished && _them.Fsm.State == FsmState.Finished)
        {
            _phase = MatchControlPhase.Finished;
            _paused = false;
            _matchTimer = 0;
        }
    }

    // ---------- scoring helpers ----------

    private void Gain(RobotRuntime r, double pts)
    {
        if (r.IsUs)
        {
            _scoreUs += pts;
        }
        else
        {
            _scoreThem += pts;
        }
    }

    private void OppGain(RobotRuntime r, double pts) => Gain(r.IsUs ? _them : _us, pts);

    private void LogScore(RobotRuntime r, string msg, EventKind kind = EventKind.BlockScore, object? data = null)
        => _events.Emit(kind, r, msg, "score", data);

    // ---------- main tick ----------

    /// <summary>
    /// Advances the match by one fixed tick (scenario <c>tickSeconds</c>).
    /// External actions follow the legacy <c>stepSimExt</c> contract: a provided
    /// action puts that robot into manual mode for the tick; null leaves the
    /// robot to its own FSM. Returns the committed snapshot.
    /// </summary>
    public Snapshot Tick(RobotAction? usAction = null, RobotAction? themAction = null)
    {
        var d = _scenario.Field.TickSeconds;
        StepSimExt(d, usAction, themAction);
        TickIndex++;
        return CommitSnapshot();
    }

    private void StepSimExt(double d, RobotAction? usAction, RobotAction? themAction)
    {
        var acts = new Dictionary<RobotRuntime, RobotAction?>
        {
            [_us] = usAction,
            [_them] = themAction,
        };
        var hasExternalAction = usAction is not null || themAction is not null;

        if (_phase is MatchControlPhase.Paused or MatchControlPhase.Finished)
        {
            return;
        }
        if (_phase == MatchControlPhase.Prep && !hasExternalAction)
        {
            _prepRemaining = Math.Max(0, _prepRemaining - d);
            if (_prepRemaining <= 1e-9)
            {
                _prepRemaining = 0;
                _phase = MatchControlPhase.Ready;
            }
            ObjFallCheckAll();
            return;
        }
        if (_phase == MatchControlPhase.Ready && !hasExternalAction)
        {
            ObjFallCheckAll();
            return;
        }
        if (hasExternalAction && _phase is MatchControlPhase.Prep or MatchControlPhase.Ready)
        {
            _phase = MatchControlPhase.Running;
            _paused = false;
            _prepRemaining = 0;
        }
        SimStepIndex++;
        _events.Tick = TickIndex + 1;
        foreach (var o in _blocks)
        {
            o.ContactThisStep.Clear();
        }
        foreach (var r in new[] { _us, _them })
        {
            if (acts[r] is { } act)
            {
                var st = r.Fsm;
                st.Armed = true;
                st.Manual = true;
                if (st.State != FsmState.Finished)
                {
                    st.State = FsmState.Manual;
                }
            }
        }
        foreach (var r in new[] { _us, _them })
        {
            var st = r.Fsm;
            if (acts[r] is { } act)
            {
                // Non-finite actions never reach the dynamics (zero-action fallback).
                var action = act.IsFinite ? act : RobotAction.Zero;
                st.SimT += d;
                st.Timer -= d;
                if (st.Timer < 1e-9)
                {
                    st.Timer = 0;
                }
                if (st.Timer == 0 && st.State != FsmState.Finished)
                {
                    _fsm.ToDoneFor(r, "比赛时间结束(手动模式)");
                }
                var vehicle = r.Vehicle;
                r.V = Js.Clamp(action.V, -vehicle.MaxSpeed, vehicle.MaxSpeed);
                r.W = Js.Clamp(action.W, -vehicle.MaxTurnRate, vehicle.MaxTurnRate);
                st.Action = $"外部策略 v={Js.ToFixed(r.V, 2)} w={Js.ToFixed(r.W, 2)}";
            }
            else if (st.Armed && st.State != FsmState.Finished)
            {
                ScoringTickFor(r, d);
            }
            _sensors.SampleSensorsFor(r);
        }
        foreach (var r in new[] { _us, _them })
        {
            if (acts[r] is not null)
            {
                continue;
            }
            if (r.Fsm.Armed && r.Fsm.State != FsmState.Finished)
            {
                _fsm.FsmTickFor(r, d);
            }
        }

        _physics.Step(d);
        ObjFallCheckAll();
        UpdateScoreClock(d);
        UpdateInactivity(d);
        _matchTimer = Math.Max(0, Math.Min(_us.Fsm.Timer, _them.Fsm.Timer));
        _us.WasOn = _physics.OnStage(_us);
        _them.WasOn = _physics.OnStage(_them);

        RecordReplayTick(acts);
    }

    private void ScoringTickFor(RobotRuntime r, double dt)
    {
        var st = r.Fsm;
        st.SimT += dt;
        st.Timer -= dt;
        // Float integration may leave a tiny positive residue on the last frame;
        // treat it as expired so headless and API done semantics agree.
        if (st.Timer < 1e-9)
        {
            st.Timer = 0;
        }
        if (st.Timer == 0 && st.State != FsmState.Finished)
        {
            _events.Emit(EventKind.Timeout, r, "[fsm] 比赛时间到 → FINISHED");
            _fsm.ToDoneFor(r, "比赛时间结束");
        }
    }

    // ---------- referee: blocks and drops ----------

    private void ObjFallCheckAll()
    {
        PhysicsWorld.FinalizeBlockContacts(_blocks);
        foreach (var o in _blocks)
        {
            if (o.Out)
            {
                continue; // 已推下台: 本场不再参与(静止)
            }
            var on = _field.OnPlatform(o.X, o.Y);
            if (o.WasOn && !on)
            {
                o.WasOn = false;
                var role = o.LastContactRole;
                var pusher = role == RoleNames.Us ? _us : role == RoleNames.Them ? _them : null;
                if (o.Kind == BlockKind.Buff && pusher is not null)
                {
                    Gain(pusher, 3);
                    LogScore(pusher,
                        $"[score] 增益块被推下擂台! {pusher.Name} +3 ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})",
                        data: new { block = o.Name, points = 3, scorer = pusher.Role, kind = o.Kind.ToString().ToLowerInvariant() });
                    _fsm.HandleBuffScored(pusher);
                }
                else if (o.Kind == BlockKind.Debuff && pusher is not null)
                {
                    OppGain(pusher, 6);
                    LogScore(pusher,
                        $"[score] 失误! 减益块被推下, 对方 +6 ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})",
                        data: new { block = o.Name, points = 6, scorer = (pusher.IsUs ? RoleNames.Them : RoleNames.Us), kind = "debuff" });
                }
                else if (role == "simultaneous")
                {
                    LogScore(_us, $"[score] 双方同时接触{o.Name}并将其推出台外, 按规则不计分",
                        EventKind.BlockOff, new { block = o.Name, reason = "simultaneous" });
                }
                else
                {
                    LogScore(_us, $"[score] {o.Name}离开擂台(无有效最后接触), 不计分",
                        EventKind.BlockOff, new { block = o.Name, reason = "no_valid_contact" });
                }
                o.Out = true;
                o.Vx = 0;
                o.Vy = 0;
            }
            else if (on)
            {
                o.WasOn = true;
            }
        }

        // Robot drops: judge both robots from the same before/after state so a
        // simultaneous drop cannot score for either side.
        var prevUs = _us.WasOn;
        var prevThem = _them.WasOn;
        var nowUs = _physics.OnStage(_us);
        var nowThem = _physics.OnStage(_them);
        var usDrop = prevUs && !nowUs;
        var themDrop = prevThem && !nowThem;
        if (usDrop)
        {
            _us.DropPending = true;
            var (lux, luy) = _field.Transform.WorldToLocalPoint(_us.X, _us.Y);
            _us.Fsm.Rec.FallDir = Math.Atan2(luy - _field.Center, lux - _field.Center);
        }
        if (themDrop)
        {
            _them.DropPending = true;
            var (ltx, lty) = _field.Transform.WorldToLocalPoint(_them.X, _them.Y);
            _them.Fsm.Rec.FallDir = Math.Atan2(lty - _field.Center, ltx - _field.Center);
        }
        if (usDrop && themDrop)
        {
            _events.Emit(EventKind.SimultaneousDrop, _us,
                $"[score] 双方同帧掉台, 按规则双方均不得分 ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})", "score",
                new { us = new { x = _us.X, y = _us.Y }, them = new { x = _them.X, y = _them.Y } }, neutral: true);
        }
        else if (usDrop && prevThem && nowThem && _us.Fsm.Armed && _us.Fsm.State != FsmState.Finished)
        {
            OppGain(_us, 1);
            LogScore(_us, $"[score] 我方掉台, 对方 +1 ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})",
                EventKind.Drop, new { role = RoleNames.Us, points = 1, scorer = RoleNames.Them });
        }
        else if (themDrop && prevUs && nowUs && _them.Fsm.Armed && _them.Fsm.State != FsmState.Finished)
        {
            OppGain(_them, 1);
            LogScore(_them, $"[score] 对手掉台, 我方 +1 ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})",
                EventKind.Drop, new { role = RoleNames.Them, points = 1, scorer = RoleNames.Us });
        }
        else if (usDrop || themDrop)
        {
            LogScore(usDrop ? _us : _them, "[score] 掉台时另一方不在台上, 按规则本次不计分",
                EventKind.Drop, new { reason = "other_not_on_stage" });
        }
        _us.WasOn = nowUs;
        _them.WasOn = nowThem;
    }

    // ---------- referee: score clock and inactivity ----------

    private string ScoreClockPhase()
        => _physics.OnStage(_us) && !_physics.OnStage(_them) ? "us_only"
        : !_physics.OnStage(_us) && _physics.OnStage(_them) ? "them_only"
        : _physics.OnStage(_us) && _physics.OnStage(_them) ? "both_on" : "both_off";

    private void UpdateScoreClock(double d)
    {
        var phase = ScoreClockPhase();
        if (phase != _scorePhase)
        {
            _scorePhase = phase;
            _scorePhaseT = 0;
            return;
        }
        if (phase is "us_only" or "them_only")
        {
            _scorePhaseT += d;
            while (_scorePhaseT >= 10)
            {
                _scorePhaseT -= 10;
                var r = phase == "us_only" ? _us : _them;
                Gain(r, 1);
                LogScore(r, $"[score] 登台/掉台读秒: {r.Name} +1 ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})",
                    EventKind.ScoreClock, new { points = 1, phase });
            }
        }
        else
        {
            _scorePhaseT = 0;
        }
    }

    private void UpdateInactivity(double d)
    {
        foreach (var r in new[] { _us, _them })
        {
            var st = r.Fsm;
            var exempt = !st.Armed
                || st.State is FsmState.WaitStart or FsmState.MountRing or FsmState.Recover or FsmState.Finished
                || !_physics.OnStage(r);
            if (exempt)
            {
                st.InactiveT = 0;
                st.InactiveWarned = false;
                continue;
            }
            var moving = Js.Hypot(r.Vx, r.Vy) > 0.05 || Math.Abs(r.W) > 0.15;
            if (moving)
            {
                st.InactiveT = 0;
                st.InactiveWarned = false;
                continue;
            }
            st.InactiveT += d;
            if (st.InactiveT >= 10 && !st.InactiveWarned)
            {
                st.InactiveWarned = true;
                OppGain(r, 1);
                _events.Emit(EventKind.Inactivity, r,
                    $"[score] 消极比赛超过10秒, 对方 +1 ({Js.Num(_scoreUs)}:{Js.Num(_scoreThem)})", "warn",
                    new { role = r.Role, points = 1 });
            }
        }
    }

    // ---------- observations ----------

    /// <summary>Builds the observation for one robot (the <c>obs</c> of decide(obs)).</summary>
    public Observation BuildObservation(RobotRuntime r)
    {
        _requestIdCounter++;
        var other = r.IsUs ? _them : _us;
        return new Observation
        {
            RequestId = _requestIdCounter,
            Tick = TickIndex,
            T = r.Fsm.SimT,
            Role = r.Role,
            Timer = r.Fsm.Timer,
            Scores = Scores,
            Robot = new RobotView
            {
                X = r.X,
                Y = r.Y,
                Th = r.Th,
                V = r.V,
                W = r.W,
                OnPlatform = _physics.OnStage(r),
                Hang = _physics.HangOn(r),
                State = FsmStateNames.ToWire(r.Fsm.State),
                Action = r.Fsm.Action,
                Vehicle = r.Vehicle,
            },
            Sensors = ToLegacySensors(r.Sens),
            RawSensors = new Dictionary<string, double>(r.RawSens),
            SensorLayout = r.Vehicle.Sensors,
            Perception = BuildPerception(),
            Opponent = new OpponentView
            {
                X = other.X,
                Y = other.Y,
                Th = other.Th,
                OnPlatform = _physics.OnStage(other),
                State = FsmStateNames.ToWire(other.Fsm.State),
            },
            Objects = BuildObjectSet(),
        };
    }

    internal static LegacySensors ToLegacySensors(Dictionary<string, double> sens) => new()
    {
        GrayFront = sens.GetValueOrDefault("gF"),
        GrayRear = sens.GetValueOrDefault("gB"),
        GrayLeft = sens.GetValueOrDefault("gL"),
        GrayRight = sens.GetValueOrDefault("gR"),
        ShovelUnderLeft = sens.GetValueOrDefault("uL"),
        ShovelUnderRight = sens.GetValueOrDefault("uR"),
        ShovelFrontLeft = sens.GetValueOrDefault("sFL"),
        ShovelFrontRight = sens.GetValueOrDefault("sFR"),
        DiagLeftFront = sens.GetValueOrDefault("dLF"),
        DiagRightFront = sens.GetValueOrDefault("dRF"),
        DiagLeftRear = sens.GetValueOrDefault("dLB"),
        DiagRightRear = sens.GetValueOrDefault("dRB"),
        Front = sens.GetValueOrDefault("f"),
        Rear = sens.GetValueOrDefault("r"),
    };

    private Perception BuildPerception() => new()
    {
        FieldGray = _field.GetFieldGrayInfo(),
        Vision = BuildVisionInfo(),
    };

    /// <summary>
    /// Vision metadata: the default classifyRate stub keeps its legacy
    /// "default" shape (bit-compatibility); the injected replay adapter
    /// reports the visionReplay mode plus its consumption registry.
    /// </summary>
    private VisionInfo BuildVisionInfo()
    {
        if (_vision is VisionReplayAdapter replay)
        {
            return new VisionInfo
            {
                Mode = VisionReplayAdapter.ModeName,
                External = replay.BuildExternalSnapshot(),
            };
        }
        return new VisionInfo
        {
            Mode = "default",
            ClassifyRate = _params.ClassifyRate,
        };
    }

    private ObjectSet BuildObjectSet()
    {
        var buffs = new List<EnergyBlockView>();
        EnergyBlockView? debuff = null;
        EnergyBlockView View(BlockRuntime b) => new()
        {
            X = b.X,
            Y = b.Y,
            OnPlatform = _field.OnPlatform(b.X, b.Y),
            Out = b.Out,
            LastTouch = b.LastContactRole,
        };
        foreach (var b in _blocks)
        {
            if (b.Kind == BlockKind.Buff)
            {
                buffs.Add(View(b));
            }
            else
            {
                debuff = View(b);
            }
        }
        return new ObjectSet { Buffs = buffs, Debuff = debuff };
    }

    // ---------- snapshots ----------

    /// <summary>Commits one immutable snapshot: events since the last commit plus the reward delta.</summary>
    public Snapshot CommitSnapshot()
    {
        var newEvents = _events.Events
            .Where(e => e.Seq > _lastCommittedSeq)
            .Select(e => e.ToProtocolEvent())
            .ToList();
        _lastCommittedSeq = _events.Events.Count == 0 ? 0 : _events.Events[^1].Seq;
        var snapshot = BuildSnapshot(newEvents,
            new Scores { Us = _scoreUs - _lastRewardUs, Them = _scoreThem - _lastRewardThem });
        _lastRewardUs = _scoreUs;
        _lastRewardThem = _scoreThem;
        return snapshot;
    }

    /// <summary>Builds a snapshot of the current state without committing event bookkeeping.</summary>
    public Snapshot BuildSnapshot(List<Event>? events = null, Scores? reward = null) => new()
    {
        Tick = TickIndex,
        T = Math.Max(_us.Fsm.SimT, _them.Fsm.SimT),
        Timer = _matchTimer,
        Phase = _phase switch
        {
            MatchControlPhase.Prep or MatchControlPhase.Ready => MatchPhase.Prep,
            MatchControlPhase.Running => MatchPhase.Run,
            MatchControlPhase.Paused => MatchPhase.Run,
            _ => MatchPhase.Done,
        },
        Paused = _paused,
        Done = Done,
        DoneReason = Done ? (_us.Fsm.DoneReason.Length > 0 ? _us.Fsm.DoneReason : _them.Fsm.DoneReason) : null,
        Scores = Scores,
        RestartPenalties = RestartPenalties,
        Robots = new Dictionary<string, RobotState>
        {
            [RoleNames.Us] = ToRobotState(_us),
            [RoleNames.Them] = ToRobotState(_them),
        },
        Sensors = new Dictionary<string, LegacySensors>
        {
            [RoleNames.Us] = ToLegacySensors(_us.Sens),
            [RoleNames.Them] = ToLegacySensors(_them.Sens),
        },
        RawSensors = new Dictionary<string, Dictionary<string, double>>
        {
            [RoleNames.Us] = new Dictionary<string, double>(_us.RawSens),
            [RoleNames.Them] = new Dictionary<string, double>(_them.RawSens),
        },
        SensorLayout = new Dictionary<string, SensorProfile>
        {
            [RoleNames.Us] = _us.Vehicle.Sensors!,
            [RoleNames.Them] = _them.Vehicle.Sensors!,
        },
        Perception = BuildPerception(),
        Objects = BuildObjectSet(),
        Events = events,
        Reward = reward,
    };

    private RobotState ToRobotState(RobotRuntime r) => new()
    {
        X = r.X,
        Y = r.Y,
        Th = r.Th,
        V = r.V,
        W = r.W,
        Vx = r.Vx,
        Vy = r.Vy,
        Speed = Js.Hypot(r.Vx, r.Vy),
        Omega = r.Omega,
        Pitch = r.Pitch,
        Roll = r.Roll,
        ZG = r.ZG,
        IsStalled = r.IsStalled,
        WedgedFront = r.WedgedFront,
        FrontLoad = r.FrontLoad,
        OnPlatform = _physics.OnStage(r),
        Hang = _physics.HangOn(r),
        State = FsmStateNames.ToWire(r.Fsm.State),
        Action = r.Fsm.Action,
        Armed = r.Fsm.Armed,
        Manual = r.Fsm.Manual,
        Timer = r.Fsm.Timer,
        Vehicle = r.Vehicle,
    };

    // ---------- replay recording ----------

    private void RecordCommand(string command) => _pendingCommands.Add(command);

    private void RecordReplayTick(Dictionary<RobotRuntime, RobotAction?> acts)
    {
        var actions = new Dictionary<string, RobotAction>();
        foreach (var (r, act) in acts)
        {
            if (act is { IsFinite: true } action)
            {
                actions[r.IsUs ? RoleNames.Us : RoleNames.Them] = action.ClampTo(r.Vehicle);
            }
        }
        List<string>? commands = null;
        if (_pendingCommands.Count > 0)
        {
            commands = new List<string>(_pendingCommands);
            _pendingCommands.Clear();
        }
        if (actions.Count > 0 || commands is not null)
        {
            _replayTicks.Add(new ReplayTick { Tick = TickIndex + 1, T = _us.Fsm.SimT, Actions = actions, Commands = commands });
        }
    }

    /// <summary>Builds the replay header for the match run so far (accepted actions/commands by tick).</summary>
    public ReplayHeader BuildReplayHeader() => new()
    {
        RulesetId = _scenario.Id,
        Seed = _scenario.Seed,
        CoreVersion = CoreVersion,
        VisionMode = _vision is VisionReplayAdapter ? VisionReplayAdapter.ModeName : "default",
        VisionEvidenceId = _vision is VisionReplayAdapter evidence ? evidence.EvidenceId : null,
        VisionEvidenceSha256 = _vision is VisionReplayAdapter sha ? sha.EvidenceSha256 : null,
        Parameters = _scenario.Parameters is null ? null : new Dictionary<string, double>(_scenario.Parameters),
        Vehicles = new Dictionary<string, VehicleProfile>
        {
            [RoleNames.Us] = _us.Vehicle,
            [RoleNames.Them] = _them.Vehicle,
        },
        FieldGray = new FieldGrayRef
        {
            Id = _field.GetFieldGrayInfo().Id ?? "hand_drawn",
            Mode = _field.GetFieldGrayInfo().Mode,
        },
        Ticks = new List<ReplayTick>(_replayTicks),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Runs the match to completion headlessly: arm both robots and tick until done or max ticks.</summary>
    public List<Snapshot> RunToEnd(int maxTicks = 10_000)
    {
        Arm();
        var snapshots = new List<Snapshot>();
        while (!Done && snapshots.Count < maxTicks)
        {
            snapshots.Add(Tick());
        }
        return snapshots;
    }
}
