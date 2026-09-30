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
    private readonly Dictionary<int, string> _staticTags = [];

    // 09-25 SEARCH 索敌闭环: 原地转向补偿系数。四轮横向滑动摩擦使原地偏航速率
    // 远低于指令(kv=0.25 工程值时代实测仅 ~3%, 该增益当时为登台柔性所设, 09-30 已
    // 按 2342 真值重标, 见 MujocoModel); 对 |CmdV|≤0.02 的
    // 原地转向命令放大差速轮目标速度, 使 kv×Δω 重新触及力上限。纵向行驶、
    // 倒车登台与推块均带纵向命令, 不受影响。实例字段: 候选对照测试
    // 经 MujocoPhysicsBackendFactory 注入自己的值, 不再改进程级状态。
    // 选定 4(候选 2/4/6, dt=0.005: 2 的 90° 对准需 9.45 s 超预算, 6 过冲,
    // 4 → <3 s 且误差 0.032 rad)。2026-09-29 QACC 修复(子步 0.005→0.002)后旧门在
    // 候选 2/4 上均无法满足(4 → 6.6 s/误差 0.543, 2 → 13.45 s/0.578) —— 但该轮
    // 还叠加了时基回归(每 tick 只积分 0.02 s 的慢动作), 上述数字不可用。
    // 2026-09-30 时基修正后(MjcTimestep 单一真值, 25 子步 = 0.05 s/tick)批 1 实测:
    // comp=4 → 发现→classify 2.65 s / 误差 0.427; comp=1(基线) → 14.10 s / 0.579。
    // 2026-09-30 批 2 电机真值标定(kv=0.136873/±1.72 N·m/±12.566 rad/s + duty 口径)后
    // 用同一 RunTurn 逻辑重扫(tmp/timescan turn): comp=1 → 31.75 s/0.591,
    // 2 → 17.00/0.583, 4 → 7.55/0.541, 6 → 3.40/0.516, 8 → 2.05/0.434 —— 补偿单调
    // 有效但整体变慢: 真车电机扭矩上限 −43%、极速 12.566 rad/s, 原地转向可达偏航率
    // 下降。选定值变更(或旧硬门重立)属 FSM 重校范围, 本批维持 4, 数据已留档。
    internal const double DefaultInPlaceTurnCompensation = 4.0;
    private readonly double _inPlaceTurnCompensation;
    private readonly MotorDriveOptions _motorOptions;
    // 倾覆判定的阈值: 车体 up 轴与世界 Z 的点积。0.5 = 倾角 60°; 实测正常行驶
    // |roll|<20°、撞坡瞬态 |pitch|<=37°, 而翻覆态点积约 -1, 两侧余量都很大。
    internal const double FlippedUprightThreshold = 0.5;
    private readonly Dictionary<string, double> _upright = [];
    private IntPtr _model;
    private bool _ownsModel = true;
    private IntPtr _data;
    private bool _disposed;

    public string BackendId => PhysicsSpec.Mujoco;
    public string? EngineVersion => MujocoNative.ExpectedVersion;
    public string? ModelSha256 { get; }

    internal MujocoPhysicsBackend(PhysicsBackendContext context,
        double inPlaceTurnCompensation = DefaultInPlaceTurnCompensation,
        MotorDriveOptions motorOptions = default)
    {
        _context = context;
        _inPlaceTurnCompensation = inPlaceTurnCompensation;
        _motorOptions = motorOptions;
        Validate(context);
        var (xml, assets, hash) = MujocoModel.Generate(context);
        ModelSha256 = hash;
        _ownsModel = true;
        try
        {
            _model = MujocoNative.CreateModel(xml, assets);
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
    internal MujocoPhysicsBackend(PhysicsBackendContext context, IntPtr externalModel,
        string modelSha256,
        double inPlaceTurnCompensation = DefaultInPlaceTurnCompensation,
        MotorDriveOptions motorOptions = default)
    {
        _context = context;
        _inPlaceTurnCompensation = inPlaceTurnCompensation;
        _motorOptions = motorOptions;
        Validate(context);
        ModelSha256 = modelSha256;
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
        if (Math.Abs(context.Scenario.Field.TickSeconds - MujocoModel.TickSeconds) > 1e-12)
        {
            throw new ArgumentException(
                $"MuJoCo model requires field.tickSeconds={MujocoModel.TickSeconds}.", nameof(context));
        }
        if (context.Blocks.Count != 3)
        {
            throw new ArgumentException("MuJoCo model requires exactly three energy blocks.", nameof(context));
        }
    }

    public bool Step(double dt)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(dt) || Math.Abs(dt - MujocoModel.TickSeconds) > 1e-12)
        {
            throw new ArgumentException(
                $"MuJoCo model advances exactly one {MujocoModel.TickSeconds} s referee tick.", nameof(dt));
        }
        var controls = new double[8];
        SetControls(_context.Us, 0, controls);
        SetControls(_context.Them, 4, controls);
        MujocoNative.WriteCtrl(_model, _data, controls);
        var robotsTouched = false;
        // 子步数 × 子步长 = 一个裁判 tick(MujocoModel.SubstepsPerTick=25, 0.002 s/步,
        // MujocoTimebaseTests 钉住 25×0.002 == 0.05)。09-29 曾出现 SubstepsPerTick
        // 未随 MjcTimestep 同步、每 tick 只积分 0.02 s 的慢动作回归。
        for (var i = 0; i < MujocoModel.SubstepsPerTick; i++)
        {
            MujocoNative.Step(_model, _data);
            // 接触时刻 = 该子步结束时的**累计物理时间**(0.002 … 0.05), 与真实推进
            // 量一致; 只比较同一 tick 内 max 接触时刻是否相等(Physics.FinalizeBlockContacts),
            // 因此 10 点(旧 0.005 网格) → 25 点(0.002 网格) 不改变归属语义。
            var contactTime = (i + 1) * MujocoModel.MjcTimestep;
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
            cmdW *= _inPlaceTurnCompensation;
        }
        var halfTrack = robot.Vehicle.TrackWidth / 2;
        // 差速合成(符号沿用旧口径): A +Y wheel angular velocity rolls its centre
        // toward local +X —— 左轮 = cmdV − cmdW×halfTrack, 右轮 = cmdV + cmdW×halfTrack。
        // duty 口径(2026-09-30 电机真值标定, design 决策④): 差速项 cmdW×halfTrack
        // 先折成轮面线速度, 再与 cmdV 同分母(MaxSpeed)、同截断(|duty|≤1)合成 ——
        // duty 是"每轮各自的开环占空比"。**必须按轮计算**: 若写成单标量
        // |cmdV|/MaxSpeed, 纯原地转向(cmdV=0)的 duty 会归零, SEARCH 原地转向点
        // (DriveToward(r,pos,0,2.0)/RotateTo)与 DefaultInPlaceTurnCompensation
        // 整体失效。MaxSpeed 已由 VehicleNormalizer 归一到 0.05..3, 非零。
        // duty→ctrl: ctrl = duty×ω_noload 等价可调压直流电机 —— 空载转速 =
        // duty×ω_noload、ω=0 起步扭矩 = duty×τ_stall(kv=τ_stall/ω_noload 的数学等价,
        // 见 MujocoModel 电机常量推导)。负 duty 即反向驱动。
        var maxSpeed = robot.Vehicle.MaxSpeed;
        var dutyLeft = Math.Clamp((cmdV - cmdW * halfTrack) / maxSpeed, -1.0, 1.0);
        var dutyRight = Math.Clamp((cmdV + cmdW * halfTrack) / maxSpeed, -1.0, 1.0);
        // 电池压降默认禁用(VoltageScale 恒 1, 逐位无影响); 启用需实测标定, 见 MotorDriveOptions。
        controls[offset] = dutyLeft * MujocoModel.MjcNoLoadSpeed * _motorOptions.VoltageScale(dutyLeft);
        controls[offset + 1] = dutyRight * MujocoModel.MjcNoLoadSpeed * _motorOptions.VoltageScale(dutyRight);
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

    /// <summary>车体局部 Z 轴在世界 Z 上的分量: 1=直立, 0=侧躺, -1=底朝天。</summary>
    private static double UprightOf(PhysicsPose3 pose) => 1 - 2 * (pose.Qx * pose.Qx + pose.Qy * pose.Qy);
    public bool IsFlipped(RobotRuntime robot)
        => _upright.TryGetValue(robot.Role, out var upright) && IsFlippedAt(upright);

    /// <summary>纯判定(可单测): 车体 up 分量低于阈值即视为倾覆。</summary>
    internal static bool IsFlippedAt(double upright) => upright < FlippedUprightThreshold;

    // ---------- 传感器探测 (R3 3D 化) ----------

    /// <summary>平面查询(通道未配 Height)退化到与 legacy 相同的解析实现 —— 逐位兼容;
    /// 3D 查询用 mj_ray 打真实碰撞 geom(传感器所见 = 物理所碰)。</summary>
    public SensorProbe? ProbeRay(ProbeQuery query)
    {
        ThrowIfDisposed();
        if (query.Planar)
        {
            return PlanarSensors.IrProbe(_context.Field, query.Robot, OpponentRole(query.Robot), _context.Blocks,
                query.X, query.Y, query.Angle, query.HalfFov, query.Range,
                query.IncludeEdge, query.IncludeFence);
        }
        return ProbeRay3D(query);
    }

    /// <summary>地面类: 平面查询只看 XY(逐位兼容); 3D 查询附加光轴朝向 + 高度差
    /// 量程判定 —— 半悬/翘头/翻覆不再凭"XY 落在台内"误报在台面。</summary>
    public double ProbeGround(GroundQuery query)
    {
        ThrowIfDisposed();
        var field = _context.Field;
        if (!query.Planar && query.DownZ > GroundFacingLimit)
        {
            return 0; // 传感器光轴偏离朝下超过 60°(翻覆朝天/侧躺): 读不到地面
        }
        if (!query.Planar)
        {
            var diff = query.Z - field.StageHeightAt(query.X, query.Y);
            if (diff < -HeightTolerance || diff > query.MaxHeight)
            {
                return 0; // 探点低于承载面(倒扣压进台面)或离面超量程(半悬/翘头)
            }
        }
        return query.SpotRadius > 0
            ? PlanarSensors.GraySpot(field, query.X, query.Y, query.SpotRadius)
            : (field.OnPlatform(query.X, query.Y) ? 1 : 0);
    }

    /// <summary>光轴朝向门限: DownZ = -cos(倾角), 倾角超过 60° 视为读不到地面。</summary>
    private const double GroundFacingLimit = -0.5;

    /// <summary>高度差判定允许探点略低于承载面的数值容差(m)。</summary>
    private const double HeightTolerance = 0.002;

    /// <summary>mj_ray 命中自身 geom 后的外推步长(m): 探点在自身 mesh 表面上
    /// (挂点=节点原点恰在底盘顶面), 光束与顶面共面时命中点需以毫米级步长爬出
    /// 共面段(前缘剩余 ≈3.4mm), 步长过小会在穿透上限内耗尽; 步长只影响穿透段
    /// 起点偏移, 命中距离按 advance 精确累加。</summary>
    private const double SelfHitSkip = 0.002;

    /// <summary>自身遮挡穿透上限: chassis + shovel + 轮最多遮少数几层。</summary>
    private const int MaxSelfHits = 4;

    /// <summary>mj_ray 组掩码: 组 0-2 参与(场地/块/车体), 组 3+ 不参与(如配重辅助
    /// geom, 物理不可碰也不应被传感器看到)。与旧 null(全组)在既有场景等效(全部 geom 在组 0)。</summary>
    private static readonly byte[] RayGroups = [1, 1, 1, 0, 0, 0];

    private SensorProbe? ProbeRay3D(ProbeQuery query)
    {
        // 已知边界(记录备查): 传感器采样在 Tick 内 Step 之前执行, _data 自上个
        // tick 末子步 integrate 后未再 mj_forward, geom_xpos 滞后一个子步
        // (v×2ms(子步 0.002s), 1.5m/s 车速下 ≈3mm), 命中距离存在同量级系统偏移 —— 与
        // irNoise 0.02~0.05 的噪声幅值同量级, 且按 spec"传感器在物理步进前采样
        // (读上一提交帧)"的既有口径一致, 不为 ray 路径单独 mj_forward(吞吐)。
        var beam = BeamDirection(query);
        var normal = new double[3];
        var geomId = new int[1];
        var selfGeoms = query.Robot.IsUs ? _usGeoms : _themGeoms;
        var advance = 0.0;
        for (var attempt = 0; attempt <= MaxSelfHits; attempt++)
        {
            var pnt = new[]
            {
                query.X + beam[0] * advance,
                query.Y + beam[1] * advance,
                query.Z + beam[2] * advance,
            };
            var t = MujocoNative.Ray(_model, _data, pnt, beam, RayGroups, 1, -1, geomId, normal);
            if (t < 0)
            {
                return null; // 未命中
            }
            var distance = advance + t;
            if (distance > query.Range)
            {
                return null;
            }
            if (!selfGeoms.Contains(geomId[0]))
            {
                return ClassifyRayHit(query, beam, geomId[0], distance, normal);
            }
            advance = distance + SelfHitSkip;
        }
        return null; // 光束被自身几何连续遮挡
    }

    /// <summary>光束世界系单位方向。ProbeQuery.Angle 是**世界系**水平方位角
    /// (SensorSampler 传 SensorAngle = r.Th + ch.Angle, 与平面分支 PlanarSensors
    /// 直接消费同一值) —— 本地方位 = Angle − 车体航向, 姿态修正
    /// Rz(yaw)·Ry(pitch)·Rx(roll) 作用于本地向量。roll=pitch=0 时退化为
    /// (cos Angle, sin Angle, 0), 与平面分支逐位一致; 倾斜姿态下光束随车体
    /// 翻转。(曾把世界系 Angle 当本地角再转一次 yaw, 造成方位偏差恒等于航向。)</summary>
    internal static double[] BeamDirection(ProbeQuery query)
    {
        var th = query.Robot.Th;
        var cr = Math.Cos(query.Robot.Roll);
        var sr = Math.Sin(query.Robot.Roll);
        var cp = Math.Cos(query.Robot.Pitch);
        var sp = Math.Sin(query.Robot.Pitch);
        var cb = Math.Cos(th);
        var sb = Math.Sin(th);
        var localAzimuth = query.Angle - th;
        var localX = Math.Cos(query.Elevation) * Math.Cos(localAzimuth);
        var localY = Math.Cos(query.Elevation) * Math.Sin(localAzimuth);
        var localZ = Math.Sin(query.Elevation);
        var ry = cr * localY - sr * localZ;
        var rz = sr * localY + cr * localZ;
        var rx = cp * localX + sp * rz;
        rz = -sp * localX + cp * rz;
        return [cb * rx - sb * ry, sb * rx + cb * ry, rz];
    }

    private SensorProbe? ClassifyRayHit(ProbeQuery query, double[] beam, int geomId, double distance, double[] normal)
    {
        // 命中面法线衰减: mj_ray 的 normal 朝向射线来向 → 入射余弦 = |beam·normal|。
        var atten = Math.Abs(beam[0] * normal[0] + beam[1] * normal[1] + beam[2] * normal[2]);
        if (_blockGeoms.TryGetValue(geomId, out var block))
        {
            var z = _context.Field.StageHeightAt(block.X, block.Y) + _context.Scenario.Field.BlockSize / 2;
            if (!InTargetFov(query, beam, block.X, block.Y, z, block.R))
            {
                return null; // 目标中心偏离光束轴超过半视场
            }
            return new SensorProbe { D = distance, Obj = block, Atten = atten };
        }
        var opponent = OpponentGeom(query, geomId);
        if (opponent is not null)
        {
            if (!InTargetFov(query, beam, opponent.X, opponent.Y, opponent.ZG, opponent.R))
            {
                return null;
            }
            return new SensorProbe { D = distance, Obj = opponent, Atten = atten };
        }
        if (_staticTags.TryGetValue(geomId, out var tag))
        {
            // 静态面(台壁/台沿倒角/围栏/走道)不是点目标, 不做 FOV 过滤。
            return new SensorProbe { D = distance, Obj = tag, Atten = atten };
        }
        return new SensorProbe { D = distance, Obj = null, Atten = atten };
    }

    /// <summary>目标(块/对手车)中心偏离光束轴的角度过滤, 口径与平面语义一致。</summary>
    private static bool InTargetFov(ProbeQuery query, double[] beam, double ox, double oy, double oz, double radius)
    {
        var dx = ox - query.X;
        var dy = oy - query.Y;
        var dz = oz - query.Z;
        var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d < 1e-9)
        {
            return true;
        }
        var cos = Math.Clamp((dx * beam[0] + dy * beam[1] + dz * beam[2]) / d, -1, 1);
        var angle = Math.Acos(cos);
        return Math.Abs(angle) <= query.HalfFov + Math.Asin(Math.Min(1, radius / Math.Max(0.05, d)));
    }

    private RobotRuntime OpponentRole(RobotRuntime robot) => robot.IsUs ? _context.Them : _context.Us;

    private RobotRuntime? OpponentGeom(ProbeQuery query, int geomId)
        => query.Robot.IsUs
            ? (_themGeoms.Contains(geomId) ? _context.Them : null)
            : (_usGeoms.Contains(geomId) ? _context.Us : null);

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
        qpos[p + 2] = _context.Field.StageHeightAt(robot.X, robot.Y)
            + MujocoModel.RadiusFor(_context) + MujocoModel.ResetAxleOffset(_context);
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
        _upright[robot.Role] = 1;   // 复位姿态是直立的
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
            r.ZG = pose.Z - MujocoModel.RadiusFor(_context) - MujocoModel.ResetAxleOffset(_context);
            r.Roll = Math.Atan2(2 * (pose.Qw * pose.Qx + pose.Qy * pose.Qz),
                1 - 2 * (pose.Qx * pose.Qx + pose.Qy * pose.Qy));
            r.Pitch = Math.Asin(Math.Clamp(2 * (pose.Qw * pose.Qy - pose.Qz * pose.Qx), -1, 1));
            _upright[r.Role] = UprightOf(pose);
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
        // v2 的车体是两个 mesh geom(chassis + rear_shovel), 轮 geom 名两版一致。
        var bodyGeoms = MujocoModel.IsV2(_context)
            ? new[] { "robot_chassis_{0}", "robot_shovel_{0}" }
            : ["robot_body_{0}", "robot_shovel_{0}"];
        foreach (var (role, target) in new[] { (RoleNames.Us, _usGeoms), (RoleNames.Them, _themGeoms) })
        {
            foreach (var template in bodyGeoms) target.Add(GeomId(string.Format(template, role)));
            foreach (var axle in new[] { "front", "rear" })
            foreach (var side in new[] { "left", "right" })
                target.Add(GeomId($"wheel_geom_{role}_{axle}_{side}"));
        }
        for (var i = 0; i < _context.Blocks.Count; i++)
            _blockGeoms.Add(GeomId($"block_geom_{i}"), _context.Blocks[i]);
        // 传感器 ray 命中的静态面归类(legacy SensorProbe 的 string tag 口径):
        // 走道地面 vs 台壁/台沿倒角/围栏。
        _staticTags[GeomId("ground")] = "地面";
        foreach (var name in new[] { "platform", "chamfer_s", "chamfer_n", "chamfer_w", "chamfer_e",
                     "fence_s", "fence_n", "fence_w", "fence_e" })
        {
            _staticTags[GeomId(name)] = "台壁";
        }
    }
    private int GeomId(string name)
    {
        var id = MujocoNative.NameToId(_model, MujocoNative.ObjectGeom, name);
        if (id < 0) throw new InvalidOperationException($"MuJoCo model is missing geom '{name}'.");
        return id;
    }

    /// <summary>测试专用(Sim.Tests 经 InternalsVisibleTo): 探测类测试需要直接
    /// 注入 qpos 后再调 ProbeRay/ProbeGround; 只读访问, 不改变生命周期归属。</summary>
    internal IntPtr ExposedModel => _model;
    internal IntPtr ExposedData => _data;

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
