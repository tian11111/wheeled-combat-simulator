using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>Sensor channel types supported by the core (legacy JSON spellings).</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<SensorType>))]
public enum SensorType
{
    Gray,
    IrGround,
    IrEdge,
    IrDistance,
    Digital,
}

/// <summary>
/// One physical sensor channel of a vehicle profile. Body-fixed coordinates:
/// <see cref="Forward"/> is along the robot heading (m, nose positive),
/// <see cref="Lateral"/> is to the robot's left (m, left positive) and
/// <see cref="Angle"/> is the sensing direction relative to the heading (rad).
/// </summary>
public sealed record SensorChannel
{
    public string Id { get; init; } = "";

    /// <summary>Display label (not used by decision logic).</summary>
    public string? Label { get; init; }

    public SensorType Type { get; init; } = SensorType.IrDistance;

    public double Forward { get; init; }

    public double Lateral { get; init; }

    /// <summary>
    /// 探点离地高度 (m, 站立于承载面时探点相对 ZG 基准面的世界高度)。
    /// null = 旧 2D 平面语义(逐位兼容路径); 配置后探点世界坐标含姿态换算的
    /// 高度, 地面类读数随姿态变化(3D 感知)。来源 = 装配.glb 节点车体系 z
    /// (原点=轮轴平面) + 轮半径 0.0325; 真车底盘灰度贴地 → 0.0025。
    /// </summary>
    public double? Height { get; init; }

    public double Angle { get; init; }

    /// <summary>Sensing range in meters. Ground/gray channels use 0.</summary>
    public double Range { get; init; } = 0.9;

    /// <summary>Half field-of-view in radians.</summary>
    public double Fov { get; init; } = 0.35;

    /// <summary>Semantic mode: "ground", "edge", "target", "edge_target", "fence".</summary>
    public string Mode { get; init; } = "target";

    /// <summary>Lower output bound of the channel.</summary>
    public double Min { get; init; }

    /// <summary>Upper output bound: gray → 1000, digital → 1, others → 1.2 by default.</summary>
    public double Max { get; init; } = 1.2;

    /// <summary>Optional per-channel noise amplitude (0 or absent = noiseless).</summary>
    public double? Noise { get; init; }

    /// <summary>True when a high output means "reflection detected" (front shovel IR is active-high).</summary>
    public bool ActiveHigh { get; init; } = true;

    /// <summary>
    /// 禁用通道 (add-only, 默认 false 位不变): 采样读数恒为下限、探点按无命中处理、
    /// 不做 raycast。用于桌面"传感器覆盖"的通道开关 —— 逻辑别名映射保留, 缺读数
    /// 经 SensorSampler 既有容错自然降级 (FSM 门限不触发)。默认值不序列化(wire 不变)。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            yield return "sensor channel id must not be empty.";
        }
        if (Range < 0)
        {
            yield return $"sensor channel '{Id}': range must be >= 0 (0 is valid for ground/gray channels).";
        }
        if (Fov < 0)
        {
            yield return $"sensor channel '{Id}': fov must be >= 0 (0 is valid for ground/gray channels).";
        }
        if (Height is { } height && (!double.IsFinite(height) || height is < -0.2 or > 0.5))
        {
            yield return $"sensor channel '{Id}': height must be a finite number in [-0.2, 0.5] m (body frame; chassis probes are negative).";
        }
        if (Max <= Min)
        {
            yield return $"sensor channel '{Id}': max must be greater than min.";
        }
        if (Noise is < 0)
        {
            yield return $"sensor channel '{Id}': noise must be >= 0.";
        }
    }
}

/// <summary>
/// A per-vehicle sensor profile. <see cref="Channels"/> is the authoritative
/// source of the real hardware channels exposed through observation
/// "rawSensors"; <see cref="Logical"/> maps the legacy compatibility aliases
/// (observation "sensors") onto those channels.
/// </summary>
public sealed record SensorProfile
{
    public string Id { get; init; } = "custom";

    public string? Label { get; init; }

    public List<SensorChannel> Channels { get; init; } = new();

