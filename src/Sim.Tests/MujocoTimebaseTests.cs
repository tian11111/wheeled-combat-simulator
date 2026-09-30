using System.Globalization;
using Sim.Core;
using Sim.Hosting;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 09-30 时基回归的护栏。09-29 的 QACC 修复只把 MJCF 的 option timestep 字面量
/// 改成 0.002, C# 侧 SubstepsPerTick(10)×SubstepSeconds(0.005) 未同步 —— 每裁判
/// tick 只 mj_step 10 次、只积分 0.02 s, 物理时间流速是比赛钟的 0.4×(慢动作),
/// 而 FSM 时限/传感器/接触时刻全按 0.05 s 记账。本类钉住三条: 常量同源、生成
/// MJCF 同源、每 tick 的真实物理推进量。
/// </summary>
public class MujocoTimebaseTests
{
    [Fact]
    public void TimebaseConstants_AreSingleTruthForTheRefereeTick()
    {
        // 09-29 的失配正是"XML 0.002 / C# 0.005×10"两份步长。现在只有 MjcTimestep。
        Assert.Equal(0.002, MujocoModel.MjcTimestep, 12);
        Assert.Equal(MujocoModel.MjcTimestep, MujocoModel.SubstepSeconds, 12);
        Assert.Equal(25, MujocoModel.SubstepsPerTick);
        Assert.Equal(0.05, MujocoModel.TickSeconds, 12);
        // 不变量(无容差): SubstepsPerTick 由 0.05 / MjcTimestep 推导, 其乘积在 IEEE
        // 下精确等于 tick 时长 —— 0.05/0.002 的商恰为 25.0, 25×0.002 恰为 0.05 这个
        // double, 所以这里用 == 而不是区间, 任何一步失配都会立刻红。
        Assert.True(MujocoModel.SubstepsPerTick * MujocoModel.MjcTimestep == MujocoModel.TickSeconds,
            $"{MujocoModel.SubstepsPerTick} × {MujocoModel.MjcTimestep} ≠ {MujocoModel.TickSeconds}");
    }

