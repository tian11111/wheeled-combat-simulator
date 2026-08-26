using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>Energy block kinds.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<BlockKind>))]
public enum BlockKind
{
    Buff,
    Debuff,
}

/// <summary>Axis-aligned rectangular region in field coordinates (m).</summary>
public sealed record Region
{
    public double MinX { get; init; }

    public double MinY { get; init; }

    public double MaxX { get; init; }

    public double MaxY { get; init; }
}

/// <summary>Planar pose: position (m) plus heading (rad).</summary>
public sealed record Pose2
{
    public double X { get; init; }

    public double Y { get; init; }

    public double Th { get; init; }
}

/// <summary>
/// Field geometry and rules parameters. The default values encode the 2026
/// official layout (CONTRACT.md section 6): 3.8 m field, 2.4 m central
/// platform (height 6 cm), 70 cm black walkway, 20 cm fence, 50x40 cm start
/// zones 20 cm from the platform edge, facing the platform.
/// </summary>
public sealed record FieldParams
{
    /// <summary>Inner field size (m): 3.8.</summary>
    public double FieldSize { get; init; } = 3.8;

    /// <summary>Walkway width around the platform (m): 0.7.</summary>
    public double AisleWidth { get; init; } = 0.7;

    /// <summary>Fence height (m): 0.2.</summary>
    public double FenceHeight { get; init; } = 0.2;

    /// <summary>Platform step height (m): 0.06.</summary>
    public double PlatformHeight { get; init; } = 0.06;

    /// <summary>Platform footprint: [0.7, 3.1] x [0.7, 3.1].</summary>
    public Region Platform { get; init; } = new() { MinX = 0.7, MinY = 0.7, MaxX = 3.1, MaxY = 3.1 };

    /// <summary>Energy block edge length (m): 0.15.</summary>
    public double BlockSize { get; init; } = 0.15;

    /// <summary>Energy block simulation radius (m): 0.075.</summary>
    public double BlockRadius { get; init; } = 0.075;

    /// <summary>Match duration in seconds: 120.</summary>
    public double MatchDuration { get; init; } = 120;

    /// <summary>Fixed simulation tick length in seconds: 0.05.</summary>
    public double TickSeconds { get; init; } = 0.05;

    /// <summary>Start zones per role. Yellow (us): x in [0.7, 1.2], y in [0.1, 0.5]; blue (them): x in [2.6, 3.1], y in [3.3, 3.7].</summary>
    public Dictionary<string, Region> StartZones { get; init; } = new()
    {
        [RoleNames.Us] = new() { MinX = 0.7, MinY = 0.1, MaxX = 1.2, MaxY = 0.5 },
        [RoleNames.Them] = new() { MinX = 2.6, MinY = 3.3, MaxX = 3.1, MaxY = 3.7 },
    };

    /// <summary>
    /// Start poses per role. Us starts at (0.95, 0.3) heading -π/2 (tail facing
    /// the y=0.7 platform edge for the backward mount); them at (2.85, 3.5) heading +π/2.
    /// </summary>
    public Dictionary<string, Pose2> Starts { get; init; } = new()
    {
        [RoleNames.Us] = new() { X = 0.95, Y = 0.3, Th = -Math.PI / 2 },
        [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
    };

    /// <summary>The 2026 official field layout.</summary>
    public static FieldParams Default { get; } = new();

    public IEnumerable<string> Validate()
    {
        if (!(FieldSize > 0) || !double.IsFinite(FieldSize))
        {
            yield return "field: fieldSize must be a positive finite number.";
        }
        if (Platform is null)
        {
            yield return "field: platform region must be present.";
        }
        else if (Platform.MinX >= Platform.MaxX || Platform.MinY >= Platform.MaxY)
        {
            yield return "field: platform region must satisfy min < max on both axes.";
        }
        if (!(PlatformHeight >= 0) || !double.IsFinite(PlatformHeight))
        {
            yield return "field: platformHeight must be a non-negative finite number.";
        }
        if (!(BlockSize > 0) || !double.IsFinite(BlockSize))
        {
            yield return "field: blockSize must be a positive finite number.";
        }
        if (!(BlockRadius > 0) || !double.IsFinite(BlockRadius))
        {
            yield return "field: blockRadius must be a positive finite number.";
        }
        if (!(MatchDuration > 0) || !double.IsFinite(MatchDuration))
        {
            yield return "field: matchDuration must be a positive finite number.";
        }
        if (!(TickSeconds > 0) || !double.IsFinite(TickSeconds))
        {
            yield return "field: tickSeconds must be a positive finite number.";
        }

        foreach (var key in new[] { RoleNames.Us, RoleNames.Them })
        {
            if (!StartZones.TryGetValue(key, out var zone) || zone is null)
            {
                yield return $"field: startZones must contain '{key}'.";
            }
            else if (zone.MinX >= zone.MaxX || zone.MinY >= zone.MaxY)
            {
                yield return $"field: start zone '{key}' must satisfy min < max on both axes.";
            }
            if (!Starts.TryGetValue(key, out var pose) || pose is null)
            {
                yield return $"field: starts must contain '{key}'.";
            }
        }
    }
}

/// <summary>
/// One energy block in a scenario layout. When <see cref="X"/>/<see cref="Y"/>
/// are null the referee places the block deterministically from the scenario
/// seed; fixed coordinates freeze the layout for regression scenarios.
/// </summary>
public sealed record BlockSpec
{
    public BlockKind Kind { get; init; } = BlockKind.Buff;

