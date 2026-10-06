// Desktop preferences and their validation stay independent from Godot nodes.
// Display preferences are shell-owned; simulation parameters are copied into a
// new Scenario only when the next live session is created.

using Sim.Core;
using Sim.Protocol;
using Sim.VisionReplay;
using System.Text.Json.Serialization;

namespace Sim.GodotShell;

public static class ControllerModes
{
    public const string BuiltIn = "builtin";

    /// <summary>MBri 移植内置控制器 (场景 vehicles[].controller = "mbri")。</summary>
    public const string Mbri = VehicleControllers.Mbri;

    public const string External = "external";
}

public static class DisplayModes
{
    public const string Windowed = "windowed";
    public const string Fullscreen = "fullscreen";
}

public static class VisionSources
{
    /// <summary>默认: 不注入 adapter, 引擎内部 ClassifyRateVision 随机桩 ⇒ 行为逐位不变。</summary>
    public const string ClassifyRate = "classifyRate";

    /// <summary>既有证据包回放: 哈希锁定读包 → VisionReplayAdapter(与 `vision evaluate` 同一实现)。</summary>
    public const string VisionReplay = "visionReplay";

    /// <summary>真车 CSV 检测流的实时桥: CsvStreamSource + LiveVisionBridge。</summary>
    public const string LiveBridge = "liveBridge";

    /// <summary>外部 YOLO 推理进程的实时桥: stdout JSONL → ExternalProcessStreamSource + LiveVisionBridge。</summary>
    public const string LiveProcess = "liveProcess";
}

public sealed record WindowSettings
{
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public string Mode { get; init; } = DisplayModes.Windowed;
}

public sealed record ControllerProfile
{
    public string Mode { get; init; } = ControllerModes.BuiltIn;

    public string Command { get; init; } = "";

    public double TimeoutMs { get; init; } = 100;

    public bool IsExternal => string.Equals(Mode, ControllerModes.External, StringComparison.Ordinal);

    /// <summary>MBri 移植内置控制器档 (不需要命令/超时, 不启动子进程)。</summary>
    public bool IsMbri => string.Equals(Mode, ControllerModes.Mbri, StringComparison.Ordinal);
}

/// <summary>
/// 小车(比赛双方同款真车)参数: 整车质量与驱动电机规格。默认 = 现役车
/// (博创尚和 2342 开环电机 12V: 减速后 120 RPM / 输出 1.72 N·m; 整车 3.5kg,
/// 含电池/电机/主控)。轮端极速由转速×轮径严格推导, 应用于 v2 真车几何场景。
/// </summary>
public sealed record VehicleSettings
{
    /// <summary>整车质量(kg, 含电池/电机/主控)。</summary>
    public double Mass { get; init; } = 3.5;

    /// <summary>减速箱输出空载转速(RPM)。</summary>
    public double MotorRpm { get; init; } = 120;

    /// <summary>减速箱输出额定扭矩(N·m)。当前仿真是速度伺服, 该值存档备后续力矩级建模。</summary>
    public double MotorTorque { get; init; } = 1.72;

    /// <summary>驱动轮半径(m), 装配实测。</summary>
    public double WheelRadius { get; init; } = 0.0325;

    /// <summary>轮端极速 = rpm/60 × 2π × r (m/s)。派生值, 不随设置文件持久化。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double MaxSpeed => MotorRpm / 60.0 * 2 * Math.PI * WheelRadius;

    /// <summary>
    /// 传感器预设覆盖 (null = 跟随场景自带 profile, 行为不变)。显式指定时以该
    /// 预设为基底, 叠加通道开关与挂点偏移生成自定义 profile 写入双方车辆。
    /// </summary>
    public string? SensorProfileId { get; init; }

    /// <summary>禁用的通道 id 清单 (读数恒为下限, 逻辑别名经容错降级)。</summary>
    public List<string> SensorDisabled { get; init; } = new();

    /// <summary>每通道挂点/朝向偏移 (车体系, 实车标定"挪探头"语义)。</summary>
    public Dictionary<string, SensorOffset> SensorOffsets { get; init; } = new();
}

/// <summary>单通道传感器偏移 (m / rad): 车体系 dx=前向, dy=横向, dz=高度, dyaw=朝向。</summary>
public sealed record SensorOffset(double Dx, double Dy, double Dz, double Yaw);