    [Fact]
    public void GeneratedMjcf_TimestepComesFromTheConstant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var (xml, _, _) = MujocoModel.Generate(Context(V1Scenario()));
        var literal = MujocoModel.MjcTimestep.ToString("R", CultureInfo.InvariantCulture);
        // N() 的 "R" 格式化: 批 1 不改 MJCF 字节(因此 v1/v2 模型哈希不变,
        // 由 MujocoVehicleMeshTests.V1Scenario_KeepsTheRecordedModelHash 守卫)。
        Assert.Equal("0.002", literal);
        Assert.Contains($"<option timestep=\"{literal}\" gravity=", xml);
        // 生成器里不得再留第二份步长字面量(09-29 的失配形态)。
        Assert.DoesNotContain("timestep=\"0.005\"", xml);
    }

    [Fact]
    public void MujocoTick_AdvancesOneRefereeTickOfPhysics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var engine = MatchEngineHost.Create(StraightRunScenario());
        engine.Arm();
        var drive = new RobotAction { V = 1.5 };
        for (var i = 0; i < 40; i++)
        {
            engine.Tick(drive, RobotAction.Zero); // 指令斜坡(AccelK)与速度环收敛
        }
        var a = engine.CommitSnapshot().Robots[RoleNames.Us].X;
        for (var i = 0; i < 20; i++)
        {
            engine.Tick(drive, RobotAction.Zero);
        }
        var b = engine.CommitSnapshot().Robots[RoleNames.Us].X;
        var perTick = (b - a) / 20;
        // 批 2 电机真值标定后 v1 的驱动语义: MaxSpeed=1.5 档 ⇒ duty=1 ⇒ ctrl=ω_noload
        // = 12.566 rad/s, 轮径 0.065 的无载上界 = 0.817 m/s; 实测稳态车速 0.712 m/s
        // (落在转速-扭矩曲线 τ(ω)=kv×(ctrl−ω) 与轮 hinge damping/滚动阻力的交点,
        // 不再是旧速度伺服的"指令即车速"), 一裁判 tick(0.05 s)位移 = 0.0356 m。
        // 旧慢动作时基下每 tick 只推进 0.02 s 物理时间, 同一车速只走 ≈0.0142 m ——
        // [0.030, 0.042] 把两者分开, 是"每 tick 实际积分量"这条回归的行为判别式
        // (常量断言挡不住"改了常量忘了循环")。
        Assert.InRange(perTick, 0.030, 0.042);
    }

    [Fact]
    public void BlockContactTimes_AreSubstepEndTimesWithinTheRefereeTick()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var context = Context(PushScenario());
        using var backend = new MujocoPhysicsBackend(context);
        var observed = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            context.Us.V = 0.5; // 直接驱动 backend(不经 MatchEngine 的 FSM/指令钳制)
            backend.Step(MujocoModel.TickSeconds);
            foreach (var (_, t) in context.Blocks[0].ContactThisStep)
            {
                observed.Add(t);
            }
        }

        Assert.NotEmpty(observed);
        foreach (var t in observed)
        {
            // 接触时刻 = 该子步结束时的累计物理时间 k×MjcTimestep (k = 1..25),
            // 与真实推进量一致(旧口径的 0.005 网格在 0.002 子步下是错的刻度)。
            Assert.InRange(t, MujocoModel.MjcTimestep - 1e-12, MujocoModel.TickSeconds + 1e-12);
            var k = Math.Round(t / MujocoModel.MjcTimestep);
            Assert.True(Math.Abs(t - k * MujocoModel.MjcTimestep) <= 1e-12,
                $"接触时刻 {t} 不在子步网格 k×{MujocoModel.MjcTimestep} 上");
            Assert.InRange(k, 1, MujocoModel.SubstepsPerTick);
        }
        // 持续推块时末子步仍有接触 ⇒ 该 tick 的最大接触时刻恰为 tick 末尾 0.05;
        // 归属只比较同 tick 内 max 接触时刻是否相等(Physics.FinalizeBlockContacts),
        // 网格点变密不改变 "simultaneous" 语义。
        Assert.Contains(observed, t => Math.Abs(t - MujocoModel.TickSeconds) <= 1e-12);
    }

    private static Scenario V1Scenario() => new()
    {
        Id = "wushu-ring-2026",
        Seed = 42,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        Field = FieldParams.Default,
        Blocks = OfficialLayout.Blocks,
    };

    /// <summary>走道(y=0.3)内直线行驶: 远离台面/围栏, 位移只反映执行器与时基。</summary>
    private static Scenario StraightRunScenario() => V1Scenario() with
    {
        Field = FieldParams.Default with
        {
            Starts = new Dictionary<string, Pose2>
            {
                [RoleNames.Us] = new() { X = 0.95, Y = 0.3, Th = 0 },
                [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
            },
        },
    };

    /// <summary>我方(1.85, 0.35)朝 +X 顶住正前方的增益块(2.05, 0.35): 持续接触。</summary>
    private static Scenario PushScenario() => V1Scenario() with
    {
        Blocks =
        [
            OfficialLayout.Blocks[0] with { X = 2.05, Y = 0.35 },
            OfficialLayout.Blocks[1] with { X = 0.2, Y = 0.2 },
            OfficialLayout.Blocks[2] with { X = 0.2, Y = 3.6 },
        ],
        Field = FieldParams.Default with
        {
            Starts = new Dictionary<string, Pose2>
            {
                [RoleNames.Us] = new() { X = 1.85, Y = 0.35, Th = 0 },
                [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
            },
        },
    };

    /// <summary>按 MatchEngine 的实际口径组装 backend context(与 MujocoVehicleMeshTests.Context 同型)。</summary>
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
