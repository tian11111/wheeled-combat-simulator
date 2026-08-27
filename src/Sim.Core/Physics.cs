using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// Deterministic 2D motion and contact resolution, ported from the legacy
/// CORE (motionFor / stageWall / swept contacts / robot pair / block chain).
/// This model is authoritative for scores, replay and observations.
/// </summary>
public sealed class PhysicsWorld
{
    private const double MountVMin = 0.3;    // 登台最小法向速度 (m/s)
    private const double MountAngle = 0.26;  // 登台最大入射角 (rad≈15°)
    private const double BodyRadius = 0.16;  // legacy BODY fallback
    private const double FenceMargin = 0.12; // robot/block fence margin (m)

    private readonly FieldModel _field;
    private readonly SimParameters _params;
    private readonly RobotRuntime _us;
    private readonly RobotRuntime _them;
    private readonly List<BlockRuntime> _blocks;
    private readonly EventBus _events;

    public PhysicsWorld(FieldModel field, SimParameters parameters, RobotRuntime us, RobotRuntime them,
        List<BlockRuntime> blocks, EventBus events)
    {
        _field = field;
        _params = parameters;
        _us = us;
        _them = them;
        _blocks = blocks;
        _events = events;
    }

    // ---------- geometry ----------

    private static (double X, double Y) FootprintPoint(RobotRuntime r, double forward, double lateral)
    {
        var c = Math.Cos(r.Th);
        var s = Math.Sin(r.Th);
        return (r.X + forward * c - lateral * s, r.Y + forward * s + lateral * c);
    }

    private (double X, double Y)[] FootprintCorners(RobotRuntime r)
    {
        var v = r.Vehicle;
        var front = v.FrontExtent;
        var rear = v.RearExtent;
        var side = v.SideExtent;
        return
        [
            FootprintPoint(r, front, side),
            FootprintPoint(r, front, -side),
            FootprintPoint(r, -rear, side),
            FootprintPoint(r, -rear, -side),
        ];
    }

    private bool FootprintOnPlatform(RobotRuntime r)
        => FootprintCorners(r).All(p => _field.OnPlatform(p.X, p.Y));

    private bool FrontFootprintOnPlatform(RobotRuntime r)
    {
        var v = r.Vehicle;
        var a = FootprintPoint(r, v.FrontExtent, v.SideExtent);
        var b = FootprintPoint(r, v.FrontExtent, -v.SideExtent);
        return _field.OnPlatform(a.X, a.Y) && _field.OnPlatform(b.X, b.Y);
    }

    /// <summary>整车 footprint 均由台面支撑才算在台上 (掉台/读秒同一几何语义)。</summary>
    public bool OnStage(RobotRuntime r) => FootprintOnPlatform(r);

    /// <summary>车中心在台上但前端 footprint 悬出 → 悬挂在台沿。</summary>
    public bool HangOn(RobotRuntime r)
        => _field.OnPlatform(r.X, r.Y) && !FrontFootprintOnPlatform(r);

    public bool FullOn(RobotRuntime r) => FootprintOnPlatform(r);

    /// <summary>Footprint support extent along a direction (stage wall contact), given the pose heading.</summary>
    private static double FootprintSupport(RobotRuntime r, double th, double nx, double ny)
    {
        var v = r.Vehicle;
        var c = Math.Cos(th);
        var s = Math.Sin(th);
        var along = c * nx + s * ny;
        var lateral = -s * nx + c * ny;
        var longitudinal = along >= 0 ? v.FrontExtent : v.RearExtent;
        return Math.Abs(along) * longitudinal + Math.Abs(lateral) * v.SideExtent;
    }

    // ---------- stage wall (6 cm step) ----------

