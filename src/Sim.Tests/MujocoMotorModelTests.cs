using Sim.Core;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 09-30 批 2 电机真值标定的模型侧断言。口径(来源 scenarios/wushu-ring-2026-mujoco-v2.json
/// 与设置页默认值): 2342 减速电机真值 —— 减速后 120 rpm(ω_noload = 4π)、
/// 输出轴堵转扭矩 1.72 N·m。MJCF <c>velocity</c> 执行器取
/// kv = τ_stall/ω_noload、ctrlrange = ±ω_noload、forcerange = ±τ_stall 后, 其
/// 扭矩-转速曲线在 [0, ω_noload] 上精确等价直流电机线性曲线;
/// <c>SetControls</c> 的 duty 口径(ctrl = duty×ω_noload)再把它变成可调压开环驱动。
/// 三个字符串断言是 N()("R" 格式)在 .NET 8 的实测输出(tmp/fmtprobe, 与批 1 同法)。
/// </summary>
public class MujocoMotorModelTests
{
    // ---------- 常量推导(真值, 非拟合) ----------

    [Fact]
    public void MotorConstants_AreDerivedFromThe2342Datasheet()
    {
        Assert.Equal(1.72, MujocoModel.MjcStallTorque, 12);          // 输出轴堵转扭矩真值
        Assert.Equal(4 * Math.PI, MujocoModel.MjcNoLoadSpeed, 12);   // 120 rpm = 4π rad/s
        Assert.Equal(0.13687325105903, MujocoModel.MjcServoKv, 12);  // τ_stall/ω_noload
        // kv 是由两个真值推导出的, 不是独立标定量 —— 改一个真值 kv 必须跟着变。
        Assert.Equal(MujocoModel.MjcStallTorque / MujocoModel.MjcNoLoadSpeed, MujocoModel.MjcServoKv);
    }

    [Fact]
    public void GeneratedActuators_UseOnlyTheDatasheetValues()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (xml, _, _) = MujocoModel.Generate(Context(V1Scenario()));
        Assert.Contains("kv=\"0.13687325105903\"", xml);
        Assert.Contains("ctrlrange=\"-12.566370614359172 12.566370614359172\"", xml);
        Assert.Contains("forcerange=\"-1.72 1.72\"", xml);
        // 旧工程值不得残留(避免第二份"电机真值"): kv=0.25 / ctrlrange=±80 / forcerange=±3.0。
        Assert.DoesNotContain("kv=\"0.25\"", xml);
        Assert.DoesNotContain("ctrlrange=\"-80 80\"", xml);
        Assert.DoesNotContain("forcerange=\"-3 3\"", xml);
    }

    // ---------- 转速-扭矩曲线恒等(MuJoCo velocity 执行器语义的数学直接断言) ----------

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-0.25)]
    public void VelocityActuatorCurve_MatchesTheLinearDcMotorCurve(double duty)
    {
        // MuJoCo velocity 执行器: τ = clamp(kv×(ctrl − ω), ±forcerange), ctrl = duty×ω_noload。
        // ω=0(堵转): τ = kv×duty×ω_noload = duty×τ_stall —— 起步扭矩恒等。
        var ctrl = duty * MujocoModel.MjcNoLoadSpeed;
        Assert.Equal(duty * MujocoModel.MjcStallTorque, MujocoModel.MjcServoKv * ctrl, 12);
        // ω = 该占空比下的空载转速 duty×ω_noload: τ = 0。
        var noLoadSpeed = duty * MujocoModel.MjcNoLoadSpeed;
        Assert.Equal(0.0, MujocoModel.MjcServoKv * (ctrl - noLoadSpeed));
        // 中间点线性: ω = ctrl/2(空载转速的一半)时 τ = duty×τ_stall/2。
        var half = MujocoModel.MjcServoKv * (ctrl - 0.5 * ctrl);
        Assert.Equal(duty * MujocoModel.MjcStallTorque / 2, half, 12);
    }

    // ---------- 驱动路径实测(duty → 写入 mjData 的 ctrl) ----------

    [Fact]
    public void SetControls_StraightLineDutySaturatesAtNoLoadSpeed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = Context(V2RealScenario()); // MaxSpeed=0.408 真车
        using var backend = new MujocoPhysicsBackend(context, inPlaceTurnCompensation: 1.0);
        // 0.9 m/s(FSM 冲台/追人档)已 ≥ MaxSpeed ⇒ duty 截到 1 ⇒ ctrl = ω_noload,
        // 即真车开环全速(披露语义, 见任务 report 的"速度档饱和"节)。多跑几 tick
        // 让 ApplyCommandLatency 的一阶斜坡收敛到指令值。
        context.Us.V = 0.9;
        for (var i = 0; i < 3; i++)
        {
            backend.Step(MujocoModel.TickSeconds);
        }
        var ctrl = ReadCtrl(backend);
        Assert.Equal(MujocoModel.MjcNoLoadSpeed, ctrl[0], 9);
        Assert.Equal(MujocoModel.MjcNoLoadSpeed, ctrl[1], 9);
        Assert.Equal(ctrl[0], ctrl[2]); // 前后轮同值(保持旧结构)
        Assert.Equal(ctrl[1], ctrl[3]);
    }

    [Fact]
    public void SetControls_InPlaceTurn_ProducesOppositeSignedNonZeroDuty()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = Context(V2RealScenario());
        using var backend = new MujocoPhysicsBackend(context, inPlaceTurnCompensation: 1.0);
        // 纯原地转向(cmdV=0, cmdW=2 —— FSM DriveToward(r,pos,0,2.0)/RotateTo 的形态)。
        // duty 必须按轮计算: 单标量 |cmdV|/MaxSpeed 会把这里压成 duty=0。
        context.Us.V = 0;
        context.Us.W = 2.0;
        backend.Step(MujocoModel.TickSeconds);
        var ctrl = ReadCtrl(backend);
        var halfTrack = context.Us.Vehicle.TrackWidth / 2;
        var expected = 2.0 * halfTrack / context.Us.Vehicle.MaxSpeed * MujocoModel.MjcNoLoadSpeed;
        Assert.InRange(expected, 1e-9, MujocoModel.MjcNoLoadSpeed); // 杆内未饱和
        Assert.Equal(-expected, ctrl[0], 9); // 左轮反转(cmdW>0 时向左偏航)
        Assert.Equal(expected, ctrl[1], 9);  // 右轮正转
        Assert.True(ctrl[0] * ctrl[1] < 0, $"原地转向两轮 duty 必须反号: {ctrl[0]} / {ctrl[1]}");
    }

    // ---------- 行车上界(真车极速 0.408 m/s 的行为判别) ----------

    [Fact]
    public void FullDutyStraightRun_IsBoundedByTheNoLoadWheelSpeed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = Context(V2RealScenario());
        using var backend = new MujocoPhysicsBackend(context, inPlaceTurnCompensation: 1.0);
        context.Us.V = context.Us.Vehicle.MaxSpeed; // duty=1(0.408/0.408)
        for (var i = 0; i < 40; i++)
        {
            backend.Step(MujocoModel.TickSeconds); // 起步 + 速度环收敛
        }
        var a = context.Us.X;
        for (var i = 0; i < 20; i++)
        {
            backend.Step(MujocoModel.TickSeconds);
        }
        var speed = (context.Us.X - a) / (20 * MujocoModel.TickSeconds);
        // 物理上界 = ω_noload × r = 12.566370614359172 × 0.0325 = 0.408407 m/s
        // (轮端空载转速, 真车 0.408 = 场景 MaxSpeed); 任何驱动结果不得超过它。
        Assert.True(speed <= MujocoModel.MjcNoLoadSpeed * MujocoModel.WheelRadiusV2 + 1e-6,
            $"稳态车速 {speed:0.####} 超过轮端空载上界 {MujocoModel.MjcNoLoadSpeed * MujocoModel.WheelRadiusV2:0.####} m/s");
        // 下界(实测标定): 批 2 实测稳态车速 0.3554 m/s —— τ(ω)=kv×(ctrl−ω) 与
        // 轮轴 hinge damping/接触滚阻的平衡点(未到空载, 差 13%); [0.30, 0.40] 覆盖
        // 摩擦/接触细节波动, 同时把"驱动失效"(≈0)与"超真车上界"(>0.408)分开。
        Assert.InRange(speed, 0.30, 0.40);
    }

    // ---------- 电池压降接口: 默认禁用, 惰性 ----------

    [Fact]
    public void BatteryDropOptions_AreDisabledByDefaultAndLazy()
    {
        if (!OperatingSystem.IsWindows()) return;
        // 默认(未注入)与显式"启用但未标定"都必须恒返回 1: 无实测数据不得发明压降数值。
        var unspecified = new MotorDriveOptions { Enabled = true };
        Assert.Equal(1.0, unspecified.VoltageScale(1.0));
        Assert.Equal(1.0, unspecified.VoltageScale(-1.0));
        var defaultOptions = new MotorDriveOptions();
        Assert.Equal(1.0, defaultOptions.VoltageScale(1.0));
        // options 只影响 ctrl 折算, 不进 MJCF ⇒ 模型身份与注入与否无关。
        var plain = new MujocoPhysicsBackend(Context(V1Scenario()));
        var enabled = new MujocoPhysicsBackend(Context(V1Scenario()), motorOptions: new MotorDriveOptions
        {
            Enabled = true,
            BatteryInternalResistanceOhm = 0.05,
            BatteryNoLoadVoltageVolts = 8.0,
            StallCurrentAmps = 1.4,
        });
        try
        {
            Assert.Equal(plain.ModelSha256, enabled.ModelSha256);
        }
        finally
        {
            plain.Dispose();
            enabled.Dispose();
        }
    }

    [Fact]
    public void BatteryDropOptions_WhenCalibrated_ScaleCtrlByTerminalVoltage()
    {
        // 接口钩子公式(非启用: 参数是占位, 真值须实测才可注入): V = V0 − I·R,
        // I = |duty|×I_stall ⇒ scale = (8 − 1.4×0.05)/8 = 0.99125 @ duty=1;
        // duty=0(无电流)无压降。
        var options = new MotorDriveOptions
        {
            Enabled = true,
            BatteryInternalResistanceOhm = 0.05,
            BatteryNoLoadVoltageVolts = 8.0,
            StallCurrentAmps = 1.4,
        };
        Assert.Equal((8.0 - 1.4 * 0.05) / 8.0, options.VoltageScale(1.0), 12);
        Assert.Equal((8.0 - 0.7 * 0.05) / 8.0, options.VoltageScale(0.5), 12);
        Assert.Equal(1.0, options.VoltageScale(0.0));
    }

    // ---------- 辅助 ----------

    private static double[] ReadCtrl(MujocoPhysicsBackend backend)
        => MujocoNative.ReadCtrl(backend.ExposedModel, backend.ExposedData);

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

    private static Scenario V1Scenario() => new()
    {
        Id = "wushu-ring-2026",
        Seed = 42,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        Field = FieldParams.Default,
        Blocks = OfficialLayout.Blocks,
    };

    /// <summary>官方 v2 真车场景(磁盘) + 走道直行出生点: MaxSpeed=0.408 / 轮径 0.0325。</summary>
    private static Scenario V2RealScenario()
    {
        var scenario = ProtocolJson.Deserialize<Scenario>(
            File.ReadAllText(RepoFile("scenarios", "wushu-ring-2026-mujoco-v2.json")))
            ?? throw new InvalidOperationException("wushu-ring-2026-mujoco-v2.json did not deserialize");
        return scenario with
        {
            Field = scenario.Field with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 0.95, Y = 0.3, Th = 0 },
                    [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
                },
            },
        };
    }

    /// <summary>按 MatchEngine 的实际口径组装 backend context(与 MujocoTimebaseTests.Context 同型)。</summary>
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
