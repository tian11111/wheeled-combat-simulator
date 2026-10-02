using Sim.Core;
using Sim.Mujoco;
using Sim.Protocol;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// MuJoCo 域标定轮(10-02)的转正测量用例: 把临时探针的扫描结论固化成回归断言。
/// 口径与扫描一致: 机器人置于台面中心, 纯原地转(V=0, W=2.0; 默认补偿 10 ⇒ duty 饱和),
/// 取后 40 tick 逐 tick 偏航增量的中位数(°/tick → rad/s)。
///
/// 锚点: 真车原地转向实测 MOTOR_TURN_CALIBRATION(90°/0.65 s ⇒ ~2.4 rad/s)——
/// R1 的 ≥2.0 由此而来。v2 实测上限 1.0–1.35 rad/s 低于真车: MuJoCo 接触摩擦各向
/// 同性, 表达不了真车胎纹"顺滚动易/横向难"的滑移转向特性, 且四轮固定朝向本身是
/// 过约束系统(μ→∞ 反而转不动); 扫描峰 v1=f5 / v2=f6 均为工程初值(无真车锚点,
/// 已披露)。差距属已记录的保真度缺口, 不得用无锚点参数硬凑。
/// </summary>
public class MujocoDomainCalibrationTests(ITestOutputHelper output)
{
    // ---------- R1: 原地转向权限(满 duty 稳态偏航率) ----------

