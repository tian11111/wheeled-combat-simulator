using Sim.Core;
using Sim.Hosting;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// R3 传感器 3D 化: MuJoCo 后端用 mj_ray 打真实碰撞 geom(传感器所见 = 物理所碰),
/// 地面类读数随姿态变化(半悬/翘头/翻覆不再凭"XY 落在台内"误报在台面);
/// legacy 平面语义(通道未配 Height / legacy 后端)逐位退化。
/// 几何断言口径: 全部构造**编译后 mjModel + 精确 qpos** 再探测, 距离期望值
/// 来自轴对齐 box/mesh 的已知几何。
/// </summary>
public class Sensor3DTests
{
    private const double WheelRadiusV2 = 0.0325;
    private const double PlatformHeight = 0.06;
    private const double BlockHalf = 0.075;
    private const double Tolerance = 2e-3;

    /// <summary>与 MujocoVehicleMeshTests 同口径组装 v2 场景 context。</summary>
    private static PhysicsBackendContext V2Context()
    {
        var scenario = new Scenario
        {
            Id = "wushu-ring-2026",
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
            Field = FieldParams.Default,
            Blocks = OfficialLayout.Blocks,
        };
        var field = new FieldModel(scenario.Field);
        var us = new RobotRuntime
        {
            Role = RoleNames.Us, Name = "我方",
            X = 1.9, Y = 1.9, Th = 0,
            Vehicle = VehicleNormalizer.Normalize(new VehicleProfile
            {
                Id = "glb-2026", Length = 0.2, Width = 0.24, Height = 0.034,
                FrontExtent = 0.103, RearExtent = 0.16949, SideExtent = 0.12185,
                ShovelLength = 0.003, ShovelWidth = 0.19, Mass = 1.0,
                WheelBase = 0.15, TrackWidth = 0.229,
            }),
        };
        var them = new RobotRuntime
        {
            Role = RoleNames.Them, Name = "对手",
            X = 2.85, Y = 3.5, Th = Math.PI,
            Vehicle = us.Vehicle,
        };
        us.R = us.Vehicle.CollisionRadius;
        them.R = them.Vehicle.CollisionRadius;
        var blocks = OfficialLayout.Blocks
            .Select(b => new BlockRuntime { Kind = b.Kind, X = b.X ?? 0, Y = b.Y ?? 0 })
            .ToList();
        return new PhysicsBackendContext(scenario, field, SimParameters.FromDictionary(scenario.Parameters),
            us, them, blocks, new EventBus(), AntiStallPhaseUs: 0, AntiStallPhaseThem: 0);
    }

    private static double RayProbe(MujocoPhysicsBackend backend, double px, double py, double pz,
        double dx, double dy, double dz, double range, out SensorProbe? probe)
    {
        probe = backend.ProbeRay(new ProbeQuery
        {
            Robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方", Th = 0 },
            X = px, Y = py, Z = pz,
            Angle = 0,
            HalfFov = 0.55,
            Range = range,
            Planar = false,
        });
        return probe?.D ?? -1;
    }

    [Fact]
    public void RayProbe_HitsBlockFaceAtExactGeometricDistance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = V2Context();
        using var backend = new MujocoPhysicsBackend(context);
        // us 抬到台面中心直立; 块 0 放在探点正前方 +x, 台上。生产路径里块/对手的
        // runtime 位置由 CopyStateToRuntime 每 tick 同步; 测试直接注入 qpos 时必须
        // 同步 runtime, FOV 检查消费的是 runtime 坐标。
        var model = backend.ExposedModel;
        var data = backend.ExposedData;
        var qpos = MujocoNative.ReadQpos(model, data);
        qpos[0] = 1.9; qpos[1] = 1.9; qpos[2] = PlatformHeight + WheelRadiusV2;
        qpos[3] = 1; qpos[4] = 0; qpos[5] = 0; qpos[6] = 0;
        var blockX = 2.5;
        qpos[22] = blockX; qpos[23] = 1.955462; qpos[24] = PlatformHeight + BlockHalf;
        qpos[25] = 1; qpos[26] = 0; qpos[27] = 0; qpos[28] = 0;
        MujocoNative.WriteQpos(model, data, qpos);
        MujocoNative.Forward(model, data);
        context.Blocks[0].X = blockX;
        context.Blocks[0].Y = 1.955462;

