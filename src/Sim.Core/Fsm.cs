using Sim.Protocol;

namespace Sim.Core;

/// <summary>Standardized vision detection (legacy SimVision contract).</summary>
public sealed record VisionDetection
{
    public required string Label { get; init; }

    public double Confidence { get; init; }

    public required string Source { get; init; }

    public double? OffsetX { get; init; }
}

/// <summary>Context handed to the vision adapter (no object-kind leakage to external adapters).</summary>
public sealed record VisionContext
{
    public required double T { get; init; }

    public required string Role { get; init; }

    public required RobotRuntime Robot { get; init; }

    /// <summary>Null when no sensor target is in view.</summary>
    public VisionTargetInfo? Target { get; init; }

    public required RobotRuntime Opponent { get; init; }

    /// <summary>Random source — the match rng stream (deterministic replay).</summary>
    public Func<double>? Random { get; init; }
}

/// <summary>Sensor target descriptor passed to the vision adapter.</summary>
public sealed record VisionTargetInfo
{
    public required string Kind { get; init; }

    public required string Name { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double D { get; init; }

    public required string Rel { get; init; }
}

/// <summary>
/// Synchronous vision adapter (legacy SimVision). The default adapter keeps
/// the original one-rng-draw classifyRate semantics; external YOLO bridges
/// run outside the core and only refresh a cache (later dispatch).
/// </summary>
public interface IVisionAdapter
{
    string Id { get; }

    VisionDetection Classify(VisionContext context);
}

/// <summary>Default classifyRate stub — one rng() call per classification, preserving fixed-seed traces.</summary>
public sealed class ClassifyRateVision : IVisionAdapter
{
    private readonly SimParameters _parameters;

    public ClassifyRateVision(SimParameters parameters) => _parameters = parameters;

    public string Id => "classifyRate";

    public VisionDetection Classify(VisionContext context)
    {
        var random = context.Random ?? throw new InvalidOperationException("classifyRate requires the deterministic match rng.");
        var ok = random() * 100 < _parameters.ClassifyRate;
        return ok
            ? new VisionDetection
            {
                Label = Vision.KnownTargetKind(context.Target),
                Confidence = Js.Clamp(_parameters.ClassifyRate / 100, 0, 1),
                Source = "classifyRate",
            }
            : new VisionDetection { Label = "unknown", Confidence = 0, Source = "classifyRate" };
    }
}

/// <summary>Vision helpers (normalization + target kinds).</summary>
public static class Vision
{
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["bonus"] = "buff",
        ["gain"] = "buff",
        ["penalty"] = "debuff",
        ["enemy"] = "opponent",
        ["robot"] = "opponent",
        ["none"] = "unknown",
        ["miss"] = "unknown",
    };

    /// <summary>Legacy targetKindForVision: buff/debuff/opponent.</summary>
    public static string KnownTargetKind(VisionTargetInfo? target) => target?.Kind switch
    {
        "buff" => "buff",
        "debuff" => "debuff",
        _ => "opponent",
    };

    /// <summary>normalizeVisionDetection: alias mapping + label whitelist + confidence clamping.</summary>
    public static VisionDetection Normalize(VisionDetection result, string fallbackSource)
    {
        var label = (result.Label ?? "unknown").ToLowerInvariant();
        if (Aliases.TryGetValue(label, out var aliased))
        {
            label = aliased;
        }
        if (label is not ("buff" or "debuff" or "opponent" or "unknown"))
        {
            label = "unknown";
        }
        var confidence = double.IsFinite(result.Confidence) ? Js.Clamp(result.Confidence, 0, 1) : (label == "unknown" ? 0 : 1);
        var source = (result.Source is { Length: > 0 } ? result.Source : fallbackSource);
        return new VisionDetection
        {
            Label = label,
            Confidence = confidence,
            Source = source.Length > 80 ? source[..80] : source,
            OffsetX = result.OffsetX is { } offset ? Js.Clamp(offset, -1, 1) : null,
        };
    }
}

