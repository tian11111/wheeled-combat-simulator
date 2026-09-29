// Desktop preferences and their validation stay independent from Godot nodes.
// Display preferences are shell-owned; simulation parameters are copied into a
// new Scenario only when the next live session is created.

using Sim.Core;
using Sim.Protocol;
using Sim.VisionReplay;

namespace Sim.GodotShell;

public static class ControllerModes
{
    public const string BuiltIn = "builtin";
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
}

/// <summary>
/// 桌面视觉源三选一(外部进程源不进桌面, CLI-only)。默认 classifyRate = 不注入
/// adapter(引擎内部随机桩, 行为逐位不变); visionReplay 读显式证据包目录;
/// liveBridge 读显式真车 CSV 并按固定 maxAgeMs 窗口供帧。
/// </summary>
public sealed record VisionSettings
{
    /// <summary>classifyRate | visionReplay | liveBridge。</summary>
    public string Source { get; init; } = VisionSources.ClassifyRate;

    /// <summary>visionReplay 的证据包目录(frames.jsonl + import-report.json)。</summary>
    public string EvidencePath { get; init; } = "";

    /// <summary>liveBridge 的真车 MBri hunt 方言 CSV 路径。</summary>
    public string CsvPath { get; init; } = "";

    /// <summary>帧过期窗口(ms): 旧于窗口的帧返回 unknown("stale")。默认与 CLI 同值。</summary>
    public double MaxAgeMs { get; init; } = LiveVisionBridge.DefaultMaxAgeMs;
}

public sealed record DesktopSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public WindowSettings Window { get; init; } = new();

    public double UiScale { get; init; } = 1.0;

    public Dictionary<string, double> SimulationParameters { get; init; } = new();

    public VehicleSettings Vehicle { get; init; } = new();

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
        }

        if (Vision is null)
        {
            yield return "settings: vision must be present.";
        }
        else
        {
            if (Vision.Source is not (VisionSources.ClassifyRate or VisionSources.VisionReplay or VisionSources.LiveBridge))
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
            if (profile.Mode is not (ControllerModes.BuiltIn or ControllerModes.External))
            {
                yield return $"settings: {name}.mode must be 'builtin' or 'external'.";
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
    }

    public bool IsValid => !Validate().Any();

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
                vehicles[role] = profile with { Mass = Vehicle.Mass, MaxSpeed = Vehicle.MaxSpeed };
            }
        }
        return scenario with { Vehicles = vehicles };
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