    /// <summary>Fixed X position (m), or null for seeded referee placement.</summary>
    public double? X { get; init; }

    /// <summary>Fixed Y position (m), or null for seeded referee placement.</summary>
    public double? Y { get; init; }

    /// <summary>Optional radius override (m).</summary>
    public double? Radius { get; init; }

    public IEnumerable<string> Validate()
    {
        if (X.HasValue != Y.HasValue)
        {
            yield return $"block ({Kind}): x and y must be both set or both null.";
        }
        if (X.HasValue && Y.HasValue && (!double.IsFinite(X.Value) || !double.IsFinite(Y.Value)))
        {
            yield return $"block ({Kind}): x/y must be finite.";
        }
        if (Radius is not null && (!(Radius.Value > 0) || !double.IsFinite(Radius.Value)))
        {
            yield return $"block ({Kind}): radius must be a positive finite number.";
        }
    }
}

/// <summary>
/// The 2026 official match layout. Kept here (not in a scenario file) so the
/// headless CLI, the desktop shell and the kernel's seeded-placement defaults
/// all fall back to exactly the same frozen coordinates; the on-disk canonical
/// form is <c>scenarios/wushu-ring-2026.json</c>, which mirrors these values.
/// </summary>
public static class OfficialLayout
{
    /// <summary>Two buff blocks and one debuff block at their official coordinates.</summary>
    public static List<BlockSpec> Blocks =>
    [
        new BlockSpec { Kind = BlockKind.Buff, X = 1.35, Y = 1.35 },
        new BlockSpec { Kind = BlockKind.Buff, X = 2.5, Y = 2.6 },
        new BlockSpec { Kind = BlockKind.Debuff, X = 1.6, Y = 2.4 },
    ];
}

/// <summary>
/// A fully specified match setup: ruleset id, seed, field parameters, vehicle
/// profiles and block layout. Everything needed to reproduce a match is in the
/// scenario plus the recorded action stream (see <see cref="ReplayHeader"/>).
/// </summary>
public sealed record Scenario : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Ruleset identifier, e.g. "wushu-ring-2026".</summary>
    public string Id { get; init; } = "wushu-ring-2026";

    /// <summary>Optional display name.</summary>
    public string? Name { get; init; }

    /// <summary>Deterministic seed; all random draws derive from it.</summary>
    public long Seed { get; init; }

    public FieldParams Field { get; init; } = new();

    /// <summary>Vehicle profiles per role ("us"/"them").</summary>
    public Dictionary<string, VehicleProfile> Vehicles { get; init; } = new()
    {
        [RoleNames.Us] = new(),
        [RoleNames.Them] = new(),
    };

    /// <summary>
    /// Block layout: 2026 rules use two buff blocks and one debuff block.
    /// </summary>
    public List<BlockSpec> Blocks { get; init; } =
    [
        new() { Kind = BlockKind.Buff },
        new() { Kind = BlockKind.Buff },
        new() { Kind = BlockKind.Debuff },
    ];

    /// <summary>Optional simulation parameters (noise levels, thresholds, ...), keyed by name.</summary>
    public Dictionary<string, double>? Parameters { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "scenario: protocolVersion must not be empty.";
        }
        if (string.IsNullOrWhiteSpace(Id))
        {
            yield return "scenario: id (ruleset id) must not be empty.";
        }
        if (Seed < 0)
        {
            yield return "scenario: seed must be >= 0.";
        }
        if (Field is null)
        {
            yield return "scenario: field must be present.";
        }
        else
        {
            foreach (var error in Field.Validate())
            {
                yield return $"scenario: {error}";
            }
        }

        if (Vehicles is null)
        {
            yield return "scenario: vehicles must be present.";
        }
        else
        {
            foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
            {
                if (!Vehicles.TryGetValue(role, out var vehicle) || vehicle is null)
                {
                    yield return $"scenario: vehicles must contain '{role}'.";
                }
            }
            foreach (var (role, vehicle) in Vehicles)
            {
                if (vehicle is null)
                {
                    yield return $"scenario: vehicles['{role}'] must not be null.";
                    continue;
                }
                foreach (var error in vehicle.Validate())
                {
                    yield return $"scenario: vehicles['{role}']: {error}";
                }
            }
        }

        if (Blocks is not null)
        {
            foreach (var block in Blocks)
            {
                if (block is null)
                {
                    yield return "scenario: blocks must not contain null entries.";
                    continue;
                }
                foreach (var error in block.Validate())
                {
                    yield return $"scenario: {error}";
                }
            }
        }

        if (Parameters is not null)
        {
            foreach (var (name, value) in Parameters)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    yield return "scenario: parameter names must not be empty.";
                }
                if (!double.IsFinite(value))
                {
                    yield return $"scenario: parameter '{name}' must be finite.";
                }
            }
        }
    }
}