/// <summary>
/// 桌面视觉源四选一。默认 classifyRate = 不注入 adapter(引擎内部随机桩, 行为逐位
/// 不变); visionReplay 读显式证据包目录; liveBridge 读显式真车 CSV 并按固定
/// maxAgeMs 窗口供帧; liveProcess 启动外部 YOLO 推理进程(stdout JSONL 逐帧 flush),
/// 由 LiveVisionBridge 消费 —— 外部流按墙钟到达, 桌面实时模式天然 1x。
/// </summary>
public sealed record VisionSettings
{
    /// <summary>classifyRate | visionReplay | liveBridge | liveProcess。</summary>
    public string Source { get; init; } = VisionSources.ClassifyRate;

    /// <summary>visionReplay 的证据包目录(frames.jsonl + import-report.json)。</summary>
    public string EvidencePath { get; init; } = "";

    /// <summary>liveBridge 的真车 MBri hunt 方言 CSV 路径。</summary>
    public string CsvPath { get; init; } = "";

    /// <summary>liveProcess 的外部 YOLO 推理命令行 (stdout 逐帧 JSONL, 子进程必须逐帧 flush)。</summary>
    public string ProcessCommand { get; init; } = "";

    /// <summary>帧过期窗口(ms): 旧于窗口的帧返回 unknown("stale")。默认与 CLI 同值。</summary>
    public double MaxAgeMs { get; init; } = LiveVisionBridge.DefaultMaxAgeMs;
}

/// <summary>
/// 桌面能量块布局覆盖(2026-10-04 设置页)。两个数量都为 null = 跟随场景(不改,
/// 既有回放/比赛逐位不变)。落位: 默认"官方坐标优先"——前两个增益/第一个减益用
/// OfficialLayout 冻结坐标, 多出的块 X/Y=null 交给引擎裁判按 seed 确定性放置
/// (RespawnBlock 禁区: 避台沿 0.35m/避两车 0.8m/避中央 0.6m/块间 0.5m);
/// RandomPositions 则全部 null。合计超过 Scenario.MaxBlocks 时按 增益优先 截断。
/// </summary>
public sealed record BlockLayoutSettings
{
    /// <summary>增益块数量; null = 跟随场景。</summary>
    public int? BuffCount { get; init; }

    /// <summary>减益块数量; null = 跟随场景。</summary>
    public int? DebuffCount { get; init; }

    /// <summary>true = 全部由裁判按种子随机放置(忽略官方坐标)。</summary>
    public bool RandomPositions { get; init; }

    /// <summary>两个数量都为 null 时为"跟随场景"覆盖(等价于无覆盖)。</summary>
    [JsonIgnore]
    public bool IsFollowScenario => BuffCount is null && DebuffCount is null;
}

/// <summary>
/// 物理后端覆盖档位字符串 (DesktopSettings JSON 值, 批2 R2.1)。与
/// <see cref="PhysicsSpec"/> 的 backend/modelVersion 组合一一对应; Follow = 不改场景
/// 字段 (旧配置缺省即此档, 行为逐位不变)。
/// </summary>
public static class MatchBackendOverrides
{
    /// <summary>跟随场景 (默认, 与 null 等价)。</summary>
    public const string Follow = "follow";

    /// <summary>legacy 2D 物理 (覆盖后 modelVersion 必须为空)。</summary>
    public const string Legacy = PhysicsSpec.Legacy;

    /// <summary>MuJoCo 真车几何 v1 模型。</summary>
    public const string MujocoV1 = "mujoco-v1";

    /// <summary>MuJoCo 真车几何 v2 模型 (唯一支持小车页质量/转速/轮径/传感器覆盖的档)。</summary>
    public const string MujocoV2 = "mujoco-v2";
}

/// <summary>
/// "比赛/场景"页的运行时覆盖 (批2 R2.1): 全部字段缺省 = 跟随场景/启动值, 老配置
/// 反序列化后自动落缺省档, 逐位不变。物理后端覆盖在每次会话重建时按场景字段重新
/// 装配 (spike 2026-10-06: MatchEngineHost.Create 每次调用都按 scenario.physics 分派),
/// 故"应用后自动重开当前对局"即热切生效。
/// </summary>
public sealed record MatchOverrides
{
    /// <summary>
    /// null / "follow" = 跟随场景; 否则 "legacy" | "mujoco-v1" | "mujoco-v2"。
    /// 覆盖时同时写 physics.backend 与 physics.modelVersion。
    /// </summary>
    public string? PhysicsBackendOverride { get; init; }

    /// <summary>场景文件路径; 空 = 跟随启动场景 (--scenario-path 或官方布局)。</summary>
    public string ScenarioPath { get; init; } = "";

    /// <summary>比赛时长 (s); null = 跟随场景 field.matchDuration。</summary>
    public double? MatchDuration { get; init; }