/// <summary>
/// The built-in dual-use FSM (US/THEM share it; vehicle profiles differ),
/// ported verbatim from the legacy CORE. Emits structured events through the
/// shared bus while preserving the legacy log lines.
/// </summary>
public sealed class FsmController
{
    private readonly FieldModel _field;
    private readonly PhysicsWorld _physics;
    private readonly SimParameters _params;
    private readonly Func<double> _rng;
    private readonly List<BlockRuntime> _blocks;
    private readonly EventBus _events;
    private readonly IVisionAdapter _vision;
    private readonly Action<RobotRuntime, string> _onBothDone;
    private readonly RobotRuntime _us;
    private readonly RobotRuntime _them;

    public FsmController(FieldModel field, PhysicsWorld physics, SimParameters parameters, Func<double> rng,
        RobotRuntime us, RobotRuntime them, List<BlockRuntime> blocks, EventBus events,
        IVisionAdapter vision, Action<RobotRuntime, string> onBothDone)
    {
        _field = field;
        _physics = physics;
        _params = parameters;
        _rng = rng;
        _us = us;
        _them = them;
        _blocks = blocks;
        _events = events;
        _vision = vision;
        _onBothDone = onBothDone;
    }

    private RobotRuntime Other(RobotRuntime r) => r.IsUs ? _them : _us;

    /// <summary>
    /// Legacy hook called by the referee when a buff block is scored: a robot
    /// still in SCORE_BLOCK gives up its target and returns to SEARCH.
    /// </summary>
    public void HandleBuffScored(RobotRuntime pusher)
    {
        if (pusher.Fsm.State == FsmState.ScoreBlock)
        {
            pusher.V = 0;
            pusher.W = 0;
            EnterSearchFor(pusher);
        }
    }

    private static void SetAct(RobotRuntime r, string a) => r.Fsm.Action = a;

    private void Log(RobotRuntime r, string msg, string? cls = null, EventKind kind = EventKind.Fsm, object? data = null)
        => _events.Emit(kind, r, msg, cls, data);

    // ---------- FSM helpers ----------

    private void EnterSearchFor(RobotRuntime r)
    {
        var st = r.Fsm;
        st.State = FsmState.Search;
        st.Rec.Count = 0;
        st.Scan = new ScanState { Dir = _rng() < 0.5 ? 1 : -1 };
        st.ScoreTarget = null;
        st.ScoreProgressT = 0;
        r.V = 0;
        r.W = 0;
        Log(r, "[fsm] SEARCH: 旋转扫描中…");
    }

    private void EnterRecoverFor(RobotRuntime r, string phase)
    {
        var st = r.Fsm;
        st.Rec.Count++;
        if (st.Rec.Count > _params.RecoverLimit)
        {
            ToDoneFor(r, "恢复次数超限 → 停车");
            return;
        }
        st.State = FsmState.Recover;
        st.Rec.Phase = phase;
        st.Rec.T = 0;
        Log(r, $"[fsm] RECOVER: 进入恢复(第{st.Rec.Count}次, 上限{Js.Num(_params.RecoverLimit)})");
    }

    /// <summary>Legacy toDoneFor: stop the robot, log FINISHED and finish the match when both robots are done.</summary>
    public void ToDoneFor(RobotRuntime r, string reason)
    {
        var st = r.Fsm;
        st.State = FsmState.Finished;
        r.V = 0;
        r.W = 0;
        SetAct(r, "停车, 比赛结束");
        st.DoneReason = reason;
        Log(r, $"[fsm] FINISHED: {reason}", kind: EventKind.End, data: new { reason });
        _onBothDone(r, reason);
    }

    private static bool RotateTo(RobotRuntime r, double target, double wmax, double tol = 0.1)
    {
        var e = Js.Norm(target - r.Th);
        r.W = Js.Clamp(e, -wmax, wmax);
        return Math.Abs(e) < (tol != 0 ? tol : 0.1);
    }

    private static void DriveToward(RobotRuntime r, (double X, double Y) p, double v, double wmax)
    {
        var e = Js.Norm(Math.Atan2(p.Y - r.Y, p.X - r.X) - r.Th);
        r.W = Js.Clamp(e * 3.5, -wmax, wmax);
        r.V = v;
    }

    private static double AngleTo(RobotRuntime r, (double X, double Y) p) => Math.Atan2(p.Y - r.Y, p.X - r.X);

    private static string DirName(double a)
    {
        var d = Js.Norm(a - Math.PI / 2);
        if (Math.Abs(d) < 0.4)
        {
            return "南";
        }
        if (Math.Abs(Js.Norm(a)) < 0.4)
        {
            return "东";
        }
        if (Math.Abs(d - Math.PI) < 0.4 || Math.Abs(d + Math.PI) < 0.4)
        {
            return "北";
        }
        return "西";
    }