    /// <summary>
    /// 台壁阻挡: 台下→台上需要"垂直对准台沿 + 法向速度足够"才放行; 斜撞滑行、
    /// 斜穿台角、低速顶台均被阻挡; 台上→台下自由掉落。
    /// The solver runs in field-local coordinates (axis-aligned platform) and
    /// maps the corrected pose/velocity back to world; the identity layout is
    /// a bit-for-bit pass-through.
    /// </summary>
    private void StageWall(RobotRuntime r, double px, double py)
    {
        var t = _field.Transform;
        var (lpx, lpy) = t.WorldToLocalPoint(px, py);
        if (_field.OnPlatformLocal(lpx, lpy))
        {
            return; // 台上→台下允许自由掉落
        }
        var (lx, ly) = t.WorldToLocalPoint(r.X, r.Y);
        var lth = t.WorldToLocalHeading(r.Th);
        var (lvx, lvy) = t.WorldToLocalVector(r.Vx, r.Vy);
        var el = _field.El;
        var er = _field.Er;
        var walls = new (double Nx, double Ny, bool AxisX, double Boundary)[]
        {
            (0, 1, false, el),   // 南边, 向北入台
            (0, -1, false, er),  // 北边, 向南入台
            (1, 0, true, el),    // 西边, 向东入台
            (-1, 0, true, er),   // 东边, 向西入台
        };
        // 登台判定使用已经积分后的实际速度, 而不是尚未执行的控制指令。
        var desiredVx = lvx;
        var desiredVy = lvy;
        var cmdV = r.CmdV;
        var contacts = new List<(int Wall, double Support, double In1, double In0, double Vn, double Vt)>();
        for (var i = 0; i < walls.Length; i++)
        {
            var wall = walls[i];
            var coord0 = wall.AxisX ? lpx : lpy;
            var coord1 = wall.AxisX ? lx : ly;
            var nAxis = wall.AxisX ? wall.Nx : wall.Ny;
            var in0 = (coord0 - wall.Boundary) * nAxis;
            var in1 = (coord1 - wall.Boundary) * nAxis;
            var tangentSupport = FootprintSupport(r, lth, -wall.Ny, wall.Nx);
            var support = FootprintSupport(r, lth, wall.Nx, wall.Ny);
            var safeIn = -(support + 0.002);
            var entering = in0 < safeIn && in1 >= safeIn;
            if (in0 >= 0)
            {
                continue; // 中心已在台内, 向外掉落不阻挡
            }
            if (in0 < safeIn && in1 < safeIn)
            {
                continue; // 尚未接触台沿
            }
            var crossT = entering && Math.Abs(in1 - in0) > 1e-12 ? (safeIn - in0) / (in1 - in0) : 1;
            var contactX = lpx + (lx - lpx) * Js.Clamp(crossT, 0, 1);
            var contactY = lpy + (ly - lpy) * Js.Clamp(crossT, 0, 1);
            var tangentCoord = wall.AxisX ? contactY : contactX;
            if (tangentCoord < el - tangentSupport || tangentCoord > er + tangentSupport)
            {
                continue;
            }
            var vn = desiredVx * wall.Nx + desiredVy * wall.Ny;
            var cmdN = cmdV * Math.Cos(lth) * wall.Nx + cmdV * Math.Sin(lth) * wall.Ny;
            if (vn > 0.05 && vn < MountVMin && cmdN > MountVMin)
            {
                vn = cmdN;
            }
            if (vn <= 0.0001)
            {
                continue; // 没有向台内运动
            }
            var vt = Math.Abs(desiredVx * wall.Ny - desiredVy * wall.Nx);
            contacts.Add((i, support, in1, in0, vn, vt));
        }
        if (contacts.Count == 0)
        {
            return;
        }

        // 斜穿台角必须阻挡; 只有单面接触且法向速度/入射角达标才放行。
        if (contacts.Count == 1)
        {
            var c = contacts[0];
            if (c.Vn > MountVMin && c.Vt < c.Vn * Math.Tan(MountAngle))
            {
                return;
            }
        }
        foreach (var c in contacts)
        {
            var wall = walls[c.Wall];
            var nAxis = wall.AxisX ? wall.Nx : wall.Ny;
            var safe = wall.Boundary - nAxis * (c.Support + 0.002);
            if (wall.AxisX)
            {
                lx = safe;
            }
            else
            {
                ly = safe;
            }
            var avn = lvx * wall.Nx + lvy * wall.Ny;
            if (avn > 0)
            {
                lvx -= wall.Nx * avn;
                lvy -= wall.Ny * avn;
            }
        }
        (r.X, r.Y) = t.LocalToWorldPoint(lx, ly);
        (r.Vx, r.Vy) = t.LocalToWorldVector(lvx, lvy);
    }

    // ---------- swept contacts ----------

    private sealed record SweepHit(double Qx, double Qy, double Nx, double Ny, double D, double T, bool Penetrating);

