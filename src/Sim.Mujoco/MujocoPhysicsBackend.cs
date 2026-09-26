using Sim.Core;
using Sim.Protocol;

namespace Sim.Mujoco;

/// <summary>One match's independently owned MuJoCo model and mutable data.</summary>
internal sealed class MujocoPhysicsBackend : IPhysicsBackend
{
    private readonly PhysicsBackendContext _context;
    private readonly HashSet<int> _usGeoms = [];
    private readonly HashSet<int> _themGeoms = [];
    private readonly Dictionary<int, BlockRuntime> _blockGeoms = [];
    private readonly Dictionary<string, double> _driveV = [];

    // 09-25 SEARCH 索敌闭环: 原地转向补偿系数。四轮横向滑动摩擦使原地偏航速率
    // 仅为指令的 ~3%(kv=0.25 为登台柔性所必需, 不能提高); 对 |CmdV|≤0.02 的
    // 原地转向命令放大差速轮目标速度, 使 kv×Δω 重新触及力上限。纵向行驶、
    // 倒车登台与推块均带纵向命令, 不受影响。受控测试选定 4(候选 2/4/6:
    // 2 的 90° 对准需 9.45 s 超预算, 6 过冲跳过对准窗口, 4 → <3 s 且误差
    // 0.032 rad; 轮速仍经 WheelAngularSpeedLimit 截断)。静态字段仅为
    // SearchTurnCompensationTests 候选对照保留。
    internal static double InPlaceTurnCompensation = 4.0;
    private IntPtr _model;
    private bool _ownsModel = true;
    private IntPtr _data;
    private bool _disposed;

    public string BackendId => PhysicsSpec.Mujoco;
    public string? EngineVersion => MujocoNative.ExpectedVersion;
    public string? ModelSha256 { get; }