    /// <summary>
    /// 登台对准方向: 垂直指向最近台壁段; 台角走廊/台上 → null。
    /// Computed in field-local coordinates (axis-aligned platform) and
    /// returned as a world heading.
    /// </summary>
    private double? MountAlignAngle(RobotRuntime r)
    {
        var (x, y) = _field.Transform.WorldToLocalPoint(r.X, r.Y);
        var inX = x >= _field.El && x <= _field.Er;
        var inY = y >= _field.El && y <= _field.Er;
        double? local = null;
        if (inX && y < _field.El)
        {
            local = Math.PI / 2;   // 南边: 车尾朝 +y
        }
        else if (inX && y > _field.Er)
        {
            local = -Math.PI / 2;  // 北边: 车尾朝 -y
        }
        else if (inY && x < _field.El)
        {
            local = 0;             // 西边: 车尾朝 +x
        }
        else if (inY && x > _field.Er)
        {
            local = Math.PI;       // 东边: 车尾朝 -x
        }
        return local is null ? null : _field.Transform.LocalToWorldHeading(local.Value);
    }

    private double MountSpeed() => _params.MountSpeed / 1000 * 0.75;

    // ---------- crisis gate ----------

    private void CrisisGateFor(RobotRuntime r)
    {
        var st = r.Fsm;
        if (!st.Armed || st.State is FsmState.MountRing or FsmState.Recover or FsmState.Finished)
        {
            return;
        }
        var sens = r.Sens;
        // 部分 footprint 已悬出时立即进入危机门控, 不等整车中心掉下台。
        var hang = _physics.HangOn(r) && sens.GetValueOrDefault("f") < 0.25;
        if (hang && Math.Abs(r.V) > 0.05)
        {
            r.V = 0;
            r.W = 0;
            Log(r, "[fsm] 危机门控: 前红外悬空(safety 危机) → 急刹 → RECOVER", "warn", EventKind.Recover);
            EnterRecoverFor(r, "backup");
            return;
        }
        if (r.DropPending || (r.WasOn && !_physics.OnStage(r)))
        {
            r.DropPending = false;
            var (lcx, lcy) = _field.Transform.WorldToLocalPoint(r.X, r.Y);
            var direction = DirName(Math.Atan2(lcy - _field.Center, lcx - _field.Center));
            Log(r, $"[fsm] 掉台! (方向 {direction}) → RECOVER", "warn", EventKind.Drop,
                new { direction, fallDir = Math.Atan2(lcy - _field.Center, lcx - _field.Center) });
            EnterRecoverFor(r, "spin");
        }
    }

    // ---------- mount engine ----------