    public Dictionary<string, LogicalSensorMap>? Logical { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            yield return "sensor profile id must not be empty.";
        }
        if (Channels.Count == 0)
        {
            yield return $"sensor profile '{Id}' must declare at least one channel.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in Channels)
        {
            foreach (var error in channel.Validate())
            {
                yield return $"sensor profile '{Id}': {error}";
            }
            if (!seen.Add(channel.Id))
            {
                yield return $"sensor profile '{Id}': duplicate channel id '{channel.Id}'.";
            }
        }

        if (Logical is not null)
        {
            foreach (var (alias, map) in Logical)
            {
                if (map.IsNull || map is null)
                {
                    continue;
                }
                var referenced = (map.Channels ?? Array.Empty<string>())
                    .Concat(map.Channel is not null ? new[] { map.Channel } : Array.Empty<string>());
                foreach (var channelId in referenced)
                {
                    if (!seen.Contains(channelId))
                    {
                        yield return $"sensor profile '{Id}': logical alias '{alias}' references unknown channel '{channelId}'.";
                    }
                }
            }
        }
    }
}

    /// <summary>
    /// JSON binding for sensor profiles. New capability (protocol add-only):
    /// a scene/replay may reference a built-in layout by id only,
    /// <c>{"id":"wheeledCombat11"}</c> or <c>{"id":"legacy14"}</c>, which expands
    /// to the full built-in profile. Objects carrying their own <c>channels</c>
    /// bind as before, and serialization always writes the full profile, so
    /// existing wire shapes are unchanged.
public sealed class SensorProfileJsonConverter : JsonConverter<SensorProfile>
{
    public override SensorProfile? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("sensor profile must be a JSON object.");
        }
        var element = JsonElement.ParseValue(ref reader);
        if (!element.TryGetProperty("channels", out _) && BuiltinById(element) is { } builtin)
        {
            return builtin;
        }
        return new SensorProfile
        {
            Id = element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "custom",
            Label = element.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null,
            Channels = element.TryGetProperty("channels", out var channels)
                ? channels.Deserialize<List<SensorChannel>>(ProtocolJson.BareOptions) ?? new List<SensorChannel>()
                : new List<SensorChannel>(),
            Logical = element.TryGetProperty("logical", out var logical)
                ? logical.Deserialize<Dictionary<string, LogicalSensorMap>>(ProtocolJson.BareOptions)
                : null,
        };
    }

    public override void Write(Utf8JsonWriter writer, SensorProfile value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, ProtocolJson.BareOptions);

    /// <summary>Resolves an id-only reference to the built-in profile, or null.</summary>
    private static SensorProfile? BuiltinById(JsonElement element)
        => element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() switch
            {
                "legacy14" => SensorProfiles.Legacy14,
                "wheeledCombat11" => SensorProfiles.WheeledCombat11,
                _ => null,
            }
            : null;
}

/// <summary>
/// Vehicle geometry and dynamics profile (per role). Value ranges and clamping
/// semantics are defined by CONTRACT.md section 5.1; normalization of raw user
/// input happens in Sim.Core, this DTO only performs basic sanity validation.
/// </summary>
public sealed record VehicleProfile
{
    public string Id { get; init; } = "default";

    /// <summary>Body length (m), 0.08–0.8.</summary>
    public double Length { get; init; } = 0.26;

    /// <summary>Body width (m), 0.02–0.4.</summary>
    public double Width { get; init; } = 0.26;

    /// <summary>Body height (m), 0.02–0.4.</summary>
    public double Height { get; init; } = 0.09;

    /// <summary>Center-to-front footprint extent incl. shovel (m).</summary>
    public double FrontExtent { get; init; } = 0.22;

    /// <summary>Center-to-rear footprint extent incl. shovel (m).</summary>
    public double RearExtent { get; init; } = 0.14;

    /// <summary>Center-to-side footprint extent incl. shovel (m).</summary>
    public double SideExtent { get; init; } = 0.14;

    /// <summary>Shovel overhang length (m), 0–0.5.</summary>
    public double ShovelLength { get; init; } = 0.04;

    /// <summary>Shovel width (m), 0.02–0.8.</summary>
    public double ShovelWidth { get; init; } = 0.24;

    /// <summary>Conservative collision radius for robot/robot and robot/block (m).</summary>
    public double CollisionRadius { get; init; } = 0.16;