    [Fact]
    public void InPlaceTurn_V1_CalibratedDefault_ReachesRealCarTurnBand()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = Context(V1OnStage());
        using var backend = new MujocoPhysicsBackend(context); // 默认补偿 10 + 每模型摩擦 f5
        var yawRate = SteadyYawRate(context, backend);
        output.WriteLine($"v1 稳态偏航 = {yawRate:F3} rad/s (默认 f5 + comp10)");
        Assert.True(yawRate >= 2.0, $"v1 稳态偏航 {yawRate:0.###} rad/s < 2.0(真车锚点下限)");
    }

    [Fact]
    public void InPlaceTurn_V2_CalibratedDefault_StaysInMeasuredCeilingBand()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = Context(V2OnStage());
        using var backend = new MujocoPhysicsBackend(context); // 默认补偿 10 + 每模型摩擦 f6
        var yawRate = SteadyYawRate(context, backend);
        output.WriteLine($"v2 稳态偏航 = {yawRate:F3} rad/s (默认 f6 + comp10)");
        // 实测上限带(2026-10-02 扫描峰 f6 = 1.165 rad/s): 各向同性接触摩擦的滑移转向
        // 天花板; 下界防驱动/接触回归, 上界防无锚点参数把模型硬改快。
        Assert.InRange(yawRate, 1.0, 1.35);
    }

    // ---------- R2: 推击可用(单机器人推 0.3kg 增益块) ----------

    [Fact]
    public void Push_SingleRobotFullDuty_MovesBuffBlockAtLeast30CmIn2s()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = Context(PushScenario());
        using var backend = new MujocoPhysicsBackend(context);
        for (var i = 0; i < 20; i++)
        {
            context.Us.V = 0;
            context.Us.W = 0;
            backend.Step(MujocoModel.TickSeconds); // 落台稳定
        }
        var block = context.Blocks[0]; // 官方布局增益块 (1.35, 1.35)
        var (sx, sy) = (block.X, block.Y);
        context.Us.V = context.Us.Vehicle.MaxSpeed; // 满 duty 直线推
        context.Us.W = 0;
        for (var i = 0; i < 40; i++) // 2 s
        {
            backend.Step(MujocoModel.TickSeconds);
        }
        var dist = Js.Hypot(block.X - sx, block.Y - sy);
        output.WriteLine($"2 s 推块位移 = {dist:F3} m (起点 {sx:F2},{sy:F2} → {block.X:F2},{block.Y:F2})");
        Assert.True(dist >= 0.3, $"2 s 推块位移 {dist:0.###} m < 0.3 m (推击不可用)");
    }

    // ---------- 辅助 ----------

    private static double SteadyYawRate(PhysicsBackendContext context, MujocoPhysicsBackend backend)
    {
        for (var i = 0; i < 20; i++)
        {
            context.Us.V = 0;
            context.Us.W = 0;
            backend.Step(MujocoModel.TickSeconds); // 落台稳定
        }
        var prev = YawDeg(backend);
        var deltas = new List<double>(80);
        for (var i = 0; i < 80; i++)
        {
            context.Us.V = 0;
            context.Us.W = 2.0;
            backend.Step(MujocoModel.TickSeconds);
            var yaw = YawDeg(backend);
            var d = ((yaw - prev) % 360 + 540) % 360 - 180;
            deltas.Add(Math.Abs(d));
            prev = yaw;
        }
        deltas.Sort();
        return deltas[deltas.Count / 2] / 180 * Math.PI / MujocoModel.TickSeconds;
    }

    private static double YawDeg(MujocoPhysicsBackend backend)
    {
        var p = backend.BuildPhysicsPoses()!.Robots[RoleNames.Us];
        var yaw = Math.Atan2(2 * (p.Qw * p.Qz + p.Qx * p.Qy), 1 - 2 * (p.Qy * p.Qy + p.Qz * p.Qz));
        return yaw * 180 / Math.PI;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, parts[0], parts[1])))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    /// <summary>v1 训练场景 + 我方台心、对手走道角(不干扰)。</summary>
    private static Scenario V1OnStage() => WithStarts(new Scenario
    {
        Id = "wushu-ring-2026",
        Seed = 42,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        Field = FieldParams.Default,
        Blocks = OfficialLayout.Blocks,
    }, (
        RoleNames.Us, new Pose2 { X = 1.9, Y = 1.9, Th = 0 }), (
        RoleNames.Them, new Pose2 { X = 0.2, Y = 0.2, Th = 0 }));

    /// <summary>v2 真车场景(磁盘) + 台心/走道角出生点。</summary>
    private static Scenario V2OnStage()
    {
        var scenario = ProtocolJson.Deserialize<Scenario>(
            File.ReadAllText(RepoFile("scenarios", "wushu-ring-2026-mujoco-v2.json")))
            ?? throw new InvalidOperationException("wushu-ring-2026-mujoco-v2.json did not deserialize");
        return WithStarts(scenario, (
            RoleNames.Us, new Pose2 { X = 1.9, Y = 1.9, Th = 0 }), (
            RoleNames.Them, new Pose2 { X = 0.2, Y = 0.2, Th = 0 }));
    }

    /// <summary>推击场景: 我方在增益块 (1.35,1.35) 正后方 0.28 m 朝 +Y, 对手在对角走道。</summary>
    private static Scenario PushScenario() => WithStarts(V1OnStage(),
        (RoleNames.Us, new Pose2 { X = 1.35, Y = 1.07, Th = Math.PI / 2 }),
        (RoleNames.Them, new Pose2 { X = 0.3, Y = 3.5, Th = 0 }));

    private static Scenario WithStarts(Scenario s, params (string Role, Pose2 Pose)[] starts)
        => s with
        {
            Field = s.Field with
            {
                Starts = starts.ToDictionary(e => e.Role, e => e.Pose, StringComparer.Ordinal),
            },
        };

    /// <summary>按 MatchEngine 的实际口径组装 backend context(与 MujocoMotorModelTests 同型)。</summary>
    private static PhysicsBackendContext Context(Scenario scenario)
    {
        var field = new FieldModel(scenario.Field);
        var usProfile = VehicleNormalizer.Normalize(
            scenario.Vehicles.TryGetValue(RoleNames.Us, out var us) ? us : new VehicleProfile());
        var themProfile = VehicleNormalizer.Normalize(
            scenario.Vehicles.TryGetValue(RoleNames.Them, out var them) ? them : new VehicleProfile());
        var usStart = scenario.Field.Starts[RoleNames.Us];
        var themStart = scenario.Field.Starts[RoleNames.Them];
        var usRobot = new RobotRuntime
        {
            Role = RoleNames.Us, Name = "我方",
            X = usStart.X, Y = usStart.Y, Th = usStart.Th,
            Vehicle = usProfile, R = usProfile.CollisionRadius,
        };
        var themRobot = new RobotRuntime
        {
            Role = RoleNames.Them, Name = "对手",
            X = themStart.X, Y = themStart.Y, Th = themStart.Th,
            Vehicle = themProfile, R = themProfile.CollisionRadius,
        };
        var blocks = scenario.Blocks
            .Select(b => new BlockRuntime { Kind = b.Kind, X = b.X ?? 0, Y = b.Y ?? 0 })
            .ToList();
        return new PhysicsBackendContext(scenario, field, SimParameters.FromDictionary(scenario.Parameters),
            usRobot, themRobot, blocks, new EventBus(), AntiStallPhaseUs: 0, AntiStallPhaseThem: 0);
    }
}