    private void MountTick(RobotRuntime r, double dt, string mode)
    {
        var m = r.Fsm.Mount;
        m.T += dt;
        if (_physics.FullOn(r))
        {
            Log(r, "[fsm] 已上台 on_stage ✓ → SEARCH", kind: EventKind.Mount, data: new { via = "on_stage" });
            EnterSearchFor(r);
            return;
        }
        switch (m.Phase)
        {
            case "posture":
                SetAct(r, "姿态确认");
                m.Phase = "align";
                m.T = 0;
                Log(r, "[fsm] MOUNT_RING: 姿态确认 (不在台上) → 摆正");
                break;

            case "align":
            {
                SetAct(r, "摆正(后向对准擂台)");
                // 2026-08-14 规则: 屁股正对边缘垂直登台。
                var mAng = MountAlignAngle(r);
                var target = mAng is null
                    ? AngleTo(r, _field.NearestPlatPoint(r.X, r.Y)) + Math.PI
                    : mAng.Value + Math.PI;
                if (RotateTo(r, target, 1.6, 0.12))
                {
                    m.Phase = "reverse";
                    m.T = 0;
                    m.Climbed = false;
                    Log(r, $"[fsm] MOUNT_RING: 摆正完成 → 倒车登台({Js.Num(_params.MountSpeed)}/800)");
                }
                if (m.T > 5)
                {
                    m.Phase = "rush";
                    m.T = 0;
                    m.RushSeen = false;
                    Log(r, "[fsm] MOUNT_RING: 摆正超时 → 前冲找墙");
                }
                break;
            }

            case "reverse":
                SetAct(r, $"倒车冲台 {Js.Num(_params.MountSpeed)}/800");
                r.V = -MountSpeed();
                if (r.Sens.GetValueOrDefault("gB") > _params.FallThreshold && !m.Climbed)
                {
                    m.Climbed = true;
                    Log(r, $"[fsm] 登台信号: 后向灰度 {Js.ToFixed(r.Sens.GetValueOrDefault("gB"), 0)}>{Js.Num(_params.FallThreshold)} → climbed",
                        kind: EventKind.Mount, data: new { via = "climbed" });
                }
                if (m.T > 7)
                {
                    m.Phase = "rush";
                    m.T = 0;
                    m.RushSeen = false;
                    Log(r, "[fsm] MOUNT_RING: 倒车超时失败 → 前冲找墙");
                }
                else if (!m.Climbed && r.Sens.GetValueOrDefault("r") > 0.55 && m.T > 0.5)
                {
                    m.Phase = "rush";
                    m.T = 0;
                    m.RushSeen = false;
                    Log(r, "[fsm] MOUNT_RING: 后向受阻(倒车登台失败) → 前冲找墙");
                }
                break;

            case "rush":
            {
                SetAct(r, "前冲找墙");
                r.V = 1.0;
                if (r.Sens.GetValueOrDefault("sFL") > _params.IrTrigger || r.Sens.GetValueOrDefault("sFR") > _params.IrTrigger)
                {
                    m.RushSeen = true;
                }
                var front = (r.X + Math.Cos(r.Th) * 0.1, r.Y + Math.Sin(r.Th) * 0.1);
                if (m.RushSeen
                    && r.Sens.GetValueOrDefault("sFL") < _params.IrTrigger
                    && r.Sens.GetValueOrDefault("sFR") < _params.IrTrigger
                    && _field.OnPlatform(front.Item1, front.Item2))
                {
                    m.Phase = "fwdMount";
                    m.T = 0;
                    Log(r, "[fsm] 触发丢失: 铲前红外丢信号 = climbed → 正向登台");
                }
                if (m.T > 3.5)
                {
                    m.Phase = "backoff";
                    m.T = 0;
                    m.BackoffT = 0;
                    Log(r, "[fsm] MOUNT_RING: 冲满时限(围栏) → 倒车");
                }
                break;
            }

            case "fwdMount":
                SetAct(r, "正向登台");
                r.V = 0.9;
                if (m.T > 3)
                {
                    m.Phase = "backoff";
                    m.T = 0;
                    m.BackoffT = 0;
                    Log(r, "[fsm] MOUNT_RING: 正向登台受阻 → 倒车");
                }
                break;

            case "backoff":
                SetAct(r, "倒车避让");
                m.BackoffT += dt;
                if (m.BackoffT < 0.6)
                {
                    r.V = -0.6;
                }
                else
                {
                    r.V = 0;
                    RotateTo(r, r.Th + (m.Faces % 2 != 0 ? -1 : 1) * Math.PI / 2, 1.6, 0.1);
                    if (m.BackoffT > 1.7)
                    {
                        m.Faces++;
                        m.T = 0;
                        if (m.Faces >= 4)
                        {
                            if (mode == "ring")
                            {
                                m.Phase = "fwdAlt";
                                m.FwdAltAligned = false;
                                m.T = 0;
                                Log(r, "[fsm] MOUNT_RING: 换面重试完毕 → 正冲备选");
                            }
                            else
                            {
                                r.Fsm.Rec.Phase = "edgeback";
                                r.Fsm.Rec.T = 0;
                                Log(r, "[fsm] RECOVER: 姿态登台失败 → 贴边回中");
                            }
                        }
                        else
                        {
                            m.Phase = "posture";
                            m.T = 0;
                            Log(r, $"[fsm] MOUNT_RING: 换面重试 (第 {m.Faces} 面)");
                        }
                    }
                }
                break;

            case "fwdAlt":
                SetAct(r, "正冲备选(直冲擂台)");
                if (!m.FwdAltAligned)
                {
                    // 2026-08-14: 正冲也垂直对准台壁段(斜冲会被挡)。
                    var mAng = MountAlignAngle(r);
                    var tgt = mAng is null ? AngleTo(r, _field.CenterWorld) : mAng.Value;
                    if (RotateTo(r, tgt, 1.7, 0.12))
                    {
                        m.FwdAltAligned = true;
                        m.T = 0;
                    }
                    if (m.T > 4)
                    {
                        m.FwdAltAligned = false;
                        m.T = 0;
                    }
                }
                else
                {
                    r.V = 1.0;
                    if (m.T > 8)
                    {
                        Log(r, "[fsm] MOUNT_RING: 正冲备选失败, 放弃登台", "warn");
                        ToDoneFor(r, "登台失败");
                    }
                }
                break;
        }
    }