    /// <summary>Speed limit (m/s), 0.05–3. Requested actions are clamped to this.</summary>
    public double MaxSpeed { get; init; } = 1.5;

    /// <summary>Turn-rate limit (rad/s), 0.1–12. Requested actions are clamped to this.</summary>
    public double MaxTurnRate { get; init; } = 4.0;

    /// <summary>Longitudinal acceleration convergence factor (1/s), 1–40.</summary>
    public double AccelK { get; init; } = 12;

    /// <summary>Mass (kg), 0.05–10.</summary>
    public double Mass { get; init; } = 1.0;

    /// <summary>Push factor, 0.1–3.</summary>
    public double PushFactor { get; init; } = 1.0;

    /// <summary>Wheel base (m), 0.02–0.8 — used for four-wheel sampling on the step.</summary>
    public double WheelBase { get; init; } = 0.16;

    /// <summary>Track width (m), 0.02–0.8.</summary>
    public double TrackWidth { get; init; } = 0.18;

    /// <summary>Lateral friction coefficient (1/s), 0.5–60 — lateral slip decay.</summary>
    public double LatFrictionK { get; init; } = 8;

    /// <summary>Angular damping (1/s), 0–40 — post-collision spin decay.</summary>
    public double AngDamping { get; init; } = 3;

    /// <summary>Shovel blade height above ground (m), 0–0.3 — wedge-under detection.</summary>
    public double ShovelHeight { get; init; } = 0.015;

    /// <summary>The real sensor layout of this vehicle.</summary>
    public SensorProfile? Sensors { get; init; }

    /// <summary>Default profile; mirrors the legacy DEFAULT_VEHICLE (sensors: legacy14).</summary>
    public static VehicleProfile Default { get; } = new()
    {
        Sensors = SensorProfiles.Legacy14,
    };

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            yield return "vehicle profile id must not be empty.";
        }

        string[] PositiveNames =
        [
            nameof(Length), nameof(Width), nameof(Height),
            nameof(FrontExtent), nameof(RearExtent), nameof(SideExtent),
            nameof(ShovelLength), nameof(ShovelWidth), nameof(CollisionRadius),
            nameof(MaxSpeed), nameof(MaxTurnRate), nameof(AccelK),
            nameof(Mass), nameof(PushFactor), nameof(WheelBase), nameof(TrackWidth),
        ];

        double[] PositiveValues =
        [
            Length, Width, Height, FrontExtent, RearExtent, SideExtent,
            ShovelLength, ShovelWidth, CollisionRadius, MaxSpeed, MaxTurnRate,
            AccelK, Mass, PushFactor, WheelBase, TrackWidth,
        ];

        for (var i = 0; i < PositiveValues.Length; i++)
        {
            if (!(PositiveValues[i] > 0) || !double.IsFinite(PositiveValues[i]))
            {
                yield return $"vehicle profile '{Id}': {ToWireName(PositiveNames[i])} must be a positive finite number.";
            }
        }

        if (!(ShovelHeight >= 0) || !double.IsFinite(ShovelHeight))
        {
            yield return $"vehicle profile '{Id}': shovelHeight must be a non-negative finite number.";
        }
        if (!(LatFrictionK >= 0) || !double.IsFinite(LatFrictionK))
        {
            yield return $"vehicle profile '{Id}': latFrictionK must be a non-negative finite number.";
        }
        if (!(AngDamping >= 0) || !double.IsFinite(AngDamping))
        {
            yield return $"vehicle profile '{Id}': angDamping must be a non-negative finite number.";
        }

        if (Sensors is not null)
        {
            foreach (var error in Sensors.Validate())
            {
                yield return $"vehicle profile '{Id}': {error}";
            }
        }
    }

    /// <summary>Converts a C# property name to its camelCase wire name.</summary>
    private static string ToWireName(string propertyName)
        => string.IsNullOrEmpty(propertyName) ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
}

