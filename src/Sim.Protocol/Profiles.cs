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
        double range, double fov, string mode) => new()
    {
        Id = id,
        Label = label,
        Type = type,
        Forward = forward,
        Lateral = lateral,
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
    /// </summary>
    public static SensorProfile WheeledCombat11 { get; } = new()
    {
        Id = "wheeledCombat11",
        Label = "本车 11 路",
        Channels =
        [
            Ch("gray_front", "底盘灰度·前", SensorType.Gray, 0.11, 0, 0, 0, 0, "ground"),
            Ch("gray_rear", "底盘灰度·后", SensorType.Gray, -0.11, 0, Math.PI, 0, 0, "ground"),
            Ch("gray_left", "底盘灰度·左", SensorType.Gray, 0, 0.11, Math.PI / 2, 0, 0, "ground"),
            Ch("gray_right", "底盘灰度·右", SensorType.Gray, 0, -0.11, -Math.PI / 2, 0, 0, "ground"),
            Ch("diag_left_front", "数字红外·左前", SensorType.Digital, 0, 0, -Math.PI / 4, 1.60, 0.55, "target"),
            Ch("diag_left_rear", "数字红外·左后", SensorType.Digital, 0, 0, 3 * Math.PI / 4, 1.60, 0.55, "target"),
            Ch("diag_right_front", "数字红外·右前", SensorType.Digital, 0, 0, Math.PI / 4, 1.60, 0.55, "target"),
            Ch("diag_right_rear", "数字红外·右后", SensorType.Digital, 0, 0, -3 * Math.PI / 4, 1.60, 0.55, "target"),
            Ch("shovel_under_left", "铲下红外·左", SensorType.IrGround, 0.14, 0.06, 0, 0.25, 0.35, "ground"),
            Ch("shovel_under_right", "铲下红外·右", SensorType.IrGround, 0.14, -0.06, 0, 0.25, 0.35, "ground"),
            Ch("shovel_front", "铲前红外", SensorType.IrEdge, 0.16, 0, 0, 0.90, 0.30, "edge"),
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