    /// <summary>随机种子; null = 跟随启动/场景 seed。上限与 BatchCommand 种子上限一致。</summary>
    public int? Seed { get; init; }

    /// <summary>后端档为跟随 (null/空/"follow") 时为 true = 不覆盖场景物理字段。</summary>
    [JsonIgnore]
    public bool IsBackendFollow => PhysicsBackendOverride is null or "" || PhysicsBackendOverride == MatchBackendOverrides.Follow;

    /// <summary>全部字段都缺省 = 无覆盖 (等价于未启用该页任何覆盖)。</summary>
    [JsonIgnore]
    public bool IsFollowScenario => IsBackendFollow
        && string.IsNullOrWhiteSpace(ScenarioPath)
        && MatchDuration is null
        && Seed is null;
}

/// <summary>
/// 高级/开发者折叠区的 legacy 接触求解开关 (批2 R2.2): 默认全 true = 现行为。
/// 只被 legacy 后端消费 (PhysicsWorld 的 ContactResolveOptions); mujoco 后端不看。
/// 改动影响碰撞判定, 开启态回放需以同开关构造引擎 —— UI 必须原样披露。
/// </summary>
public sealed record DevContact
{
    /// <summary>L1: 车车 OBB 稳态分离 + 台壁位移钳位。</summary>
    public bool L1VehicleVehicleObb { get; init; } = true;

    /// <summary>L2: 车块 OBB 分离 + 推块速度镜像。</summary>
    public bool L2VehicleBlockObb { get; init; } = true;

    /// <summary>L3: 块-台壁阻挡。</summary>
    public bool L3BlockWallBlock { get; init; } = true;
}