    // ---------- recover ----------

    private void RecoverTick(RobotRuntime r, double dt)
    {
        var rc = r.Fsm.Rec;
        rc.T += dt;
        switch (rc.Phase)
        {
            case "backup":
                SetAct(r, "危机恢复: 屁股朝擂台倒车回台");
                RotateTo(r, AngleTo(r, _field.CenterWorld) + Math.PI, 1.6, 0.15);
                r.V = -0.6;
                if (r.Sens.GetValueOrDefault("f") > 0.25 && !_physics.HangOn(r))
                {
                    Log(r, "[fsm] RECOVER: 倒车回台成功 → SEARCH", kind: EventKind.Recover);
                    EnterSearchFor(r);
                }
                else if (rc.T > 4)
                {
                    rc.T = 0;
                    Log(r, "[fsm] RECOVER: 回台超时 → 重新摆位");
                }
                // 只有车中心完全离开平台才切回 spin。
                if (!_field.OnPlatform(r.X, r.Y))
                {
                    rc.Phase = "spin";
                    rc.T = 0;
                    Log(r, "[fsm] RECOVER: 已离台 → 转为掉台恢复流程");
                }
                break;

            case "spin":
                SetAct(r, "掉台恢复: 屁股朝擂台");
                if (RotateTo(r, AngleTo(r, _field.CenterWorld) + Math.PI, 1.7, 0.21))
                {
                    rc.Phase = "mount";
                    rc.T = 0;
                    r.Fsm.Mount = new MountState();
                    Log(r, "[fsm] RECOVER: 屁股已对准擂台 → 姿态登台");
                }
                if (rc.T > 5)
                {
                    rc.Phase = "edgeback";
                    rc.T = 0;
                    Log(r, "[fsm] RECOVER: 摆位超时 → 贴边回中");
                }
                break;

            case "mount":
                SetAct(r, "姿态登台");
                MountTick(r, dt, "recover");
                break;

            case "edgeback":
            {
                SetAct(r, "贴边回中");
                // FallDir is a field-local direction; build the target in local
                // space, clamp to the platform, then map to world.
                var tx = _field.Center + Math.Cos(rc.FallDir) * _field.Half * 0.9;
                var ty = _field.Center + Math.Sin(rc.FallDir) * _field.Half * 0.9;
                var (tlx, tly) = _field.NearestPlatPointLocal(tx, ty);
                var (lx, ly) = _field.Transform.LocalToWorldPoint(tlx, tly);
                var tgt = (X: lx, Y: ly);
                DriveToward(r, tgt, 0.7, 1.2);
                var dist = Js.Hypot(tgt.Item1 - r.X, tgt.Item2 - r.Y);
                if (dist < 0.2 || rc.T > 2.5)
                {
                    rc.Count++;
                    Log(r, $"[fsm] RECOVER: 贴边回中完成(第{rc.Count}次) → 重新摆位");
                    if (rc.Count > _params.RecoverLimit)
                    {
                        ToDoneFor(r, "恢复次数超限 → 停车");
                        return;
                    }
                    rc.Phase = "spin";
                    rc.T = 0;
                }
                break;
            }
        }
    }

    // ---------- search ----------