        // 探点 = 左前对角 IR 实测挂点的世界坐标 (车体系 0.099598, 0.055462),
        // 高度取底盘顶面上方 1.7mm: 真实挂点节点恰在顶面上, 静止时光束与顶面
        // 共面是已知数值边界(运动微倾即脱离); 测试取非共面高度验证几何口径。
        var probeX = 1.9 + 0.099598;
        var probeY = 1.9 + 0.055462;
        var probeZ = PlatformHeight + WheelRadiusV2 + 0.02575 + 0.0017;
        var d = RayProbe(backend, probeX, probeY, probeZ, 1, 0, 0, 1.6, out var probe);
        Assert.True(d > 0, "expected a block hit");
        Assert.Equal(blockX - BlockHalf - probeX, d, Tolerance);
        Assert.NotNull(probe!.Obj);
        Assert.IsType<BlockRuntime>(probe.Obj);
        // 正对块面 → 法线衰减 ≈ 1。
        Assert.Equal(1.0, probe.Atten ?? -1, 3);

        // 未命中方向返回 null: yaw=90° 车头方位(世界 +y)上无目标, 量程内无静态面。
        // (yaw 双算修复后本地方位 = Angle − yaw; 该用例同时锁定方向语义 ——
        // 修复前 Angle=0 被当本地角, 光束歪到世界 +y 反而 miss。)
        var miss = backend.ProbeRay(new ProbeQuery
        {
            Robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方", Th = Math.PI / 2 },
            X = probeX, Y = probeY, Z = probeZ,
            Angle = Math.PI / 2, HalfFov = 0.55, Range = 1.6, Planar = false,
        });
        Assert.Null(miss);
    }

    [Fact]
    public void RayProbe_HitsOpponentMesh_WithFaceNormalAttenuation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = V2Context();
        using var backend = new MujocoPhysicsBackend(context);
        var model = backend.ExposedModel;
        var data = backend.ExposedData;
        var qpos = MujocoNative.ReadQpos(model, data);
        // us 在台中心, 对手在正前方 (车头朝 +x, 车尾正对 us), 台上直立。
        qpos[0] = 1.9; qpos[1] = 1.9; qpos[2] = PlatformHeight + WheelRadiusV2;
        qpos[3] = 1; qpos[4] = 0; qpos[5] = 0; qpos[6] = 0;
        qpos[11] = 2.5; qpos[12] = 1.955462; qpos[13] = PlatformHeight + WheelRadiusV2;
        qpos[14] = 1; qpos[15] = 0; qpos[16] = 0; qpos[17] = 0;
        MujocoNative.WriteQpos(model, data, qpos);
        MujocoNative.Forward(model, data);
        context.Them.X = 2.5;
        context.Them.Y = 1.955462;
        context.Them.ZG = PlatformHeight;

        // 探点取 us 车头前缘外 (世界 x=2.01 > 前伸 0.103), 高度在对手底盘带内
        // (z=0.10 ∈ 对手 chassis 世界 [0.084, 0.118]), 命中对手尾部 mesh。
        var probeX = 2.01; var probeY = 1.955462; var probeZ = 0.10;
        var d = RayProbe(backend, probeX, probeY, probeZ, 1, 0, 0, 1.6, out var probe);
        Assert.True(d > 0, "expected an opponent hit");
        Assert.IsType<RobotRuntime>(probe!.Obj);
        Assert.Equal(RoleNames.Them, ((RobotRuntime)probe.Obj).Role);
        // 命中距离 ∈ 对手尾缘(-0.16949 含尾铲)与底盘后缘(-0.117)之间 (mesh 形状容差)。
        Assert.InRange(d, 2.5 - 0.16949 - probeX - 0.02, 2.5 - 0.117 - probeX + 0.02);
        // 命中面法线衰减生效: 尾面与光束近正交 → 接近 1, 且字段确实被填充。
        Assert.True(probe.Atten is { } atten && atten > 0.7, $"atten={probe.Atten}");

        // 俯角打块顶面 → 命中法线 (0,0,1) → 衰减 = |beam·n| = cos(45°)。
        var qposBlocks = MujocoNative.ReadQpos(model, data);
        qposBlocks[22] = 2.5; qposBlocks[23] = 1.9; qposBlocks[24] = PlatformHeight + BlockHalf;
        MujocoNative.WriteQpos(model, data, qposBlocks);
        MujocoNative.Forward(model, data);
        context.Blocks[0].X = 2.5;
        context.Blocks[0].Y = 1.9;
        // 光束 (0, -sin45, -cos45): 从块斜上方 45° 打顶面, 距离 = 0.2·√2。
        var oblique = backend.ProbeRay(new ProbeQuery
        {
            Robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方" },
            X = 2.5, Y = 1.9 + 0.2, Z = PlatformHeight + BlockHalf * 2 + 0.2,
            Angle = -Math.PI / 2, HalfFov = 0.55, Range = 1.6, Planar = false,
            Elevation = -Math.PI / 4,
        });
        Assert.NotNull(oblique);
        Assert.IsType<BlockRuntime>(oblique!.Obj);
        Assert.Equal(Math.Cos(Math.PI / 4), oblique.Atten ?? -1, 2);
    }

    [Fact]
    public void RayProbe_FromWalkway_SeesThePlatformWall()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var backend = new MujocoPhysicsBackend(V2Context());
        // us 摆在走道 (x=0.4), 探点高度低于台沿 → 水平光束先命中台沿 20° 倒角斜面。
        var probeX = 0.4 + 0.099598;
        var probeY = 1.955462;
        var probeZ = WheelRadiusV2 + 0.02575;
        var d = RayProbe(backend, probeX, probeY, probeZ, 1, 0, 0, 0.9, out var probe);
        Assert.True(d > 0, "expected a platform hit from the walkway");
        Assert.Equal("台壁", probe!.Obj);
        // 倒角斜面顶从 (0.7, 0.06) 斜到 (0.7-0.1648, 0): 光束高度处的命中 x 在斜面段。
        Assert.InRange(probeX + d, 0.535, 0.7);
    }

    [Fact]
    public void GroundProbe_ReadingChangesWithPose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var backend = new MujocoPhysicsBackend(V2Context());
        // 探点在台面投影内, 用探点世界 Z 表征姿态。站立时探点世界 Z = ZG(=台面高)
        // + Height(离地高): 灰度贴地 diff=0.0025; 20° 倒角爬坡 diff≈0.0229 必须放行
        // (登台信号); 半悬翘头把探点抬高到 diff>量程(0.03) → 拦截。
        var groundQuery = new Func<double, double, GroundQuery>((z, downZ) => new GroundQuery
        {
            X = 1.9, Y = 1.9, Z = z,
            Planar = false,
            SpotRadius = 0.025,
            MaxHeight = 0.03,
            DownZ = downZ,
        });
        var onGround = backend.ProbeGround(groundQuery(PlatformHeight + 0.0025, -1));
        var climbingChamfer = backend.ProbeGround(groundQuery(PlatformHeight + 0.0229, -1));
        var lifted = backend.ProbeGround(groundQuery(PlatformHeight + 0.04, -1));
        Assert.True(onGround > 0, "ground-level probe must read the platform gray field");
        Assert.True(climbingChamfer > 0, "20° chamfer climb (diff=0.0229) must still read the platform");
        Assert.Equal(0, lifted); // 半悬: 探点离面超量程 → 不再报台面

        // 翻覆(倒扣): 传感器光轴朝上 → 读数失效。
        Assert.Equal(0, backend.ProbeGround(groundQuery(PlatformHeight + 0.0025, 1)));

        // 铲下 IR 语义 (SpotRadius=0 → 台面反射 0/1): 离地 41mm=1, 半悬=0。
        var underShovel = new Func<double, GroundQuery>(z => new GroundQuery
        {
            X = 1.9, Y = 1.9, Z = z,
            Planar = false,
            MaxHeight = 0.06,
            DownZ = -1,
        });
        Assert.Equal(1, backend.ProbeGround(underShovel(PlatformHeight + 0.04125)));
        Assert.Equal(0, backend.ProbeGround(underShovel(PlatformHeight + 0.07)));

        // 平面语义退化: 同一半悬查询标 Planar 后忽略 Z/朝向, 与 2D 时代逐位一致。
        var planar = new GroundQuery
        {
            X = 1.9, Y = 1.9, Z = PlatformHeight + 0.04,
            Planar = true, SpotRadius = 0.025, MaxHeight = 0.03, DownZ = 1,
        };
        Assert.True(backend.ProbeGround(planar) > 0, "planar query must ignore height/facing");
    }

    /// <summary>回归: 3D 光束方向不得把世界系方位角再旋转一次车体航向(yaw 双算)。
    /// ProbeQuery.Angle 与平面分支同语义(世界系), 本地方位 = Angle − yaw。</summary>
    [Fact]
    public void BeamDirection_WorldAzimuthMatchesPlanarSemantics()
    {
        if (!OperatingSystem.IsWindows()) return;
        // yaw=90°, 通道本地角 +45° → 世界方位 135°: beam = (-√2/2, +√2/2, 0)。
        var beam = MujocoPhysicsBackend.BeamDirection(new ProbeQuery
        {
            Robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方", Th = Math.PI / 2 },
            X = 0, Y = 0, Z = 0,
            Angle = Math.PI / 2 + Math.PI / 4,
            HalfFov = 0.55, Range = 1.6, Planar = false,
        });
        Assert.Equal(-Math.Cos(Math.PI / 4), beam[0], 12);
        Assert.Equal(Math.Sin(Math.PI / 4), beam[1], 12);
        Assert.Equal(0, beam[2], 12);

        // yaw=180°, 通道本地角 +45° → 世界方位 225°。
        var beam2 = MujocoPhysicsBackend.BeamDirection(new ProbeQuery
        {
            Robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方", Th = Math.PI },
            X = 0, Y = 0, Z = 0,
            Angle = Math.PI + Math.PI / 4,
            HalfFov = 0.55, Range = 1.6, Planar = false,
        });
        Assert.Equal(-Math.Cos(Math.PI / 4), beam2[0], 12);
        Assert.Equal(-Math.Sin(Math.PI / 4), beam2[1], 12);

        // 直立(roll=pitch=0)任意航向下 3D 光束必须与平面语义逐位一致:
        // beam = (cos Angle, sin Angle, 0)。
        var robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方", Th = 1.234 };
        var beam3 = MujocoPhysicsBackend.BeamDirection(new ProbeQuery
        {
            Robot = robot,
            X = 0, Y = 0, Z = 0,
            Angle = robot.Th + Math.PI / 4,
            HalfFov = 0.55, Range = 1.6, Planar = false,
        });
        Assert.Equal(Math.Cos(robot.Th + Math.PI / 4), beam3[0], 15);
        Assert.Equal(Math.Sin(robot.Th + Math.PI / 4), beam3[1], 15);
        Assert.Equal(0, beam3[2], 15);

        // 姿态倾斜: pitch=90°(车头朝下)时本地 +x 光束 → 世界 (0,0,-1)。
        var beam4 = MujocoPhysicsBackend.BeamDirection(new ProbeQuery
        {
            Robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方", Th = 0, Pitch = Math.PI / 2 },
            X = 0, Y = 0, Z = 0,
            Angle = 0,
            HalfFov = 0.55, Range = 1.6, Planar = false,
        });
        Assert.Equal(0, beam4[0], 12);
        Assert.Equal(0, beam4[1], 12);
        Assert.Equal(-1, beam4[2], 12);
    }

    /// <summary>端到端 yaw≠0 回归: yaw=90° 车的 +45° 通道光束必须沿世界方位 135°
    /// 命中放在该方位上的块 (yaw 双算时该光束朝 225°, 测试失败)。</summary>
    [Fact]
    public void RayProbe_HonoursHeadingForNonZeroYaw()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = V2Context();
        using var backend = new MujocoPhysicsBackend(context);
        var model = backend.ExposedModel;
        var data = backend.ExposedData;
        var qpos = MujocoNative.ReadQpos(model, data);
        // us yaw=90°: 探点 = 原点 + Rz(90°)·(0.099598, 0.055462) = (1.8445, 1.9996),
        // 高度取底盘顶面上方(非共面)。
        qpos[0] = 1.9; qpos[1] = 1.9; qpos[2] = PlatformHeight + WheelRadiusV2;
        qpos[3] = Math.Cos(Math.PI / 4); qpos[4] = 0; qpos[5] = 0; qpos[6] = Math.Sin(Math.PI / 4);
        var blockCentre = (X: 1.4905, Y: 2.3536); // 探点沿世界方位 135° 前方 0.5
        qpos[22] = blockCentre.X; qpos[23] = blockCentre.Y; qpos[24] = PlatformHeight + BlockHalf;
        MujocoNative.WriteQpos(model, data, qpos);
        MujocoNative.Forward(model, data);
        context.Blocks[0].X = blockCentre.X;
        context.Blocks[0].Y = blockCentre.Y;

        var probeX = 1.9 - 0.055462;
        var probeY = 1.9 + 0.099598;
        var probeZ = PlatformHeight + WheelRadiusV2 + 0.02575 + 0.0017;
        var probe = backend.ProbeRay(new ProbeQuery
        {
            Robot = new RobotRuntime { Role = RoleNames.Us, Name = "我方", Th = Math.PI / 2 },
            X = probeX, Y = probeY, Z = probeZ,
            Angle = Math.PI / 2 + Math.PI / 4,
            HalfFov = 0.55, Range = 1.6, Planar = false,
        });
        Assert.NotNull(probe);
        Assert.IsType<BlockRuntime>(probe!.Obj);
        // 45° 斜打轴对齐块: D = 中心距 − box 沿光束支撑 (0.075·√2)。
        Assert.Equal(0.5 - BlockHalf * Math.Sqrt(2), probe.D, Tolerance);
    }

    /// <summary>探点姿态换算矩阵的直接断言 (SensorPoint 的 Rz·Ry·Rx 组合与 ZG 锚定,
    /// 含 DownZ) —— 半悬/翻覆读数测试依赖此矩阵, yaw 双算正是同类盲区。</summary>
    [Fact]
    public void SensorPoint_AppliesFullBodyAttitude()
    {
        const double forward = 0.099598;
        const double lateral = 0.055462;
        const double height = 0.05825;
        var channel = new SensorChannel { Id = "diag", Forward = forward, Lateral = lateral, Height = height };

        // 直立: XY 与平面公式逐位一致, Z = ZG + Height。
        var upright = new RobotRuntime { Role = RoleNames.Us, Name = "我方", X = 2, Y = 3, Th = 0.7, ZG = 0.06 };
        var p = SensorSampler.SensorPoint(upright, channel);
        Assert.Equal(2 + Math.Cos(0.7) * forward - Math.Sin(0.7) * lateral, p.X, 15);
        Assert.Equal(3 + Math.Sin(0.7) * forward + Math.Cos(0.7) * lateral, p.Y, 15);
        Assert.Equal(0.06 + height, p.Z, 15);
        Assert.Equal(-1, SensorSampler.SensorDownZ(upright), 15);

        // 倒扣(roll=180°): 本地 (F,L,H) → (F,−L,−H), Z = ZG − Height, 光轴朝上。
        var flipped = new RobotRuntime { Role = RoleNames.Us, Name = "我方", X = 2, Y = 3, Th = 0, ZG = 0.06, Roll = Math.PI };
        var pf = SensorSampler.SensorPoint(flipped, channel);
        Assert.Equal(2 + forward, pf.X, 12);
        Assert.Equal(3 - lateral, pf.Y, 12);
        Assert.Equal(0.06 - height, pf.Z, 12);
        Assert.Equal(1, SensorSampler.SensorDownZ(flipped), 12);

        // pitch=+30°(ZYX 语义下车头向下): 前向探点 Z 下降; 侧倾(roll)把 lateral
        // 耦合进 Z。符号由同一 ZYX 分解闭环(与 MatchEngine 拷贝的 MuJoCo 欧拉一致)。
        var pitched = new RobotRuntime { Role = RoleNames.Us, Name = "我方", X = 2, Y = 3, Th = 0, ZG = 0.06, Pitch = Math.PI / 6 };
        var pp = SensorSampler.SensorPoint(pitched, channel);
        Assert.Equal(0.06 - Math.Sin(Math.PI / 6) * forward + Math.Cos(Math.PI / 6) * height, pp.Z, 12);

        // Height=null → 平面语义 (Z=0, 无姿态换算)。
        var planar = new RobotRuntime { Role = RoleNames.Us, Name = "我方", X = 2, Y = 3, Th = 0.7, ZG = 0.06, Roll = Math.PI };
        var p0 = SensorSampler.SensorPoint(planar, channel with { Height = null });
        Assert.Equal(0, p0.Z, 15);
        Assert.Equal(2 + Math.Cos(0.7) * forward - Math.Sin(0.7) * lateral, p0.X, 15);
    }

    [Fact]
    public void PlanarPath_StaysBitIdenticalWithLegacyAnalyticModel()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = V2Context();
        using var backend = new MujocoPhysicsBackend(context);
        // 平面查询必须与核心层 PlanarSensors 解析结果逐位一致 (legacy14 的兼容路径)。
        var query = new ProbeQuery
        {
            Robot = context.Us,
            X = 1.9, Y = 1.9, Z = 0,
            Angle = Math.PI / 5,
            HalfFov = 0.55,
            Range = 1.6,
            IncludeEdge = true,
            Planar = true,
        };
        var viaBackend = backend.ProbeRay(query);
        var direct = PlanarSensors.IrProbe(context.Field, context.Us, context.Them, context.Blocks,
            query.X, query.Y, query.Angle, query.HalfFov, query.Range, query.IncludeEdge, query.IncludeFence);
        Assert.Equal(direct?.D ?? -1, viaBackend?.D ?? -1);
        Assert.Same(direct?.Obj, viaBackend?.Obj);
        Assert.Equal(direct?.Atten, viaBackend?.Atten);
    }

    /// <summary>
    /// 端到端: v2 场景 vehicles 显式 wheeledCombat11 (R2) → 11 路 rawSensors 走
    /// 3D 采样管线; snapshot 的 sensorLayout 必须带 wheeledCombat11。
    /// </summary>
    [Fact]
    public void WheeledCombat11_FlowsThroughTheThreeDimensionalPipeline()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scenario = new Scenario
        {
            Id = "wushu-ring-2026",
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
            Field = FieldParams.Default,
            Blocks = OfficialLayout.Blocks,
            Vehicles = new Dictionary<string, VehicleProfile>
            {
                [RoleNames.Us] = new() { Id = "glb-2026", Sensors = SensorProfiles.WheeledCombat11 },
                [RoleNames.Them] = new() { Id = "glb-2026", Sensors = SensorProfiles.WheeledCombat11 },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        var snapshot = engine.CommitSnapshot();
        Assert.Equal("wheeledCombat11", snapshot.SensorLayout![RoleNames.Us].Id);
        var expectedIds = SensorProfiles.WheeledCombat11.Channels.Select(c => c.Id).ToHashSet();
        Assert.Equal(expectedIds, snapshot.RawSensors![RoleNames.Us].Keys.ToHashSet());
        for (var i = 0; i < 20; i++)
        {
            snapshot = engine.Tick(new RobotAction { V = 0.4 }, RobotAction.Zero);
            Assert.All(snapshot.RawSensors![RoleNames.Us], kv => Assert.True(double.IsFinite(kv.Value)));
        }
        Assert.Equal("wheeledCombat11", snapshot.SensorLayout![RoleNames.Us].Id);
    }
}