public sealed record DesktopSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public WindowSettings Window { get; init; } = new();

    public double UiScale { get; init; } = 1.0;

    public Dictionary<string, double> SimulationParameters { get; init; } = new();

    public VehicleSettings Vehicle { get; init; } = new();

    /// <summary>能量块布局覆盖; null = 跟随场景(不改, 逐位不变)。</summary>
    public BlockLayoutSettings? BlockLayout { get; init; }

    /// <summary>
    /// 比赛/场景覆盖 (批2); null = 全部跟随场景/启动值 (老配置缺省, 不落盘不产生影响)。
    /// </summary>
    public MatchOverrides? MatchOverrides { get; init; }

    /// <summary>
    /// 高级/开发者 legacy 接触开关 (批2); null = 全开 = 现行为 (老配置缺省)。
    /// </summary>
    public DevContact? DevContact { get; init; }

    public VisionSettings Vision { get; init; } = new();

    public ControllerProfile UsController { get; init; } = new();

    public ControllerProfile ThemController { get; init; } = new();

    public static DesktopSettings Default => new();

    public IEnumerable<string> Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            yield return $"settings: unsupported schemaVersion '{SchemaVersion}'.";
        }

        if (Window is null)
        {
            yield return "settings: window must be present.";
        }
        else
        {
            if (Window.Width is < 640 or > 7680)
            {
                yield return "settings: window.width must be between 640 and 7680.";
            }
            if (Window.Height is < 360 or > 4320)
            {
                yield return "settings: window.height must be between 360 and 4320.";
            }
            if (Window.Mode is not (DisplayModes.Windowed or DisplayModes.Fullscreen))
            {
                yield return $"settings: unsupported window.mode '{Window.Mode}'.";
            }
        }

        if (!double.IsFinite(UiScale) || UiScale is < 0.8 or > 1.4)
        {
            yield return "settings: uiScale must be a finite value between 0.8 and 1.4.";
        }

        foreach (var error in SimulationParameterCatalog.Validate(SimulationParameters))
        {
            yield return error;
        }

        if (Vehicle is null)
        {
            yield return "settings: vehicle must be present.";
        }
        else
        {
            if (!double.IsFinite(Vehicle.Mass) || Vehicle.Mass is < 0.2 or > 20)
            {
                yield return "settings: vehicle.mass must be between 0.2 and 20 kg.";
            }
            if (!double.IsFinite(Vehicle.MotorRpm) || Vehicle.MotorRpm is < 10 or > 2000)
            {
                yield return "settings: vehicle.motorRpm must be between 10 and 2000.";
            }
            if (!double.IsFinite(Vehicle.MotorTorque) || Vehicle.MotorTorque is < 0.05 or > 50)
            {
                yield return "settings: vehicle.motorTorque must be between 0.05 and 50 N·m.";
            }
            if (!double.IsFinite(Vehicle.WheelRadius) || Vehicle.WheelRadius is < 0.005 or > 0.1)
            {
                yield return "settings: vehicle.wheelRadius must be between 0.005 and 0.1 m.";
            }
            if (Vehicle.SensorProfileId is not null
                && Vehicle.SensorProfileId != SensorProfiles.WheeledCombat11.Id
                && Vehicle.SensorProfileId != SensorProfiles.Legacy14.Id)
            {
                yield return $"settings: vehicle.sensorProfileId must be null (follow scenario), '{SensorProfiles.WheeledCombat11.Id}' or '{SensorProfiles.Legacy14.Id}'.";
            }
            foreach (var offset in Vehicle.SensorOffsets.Values)
            {
                if (!double.IsFinite(offset.Dx) || !double.IsFinite(offset.Dy)
                    || !double.IsFinite(offset.Dz) || !double.IsFinite(offset.Yaw)
                    || Math.Abs(offset.Dx) > 0.5 || Math.Abs(offset.Dy) > 0.5
                    || Math.Abs(offset.Dz) > 0.2 || Math.Abs(offset.Yaw) > Math.PI)
                {
                    yield return "settings: vehicle.sensorOffsets must be finite with |dx|,|dy| <= 0.5 m, |dz| <= 0.2 m, |yaw| <= π.";
                    break;
                }
            }
        }

        if (Vision is null)
        {
            yield return "settings: vision must be present.";
        }
        else
        {
            if (Vision.Source is not (VisionSources.ClassifyRate or VisionSources.VisionReplay
                or VisionSources.LiveBridge or VisionSources.LiveProcess))
            {
                yield return $"settings: unsupported vision.source '{Vision.Source}'.";
            }
            if (Vision.Source == VisionSources.VisionReplay && string.IsNullOrWhiteSpace(Vision.EvidencePath))
            {
                yield return "settings: vision.evidencePath is required for the visionReplay source.";
            }
            if (Vision.Source == VisionSources.LiveBridge && string.IsNullOrWhiteSpace(Vision.CsvPath))
            {
                yield return "settings: vision.csvPath is required for the liveBridge source.";
            }
            if (Vision.Source == VisionSources.LiveProcess && string.IsNullOrWhiteSpace(Vision.ProcessCommand))
            {
                yield return "settings: vision.processCommand is required for the liveProcess source.";
            }
            if (!double.IsFinite(Vision.MaxAgeMs) || Vision.MaxAgeMs is < 1 or > 5000)
            {
                yield return "settings: vision.maxAgeMs must be between 1 and 5000.";
            }
        }

        foreach (var (name, profile) in new[]
        {
            ("usController", UsController),
            ("themController", ThemController),
        })
        {
            if (profile is null)
            {
                yield return $"settings: {name} must be present.";
                continue;
            }
            if (profile.Mode is not (ControllerModes.BuiltIn or ControllerModes.Mbri or ControllerModes.External))
            {
                yield return $"settings: {name}.mode must be 'builtin', 'mbri' or 'external'.";
            }
            if (profile.IsExternal && string.IsNullOrWhiteSpace(profile.Command))
            {
                yield return $"settings: {name}.command is required for an external controller.";
            }
            if (!double.IsFinite(profile.TimeoutMs) || profile.TimeoutMs is < 1 or > 5000)
            {
                yield return $"settings: {name}.timeoutMs must be between 1 and 5000.";
            }
        }

        // 批2 比赛/场景覆盖: 全部可选; 缺省(null/空)通过 = 老配置逐位不变。
        if (MatchOverrides is { } match)
        {
            if (!match.IsBackendFollow
                && match.PhysicsBackendOverride is not (MatchBackendOverrides.Legacy
                    or MatchBackendOverrides.MujocoV1 or MatchBackendOverrides.MujocoV2))
            {
                yield return "settings: matchOverrides.physicsBackendOverride must be null (follow scenario), "
                    + "'follow', 'legacy', 'mujoco-v1' or 'mujoco-v2'.";
            }
            if (match.MatchDuration is { } duration && (!double.IsFinite(duration) || duration <= 0))
            {
                yield return "settings: matchOverrides.matchDuration must be a finite value greater than 0.";
            }
            if (match.Seed is { } seed && seed is < 0 or > 4096)
            {
                yield return "settings: matchOverrides.seed must be between 0 and 4096.";
            }
        }
    }

    public bool IsValid => !Validate().Any();

    /// <summary>
    /// 把桌面控制器档位写进场景 <c>vehicles[].controller</c> (协议加性字段):
    /// 仅显式选择"内置 MBri"的角色写入 "mbri"; builtin/external 保持场景原值
    /// (external 由 driver 进程桥在运行时覆盖, 不占场景字段)。无 MBri 选择时
    /// 原样返回同一场景 —— 既有场景序列化/回放身份逐位不变。
    /// </summary>
    public Scenario ApplyControllerSelection(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var usMbri = (UsController ?? new ControllerProfile()).IsMbri;
        var themMbri = (ThemController ?? new ControllerProfile()).IsMbri;
        if (!usMbri && !themMbri)
        {
            return scenario;
        }
        if (scenario.Vehicles is null || scenario.Vehicles.Count == 0)
        {
            return scenario;
        }
        var vehicles = new Dictionary<string, VehicleProfile>(scenario.Vehicles, StringComparer.Ordinal);
        foreach (var (role, mbri) in new[] { (RoleNames.Us, usMbri), (RoleNames.Them, themMbri) })
        {
            if (!mbri || !vehicles.TryGetValue(role, out var profile) || profile is null)
            {
                continue;
            }
            vehicles[role] = profile with { Controller = VehicleControllers.Mbri };
        }
        return scenario with { Vehicles = vehicles };
    }

    /// <summary>
    /// 比赛/场景覆盖 (批2, 装配链最先): 仅非跟随档才写 physics.backend/
    /// physics.modelVersion、field.matchDuration 与 seed; 无任何覆盖时原样返回同一
    /// 场景 —— 老配置/旧 bundle 行为逐位不变。覆盖在后端字段上写出完整合法组合
    /// (legacy 不带 modelVersion, mujoco 必带), 场景 Validate 不会因此产生新违规。
    /// 场景文件路径 (ScenarioPath) 是"选模板"而不是字段覆盖, 由 Main 的加载入口处理。
    /// </summary>
    public Scenario ApplyMatchOverrides(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var match = MatchOverrides ?? new MatchOverrides();
        var result = scenario;
        if (!match.IsBackendFollow)
        {
            result = match.PhysicsBackendOverride switch
            {
                MatchBackendOverrides.Legacy =>
                    result with { Physics = new PhysicsSpec { Backend = PhysicsSpec.Legacy } },
                MatchBackendOverrides.MujocoV1 =>
                    result with
                    {
                        Physics = new PhysicsSpec
                        {
                            Backend = PhysicsSpec.Mujoco,
                            ModelVersion = PhysicsSpec.MujocoModelV1,
                        },
                    },
                MatchBackendOverrides.MujocoV2 =>
                    result with
                    {
                        Physics = new PhysicsSpec
                        {
                            Backend = PhysicsSpec.Mujoco,
                            ModelVersion = PhysicsSpec.MujocoModelV2,
                        },
                    },
                _ => result,
            };
        }
        if (match.MatchDuration is { } duration)
        {
            result = result with { Field = result.Field with { MatchDuration = duration } };
        }
        if (match.Seed is { } seed)
        {
            result = result with { Seed = seed };
        }
        return result;
    }

    /// <summary>
    /// 高级/开发者 legacy 接触开关 → MatchEngine 注入选项 (批2 R2.2)。缺省全开 =
    /// 既有默认物理; 无覆盖时返回的对象与 <c>new ContactResolveOptions()</c> 逐位一致。
    /// </summary>
    public ContactResolveOptions CreateContactResolveOptions()
    {
        var dev = DevContact ?? new DevContact();
        return new ContactResolveOptions
        {
            RobotPairObbSeparation = dev.L1VehicleVehicleObb,
            RobotBlockObbSeparation = dev.L2VehicleBlockObb,
            BlockStageWall = dev.L3BlockWallBlock,
        };
    }

    /// <summary>
    /// Applies only explicit desktop overrides to a fresh scenario copy. The
    /// settings object never mutates the caller's parameter dictionary.
    /// </summary>
    public Scenario ApplySimulationParameters(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (SimulationParameters is null || SimulationParameters.Count == 0)
        {
            return scenario;
        }

        var merged = scenario.Parameters is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(scenario.Parameters, StringComparer.Ordinal);
        foreach (var (key, value) in SimulationParameters)
        {
            merged[key] = value;
        }
        return scenario with { Parameters = merged };
    }

    /// <summary>
    /// 应用小车设置(质量 + 轮端极速推导)到 v2 真车几何场景的 us/them。
    /// 其余场景(legacy/v1)不触碰 —— 既有回放/测试身份逐位不变。
    /// </summary>
    public Scenario ApplyVehicleOverrides(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Physics is not { Backend: PhysicsSpec.Mujoco, ModelVersion: PhysicsSpec.MujocoModelV2 })
        {
            return scenario;
        }
        if (scenario.Vehicles is null || scenario.Vehicles.Count == 0)
        {
            return scenario;
        }
        var vehicles = new Dictionary<string, VehicleProfile>(scenario.Vehicles, StringComparer.Ordinal);
        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            if (vehicles.TryGetValue(role, out var profile))
            {
                vehicles[role] = profile with
                {
                    Mass = Vehicle.Mass,
                    MaxSpeed = Vehicle.MaxSpeed,
                    Sensors = ResolveSensorProfile(profile),
                };
            }
        }
        return scenario with { Vehicles = vehicles };
    }

    /// <summary>
    /// <summary>
    /// 应用能量块布局覆盖(设置页"能量块"): 数量/类型可调, 落位默认官方坐标优先。
    /// 无覆盖(IsFollowScenario)时原样返回同一场景 —— 既有回放/比赛逐位不变。
    /// 合计超过 MaxBlocks 时增益优先截断(与设置页提示一致)。
    /// </summary>
    public Scenario ApplyBlocks(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var layout = BlockLayout;
        if (layout is null || layout.IsFollowScenario)
        {
            return scenario;
        }
        var buffCount = Math.Clamp(layout.BuffCount ?? 0, 0, Scenario.MaxBlocks);
        var debuffCount = Math.Clamp(layout.DebuffCount ?? 0, 0, Scenario.MaxBlocks - buffCount);
        var officialBuffs = OfficialLayout.Blocks.Where(b => b.Kind == BlockKind.Buff).ToList();
        var officialDebuffs = OfficialLayout.Blocks.Where(b => b.Kind == BlockKind.Debuff).ToList();

        BlockSpec Spec(BlockKind kind, int index, IReadOnlyList<BlockSpec> official)
            => layout.RandomPositions || index >= official.Count
                ? new BlockSpec { Kind = kind }
                : new BlockSpec { Kind = kind, X = official[index].X, Y = official[index].Y };

        var blocks = new List<BlockSpec>();
        for (var i = 0; i < buffCount; i++)
        {
            blocks.Add(Spec(BlockKind.Buff, i, officialBuffs));
        }
        for (var i = 0; i < debuffCount; i++)
        {
            blocks.Add(Spec(BlockKind.Debuff, i, officialDebuffs));
        }
        return scenario with { Blocks = blocks };
    }

    /// <summary>
    /// 传感器覆盖解析: 显式预设(或场景自带 profile)为基底, 叠加通道开关与挂点
    /// 偏移生成自定义 profile 写入车辆; 无任何覆盖时原样返回场景自带 profile
    /// (返回 null 会把它清空成 fallback, 违反"无覆盖位不变")。
    /// </summary>
    private SensorProfile? ResolveSensorProfile(VehicleProfile profile)
    {
        var vehicle = Vehicle;
        if (vehicle.SensorProfileId is null && vehicle.SensorDisabled.Count == 0 && vehicle.SensorOffsets.Count == 0)
        {
            return profile.Sensors;
        }
        var baseProfile = vehicle.SensorProfileId switch
        {
            null => profile.Sensors ?? SensorProfiles.Legacy14,
            var id when id == SensorProfiles.WheeledCombat11.Id => SensorProfiles.WheeledCombat11,
            var id when id == SensorProfiles.Legacy14.Id => SensorProfiles.Legacy14,
            var other => throw new InvalidOperationException($"settings: unknown sensor profile '{other}'."),
        };
        return SensorProfileCustomizer.Apply(
            baseProfile,
            $"custom:{baseProfile.Id}",
            vehicle.SensorDisabled,
            vehicle.SensorOffsets.ToDictionary(
                kv => kv.Key,
                kv => new SensorProfileCustomizer.ChannelOffset(kv.Value.Dx, kv.Value.Dy, kv.Value.Dz, kv.Value.Yaw)));
    }

    /// <summary>
    /// 按视觉设置构造"每场一次"的视觉源工厂: 默认 classifyRate 返回 null —— 不注入
    /// adapter, 引擎内部照旧构造 ClassifyRateVision(默认链路逐位不变)。只有显式选择
    /// visionReplay/liveBridge 时才读取证据包/CSV, 且构造工厂时先整体预检一次
    /// (路径/哈希/方言/行级校验), 失败立即抛出 —— 壳层据此在"应用设置"时高声报错并
    /// 停下, 绝不静默换源。适配器带每场消费台账与 SimT 0 基准, 故工厂每次调用都新建
    /// 实例, 绝不跨场复用(与 VehicleSettings.ApplyVehicleOverrides 同样的"仅显式
    /// 非默认设置才生效"范围语义)。
    /// </summary>
    public Func<IVisionAdapter?>? CreateVisionFactory()
    {
        var vision = Vision ?? new VisionSettings();
        switch (vision.Source)
        {
            case VisionSources.ClassifyRate:
                return null;
            case VisionSources.VisionReplay:
            {
                var package = VisionEvidencePackage.Load(vision.EvidencePath);
                var session = package.SelectSession(null);
                return () => package.CreateAdapter(session, vision.MaxAgeMs);
            }
            case VisionSources.LiveBridge:
            {
                // 预检一次让坏路径/非方言 CSV 在"应用设置"时暴露; 源带释放游标, 每场
                // 必须从磁盘重读(第二次走 OS 缓存, 只剩解析成本), 不复用游标。
                _ = CsvStreamSource.Load(vision.CsvPath);
                return () => new LiveVisionBridge(CsvStreamSource.Load(vision.CsvPath), vision.MaxAgeMs);
            }
            case VisionSources.LiveProcess:
            {
                // 预检: 启动一次进程源再立即释放 —— 坏命令行/进程起不来在"应用设置"时
                // 响亮暴露(同 CSV 预检先例); 每场新起进程, 旧场适配器由 MatchSession 释放。
                using var probe = ExternalProcessStreamSource.Start(vision.ProcessCommand);
                return () => new LiveVisionBridge(ExternalProcessStreamSource.Start(vision.ProcessCommand), vision.MaxAgeMs);
            }
            default:
                throw new InvalidOperationException(
                    $"settings: unsupported vision.source '{vision.Source}' (Validate 应先拦截)。");
        }
    }
}