    private TargetInfo? FindTargetFor(RobotRuntime r)
    {
        TargetInfo? best = null;
        var list = new (string Key, string Rel)[]
        {
            ("dLF", "左前"), ("dRF", "右前"), ("dLB", "左后"), ("dRB", "右后"), ("f", "正前"),
        };
        foreach (var (k, rel) in list)
        {
            if (!r.Probe.TryGetValue(k, out var pr) || pr is null || pr.Obj is null)
            {
                continue;
            }
            if (r.Sens.GetValueOrDefault(k) < _params.IrTrigger)
            {
                continue;
            }
            double ox, oy;
            if (pr.Obj is BlockRuntime b)
            {
                ox = b.X; oy = b.Y;
            }
            else if (pr.Obj is RobotRuntime rb)
            {
                ox = rb.X; oy = rb.Y;
            }
            else
            {
                continue; // 台壁/地面 tag: not an on-stage target
            }
            if (!_field.OnPlatform(ox, oy))
            {
                continue; // 只追台上的目标
            }
            if (best is null || pr.D < best.D)
            {
                best = new TargetInfo { Obj = pr.Obj, D = pr.D, Rel = rel };
            }
        }
        return best;
    }

    private VisionDetection ClassifyTargetFor(RobotRuntime r, TargetInfo target)
    {
        var obj = target.Obj;
        var targetInfo = new VisionTargetInfo
        {
            Kind = obj switch
            {
                BlockRuntime b => b.Kind == BlockKind.Buff ? "buff" : "debuff",
                RobotRuntime => "opponent",
                _ => "opponent",
            },
            Name = obj switch
            {
                BlockRuntime b => b.Name,
                RobotRuntime rb => rb.Name,
                _ => "",
            },
            X = obj switch { BlockRuntime b => b.X, RobotRuntime rb => rb.X, _ => 0 },
            Y = obj switch { BlockRuntime b => b.Y, RobotRuntime rb => rb.Y, _ => 0 },
            D = target.D,
            Rel = target.Rel,
        };
        var context = new VisionContext
        {
            T = r.Fsm.SimT,
            Role = r.Role,
            Robot = r,
            Target = targetInfo,
            Opponent = Other(r),
            Random = _rng,
        };
        return Vision.Normalize(_vision.Classify(context), _vision.Id);
    }