    private static SweepHit? SweptCircleContact(double ax, double ay, double bx, double by, double cx, double cy, double radius)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var fx = ax - cx;
        var fy = ay - cy;
        var a = dx * dx + dy * dy;
        var c = fx * fx + fy * fy - radius * radius;
        if (a < 1e-12)
        {
            if (c >= 0)
            {
                return null;
            }
            var d0 = Js.Hypot(cx - ax, cy - ay);
            if (d0 == 0)
            {
                d0 = 1;
            }
            return new SweepHit(ax, ay, (cx - ax) / d0, (cy - ay) / d0, d0, 0, true);
        }
        if (c < 0)
        {
            var d0 = Js.Hypot(cx - ax, cy - ay);
            if (d0 == 0)
            {
                d0 = 1;
            }
            return new SweepHit(ax, ay, (cx - ax) / d0, (cy - ay) / d0, d0, 0, true);
        }
        var b = 2 * (fx * dx + fy * dy);
        var disc = b * b - 4 * a * c;
        if (disc < 0)
        {
            return null;
        }
        var t = (-b - Math.Sqrt(disc)) / (2 * a);
        if (t < 0 || t > 1)
        {
            return null;
        }
        var qx = ax + dx * t;
        var qy = ay + dy * t;
        var ex = cx - qx;
        var ey = cy - qy;
        var d = Js.Hypot(ex, ey);
        if (d == 0)
        {
            d = radius;
        }
        var nx = ex / d;
        var ny = ey / d;
        // A tangent at t=0 while separating is not a new impact.
        if (dx * nx + dy * ny <= 1e-9 && c >= -1e-9)
        {
            return null;
        }
        return new SweepHit(qx, qy, nx, ny, d, t, false);
    }

    private sealed record RobotPairHit(SweepHit Hit, double Ax, double Ay, double Bx, double By);

    private static RobotPairHit? SweptRobotPairContact((double X, double Y) a0, RobotRuntime a, (double X, double Y) b0, RobotRuntime b, double radius)
    {
        var initialD = Js.Hypot(a0.X - b0.X, a0.Y - b0.Y);
        // 起帧已重叠而本帧分开时, 不能把车拉回旧接触点。
        if (initialD < radius - 1e-9)
        {
            return null;
        }
        var hit = SweptCircleContact(a0.X - b0.X, a0.Y - b0.Y, a.X - b.X, a.Y - b.Y, 0, 0, radius);
        if (hit is null)
        {
            return null;
        }
        var t = hit.T;
        return new RobotPairHit(hit,
            a0.X + (a.X - a0.X) * t, a0.Y + (a.Y - a0.Y) * t,
            b0.X + (b.X - b0.X) * t, b0.Y + (b.Y - b0.Y) * t);
    }

    // ---------- body helpers ----------

    private static double BodyMass(IBody o) => Math.Max(0.05, o.Mass);

    private static double MomentInertia(RobotRuntime r)
    {
        var v = r.Vehicle;
        var m = BodyMass(r);
        return Math.Max(1e-6, m * (v.Length * v.Length + v.Width * v.Width) / 12);
    }

    private static (double X, double Y) ClosestPointOnRobot(RobotRuntime r, double px, double py)
    {
        var v = r.Vehicle;
        var c = Math.Cos(r.Th);
        var s = Math.Sin(r.Th);
        var dx = px - r.X;
        var dy = py - r.Y;
        var lx = dx * c + dy * s;
        var ly = -dx * s + dy * c;
        var cl = Js.Clamp(lx, -v.RearExtent, v.FrontExtent);
        var ct = Js.Clamp(ly, -v.SideExtent, v.SideExtent);
        return (r.X + cl * c - ct * s, r.Y + cl * s + ct * c);
    }

    /// <summary>偏心接触点力臂 r×J → 独立 spinOmega 状态衰减叠加。</summary>
    private static void ApplyContactTorque(RobotRuntime r, double jx, double jy, double leverX, double leverY)
    {
        var inertia = MomentInertia(r);
        var tau = leverX * jy - leverY * jx;
        r.SpinOmega += tau / inertia;
    }

    /// <summary>台阶 3D 姿态: 4 轮采样 → 最小二乘平面 → 平滑沉降 (显示/诊断)。</summary>
    private void ComputeRobotPose(RobotRuntime r, double dt)
    {
        var v = r.Vehicle;
        var c = Math.Cos(r.Th);
        var s = Math.Sin(r.Th);
        var wb = v.WheelBase / 2;
        var tw = v.TrackWidth / 2;
        double H(double fx, double ly)
        {
            var wx = r.X + fx * c - ly * s;
            var wy = r.Y + fx * s + ly * c;
            return _field.StageHeightAt(wx, wy);
        }
        var hFL = H(wb, tw);
        var hFR = H(wb, -tw);
        var hRL = H(-wb, tw);
        var hRR = H(-wb, -tw);
        var targetZG = (hFL + hFR + hRL + hRR) / 4;
        var frontAvg = (hFL + hFR) / 2;
        var rearAvg = (hRL + hRR) / 2;
        var leftAvg = (hFL + hRL) / 2;
        var rightAvg = (hFR + hRR) / 2;
        var targetPitch = Math.Atan((frontAvg - rearAvg) / (2 * wb));
        var targetRoll = Math.Atan((leftAvg - rightAvg) / (2 * tw));
        var k = 1 - Math.Exp(-12 * dt);
        r.ZG += (targetZG - r.ZG) * k;
        r.Pitch += (targetPitch - r.Pitch) * k;
        r.Roll += (targetRoll - r.Roll) * k;
    }

    /// <summary>堵转/电流过载检测: 指令非零但实际线速度持续接近 0 → isStalled。</summary>
    private void UpdateStall(RobotRuntime r, double dt)
    {
        var commandV = double.IsFinite(r.CmdV) ? r.CmdV : (r.V != 0 ? r.V : 0);
        var commanded = Math.Abs(commandV) > 0.05;
        var actual = Js.Hypot(r.Vx, r.Vy);
        var displacement = Js.Hypot(r.X - r.StallAnchorX, r.Y - r.StallAnchorY);
        var stallSpeed = _params.StallSpeed != 0 ? _params.StallSpeed : 0.03;
        var stallDisplacement = _params.StallDisplacement != 0 ? _params.StallDisplacement : 0.006;
        var stallTime = _params.StallTime != 0 ? _params.StallTime : 0.4;
        var stallRelease = _params.StallRelease != 0 ? _params.StallRelease : 0.06;
        var lowSpeed = actual < stallSpeed;
        var noProgress = displacement < stallDisplacement;
        if (commanded && (lowSpeed || noProgress))
        {
            r.StallT += dt;
            if (r.StallT >= stallTime)
            {
                r.IsStalled = true;
            }
        }
        else
        {
            r.StallT = 0;
            r.IsStalled = false;
        }
        if (!commanded || displacement >= stallDisplacement)
        {
            r.StallAnchorX = r.X;
            r.StallAnchorY = r.Y;
            if (!commanded || actual > stallRelease)
            {
                r.StallT = 0;
                r.IsStalled = false;
            }
        }
    }

    /// <summary>指令延迟环形队列 (cmdLatencyFrames=0 时等价直通)。</summary>
    private void ApplyCommandLatency(RobotRuntime r)
    {
        var n = (int)Math.Max(0, Math.Floor(_params.CmdLatencyFrames));
        if (n == 0)
        {
            r.CmdV = r.V;
            r.CmdW = r.W;
            return;
        }
        r.CmdQueue.Enqueue((r.V, r.W));
        if (r.CmdQueue.Count > n)
        {
            var cmd = r.CmdQueue.Dequeue();
            r.CmdV = cmd.V;
            r.CmdW = cmd.W;
        }
        else
        {
            r.CmdV = 0;
            r.CmdW = 0; // 管线未满: 电机尚未收到指令
        }
    }

    private static void SeparatePair(IBody a, IBody b, double nx, double ny, double overlap)
    {
        if (!(overlap > 0))
        {
            return;
        }
        var invA = 1 / BodyMass(a);
        var invB = 1 / BodyMass(b);
        var total = invA + invB;
        a.X -= nx * overlap * (invA / total);
        a.Y -= ny * overlap * (invA / total);
        b.X += nx * overlap * (invB / total);
        b.Y += ny * overlap * (invB / total);
    }

    private static void ApplyPairImpulse(IBody a, IBody b, double nx, double ny, double restitution, double scale)
    {
        var rel = (a.Vx * nx + a.Vy * ny) - (b.Vx * nx + b.Vy * ny);
        if (rel <= 0)
        {
            return;
        }
        var invA = 1 / BodyMass(a);
        var invB = 1 / BodyMass(b);
        var impulse = rel * (1 + restitution) * scale / (invA + invB);
        a.Vx -= nx * impulse * invA;
        a.Vy -= ny * impulse * invA;
        b.Vx += nx * impulse * invB;
        b.Vy += ny * impulse * invB;
    }

    private void KeepBlockInsideFence(BlockRuntime o)
    {
        // Same local-coordinate fence square as the robot clamp; blocks bounce.
        var t = _field.Transform;
        var (x, y) = t.WorldToLocalPoint(o.X, o.Y);
        var (vx, vy) = t.WorldToLocalVector(o.Vx, o.Vy);
        var b = FenceMargin;
        var hi = _field.Field.FieldSize - b;
        if (x < b)
        {
            x = b;
            if (vx < 0)
            {
                vx *= -0.10;
            }
        }
        else if (x > hi)
        {
            x = hi;
            if (vx > 0)
            {
                vx *= -0.10;
            }
        }
        if (y < b)
        {
            y = b;
            if (vy < 0)
            {
                vy *= -0.10;
            }
        }
        else if (y > hi)
        {
            y = hi;
            if (vy > 0)
            {
                vy *= -0.10;
            }
        }
        (o.X, o.Y) = t.LocalToWorldPoint(x, y);
        (o.Vx, o.Vy) = t.LocalToWorldVector(vx, vy);
    }

    // ---------- robot-block contacts ----------

    private sealed record RobotBlockContact(RobotRuntime R, BlockRuntime O, double Nx, double Ny, double Overlap, SweepHit? Hit, double T, int BlockIndex);

    private RobotBlockContact? CollectRobotBlockContact(RobotRuntime r, BlockRuntime o, double px, double py, int blockIndex, double dt)
    {
        var radius = (r.R != 0 ? r.R : BodyRadius) + o.R;
        var stepDt = Math.Max(0, dt);
        // 车和能量块都可能在同一个外层步中移动 → 相对位移扫掠。
        var b0x = o.X;
        var b0y = o.Y;
        var b1x = b0x + o.Vx * stepDt;
        var b1y = b0y + o.Vy * stepDt;
        var rel0x = px - b0x;
        var rel0y = py - b0y;
        var rel1x = r.X - b1x;
        var rel1y = r.Y - b1y;
        var startD = Js.Hypot(rel0x, rel0y);
        var endD = Js.Hypot(rel1x, rel1y);
        SweepHit? hit = null;
        if (startD >= radius - 1e-9)
        {
            var swept = SweptCircleContact(rel0x, rel0y, rel1x, rel1y, 0, 0, radius);
            if (swept is not null)
            {
                var t = swept.T;
                hit = new SweepHit(
                    px + (r.X - px) * t, py + (r.Y - py) * t,
                    swept.Nx, swept.Ny, swept.D, t, swept.Penetrating);
            }
        }
        if (endD >= radius - 1e-9 && hit is null)
        {
            return null;
        }
        double nx, ny, overlap = 0;
        if (hit is not null && startD >= radius - 1e-9)
        {
            nx = hit.Nx;
            ny = hit.Ny;
            // Put the car back at first contact so a swept hit cannot cross the block.
            r.X = hit.Qx - nx * 0.0005;
            r.Y = hit.Qy - ny * 0.0005;
        }
        else if (endD < radius)
        {
            var dx = b1x - r.X;
            var dy = b1y - r.Y;
            if (endD < 1e-9)
            {
                var h = Js.Hypot(r.Vx - o.Vx, r.Vy - o.Vy);
                if (h == 0)
                {
                    h = 1;
                }
                nx = (r.Vx - o.Vx) / h;
                ny = (r.Vy - o.Vy) / h;
            }
            else
            {
                nx = dx / endD;
                ny = dy / endD;
            }
            overlap = radius - endD;
        }
        else
        {
            // endD >= radius and no hit: covered by the early return above.
            return null;
        }
        return new RobotBlockContact(r, o, nx, ny, overlap, hit, hit is null ? 1 : hit.T, blockIndex);
    }

    private static void MarkBlockContact(BlockRuntime o, RobotRuntime r, double t)
    {
        var role = r.IsUs ? "us" : "them";
        var time = double.IsFinite(t) ? Js.Clamp(t, 0, 1) : 1;
        o.ContactThisStep.Add((role, time));
    }

    /// <summary>Derives each block's last-contact role from this step's contact set.</summary>
    public static void FinalizeBlockContacts(List<BlockRuntime> blocks)
    {
        foreach (var o in blocks)
        {
            if (o.ContactThisStep.Count == 0)
            {
                continue;
            }
            var maxT = o.ContactThisStep.Max(c => c.T);
            var last = o.ContactThisStep.Where(c => Math.Abs(c.T - maxT) <= 1e-6).ToList();
            o.LastContactRole = last.Count == 1 ? last[0].Role : "simultaneous";
        }
    }

    private void ResolveRobotBlockContacts(List<RobotBlockContact> contacts)
    {
        if (contacts.Count == 0)
        {
            return;
        }
        // 按首次接触时间/实体索引稳定排序; 速度冲量基于同一帧快照累加。
        contacts.Sort((a, b) =>
        {
            var byT = a.T.CompareTo(b.T);
            if (byT != 0)
            {
                return byT;
            }
            var byIndex = a.BlockIndex.CompareTo(b.BlockIndex);
            if (byIndex != 0)
            {
                return byIndex;
            }
            return (a.R.IsUs ? 0 : 1) - (b.R.IsUs ? 0 : 1);
        });
        var robots = new[] { _us, _them };
        var entities = new List<IBody>(robots.Cast<IBody>().Concat(_blocks));
        var baseVel = new Dictionary<IBody, (double X, double Y)>();
        var deltaVel = new Dictionary<IBody, (double X, double Y)>();
        var deltaPos = new Dictionary<IBody, (double X, double Y)>();
        var firstSweep = new Dictionary<RobotRuntime, RobotBlockContact>();
        foreach (var entity in entities)
        {
            baseVel[entity] = (entity.Vx, entity.Vy);
            deltaVel[entity] = (0, 0);
            deltaPos[entity] = (0, 0);
        }
        void Add(Dictionary<IBody, (double X, double Y)> table, IBody entity, double x, double y)
        {
            var d = table[entity];
            table[entity] = (d.X + x, d.Y + y);
        }
        foreach (var c in contacts)
        {
            var (r, o, nx, ny, overlap) = (c.R, c.O, c.Nx, c.Ny, c.Overlap);
            MarkBlockContact(o, r, c.T);
            if (c.Hit is not null)
            {
                if (!firstSweep.TryGetValue(r, out var old) || c.T < old.T)
                {
                    firstSweep[r] = c;
                }
            }
            else if (overlap > 0)
            {
                var invR = 1 / BodyMass(r);
                var invO = 1 / BodyMass(o);
                var total = invR + invO;
                Add(deltaPos, r, -nx * overlap * (invR / total), -ny * overlap * (invR / total));
                Add(deltaPos, o, nx * overlap * (invO / total), ny * overlap * (invO / total));
            }
            var rv = baseVel[r];
            var ov = baseVel[o];
            var rel = (rv.X - ov.X) * nx + (rv.Y - ov.Y) * ny;
            if (rel <= 0)
            {
                continue;
            }
            var invR2 = 1 / BodyMass(r);
            var invO2 = 1 / BodyMass(o);
            var push = Js.Clamp(r.Vehicle.PushFactor, 0.1, 3);
            var impulse = rel * (1 + 0.08) * push / (invR2 + invO2);
            Add(deltaVel, r, -nx * impulse * invR2, -ny * impulse * invR2);
            Add(deltaVel, o, nx * impulse * invO2, ny * impulse * invO2);
            var contactX = c.Hit is not null ? c.Hit.Qx : r.X;
            var contactY = c.Hit is not null ? c.Hit.Qy : r.Y;
            ApplyContactTorque(r, -nx * impulse, -ny * impulse, contactX - r.X, contactY - r.Y);
        }
        // 同一车辆一帧跨过多个块时只回退到最早接触点。
        foreach (var (_, c) in firstSweep)
        {
            c.R.X = c.Hit!.Qx - c.Nx * 0.0005;
            c.R.Y = c.Hit.Qy - c.Ny * 0.0005;
        }
        foreach (var entity in entities)
        {
            var dp = deltaPos[entity];
            var dv = deltaVel[entity];
            entity.X += dp.X;
            entity.Y += dp.Y;
            entity.Vx += dv.X;
            entity.Vy += dv.Y;
        }
    }

    private bool ResolveBlockPair(BlockRuntime a, BlockRuntime b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var radius = a.R + b.R;
        var d = Js.Hypot(dx, dy);
        var gap = 0.001;
        if (!(d < radius + gap))
        {
            return false;
        }
        var nx = d > 1e-9 ? dx / d : 1;
        var ny = d > 1e-9 ? dy / d : 0;
        SeparatePair(a, b, nx, ny, Math.Max(0, radius + gap - d));
        ApplyPairImpulse(a, b, nx, ny, 0.06, 1);
        return true;
    }

    private void SettleBlockPairs()
    {
        // 4 passes remove residual overlaps deterministically for 3 blocks.
        for (var pass = 0; pass < 4; pass++)
        {
            var moved = false;
            for (var i = 0; i < _blocks.Count; i++)
            {
                for (var j = i + 1; j < _blocks.Count; j++)
                {
                    moved = ResolveBlockPair(_blocks[i], _blocks[j]) || moved;
                }
            }
            if (!moved)
            {
                break;
            }
        }
        foreach (var block in _blocks)
        {
            KeepBlockInsideFence(block);
        }
    }

    // ---------- robot pair ----------

    /// <summary>每步只解一次 US/THEM 对: 唯一接触法线/接触点, 不依赖遍历顺序。</summary>
    private bool ResolveRobotPair(RobotRuntime a, RobotRuntime b, (double X, double Y) a0, (double X, double Y) b0)
    {
        var radius = (a.R != 0 ? a.R : BodyRadius) + (b.R != 0 ? b.R : BodyRadius);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var d = Js.Hypot(dx, dy);
        var swept = d >= radius ? SweptRobotPairContact(a0, a, b0, b, radius) : null;        if (!(d < radius) && swept is null)
        {
            return false;
        }

        double nx, ny, overlap = 0;
        if (swept is not null && !(d < radius))
        {
            // 帧末可能已从对方另一侧穿出, 必须用首次接触法线。
            nx = swept.Hit.Nx;
            ny = swept.Hit.Ny;
        }
        else if (d > 1e-9)
        {
            nx = dx / d;
            ny = dy / d;
            overlap = Math.Max(0, radius - d);
        }
        else
        {
            // 完全重合: 优先按相对速度选法线; 静止重合回退上一帧几何方向。
            var rvx = a.Vx - b.Vx;
            var rvy = a.Vy - b.Vy;
            var speed = Js.Hypot(rvx, rvy);
            var pdx = b0.X - a0.X;
            var pdy = b0.Y - a0.Y;
            var previousD = Js.Hypot(pdx, pdy);
            nx = speed > 1e-9 ? rvx / speed : (previousD > 1e-9 ? pdx / previousD : 1);
            ny = speed > 1e-9 ? rvy / speed : (previousD > 1e-9 ? pdy / previousD : 0);
            overlap = radius;
        }
        if (swept is not null && !(d < radius))
        {
            // 双方回退到首次接触时刻, 保留微小间隙避免数值抖动。
            a.X = swept.Ax - nx * 0.0005;
            a.Y = swept.Ay - ny * 0.0005;
            b.X = swept.Bx + nx * 0.0005;
            b.Y = swept.By + ny * 0.0005;
        }
        else
        {
            SeparatePair(a, b, nx, ny, overlap);
        }

        var avn = a.Vx * nx + a.Vy * ny;
        var bvn = b.Vx * nx + b.Vy * ny;
        // 已分离但仍有很小向内相对速度时清掉, 避免"贴住—重叠—再分离"抖动。
        if (avn <= bvn + 0.02)
        {
            if (avn > bvn)
            {
                var invA = 1 / BodyMass(a);
                var invB = 1 / BodyMass(b);
                var correction = (avn - bvn) / (invA + invB);
                a.Vx -= nx * correction * invA;
                a.Vy -= ny * correction * invA;
                b.Vx += nx * correction * invB;
                b.Vy += ny * correction * invB;
            }
            return true;
        }
        var rel = avn - bvn;
        var configuredE = _params.CollisionRestitution;
        var restitution = configuredE is { } e && double.IsFinite(e)
            ? Js.Clamp(e, 0, 0.9)
            : Js.Clamp(0.2 + rel * 0.15, 0.2, 0.45);
        // pushFactor 取两车几何平均, 避免遍历顺序偏置。
        var push = Math.Sqrt(Js.Clamp(a.Vehicle.PushFactor, 0.1, 3) * Js.Clamp(b.Vehicle.PushFactor, 0.1, 3));
        var invA2 = 1 / BodyMass(a);
        var invB2 = 1 / BodyMass(b);
        var impulse = rel * (1 + restitution) * push / (invA2 + invB2);
        a.Vx -= nx * impulse * invA2;
        a.Vy -= ny * impulse * invA2;
        b.Vx += nx * impulse * invB2;
        b.Vy += ny * impulse * invB2;

        // 偏心接触点力臂 r×J: 一对实体只各累加一次角冲量。
        var cpA = ClosestPointOnRobot(a, b.X, b.Y);
        ApplyContactTorque(a, -nx * impulse, -ny * impulse, cpA.X - a.X, cpA.Y - a.Y);
        var cpB = ClosestPointOnRobot(b, a.X, a.Y);
        ApplyContactTorque(b, nx * impulse, ny * impulse, cpB.X - b.X, cpB.Y - b.Y);

        // 切向摩擦 (对称地只施加一次)。
        var tx = -ny;
        var ty = nx;
        var tangent = (a.Vx * tx + a.Vy * ty) - (b.Vx * tx + b.Vy * ty);
        var friction = tangent * 0.2;
        a.Vx -= tx * friction;
        a.Vy -= ty * friction;
        b.Vx += tx * friction;
        b.Vy += ty * friction;

        // 铲子楔入: 铲刃较高的一侧被垫起, 另一侧前轮载荷下降。
        var facing = Math.Abs(Js.Norm(a.Th - b.Th));
        if (facing > Math.PI * 0.6)
        {
            var aBlade = a.ZG + a.Vehicle.ShovelHeight;
            var bBlade = b.ZG + b.Vehicle.ShovelHeight;
            if (Math.Abs(aBlade - bBlade) > 0.004)
            {
                (aBlade > bBlade ? a : b).WedgedFront = true;
            }
        }
        return true;
    }

    // ---------- motion ----------

    /// <summary>轮式驱动动力学: 纵向 accelK 收敛 + 侧向 latFrictionK 衰减 + 打转叠加。</summary>
    private void MotionFor(RobotRuntime r, double dt, bool applyLatency)
    {
        var vehicle = r.Vehicle;
        // 控制指令限幅 ({v,w} 接口不变)
        r.V = Js.Clamp(r.V, -vehicle.MaxSpeed, vehicle.MaxSpeed);
        r.W = Js.Clamp(r.W, -vehicle.MaxTurnRate, vehicle.MaxTurnRate);
        if (applyLatency)
        {
            ApplyCommandLatency(r);
        }

        var th0 = r.Th;
        var c0 = Math.Cos(th0);
        var s0 = Math.Sin(th0);
        var vF = r.Vx * c0 + r.Vy * s0;   // 纵向速度
        var vL = -r.Vx * s0 + r.Vy * c0;  // 侧向速度
        var spin = r.SpinOmega;
        var vCmd = r.CmdV;
        var wCmd = r.CmdW;

        var kAcc = 1 - Math.Exp(-(vehicle.AccelK != 0 ? vehicle.AccelK : 12) * dt);
        var latF = Math.Exp(-(vehicle.LatFrictionK != 0 ? vehicle.LatFrictionK : 8) * dt);
        var angF = Math.Exp(-(vehicle.AngDamping != 0 ? vehicle.AngDamping : 3) * dt);
        // 铲子楔入: 前轮被垫起 → 驱动推力急剧下降。
        var frontLoad = r.FrontLoad != 0 ? r.FrontLoad : 1;
        var thrust = 0.2 + 0.8 * frontLoad;
        vF += (vCmd - vF) * kAcc * thrust;
        vL *= latF;
        spin *= angF;

        // 指令横摆瞬时响应 + 碰撞打转衰减叠加。
        var omega = wCmd + spin;
        r.Th += omega * dt;
        var c = Math.Cos(r.Th);
        var s = Math.Sin(r.Th);
        r.Vx = vF * c - vL * s;
        r.Vy = vF * s + vL * c;
        r.Omega = omega;
        r.SpinOmega = spin;

        var px = r.X;
        var py = r.Y;
        r.X += r.Vx * dt;
        r.Y += r.Vy * dt;
        StageWall(r, px, py);
        ClampRobotInsideFence(r);
    }

    /// <summary>
    /// 围栏夹取: 场地是场局部 [margin, FieldSize-margin] 的正方形;
    /// 旋转布局下在局部坐标内夹取并清零向内速度, 再变换回世界坐标。
    /// </summary>
    private void ClampRobotInsideFence(RobotRuntime r)
    {
        var t = _field.Transform;
        var (x, y) = t.WorldToLocalPoint(r.X, r.Y);
        var (vx, vy) = t.WorldToLocalVector(r.Vx, r.Vy);
        var b = FenceMargin;
        var hi = _field.Field.FieldSize - b;
        if (x < b)
        {
            x = b;
            if (vx < 0)
            {
                vx = 0;
            }
        }
        else if (x > hi)
        {
            x = hi;
            if (vx > 0)
            {
                vx = 0;
            }
        }
        if (y < b)
        {
            y = b;
            if (vy < 0)
            {
                vy = 0;
            }
        }
        else if (y > hi)
        {
            y = hi;
            if (vy > 0)
            {
                vy = 0;
            }
        }
        (r.X, r.Y) = t.LocalToWorldPoint(x, y);
        (r.Vx, r.Vy) = t.LocalToWorldVector(vx, vy);
    }

    private void IntegrateBlocks(double dt, Dictionary<RobotRuntime, (double X, double Y)> previousPos)
    {
        var contacts = new List<RobotBlockContact>();
        foreach (var r in new[] { _us, _them })
        {
            if (!(r.Fsm.Armed || r.Fsm.Manual))
            {
                continue;
            }
            var prev = previousPos.TryGetValue(r, out var p) ? p : (r.X, r.Y);
            for (var i = 0; i < _blocks.Count; i++)
            {
                var o = _blocks[i];
                var contact = CollectRobotBlockContact(r, o, prev.X, prev.Y, i, dt);
                if (contact is not null)
                {
                    contacts.Add(contact);
                }
            }
        }
        ResolveRobotBlockContacts(contacts);
        // out = 已计分下场; 掉台块在走道上仍是实体, 可继续被推到围栏。
        foreach (var o in _blocks)
        {
            o.X += o.Vx * dt;
            o.Y += o.Vy * dt;
            // 库仑摩擦: 低速粘住, 高速动摩擦 + 指数背压。
            var spd = Js.Hypot(o.Vx, o.Vy);
            if (spd < (_params.BlockStickSpeed != 0 ? _params.BlockStickSpeed : 0.02))
            {
                o.Vx = 0;
                o.Vy = 0;
            }
            else
            {
                var fric = Math.Exp(-2.2 * dt);
                o.Vx *= fric;
                o.Vy *= fric;
                var muK = (_params.BlockMuK != 0 ? _params.BlockMuK : 0.5) * 9.81 * dt;
                spd = Js.Hypot(o.Vx, o.Vy);
                if (spd > muK)
                {
                    var sc = (spd - muK) / spd;
                    o.Vx *= sc;
                    o.Vy *= sc;
                }
                else
                {
                    o.Vx = 0;
                    o.Vy = 0;
                }
            }
            if (spd > 2.4)
            {
                o.Vx *= 2.4 / spd;
                o.Vy *= 2.4 / spd;
            }
            KeepBlockInsideFence(o);
        }
        SettleBlockPairs();
    }

    private void FinishRobotMotion(RobotRuntime r, double dt)
    {
        UpdateStall(r, dt);
        var targetLoad = r.WedgedFront ? 0 : 1;
        r.FrontLoad += (targetLoad - r.FrontLoad) * (1 - Math.Exp(-8 * dt));
    }

    /// <summary>
    /// One outer physics step: reset wedge flags, integrate each robot with
    /// displacement-adaptive substeps (stage wall inside), then resolve the
    /// robot pair once, integrate the block chain and finish per-robot state.
    /// Returns true when the robots touched this step.
    /// </summary>
    public bool Step(double d)
    {
        var robots = new[] { _us, _them };
        // 每步先清空楔入标记, 由本帧碰撞重新判定。
        foreach (var r in robots)
        {
            r.WedgedFront = false;
        }
        var previousPos = new Dictionary<RobotRuntime, (double X, double Y)>
        {
            [_us] = (_us.X, _us.Y),
            [_them] = (_them.X, _them.Y),
        };
        var maxSweep = 0.0;
        foreach (var r in robots)
        {
            if (!(r.Fsm.Armed || r.Fsm.Manual))
            {
                continue;
            }
            var v = r.Vehicle;
            var commandSpeed = Math.Max(Math.Max(Math.Abs(r.V), Math.Abs(r.CmdV)), Math.Max(Js.Hypot(r.Vx, r.Vy), v.MaxSpeed));
            var commandTurn = Math.Max(Math.Abs(r.W), Math.Abs(r.CmdW));
            var extent = Math.Max(Math.Max(v.FrontExtent, v.RearExtent), v.SideExtent);
            maxSweep = Math.Max(maxSweep, commandSpeed * d + commandTurn * extent * d);
        }
        var physicsSubsteps = Js.Clamp(Math.Max(Math.Max(1, Math.Ceiling(d / 0.025)), Math.Ceiling(maxSweep / 0.035)), 1, 16);
        var physicsDt = d / physicsSubsteps;
        for (var sub = 0; sub < physicsSubsteps; sub++)
        {
            foreach (var r in robots)
            {
                if (r.Fsm.Armed || r.Fsm.Manual)
                {
                    MotionFor(r, physicsDt, sub == 0);
                }
            }
            foreach (var r in robots)
            {
                ComputeRobotPose(r, physicsDt);
            }
        }
        var contact = ResolveRobotPair(_us, _them, Restore(previousPos, _us), Restore(previousPos, _them));
        IntegrateBlocks(d, previousPos);
        foreach (var r in robots)
        {
            FinishRobotMotion(r, d);
        }
        return contact;

        static (double X, double Y) Restore(Dictionary<RobotRuntime, (double X, double Y)> map, RobotRuntime r)
            => map.TryGetValue(r, out var p) ? p : (r.X, r.Y);
    }
}