/// <summary>
/// Small file-system boundary for the desktop settings file. It is kept
/// Godot-free so validation and persistence behavior can be tested without
/// starting a renderer; the Godot shell supplies the user://-globalized path.
/// </summary>
public sealed class SettingsStore
{
    public const string DefaultFileName = "wushu-ring-settings.json";

    private readonly string _path;
    private readonly Action<string>? _diagnostic;

    public SettingsStore(string path, Action<string>? diagnostic = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Settings path must not be empty.", nameof(path));
        }
        _path = Path.GetFullPath(path);
        _diagnostic = diagnostic;
    }

    public DesktopSettings Load()
    {
        if (!File.Exists(_path))
        {
            return DesktopSettings.Default;
        }

        try
        {
            var settings = ProtocolJson.Deserialize<DesktopSettings>(File.ReadAllText(_path));
            if (settings is null)
            {
                Report("配置内容为空对象，已回退默认值");
                return DesktopSettings.Default;
            }
            var errors = settings.Validate().ToArray();
            if (errors.Length > 0)
            {
                Report($"配置校验失败，已回退默认值: {string.Join(" | ", errors)}");
                return DesktopSettings.Default;
            }
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Report($"配置读取失败，已回退默认值: {error.Message}");
            return DesktopSettings.Default;
        }
    }

    public void Save(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate().ToArray();
        if (errors.Length > 0)
        {
            throw new ArgumentException($"Invalid desktop settings: {string.Join(" | ", errors)}", nameof(settings));
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, ProtocolJson.Serialize(settings));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void Report(string message) => _diagnostic?.Invoke($"[settings] {message}");
}