    private void SearchTick(RobotRuntime r, double dt)
    {
        var sc = r.Fsm.Scan;
        switch (sc.Phase)
        {
            case "scan":
            {
                // 扫描避边: 在台上、车头朝外且前灰度压到黑带 → 倒车回台再扫描。
                var front = (r.X + Math.Cos(r.Th) * 0.35, r.Y + Math.Sin(r.Th) * 0.35);
                if (_physics.OnStage(r)
                    && r.Sens.GetValueOrDefault("gF") < _params.EdgeThreshold
                    && !_field.OnPlatform(front.Item1, front.Item2))
                {
                    SetAct(r, "扫描避边(压到黑带, 倒车回台)");
                    r.V = -0.5;
                    r.W = 0;
                    sc.T += dt;
                    if (sc.T > 0.6)
                    {
                        sc.T = 0;
                    }
                    break;
                }
                SetAct(r, "旋转扫描");
                r.W = sc.Dir * 2.0;
                r.V = 0;
                sc.T += dt;
                if (sc.T > 0.2)
                {
                    var t = FindTargetFor(r);
                    if (t is not null)
                    {
                        sc.Target = t;
                        sc.Phase = "turn";
                        sc.T = 0;
                        var name = TargetName(t.Obj);
                        Log(r, $"[fsm] SEARCH: 对角红外发现目标[{name}] ({Js.ToFixed(t.D, 1)}m, {t.Rel})");
                    }
                    else
                    {
                        sc.T = 0;
                    }
                }
                break;
            }

            case "turn":
            {
                var target = sc.Target!;
                SetAct(r, $"转向目标[{TargetName(target.Obj)}]");
                var pos = TargetPos(target.Obj);
                DriveToward(r, pos, 0, 2.0);
                sc.T += dt;
                if (Math.Abs(Js.Norm(AngleTo(r, pos) - r.Th)) < 0.15)
                {
                    sc.Phase = "classify";
                    sc.T = 0;
                    Log(r, "[fsm] SEARCH: 已对准 → 视觉识别中…");
                }
                if (sc.T > 3 || !_field.OnPlatform(pos.X, pos.Y))
                {
                    Log(r, "[fsm] SEARCH: 目标丢失 → 继续扫描");
                    sc.Phase = "scan";
                    sc.Target = null;
                }
                break;
            }

            case "classify":
            {
                SetAct(r, "视觉识别中…");
                sc.T += dt;
                if (sc.T > 0.5)
                {
                    var t = sc.Target!;
                    var detection = ClassifyTargetFor(r, t);
                    if (detection.Label == "buff")
                    {
                        // 真实回放证据可能把红外目标识别成 buff(模型输出与模拟器
                        // 世界真值允许不一致): 目标不是增益块时不伪造追踪对象,
                        // ScoreTarget 留空由 ScoreTick 兜底选取台上增益块。
                        r.Fsm.ScoreTarget = t.Obj as BlockRuntime;
                        if (r.Fsm.ScoreTarget is { } scoreTarget)
                        {
                            r.Fsm.ScoreLastX = scoreTarget.X;
                            r.Fsm.ScoreLastY = scoreTarget.Y;
                        }
                        r.Fsm.ScoreProgressT = 0;
                        Log(r, "[fsm] 视觉分类 → 增益块 → SCORE_BLOCK");
                        r.Fsm.State = FsmState.ScoreBlock;
                        r.V = 0;
                        r.W = 0;
                    }
                    else if (detection.Label == "debuff")
                    {
                        Log(r, "[fsm] 视觉分类 → 减益块 → 绕行规避");
                        sc.Phase = "evade";
                        sc.T = 0;
                        sc.Side = _rng() < 0.5 ? 1 : -1;
                    }
                    else if (detection.Label == "opponent")
                    {
                        Log(r, "[fsm] 视觉分类 → 对手 → ATTACK");
                        r.Fsm.State = FsmState.Attack;
                        r.V = 0;
                        r.W = 0;
                    }
                    else
                    {
                        if (r.Sens.GetValueOrDefault("f") > _params.IrTrigger)
                        {
                            Log(r, "[fsm] 识别不到 + 前向持续 → 判定为对手 → ATTACK");
                            r.Fsm.State = FsmState.Attack;
                            r.V = 0;
                            r.W = 0;
                        }
                        else
                        {
                            Log(r, "[fsm] 识别不到, 前向无持续 → 放弃, 继续扫描");
                            sc.Phase = "scan";
                            sc.Target = null;
                        }
                    }
                }
                break;
            }

            case "evade":
            {
                SetAct(r, "绕行规避(减益块)");
                sc.T += dt;
                var d = _blocks.FirstOrDefault(b => b.Kind == BlockKind.Debuff);
                if (d is null || !_field.OnPlatform(d.X, d.Y))
                {
                    Log(r, "[fsm] 绕行: 减益块消失 → 继续扫描");
                    sc.Phase = "scan";
                    break;
                }
                var pvx = -(d.Y - r.Y);
                var pvy = d.X - r.X;
                var n = Js.Hypot(pvx, pvy);
                if (n == 0)
                {
                    n = 1;
                }
                var gx = d.X + pvx / n * 0.7 * sc.Side;
                var gy = d.Y + pvy / n * 0.7 * sc.Side;
                DriveToward(r, (gx, gy), 0.9, 2.0);
                if (Js.Hypot(gx - r.X, gy - r.Y) < 0.25 || sc.T > 4)
                {
                    Log(r, "[fsm] 绕行完成 → 继续扫描");
                    sc.Phase = "scan";
                    sc.Target = null;
                }
                break;
            }
        }
    }

    private static string TargetName(object obj) => obj switch
    {
        BlockRuntime b => b.Name,
        RobotRuntime rb => rb.Name,
        _ => "",
    };

    private static (double X, double Y) TargetPos(object obj) => obj switch
    {
        BlockRuntime b => (b.X, b.Y),
        RobotRuntime rb => (rb.X, rb.Y),
        _ => (0, 0),
    };

    // ---------- attack / score ----------

    private void AttackTick(RobotRuntime r, double dt)
    {
        var o = Other(r);
        if (!_physics.OnStage(o))
        {
            Log(r, "[fsm] ATTACK: 目标失效(不在台上) → SEARCH");
            EnterSearchFor(r);
            return;
        }
        var d = Js.Hypot(o.X - r.X, o.Y - r.Y);
        SetAct(r, d < 0.42 ? "全速推进(推对手, 逼近边缘)" : "全速推进(追击对手)");
        DriveToward(r, (o.X, o.Y), 1.25, 2.5);
    }