/// <summary>
/// Built-in sensor profiles ported from the legacy core. Geometry, types and
/// ids are identical to the reference profiles; only display labels are kept
/// verbatim for trace comparability.
/// </summary>
public static class SensorProfiles
{
    private static SensorChannel Ch(
        string id, string label, SensorType type,
        double forward, double lateral, double angle,
        double range, double fov, string mode, double? height = null) => new()
    {
        Id = id,
        Label = label,
        Type = type,
        Forward = forward,
        Lateral = lateral,
        Height = height,
        Angle = angle,
        Range = range,
        Fov = fov,
        Mode = mode,
        Max = type == SensorType.Gray ? 1000 : (type == SensorType.Digital ? 1 : 1.2),
    };

    /// <summary>Legacy 14-channel compatibility profile (headless/API default).</summary>
    public static SensorProfile Legacy14 { get; } = new()
    {
        Id = "legacy14",
        Label = "兼容 14 路",
        Channels =
        [
            Ch("gF", "灰度·前", SensorType.Gray, 0.11, 0, 0, 0, 0, "ground"),
            Ch("gB", "灰度·后", SensorType.Gray, -0.11, 0, Math.PI, 0, 0, "ground"),
            Ch("gL", "灰度·左", SensorType.Gray, 0, 0.11, Math.PI / 2, 0, 0, "ground"),
            Ch("gR", "灰度·右", SensorType.Gray, 0, -0.11, -Math.PI / 2, 0, 0, "ground"),
            Ch("uL", "铲下·L", SensorType.IrGround, 0.14, 0.06, 0, 0.25, 0.35, "ground"),
            Ch("uR", "铲下·R", SensorType.IrGround, 0.14, -0.06, 0, 0.25, 0.35, "ground"),
            Ch("sFL", "铲前·L", SensorType.IrEdge, 0.14, 0.06, 0, 0.90, 0.30, "edge"),
            Ch("sFR", "铲前·R", SensorType.IrEdge, 0.14, -0.06, 0, 0.90, 0.30, "edge"),
            Ch("dLF", "对角·左前", SensorType.IrDistance, 0, 0, -Math.PI / 4, 1.60, 0.55, "target"),
            Ch("dRF", "对角·右前", SensorType.IrDistance, 0, 0, Math.PI / 4, 1.60, 0.55, "target"),
            Ch("dLB", "对角·左后", SensorType.IrDistance, 0, 0, 3 * Math.PI / 4, 1.60, 0.55, "target"),
            Ch("dRB", "对角·右后", SensorType.IrDistance, 0, 0, -3 * Math.PI / 4, 1.60, 0.55, "target"),
            Ch("f", "正前·远", SensorType.IrDistance, 0, 0, 0, 2.20, 0.40, "edge_target"),
            Ch("r", "后向", SensorType.IrDistance, 0, 0, Math.PI, 1.30, 0.70, "fence"),
        ],
        Logical = new Dictionary<string, LogicalSensorMap>
        {
            // Legacy profile: each alias maps to its own physical channel.
            ["gF"] = LogicalSensorMap.FromChannel("gF"),
            ["gB"] = LogicalSensorMap.FromChannel("gB"),
            ["gL"] = LogicalSensorMap.FromChannel("gL"),
            ["gR"] = LogicalSensorMap.FromChannel("gR"),
            ["uL"] = LogicalSensorMap.FromChannel("uL"),
            ["uR"] = LogicalSensorMap.FromChannel("uR"),
            ["sFL"] = LogicalSensorMap.FromChannel("sFL"),
            ["sFR"] = LogicalSensorMap.FromChannel("sFR"),
            ["dLF"] = LogicalSensorMap.FromChannel("dLF"),
            ["dRF"] = LogicalSensorMap.FromChannel("dRF"),
            ["dLB"] = LogicalSensorMap.FromChannel("dLB"),
            ["dRB"] = LogicalSensorMap.FromChannel("dRB"),
            ["f"] = LogicalSensorMap.FromChannel("f"),
            ["r"] = LogicalSensorMap.FromChannel("r"),
        },
    };

