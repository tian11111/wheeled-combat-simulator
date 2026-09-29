using Sim.Core;
using Sim.Hosting;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 09-28 真车几何物理模型(mesh 车体 + 真尺寸圆柱轮)的模型侧断言。
/// 断言口径: 全部从**编译后的 mjModel** 读(mjModel.geom_type/size/pos 数组 +
/// mj_geomDistance 量编译后网格/圆柱的实际几何), 不读生成器字符串。
/// </summary>
public class MujocoVehicleMeshTests
{
    /// <summary>
    /// v1 身份基线: <c>scenarios/wushu-ring-2026-mujoco.json</c> 在 2026-09-26 的
    /// score-block v3 轮次里记录的 physicsModelSha256(.sim_runs/…/replay-seed-42-mujoco-v3.json)。
    /// 2026-09-29: MuJoCo timestep 0.005→0.002 (QACC 数值稳定修复, 10 seeds 验证爆炸归零),
    /// v1 模型哈希有意更新; 既有 MuJoCo replay 身份按门禁自然失效。
    /// </summary>
    private const string V1ModelSha256 = "7160897ccd3fe978ce000686904b7d4ce0004edebfbaa79e7aaf5b313c67a6e5";

    // 实测常量(装配.glb, 见 tools/mesh/README.md): 轮径/半宽与四轮轮心。
    private const double WheelRadius = 0.0325;
    private const double WheelHalfWidth = 0.0145;
    private const double WheelFrontX = 0.0730;
    private const double WheelRearX = -0.0770;
    private const double WheelLeftY = 0.11635;
    private const double WheelRightY = -0.11265;
    // 后铲/底盘的实测最低点与它们在车体局部系里的位置(STL 顶点)。
    private const double ShovelLowestZ = -0.01245;
    private const double ShovelLowestX = -0.099;
    private const double ChassisLowestZ = -0.00825;
    private const double ChassisLowestX = 0.103;

    private const int GeomCylinder = 5;
    private const int GeomMesh = 7;
    /// <summary>测量时把车吊到离地 0.5 m(轮不受地面约束), 便于用距离反解几何。</summary>
    private const double ProbeHeight = 0.5;
    private const double ArenaCentre = 1.9;
    private const double Tolerance = 1e-6;

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

    private static Scenario LoadScenario(string fileName)
        => ProtocolJson.Deserialize<Scenario>(File.ReadAllText(RepoFile("scenarios", fileName)))
            ?? throw new InvalidOperationException($"{fileName} did not deserialize");

    /// <summary>v2 场景(与 scenarios/wushu-ring-2026-mujoco-v2.json 同值, 便于测试内构造)。</summary>
    private static Scenario V2Scenario() => new()
    {
        Id = "wushu-ring-2026",
        Seed = 42,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
        Field = FieldParams.Default,
        Blocks = OfficialLayout.Blocks,
    };

    /// <summary>按 MatchEngine 的实际口径组装 backend context(车辆归一化 + 固定能量块位置)。</summary>
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

    private sealed class CompiledModel(IntPtr model, IntPtr data) : IDisposable
    {
        internal IntPtr Model { get; } = model;
        internal IntPtr Data { get; } = data;

        public void Dispose()
        {
            if (Data != IntPtr.Zero) MujocoNative.DeleteData(Data);
            if (Model != IntPtr.Zero) MujocoNative.DeleteModel(Model);
        }
    }

    /// <summary>生成 v2 模型 → 用内存 VFS 编译 → 把车吊到 arena 中心 0.5 m 高并 Forward。</summary>
    private static CompiledModel CompileV2(out string xml, out IReadOnlyList<MujocoMeshAsset> assets)
    {
        var (generatedXml, generatedAssets, _) = MujocoModel.Generate(Context(V2Scenario()));
        xml = generatedXml;
        assets = generatedAssets;
        var model = MujocoNative.CreateModel(xml, assets);
        var data = MujocoNative.MakeData(model);
        Assert.NotEqual(IntPtr.Zero, data);
        var qpos = MujocoNative.ReadQpos(model, data);
        qpos[0] = ArenaCentre;
        qpos[1] = ArenaCentre;
        qpos[2] = ProbeHeight;
        qpos[3] = 1;
        qpos[4] = 0;
        qpos[5] = 0;
        qpos[6] = 0;
        MujocoNative.WriteQpos(model, data, qpos);
        MujocoNative.Forward(model, data);
        return new CompiledModel(model, data);
    }