    internal MujocoPhysicsBackend(PhysicsBackendContext context)
    {
        _context = context;
        Validate(context);
        var (xml, hash) = MujocoModel.Generate(context);
        ModelSha256 = hash;
        _ownsModel = true;
        try
        {
            _model = MujocoNative.CreateModel(xml);
            _data = MujocoNative.MakeData(_model);
            if (_data == IntPtr.Zero)
            {
                throw new InvalidOperationException("MuJoCo could not allocate per-match simulation data.");
            }
            CheckStateShape();
            RegisterGeoms();
            MujocoNative.Forward(_model, _data);
            CopyStateToRuntime();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>训练专用: 复用会话持有的已编译 mjModel(本 backend 不拥有模型,
    /// Dispose 只释放 mjData)。每集独立 mjData, 模型由训练 factory 统一释放。</summary>
    internal MujocoPhysicsBackend(PhysicsBackendContext context, IntPtr externalModel)
    {
        _context = context;
        Validate(context);
        var (_, hash) = MujocoModel.Generate(context);
        ModelSha256 = hash;
        _ownsModel = false;
        try
        {
            _model = externalModel;
            _data = MujocoNative.MakeData(_model);
            if (_data == IntPtr.Zero)
            {
                throw new InvalidOperationException("MuJoCo could not allocate per-match simulation data.");
            }
            CheckStateShape();
            RegisterGeoms();
            MujocoNative.Forward(_model, _data);
            CopyStateToRuntime();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void Validate(PhysicsBackendContext context)
    {
        if (Math.Abs(context.Scenario.Field.TickSeconds - 0.05) > 1e-12)
        {
            throw new ArgumentException("MuJoCo model v1 requires field.tickSeconds=0.05.", nameof(context));
        }
        if (context.Blocks.Count != 3)
        {
            throw new ArgumentException("MuJoCo model v1 requires exactly three energy blocks.", nameof(context));
        }
    }

    public bool Step(double dt)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(dt) || Math.Abs(dt - 0.05) > 1e-12)
        {
            throw new ArgumentException("MuJoCo model v1 advances exactly one 0.05 s referee tick.", nameof(dt));
        }
        var controls = new double[8];
        SetControls(_context.Us, 0, controls);
        SetControls(_context.Them, 4, controls);
        MujocoNative.WriteCtrl(_model, _data, controls);
        var robotsTouched = false;
        for (var i = 0; i < MujocoModel.SubstepsPerTick; i++)
        {
            MujocoNative.Step(_model, _data);
            var contactTime = (i + 1) * MujocoModel.SubstepSeconds;
            MujocoContacts.Visit(_data, (a, b) =>
            {
                if ((_usGeoms.Contains(a) && _themGeoms.Contains(b))
                    || (_usGeoms.Contains(b) && _themGeoms.Contains(a)))
                {
                    robotsTouched = true;
                }
                if (_blockGeoms.TryGetValue(a, out var blockA))
                {
                    RecordBlockContact(blockA, b, contactTime);
                }
                if (_blockGeoms.TryGetValue(b, out var blockB))
                {
                    RecordBlockContact(blockB, a, contactTime);
                }
            });
        }
        CopyStateToRuntime();
        UpdateStall(_context.Us, dt);
        UpdateStall(_context.Them, dt);
        return robotsTouched;
    }

    private void RecordBlockContact(BlockRuntime block, int geom, double contactTime)
    {
        if (_usGeoms.Contains(geom)) block.ContactThisStep.Add((RoleNames.Us, contactTime));
        else if (_themGeoms.Contains(geom)) block.ContactThisStep.Add((RoleNames.Them, contactTime));
    }

    private void SetControls(RobotRuntime robot, int offset, double[] controls)
    {
        // Commands have already been bounded by MatchEngine. Keep its optional
        // frame latency semantics at the physical actuator boundary.
        ApplyCommandLatency(robot);
        var cmdV = robot.CmdV;
        var cmdW = robot.CmdW;
        if (Math.Abs(cmdV) <= 0.02 && Math.Abs(cmdW) > 0)
        {
            cmdW *= InPlaceTurnCompensation;
        }
        var halfTrack = robot.Vehicle.TrackWidth / 2;
        // A +Y wheel angular velocity rolls its centre toward local +X.
        var left = (cmdV - cmdW * halfTrack) / MujocoModel.WheelRadius;
        var right = (cmdV + cmdW * halfTrack) / MujocoModel.WheelRadius;
        controls[offset] = Math.Clamp(left, -MujocoModel.WheelAngularSpeedLimit, MujocoModel.WheelAngularSpeedLimit);
        controls[offset + 1] = Math.Clamp(right, -MujocoModel.WheelAngularSpeedLimit, MujocoModel.WheelAngularSpeedLimit);
        controls[offset + 2] = controls[offset];
        controls[offset + 3] = controls[offset + 1];
    }

    private void ApplyCommandLatency(RobotRuntime robot)
    {
        var frames = (int)Math.Max(0, Math.Floor(_context.Parameters.CmdLatencyFrames));
        double targetV;
        double targetW;
        if (frames == 0)
        {
            targetV = robot.V;
            targetW = robot.W;
        }
        else
        {
            robot.CmdQueue.Enqueue((robot.V, robot.W));
            if (robot.CmdQueue.Count > frames)
            {
                var command = robot.CmdQueue.Dequeue();
                targetV = command.V;
                targetW = command.W;
            }
            else
            {
                targetV = 0;
                targetW = 0;
            }
        }
        // 2026-09-25 登台修复配套: 力上限提高后, 指令阶跃的第一帧就会打满伺服力上限,
        // 造成整车抬头-砸地弹跳。镜像 legacy Physics 的一阶加速(AccelK, 只滤纵向 v,
        // w 保持瞬时响应), 在执行器边界把驱动指令斜坡化; 稳态误差不衰减, 倒车登台
        // 顶住台沿时爬升扭矩仍可达力上限。
        var dt = _context.Scenario.Field.TickSeconds;
        var k = 1 - Math.Exp(-(robot.Vehicle.AccelK != 0 ? robot.Vehicle.AccelK : 12) * dt);
        _driveV[robot.Role] = (_driveV.TryGetValue(robot.Role, out var smoothed) ? smoothed : 0)
            + (targetV - (_driveV.TryGetValue(robot.Role, out var v0) ? v0 : 0)) * k;
        robot.CmdV = _driveV[robot.Role];
        robot.CmdW = targetW;
    }

    private void UpdateStall(RobotRuntime robot, double dt)
    {
        var p = _context.Parameters;
        var commanded = Math.Abs(robot.CmdV) > 0.05;
        var actual = Math.Sqrt(robot.Vx * robot.Vx + robot.Vy * robot.Vy);
        var displacement = Math.Sqrt(Math.Pow(robot.X - robot.StallAnchorX, 2) + Math.Pow(robot.Y - robot.StallAnchorY, 2));
        var stallSpeed = p.StallSpeed != 0 ? p.StallSpeed : 0.03;
        var stallDisplacement = p.StallDisplacement != 0 ? p.StallDisplacement : 0.006;
        var stallTime = p.StallTime != 0 ? p.StallTime : 0.4;
        var stallRelease = p.StallRelease != 0 ? p.StallRelease : 0.06;
        if (commanded && (actual < stallSpeed || displacement < stallDisplacement))
        {
            robot.StallT += dt;
            if (robot.StallT >= stallTime) robot.IsStalled = true;
        }
        else
        {
            robot.StallT = 0;
            robot.IsStalled = false;
        }
        if (!commanded || displacement >= stallDisplacement)
        {
            robot.StallAnchorX = robot.X;
            robot.StallAnchorY = robot.Y;
            if (!commanded || actual > stallRelease)
            {
                robot.StallT = 0;
                robot.IsStalled = false;
            }
        }
    }

    public bool OnStage(RobotRuntime robot) => FootprintCorners(robot).All(p => _context.Field.OnPlatform(p.X, p.Y));
    public bool HangOn(RobotRuntime robot)
    {
        var v = robot.Vehicle;
        return _context.Field.OnPlatform(robot.X, robot.Y)
            && (!OnPlatformAt(robot, v.FrontExtent, v.SideExtent)
                || !OnPlatformAt(robot, v.FrontExtent, -v.SideExtent));
    }
    public bool FullOn(RobotRuntime robot) => OnStage(robot);

    private (double X, double Y)[] FootprintCorners(RobotRuntime r)
    {
        var v = r.Vehicle;
        return [Point(r, v.FrontExtent, v.SideExtent), Point(r, v.FrontExtent, -v.SideExtent),
            Point(r, -v.RearExtent, v.SideExtent), Point(r, -v.RearExtent, -v.SideExtent)];
    }
    private bool OnPlatformAt(RobotRuntime r, double forward, double lateral)
    {
        var (x, y) = Point(r, forward, lateral);
        return _context.Field.OnPlatform(x, y);
    }
    private static (double X, double Y) Point(RobotRuntime r, double forward, double lateral)
    {
        var c = Math.Cos(r.Th);
        var s = Math.Sin(r.Th);
        return (r.X + forward * c - lateral * s, r.Y + forward * s + lateral * c);
    }

    public void ResetRobot(RobotRuntime robot)
    {
        ThrowIfDisposed();
        var roleIndex = robot.Role == RoleNames.Us ? 0 : robot.Role == RoleNames.Them ? 1 : -1;
        if (roleIndex < 0) throw new ArgumentException($"Unknown robot role '{robot.Role}'.", nameof(robot));
        var qpos = MujocoNative.ReadQpos(_model, _data);
        var qvel = MujocoNative.ReadQvel(_model, _data);
        var p = roleIndex * 11;
        var v = roleIndex * 10;
        qpos[p] = robot.X;
        qpos[p + 1] = robot.Y;
        qpos[p + 2] = _context.Field.StageHeightAt(robot.X, robot.Y) + MujocoModel.WheelRadius + 0.04;
        qpos[p + 3] = Math.Cos(robot.Th / 2);
        qpos[p + 4] = 0;
        qpos[p + 5] = 0;
        qpos[p + 6] = Math.Sin(robot.Th / 2);
        Array.Clear(qpos, p + 7, 4);
        Array.Clear(qvel, v, 10);
        MujocoNative.WriteQpos(_model, _data, qpos);
        MujocoNative.WriteQvel(_model, _data, qvel);
        var controls = MujocoNative.ReadCtrl(_model, _data);
        Array.Clear(controls, roleIndex * 4, 4);
        MujocoNative.WriteCtrl(_model, _data, controls);
        MujocoNative.Forward(_model, _data);
        CopyStateToRuntime();
    }

    public PhysicsPoses? BuildPhysicsPoses()
    {
        ThrowIfDisposed();
        var qpos = MujocoNative.ReadQpos(_model, _data);
        var poses = new PhysicsPoses
        {
            Robots = new Dictionary<string, PhysicsPose3>
            {
                [RoleNames.Us] = PoseAt(qpos, 0),
                [RoleNames.Them] = PoseAt(qpos, 11),
            },
            Buffs = [],
        };
        for (var i = 0; i < _context.Blocks.Count; i++)
        {
            var pose = PoseAt(qpos, 22 + i * 7);
            if (_context.Blocks[i].Kind == BlockKind.Buff) poses.Buffs.Add(pose);
            else poses = poses with { Debuff = pose };
        }
        return poses;
    }

    private void CopyStateToRuntime()
    {
        var qpos = MujocoNative.ReadQpos(_model, _data);
        var qvel = MujocoNative.ReadQvel(_model, _data);
        foreach (var (r, p, v) in new[] { (_context.Us, 0, 0), (_context.Them, 11, 10) })
        {
            var pose = PoseAt(qpos, p);
            r.X = pose.X;
            r.Y = pose.Y;
            r.Th = Yaw(pose);
            r.Vx = qvel[v];
            r.Vy = qvel[v + 1];
            r.Omega = qvel[v + 5];
            r.ZG = pose.Z - MujocoModel.WheelRadius - 0.04;
            r.Roll = Math.Atan2(2 * (pose.Qw * pose.Qx + pose.Qy * pose.Qz),
                1 - 2 * (pose.Qx * pose.Qx + pose.Qy * pose.Qy));
            r.Pitch = Math.Asin(Math.Clamp(2 * (pose.Qw * pose.Qy - pose.Qz * pose.Qx), -1, 1));
            if (!Finite(pose) || !double.IsFinite(r.Vx) || !double.IsFinite(r.Vy) || !double.IsFinite(r.Omega))
            {
                throw new InvalidOperationException("MuJoCo produced a non-finite robot state.");
            }
        }
        for (var i = 0; i < _context.Blocks.Count; i++)
        {
            var b = _context.Blocks[i];
            var p = 22 + i * 7;
            var v = 20 + i * 6;
            var pose = PoseAt(qpos, p);
            b.X = pose.X;
            b.Y = pose.Y;
            b.Vx = qvel[v];
            b.Vy = qvel[v + 1];
            if (!Finite(pose) || !double.IsFinite(b.Vx) || !double.IsFinite(b.Vy))
            {
                throw new InvalidOperationException("MuJoCo produced a non-finite block state.");
            }
        }
    }

    private static PhysicsPose3 PoseAt(double[] qpos, int p) => new()
    {
        X = qpos[p], Y = qpos[p + 1], Z = qpos[p + 2],
        Qw = qpos[p + 3], Qx = qpos[p + 4], Qy = qpos[p + 5], Qz = qpos[p + 6],
    };
    private static double Yaw(PhysicsPose3 p) => Math.Atan2(
        2 * (p.Qw * p.Qz + p.Qx * p.Qy), 1 - 2 * (p.Qy * p.Qy + p.Qz * p.Qz));
    private static bool Finite(PhysicsPose3 p) =>
        double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z)
        && double.IsFinite(p.Qw) && double.IsFinite(p.Qx) && double.IsFinite(p.Qy) && double.IsFinite(p.Qz);

    private void CheckStateShape()
    {
        var nq = MujocoNative.ReadQpos(_model, _data).Length;
        var nv = MujocoNative.ReadQvel(_model, _data).Length;
        if (nq != 43 || nv != 38)
        {
            throw new InvalidOperationException($"MuJoCo model layout mismatch: expected nq=43/nv=38, got nq={nq}/nv={nv}.");
        }
        MujocoNative.WriteCtrl(_model, _data, new double[8]);
    }

    private void RegisterGeoms()
    {
        foreach (var (role, target) in new[] { (RoleNames.Us, _usGeoms), (RoleNames.Them, _themGeoms) })
        {
            target.Add(GeomId($"robot_body_{role}"));
            target.Add(GeomId($"robot_shovel_{role}"));
            foreach (var axle in new[] { "front", "rear" })
            foreach (var side in new[] { "left", "right" })
                target.Add(GeomId($"wheel_geom_{role}_{axle}_{side}"));
        }
        for (var i = 0; i < _context.Blocks.Count; i++)
            _blockGeoms.Add(GeomId($"block_geom_{i}"), _context.Blocks[i]);
    }
    private int GeomId(string name)
    {
        var id = MujocoNative.NameToId(_model, MujocoNative.ObjectGeom, name);
        if (id < 0) throw new InvalidOperationException($"MuJoCo model is missing geom '{name}'.");
        return id;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MujocoPhysicsBackend));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_data != IntPtr.Zero) { MujocoNative.DeleteData(_data); _data = IntPtr.Zero; }
        if (_ownsModel && _model != IntPtr.Zero) { MujocoNative.DeleteModel(_model); _model = IntPtr.Zero; }
    }
}
