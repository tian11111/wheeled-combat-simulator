using Sim.Core;
using Sim.Hosting;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 2026-10-03 M1 v2 轮接触 solref 注入点的模型侧断言(见
/// <see cref="WheelContactOptions.SolRefTimeconst"/>)。背景: 轮 geom 的
/// solref="0.02 1" 是整车 19–35mm 级穿透的共同根因(mjpenprobe 实测: v2 FSM
/// 轮-地 -34.1mm / 块对挤 轮-块 -34.8mm / 轮-车 -19.7mm, 全部含轮 geom),
/// 而其余 geom 全走 default class 的 0.008。注入点契约:
/// - **默认(未注入)逐字节不变** —— v1/v2 ModelSha256 不变, 既有 MuJoCo
///   回放与 RL checkpoint 全部有效(MujocoVehicleMeshTests 的身份守卫即此契约);
/// - **显式注入只改轮 geom 的 solref 属性**, 摩擦三元组/condim/其余字节全部不动;
/// - 注入态哈希必变 ⇒ 该注入态下旧回放/checkpoint 失配(本轮注入仅探针/测试可达)。
/// </summary>
public class MujocoWheelSolrefTests
{
    private const string WheelSolrefLiteral = "solref=\"0.02 1\"/>";
    private const string WheelSolrefInjected = "solref=\"0.008 1\"/>";

    /// <summary>今日记录的 v2 默认路径身份(来源 tmp/hashpin 探针, 生产改动前
    /// HEAD 工作树实测, 改动后复跑逐字节一致; v1 由 MujocoVehicleMeshTests
    /// V1ModelSha256 钉扎)。v2 此前无身份常量, 本日随 M1 注入点补钉:
    /// 2026-10-03 轮 solref 注入点落地, 未注入路径 MJCF 逐字节不变, 哈希维持此值。</summary>
    private const string V2ModelSha256 = "6ce51b19c37045f21f20a15affcb80212d929cb186cd7f143ffdf74d60a83496";

    // ---------- 场景/上下文组装(与 MujocoVehicleMeshTests 同型) ----------

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

    private static Scenario V1Scenario() => new()
    {
        Id = "wushu-ring-2026",
        Seed = 42,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        Field = FieldParams.Default,
        Blocks = OfficialLayout.Blocks,
    };

    /// <summary>v2 场景(与 scenarios/wushu-ring-2026-mujoco-v2.json 同值)。</summary>
    private static Scenario V2Scenario() => new()
    {
        Id = "wushu-ring-2026",
        Seed = 42,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
        Field = FieldParams.Default,
        Blocks = OfficialLayout.Blocks,
    };

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

    private static int CountOf(string text, string sub) => text.Split(sub).Length - 1;

    // ---------- 1. 默认路径: 逐字节不变(本项的"能失败"守卫) ----------

    [Fact]
    public void DefaultPath_WheelSolrefStaysTheHistoricalLiteralByteForByte()
    {
        // v1 与 v2: 不传 wheelContact、显式 default、显式 SolRefTimeconst=0.02 三条
        // 生成路径必须产出同一份 XML(逐字节)与同一哈希 —— 未注入 ⇒ 字节不变。
        var v1Context = Context(V1Scenario());
        var v1Plain = MujocoModel.Generate(v1Context);
        var v1Off = MujocoModel.Generate(v1Context, Array.Empty<MujocoMeshAsset>(), default);
        var v1Explicit = MujocoModel.Generate(v1Context, Array.Empty<MujocoMeshAsset>(),
            new WheelContactOptions { SolRefTimeconst = 0.02 });
        Assert.Equal(v1Plain.Xml, v1Off.Xml);
        Assert.Equal(v1Plain.Sha256, v1Off.Sha256);
        Assert.Equal(v1Plain.Xml, v1Explicit.Xml);

        var v2Context = Context(V2Scenario());
        var v2Plain = MujocoModel.Generate(v2Context);
        var v2Off = MujocoModel.Generate(v2Context, MujocoMeshAssets.Load(), default);
        var v2Explicit = MujocoModel.Generate(v2Context, MujocoMeshAssets.Load(),
            new WheelContactOptions { SolRefTimeconst = 0.02 });
        Assert.Equal(v2Plain.Xml, v2Off.Xml);
        Assert.Equal(v2Plain.Sha256, v2Off.Sha256);
        Assert.Equal(v2Plain.Xml, v2Explicit.Xml);

        // 8 个轮 geom(2 车 × 4 轮)各写一次历史字面量; default class 的 0.008 不受影响。
        Assert.Equal(8, CountOf(v1Plain.Xml, WheelSolrefLiteral));
        Assert.Equal(8, CountOf(v2Plain.Xml, WheelSolrefLiteral));
        Assert.Equal(1, CountOf(v2Plain.Xml, "solref=\"0.008 1\" solimp="));
    }

    [Fact]
    public void V2DefaultBackend_KeepsTodaysRecordedModelHash()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = MatchEngineHost.Create(LoadScenario("wushu-ring-2026-mujoco-v2.json"));
        Assert.Equal(V2ModelSha256, engine.BuildReplayHeader().PhysicsModelSha256);
    }

    // ---------- 2. 显式注入: 只改轮 geom 的 solref 属性, 哈希必变 ----------

    [Fact]
    public void Injection_ChangesOnlyTheWheelSolrefAttribute()
    {
        // 注入 0.008(锚点 = default class 既有标定值)后, 把默认 XML 里的 8 处
        // 轮 solref 字面量逐一替换成注入值, 结果必须与注入 XML **逐字节相等** ——
        // 任何"顺手"改动的字节(摩擦/condim/其它属性)都会让本断言失败。
        var v1Default = MujocoModel.Generate(Context(V1Scenario())).Xml;
        var v1Injected = MujocoModel.Generate(Context(V1Scenario()), Array.Empty<MujocoMeshAsset>(),
            new WheelContactOptions { SolRefTimeconst = 0.008 }).Xml;
        Assert.Equal(v1Default.Replace(WheelSolrefLiteral, WheelSolrefInjected), v1Injected);

        var v2Context = Context(V2Scenario());
        var v2Default = MujocoModel.Generate(v2Context);
        var v2Injected = MujocoModel.Generate(v2Context, MujocoMeshAssets.Load(),
            new WheelContactOptions { SolRefTimeconst = 0.008 });
        Assert.Equal(v2Default.Xml.Replace(WheelSolrefLiteral, WheelSolrefInjected), v2Injected.Xml);
        Assert.NotEqual(v2Default.Sha256, v2Injected.Sha256);

        // 注入态结构不变: 8 个轮 geom + default class 仍在; 摩擦三元组不动(v2 f6)。
        Assert.Equal(8, CountOf(v2Injected.Xml, WheelSolrefInjected));
        Assert.Equal(0, CountOf(v2Injected.Xml, WheelSolrefLiteral));
        Assert.Equal(1, CountOf(v2Injected.Xml, "solref=\"0.008 1\" solimp="));
        Assert.Equal(CountOf(v2Default.Xml, "friction=\"6 0.02 0.002\""),
            CountOf(v2Injected.Xml, "friction=\"6 0.02 0.002\""));

        // 稳定域守卫: 锚点/次选都必须满足 timeconst ≥ 2×timestep(MujocoModel 注释
        // 的显式稳定域) —— timestep 若再上调而不复核 solref 锚点, 这里先红。
        Assert.True(2 * MujocoModel.MjcTimestep <= 0.008);
        Assert.True(2 * MujocoModel.MjcTimestep <= 0.005);
    }

    // ---------- 3. 后端注入链: 构造器 wheelContact 参数 → Generate → 身份 ----------

    [Fact]
    public void BackendIdentity_OffStateMatchesDefault_InjectionChangesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var v1Context = Context(V1Scenario());
        using var v1Plain = new MujocoPhysicsBackend(v1Context);
        using var v1Off = new MujocoPhysicsBackend(Context(V1Scenario()), wheelContact: default);
        using var v1On = new MujocoPhysicsBackend(Context(V1Scenario()),
            wheelContact: new WheelContactOptions { SolRefTimeconst = 0.008 });
        Assert.Equal(v1Plain.ModelSha256, v1Off.ModelSha256);
        Assert.NotEqual(v1Plain.ModelSha256, v1On.ModelSha256);

        var v2Context = Context(V2Scenario());
        using var v2Plain = new MujocoPhysicsBackend(v2Context);
        using var v2Off = new MujocoPhysicsBackend(Context(V2Scenario()), wheelContact: default);
        using var v2On = new MujocoPhysicsBackend(Context(V2Scenario()),
            wheelContact: new WheelContactOptions { SolRefTimeconst = 0.008 });
        Assert.Equal(v2Plain.ModelSha256, v2Off.ModelSha256);
        Assert.NotEqual(v2Plain.ModelSha256, v2On.ModelSha256);
    }
}