    /// <summary>
    /// The real 2026 wheeled-combat vehicle: 4 chassis gray + 4 diagonal digital
    /// IR + 2 shovel-under IR + 1 shovel-front IR = 11 channels. The single
    /// shovel-front channel feeds both sFL/sFR compatibility aliases; there is
    /// no dedicated front/rear digital IR, so "f" is a virtual max() of the two
    /// front diagonal channels and "r" is unmapped (compatibility value 0).
    ///
    /// Forward/Lateral 来自装配.glb 光电节点的实测车体坐标, Height 为站立时
    /// 离地高度 = 节点车体系 z(原点=轮轴平面) + 轮半径 0.0325 —— 运行时探点
    /// 世界 Z 以 ZG(承载面高度, legacy 四轮采样语义) 为锚, 核心层不持有轮半径,
    /// 因此以离地高度入档 (tools/mesh/sensor_mounts.json, 2026-09-29 重标):
    /// 对角/铲下/铲前为节点实测值(高/中置信), 底盘灰度 4 路为工程默认兜底
    /// (低置信, 投影内贴地, 离地 2.5mm)。通道 id 与 Angle/Range/Fov/Mode
    /// 语义保持现行定义不变(决策⑦)。
    /// </summary>
    public static SensorProfile WheeledCombat11 { get; } = new()
    {
        Id = "wheeledCombat11",
        Label = "本车 11 路",
        Channels =
        [
            Ch("gray_front", "底盘灰度·前", SensorType.Gray, 0.06, 0, 0, 0, 0, "ground", 0.0025),
            Ch("gray_rear", "底盘灰度·后", SensorType.Gray, -0.06, 0, Math.PI, 0, 0, "ground", 0.0025),
            Ch("gray_left", "底盘灰度·左", SensorType.Gray, 0, 0.06, Math.PI / 2, 0, 0, "ground", 0.0025),
            Ch("gray_right", "底盘灰度·右", SensorType.Gray, 0, -0.06, -Math.PI / 2, 0, 0, "ground", 0.0025),
            Ch("diag_left_front", "数字红外·左前", SensorType.Digital, 0.099598, 0.055462, -Math.PI / 4, 1.60, 0.55, "target", 0.05825),
            Ch("diag_left_rear", "数字红外·左后", SensorType.Digital, 0.029459, 0.100622, 3 * Math.PI / 4, 1.60, 0.55, "target", 0.05825),
            Ch("diag_right_front", "数字红外·右前", SensorType.Digital, 0.085178, -0.078079, Math.PI / 4, 1.60, 0.55, "target", 0.05825),
            Ch("diag_right_rear", "数字红外·右后", SensorType.Digital, 0.008171, -0.118071, -3 * Math.PI / 4, 1.60, 0.55, "target", 0.05825),
            Ch("shovel_under_left", "铲下红外·左", SensorType.IrGround, -0.002, 0.108845, 0, 0.25, 0.35, "ground", 0.04125),
            Ch("shovel_under_right", "铲下红外·右", SensorType.IrGround, -0.002, -0.105155, 0, 0.25, 0.35, "ground", 0.04125),
            Ch("shovel_front", "铲前红外", SensorType.IrEdge, -0.165405, 0.001845, 0, 0.90, 0.30, "edge", 0.093694),
        ],
        Logical = new Dictionary<string, LogicalSensorMap>
        {
            ["gF"] = LogicalSensorMap.FromChannel("gray_front"),
            ["gB"] = LogicalSensorMap.FromChannel("gray_rear"),
            ["gL"] = LogicalSensorMap.FromChannel("gray_left"),
            ["gR"] = LogicalSensorMap.FromChannel("gray_right"),
            ["uL"] = LogicalSensorMap.FromChannel("shovel_under_left"),
            ["uR"] = LogicalSensorMap.FromChannel("shovel_under_right"),
            ["sFL"] = LogicalSensorMap.FromChannel("shovel_front"),
            ["sFR"] = LogicalSensorMap.FromChannel("shovel_front"),
            ["dLF"] = LogicalSensorMap.FromChannel("diag_left_front"),
            ["dRF"] = LogicalSensorMap.FromChannel("diag_right_front"),
            ["dLB"] = LogicalSensorMap.FromChannel("diag_left_rear"),
            ["dRB"] = LogicalSensorMap.FromChannel("diag_right_rear"),
            ["f"] = new LogicalSensorMap
            {
                Channels = ["diag_left_front", "diag_right_front"],
                Reducer = "max",
                Virtual = true,
            },
            ["r"] = LogicalSensorMap.Unmapped,
        },
    };
}