    private void ScoreTick(RobotRuntime r, double dt)
    {
        var st = r.Fsm;
        var otherR = Other(r);
        var b = st.ScoreTarget;
        if (b is null || b.Kind != BlockKind.Buff || b.Out || !_field.OnPlatform(b.X, b.Y))
        {
            b = _blocks.FirstOrDefault(x => x.Kind == BlockKind.Buff && !x.Out && _field.OnPlatform(x.X, x.Y));
            st.ScoreTarget = b;
            if (b is null)
            {
                Log(r, "[fsm] SCORE_BLOCK: 增益块丢失 → SEARCH");
                EnterSearchFor(r);
                return;
            }
            st.ScoreLastX = b.X;
            st.ScoreLastY = b.Y;
            st.ScoreProgressT = 0;
        }
        // 另一台车已贴近同一块且自身并未有效推动时, 让出目标。
        var otherTarget = otherR.Fsm.ScoreTarget;
        if (otherTarget == b && Js.Hypot(otherR.X - b!.X, otherR.Y - b.Y) < 0.48 && Js.Hypot(r.X - b.X, r.Y - b.Y) > 0.32)
        {
            var alt = _blocks.FirstOrDefault(x => !ReferenceEquals(x, b) && x.Kind == BlockKind.Buff && !x.Out && _field.OnPlatform(x.X, x.Y));
            if (alt is not null)
            {
                st.ScoreTarget = alt;
                b = alt;
                st.ScoreLastX = b.X;
                st.ScoreLastY = b.Y;
                st.ScoreProgressT = 0;
                Log(r, "[fsm] SCORE_BLOCK: 目标被占用 → 切换另一增益块");
            }
        }
        var moved = Js.Hypot(b!.X - st.ScoreLastX, b.Y - st.ScoreLastY);
        if (moved > 0.012)
        {
            st.ScoreLastX = b.X;
            st.ScoreLastY = b.Y;
            st.ScoreProgressT = 0;
        }
        else
        {
            st.ScoreProgressT += dt;
        }
        if (st.ScoreProgressT > 2.0)
        {
            var alt = _blocks.FirstOrDefault(x => !ReferenceEquals(x, b) && x.Kind == BlockKind.Buff && !x.Out && _field.OnPlatform(x.X, x.Y));
            if (alt is not null)
            {
                st.ScoreTarget = alt;
                st.ScoreLastX = alt.X;
                st.ScoreLastY = alt.Y;
                st.ScoreProgressT = 0;
                Log(r, "[fsm] SCORE_BLOCK: 目标无进展超时 → 切换目标");
                return;
            }
            Log(r, "[fsm] SCORE_BLOCK: 目标无进展超时 → SEARCH");
            EnterSearchFor(r);
            return;
        }
        if (_field.DistToNearestEdge(b.X, b.Y) < 0.45)
        {
            SetAct(r, "推增益块接近边缘, 减速");
            DriveToward(r, (b.X, b.Y), 0.35, 2.0);
        }
        else
        {
            SetAct(r, "推增益块");
            DriveToward(r, (b.X, b.Y), 0.9, 2.0);
        }
    }

    // ---------- main dispatch ----------

    /// <summary>One FSM tick for one robot (crisis gate first, exactly like the legacy fsmTickFor).</summary>
    public void FsmTickFor(RobotRuntime r, double dt)
    {
        CrisisGateFor(r);
        var st = r.Fsm;
        if (st.State == FsmState.Recover)
        {
            RecoverTick(r, dt);
            return;
        }
        switch (st.State)
        {
            case FsmState.WaitStart:
                SetAct(r, "等待发令");
                r.V = 0;
                r.W = 0;
                break;
            case FsmState.MountRing:
                MountTick(r, dt, "ring");
                break;
            case FsmState.Search:
                SearchTick(r, dt);
                break;
            case FsmState.Attack:
                AttackTick(r, dt);
                break;
            case FsmState.ScoreBlock:
                ScoreTick(r, dt);
                break;
            case FsmState.Manual:
                // 外部策略超时/退出 → 显式停车, 不沿用上一帧指令。
                SetAct(r, "外部策略未提供动作 → 停车");
                r.V = 0;
                r.W = 0;
                break;
            case FsmState.Finished:
                SetAct(r, "停车, 比赛结束");
                r.V = 0;
                r.W = 0;
                break;
        }
    }
}