    private static int GeomId(IntPtr model, string name)
    {
        var id = MujocoNative.NameToId(model, MujocoNative.ObjectGeom, name);
        Assert.True(id >= 0, $"MuJoCo model is missing geom '{name}'.");
        return id;
    }

    [Fact]
    public void V1Scenario_KeepsTheRecordedModelHash()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = MatchEngineHost.Create(LoadScenario("wushu-ring-2026-mujoco.json"));
        Assert.Equal(V1ModelSha256, engine.BuildReplayHeader().PhysicsModelSha256);
    }

    [Fact]
    public void V1Model_UsesNoMeshAssetsAndKeepsThePlainXmlHash()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (xml, assets, sha) = MujocoModel.Generate(Context(LoadScenario("wushu-ring-2026-mujoco.json")));
        Assert.Empty(assets);
        Assert.DoesNotContain("<asset>", xml);
        Assert.DoesNotContain("<mesh ", xml);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
        Assert.Equal(expected, sha);
    }

    [Fact]
    public void V2Scenario_NormalizesToTheMeasuredFootprint()
    {
        var scenario = LoadScenario("wushu-ring-2026-mujoco-v2.json");
        Assert.Empty(scenario.Validate());
        var profile = VehicleNormalizer.Normalize(scenario.Vehicles[RoleNames.Us]);
        // 规范化不得改动实测足迹(legacy 对称体不变量 FrontExtent ≥ Length/2 + ShovelLength
        // 会吃掉车头方向的量, 场景文件里的 length/shovelLength 就是为此选定的)。
        Assert.Equal(0.103, profile.FrontExtent, 9);
        Assert.Equal(0.16949, profile.RearExtent, 9);
        Assert.Equal(0.12185, profile.SideExtent, 9);
        Assert.Equal(0.15, profile.WheelBase, 9);
        Assert.Equal(0.229, profile.TrackWidth, 9);
        // 2026-09-29: 真车整车重量(含电池/电机/主控; 旧默认 1kg 漏算, 车过轻被顶飞)。
        Assert.Equal(3.5, profile.Mass, 9);
    }

    [Fact]
    public void V2Model_XmlOnlyUsesLogicalAssetNames()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = CompileV2(out var xml, out var assets);
        Assert.Contains("model=\"wushu-mjcf-v2\"", xml);
        foreach (var asset in assets)
        {
            // 资产以逻辑文件名引用: 既没有盘符/分隔符, 也不含构建目录。
            Assert.Contains($"file=\"{asset.Name}\"", xml);
            Assert.DoesNotContain(AppContext.BaseDirectory, xml);
            Assert.DoesNotContain("/", asset.Name);
            Assert.DoesNotContain("\\", asset.Name);
            Assert.DoesNotContain(":", asset.Name);
        }
        // <asset> 段里的 file 属性只出现逻辑名(整串 XML 里没有盘符路径)。
        Assert.DoesNotContain(":\\", xml);
        Assert.DoesNotContain("file=\"C", xml);
        Assert.Equal(2, assets.Count);
    }

    [Fact]
    public void V2Model_CompiledWheelsAreTheMeasuredCylinders()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var compiled = CompileV2(out _, out _);
        var model = compiled.Model;
        // ABI 自校验: geom 0 是 ground box(场景 fieldSize 3.8 → 半边长 1.9, 顶面 z=0)。
        Assert.Equal(GeomCylinder + 1, MujocoNative.ReadGeomType(model, 0));
        Assert.Equal(new[] { 1.9, 1.9, 0.025 }, MujocoNative.ReadGeomSize(model, 0));
        Assert.Equal(new[] { 1.9, 1.9, -0.025 }, MujocoNative.ReadGeomPos(model, 0));

        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            Assert.Equal(GeomMesh, MujocoNative.ReadGeomType(model, GeomId(model, $"robot_chassis_{role}")));
            Assert.Equal(GeomMesh, MujocoNative.ReadGeomType(model, GeomId(model, $"robot_shovel_{role}")));
            foreach (var axle in new[] { "front", "rear" })
            foreach (var side in new[] { "left", "right" })
            {
                var geom = GeomId(model, $"wheel_geom_{role}_{axle}_{side}");
                Assert.Equal(GeomCylinder, MujocoNative.ReadGeomType(model, geom));
                Assert.Equal(new[] { WheelRadius, WheelHalfWidth, 0.0 }, MujocoNative.ReadGeomSize(model, geom));
                // 轮 geom 在轮 body 的局部原点, 轮 body 的位置由下面的距离测量核对。
                Assert.Equal(new[] { 0.0, 0.0, 0.0 }, MujocoNative.ReadGeomPos(model, geom));
            }
        }
    }

    [Fact]
    public void V2Model_CompiledWheelPositionsMatchTheMeasuredAssembly()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var compiled = CompileV2(out _, out _);
        var model = compiled.Model;
        var data = compiled.Data;
        var ground = GeomId(model, "ground");

        // 每个轮子到地面 box 的最近点 = 轮子最低点(车被吊起 0.5 m, 轮心局部 z=0)。
        // 距离 = 0.5 − r 同时钉住: 轮半径、轮心相对体原点的 z。
        foreach (var (axle, expectedX) in new[] { ("front", WheelFrontX), ("rear", WheelRearX) })
        foreach (var side in new[] { "left", "right" })
        {
            var (distance, fromTo) = MujocoNative.GeomDistance(model, data,
                GeomId(model, $"wheel_geom_us_{axle}_{side}"), ground);
            Assert.Equal(ProbeHeight - WheelRadius, distance, 6);
            Assert.Equal(ArenaCentre + expectedX, fromTo[0], 6);
            Assert.Equal(ProbeHeight - WheelRadius, fromTo[2], 6);
            Assert.Equal(0.0, fromTo[5], 6);
        }

        // 前左↔后左: 距离 = 轴距 − 2r, 最近点 x = 轮心 x ∓ r → 同时钉住前后轴位置与半径。
        var (frontRear, axisFromTo) = MujocoNative.GeomDistance(model, data,
            GeomId(model, "wheel_geom_us_front_left"), GeomId(model, "wheel_geom_us_rear_left"));
        Assert.Equal(0.15 - 2 * WheelRadius, frontRear, 6);
        Assert.Equal(ArenaCentre + WheelFrontX - WheelRadius, axisFromTo[0], 6);
        Assert.Equal(ArenaCentre + WheelRearX + WheelRadius, axisFromTo[3], 6);
        // 侧面: 最近点在左轮内侧面 y = 轮距左侧 − 半宽。
        Assert.Equal(ArenaCentre + WheelLeftY - WheelHalfWidth, axisFromTo[1], 6);

        // 前左↔前右(同轴): 距离 = 轮距 − 2×半宽, 最近点 y = 两个内侧面的 y。
        var (track, trackFromTo) = MujocoNative.GeomDistance(model, data,
            GeomId(model, "wheel_geom_us_front_left"), GeomId(model, "wheel_geom_us_front_right"));
        Assert.Equal(0.229 - 2 * WheelHalfWidth, track, 6);
        Assert.Equal(ArenaCentre + WheelLeftY - WheelHalfWidth, trackFromTo[1], 6);
        Assert.Equal(ArenaCentre + WheelRightY + WheelHalfWidth, trackFromTo[4], 6);

        // 后轴同样成立(左右不对称: 左 0.11635 / 右 −0.11265 都按实测放置)。
        // 同轴两轮共轴 → 最近点落在同一个 x(轮心 x), 不像前后轴那样带 ∓r。
        var (rearTrack, rearFromTo) = MujocoNative.GeomDistance(model, data,
            GeomId(model, "wheel_geom_us_rear_left"), GeomId(model, "wheel_geom_us_rear_right"));
        Assert.Equal(0.229 - 2 * WheelHalfWidth, rearTrack, 6);
        Assert.Equal(ArenaCentre + WheelRearX, rearFromTo[0], 6);
        Assert.Equal(ArenaCentre + WheelRearX, rearFromTo[3], 6);
    }

    [Fact]
    public void V2Model_CompiledMeshesSitAtTheMeasuredChassisAndShovelExtremes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var compiled = CompileV2(out _, out _);
        var model = compiled.Model;
        var data = compiled.Data;
        var ground = GeomId(model, "ground");

        // 后铲网格: 最低边实测 z=−0.01245(x≈−0.099)。距离 = 0.5 + 最低边 z。
        var (shovelDistance, shovelFromTo) = MujocoNative.GeomDistance(model, data,
            GeomId(model, "robot_shovel_us"), ground);
        Assert.Equal(ProbeHeight + ShovelLowestZ, shovelDistance, 6);
        Assert.Equal(ArenaCentre + ShovelLowestX, shovelFromTo[0], 6);
        Assert.Equal(ProbeHeight + ShovelLowestZ, shovelFromTo[2], 6);

        // 底盘凸体: 最低点实测 z=−0.00825(x=+0.103 = 车头最前沿, 即 FrontExtent)。
        var (chassisDistance, chassisFromTo) = MujocoNative.GeomDistance(model, data,
            GeomId(model, "robot_chassis_us"), ground);
        Assert.Equal(ProbeHeight + ChassisLowestZ, chassisDistance, 6);
        Assert.Equal(ArenaCentre + ChassisLowestX, chassisFromTo[0], 6);
        Assert.Equal(ProbeHeight + ChassisLowestZ, chassisFromTo[2], 6);

        // 后铲在车尾: 其最低点 x 明显小于车体中心, 且比底盘最低点更靠后(凹角几何仍在)。
        Assert.True(shovelFromTo[0] < ArenaCentre - 0.09, $"shovel is not at the rear: x={shovelFromTo[0]}");
        Assert.True(shovelFromTo[2] < chassisFromTo[2], "shovel must hang below the chassis plate");
    }

    [Fact]
    public void V2ModelHash_ChangesWhenAnyAssetByteChanges()
    {
        var context = Context(V2Scenario());
        var (_, assets, baseline) = MujocoModel.Generate(context);
        Assert.Equal(MujocoMeshAssets.Required.Length, assets.Count);
        // 同一份资产必须给同一个哈希(确定性)。
        Assert.Equal(baseline, MujocoModel.Generate(context, assets).Sha256);

        for (var i = 0; i < assets.Count; i++)
        {
            var tampered = assets.Select((a, index) => index == i
                ? a with { Bytes = FlipOneByte(a.Bytes) }
                : a).ToArray();
            var (_, _, changed) = MujocoModel.Generate(context, tampered);
            Assert.NotEqual(baseline, changed);
        }

        // 换名(内容不变)也必须改变身份。
        var renamed = assets.Select(a => a with { Name = a.Name + ".other" }).ToArray();
        Assert.Throws<ArgumentException>(() => MujocoModel.Generate(context, renamed));
    }

    [Fact]
    public void V2Scenario_BackendCompilesWithTheAssetDerivedIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scenario = LoadScenario("wushu-ring-2026-mujoco-v2.json");
        using var engine = MatchEngineHost.Create(scenario);
        var header = engine.BuildReplayHeader();
        Assert.Equal(PhysicsSpec.Mujoco, header.PhysicsBackend);
        Assert.Equal(PhysicsSpec.MujocoModelV2, scenario.Physics!.ModelVersion);
        Assert.NotNull(header.PhysicsModelSha256);
        Assert.Equal(64, header.PhysicsModelSha256!.Length);
        Assert.NotEqual(V1ModelSha256, header.PhysicsModelSha256);
        // 资产参与哈希: 场景身份 ≠ 纯 XML 哈希。
        var (xml, _, xmlOnly) = MujocoModel.Generate(Context(scenario));
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
        Assert.NotEqual(plain, header.PhysicsModelSha256);
        Assert.NotEqual(xmlOnly, plain);
    }

    private static byte[] FlipOneByte(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        var index = copy.Length / 2;
        copy[index] = (byte)(copy[index] ^ 0x01);
        return copy;
    }
}