public sealed record SimulationParameterDefinition(
    string Key,
    string Label,
    string Unit,
    string Group,
    double DefaultValue,
    double Minimum,
    double Maximum,
    double Step,
    bool Experimental,
    bool Integer,
    bool AllowAutomatic = false,
    bool MinimumExclusive = false,
    bool MaximumExclusive = false)
{
    public bool IsValid(double value)
    {
        if (!double.IsFinite(value))
        {
            return false;
        }
        var minOk = MinimumExclusive ? value > Minimum : value >= Minimum;
        var maxOk = MaximumExclusive ? value < Maximum : value <= Maximum;
        if (!minOk || !maxOk)
        {
            return false;
        }
        return !Integer || Math.Abs(value - Math.Round(value)) < 1e-9;
    }
}

/// <summary>
/// UI-facing whitelist for every key accepted by SimParameters.FromDictionary.
/// Bounds are shell input guards; they do not change the core's parameter model.
/// </summary>
public static class SimulationParameterCatalog
{
    private static readonly IReadOnlyList<SimulationParameterDefinition> Definitions =
    [
        new("EDGE_THRESHOLD", "边缘阈值", "灰度", "常用", 400, 0, 1000, 1, false, true),
        new("FALL_THRESHOLD", "掉台阈值", "灰度", "常用", 150, 0, 1000, 1, false, true),
        new("ON_STAGE_THRESHOLD", "登台阈值", "灰度", "常用", 500, 0, 1000, 1, false, true),
        new("grayNoise", "灰度噪声", "±灰度", "常用", 30, 0, 1000, 1, true, true),
        new("irNoise", "红外噪声", "比例", "常用", 0.02, 0, 1, 0.01, true, false),
        new("IR_TRIGGER", "红外触发", "比例", "常用", 0.35, 0, 1, 0.01, true, false),
        new("MOUNT_SPEED", "登台速度", "代码单位", "常用", 780, 0, 2000, 1, true, false),
        new("classifyRate", "视觉识别成功率", "%", "常用", 100, 0, 100, 1, true, false),
        new("RECOVER_LIMIT", "恢复次数上限", "次", "常用", 3, 0, 100, 1, false, true),
        new("STALL_TIME", "堵转持续时间", "s", "高级", 0.4, 0, 30, 0.01, true, false),
        new("STALL_SPEED", "堵转速度阈值", "m/s", "高级", 0.03, 0, 3, 0.001, true, false),
        new("STALL_RELEASE", "堵转解除速度", "m/s", "高级", 0.06, 0, 3, 0.001, true, false),
        new("STALL_DISPLACEMENT", "堵转位移阈值", "m/窗口", "高级", 0.006, 0, 1, 0.001, true, false),
        new("cmdLatencyFrames", "指令延迟", "帧", "高级", 0, 0, 120, 1, true, true),
        new("IR_HYST_BAND", "红外迟滞带", "比例", "高级", 0.10, 0, 1, 0.01, true, false),
        new("graySpotRadius", "灰度光斑半径", "m", "高级", 0.025, 0, 1, 0.001, true, false),
        new("BLOCK_STICK_SPEED", "方块静摩擦阈值", "m/s", "高级", 0.02, 0, 3, 0.001, true, false),
        new("BLOCK_MU_K", "方块动摩擦系数", "μ", "高级", 0.5, 0, 10, 0.01, true, false),
        new("COLLISION_RESTITUTION", "碰撞恢复系数", "比例", "高级", 0.5, 0, 1, 0.01, true, false, true),
        new("MOUNT_V_MIN", "登台法向速度", "m/s", "高级", 0.3, 0, 2, 0.01, true, false, false, true),
        new("MOUNT_ANGLE_MAX", "登台最大入射角", "rad", "高级", 0.26, 0, 1.2, 0.01, true, false, false, true, true),
        new("antiStallBladeAmp", "反僵局铲刃振幅", "m", "高级", 0.006, 0, 0.1, 0.001, true, false, true),
        new("antiStallBladePeriodUs", "我方反僵局周期", "s", "高级", 2.1, 0, 60, 0.1, true, false, true, true),
        new("antiStallBladePeriodThem", "对手反僵局周期", "s", "高级", 2.7, 0, 60, 0.1, true, false, true, true),
    ];

    public static IReadOnlyList<SimulationParameterDefinition> All => Definitions;

    private static readonly IReadOnlyDictionary<string, SimulationParameterDefinition> ByKey =
        Definitions.ToDictionary(item => item.Key, StringComparer.Ordinal);

    public static bool TryGet(string key, out SimulationParameterDefinition definition)
        => ByKey.TryGetValue(key, out definition!);

    public static IEnumerable<SimulationParameterDefinition> ForGroup(string group)
        => Definitions.Where(item => string.Equals(item.Group, group, StringComparison.Ordinal));

    public static IEnumerable<string> Validate(IReadOnlyDictionary<string, double>? values)
    {
        if (values is null)
        {
            yield break;
        }
        foreach (var (key, value) in values)
        {
            if (!ByKey.TryGetValue(key, out var definition))
            {
                yield return $"settings: unknown simulation parameter '{key}'.";
                continue;
            }
            if (!definition.IsValid(value))
            {
                var bounds = definition.MinimumExclusive
                    ? $"> {definition.Minimum.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"
                    : $">= {definition.Minimum.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
                var upper = definition.MaximumExclusive
                    ? $"< {definition.Maximum.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"
                    : $"<= {definition.Maximum.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
                yield return $"settings: simulation parameter '{key}' must be finite, {bounds} and {upper}"
                    + (definition.Integer ? ", and an integer." : ".");
            }
        }
    }
}
