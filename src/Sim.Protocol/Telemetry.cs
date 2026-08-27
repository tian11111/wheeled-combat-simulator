using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// telemetry-v1: the offline real-robot telemetry contract consumed by the
/// calibration tool. Strict SI (metres, seconds, radians) — validated once at
/// entry by <c>TelemetryFile.Validate()</c>; downstream layers can assume
/// every accepted value is finite, typed and timestamp-ordered.
/// </summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<TelemetryTrialKind>))]
public enum TelemetryTrialKind
{
    /// <summary>Free lateral-slip decay (fit vehicle.latFrictionK).</summary>
    LateralCoast,

    /// <summary>Free spin-down decay (fit vehicle.angDamping).</summary>
    AngularCoast,

    /// <summary>Energy-block glide after a push (fit BLOCK_MU_K).</summary>
    BlockPush,

    /// <summary>Wall/robot impact with pre/post normal velocities (fit COLLISION_RESTITUTION).</summary>
    Collision,

    /// <summary>Commanded motion with boolean stall labels (fit STALL_SPEED).</summary>
    Stall,

    /// <summary>Approach-onto-platform trial with measured outcome (validate the mount gate).</summary>
    Mount,
}

/// <summary>Declared measurement units. Values must be exactly "m", "s" and "rad".</summary>
public sealed record TelemetryUnits
{
    public string Length { get; init; } = "m";

    public string Time { get; init; } = "s";

    public string Angle { get; init; } = "rad";

    public IEnumerable<string> Validate()
    {
        if (Length != "m")
        {
            yield return "telemetry: units.length must be exactly \"m\".";
        }
        if (Time != "s")
        {
            yield return "telemetry: units.time must be exactly \"s\".";
        }
        if (Angle != "rad")
        {
            yield return "telemetry: units.angle must be exactly \"rad\".";
        }
    }
}

/// <summary>Vehicle identification the trials were captured for.</summary>
public sealed record TelemetryVehicle
{
    public string Id { get; init; } = "";

    public string? Name { get; init; }
}

/// <summary>Field/environment metadata for the capture session.</summary>
public sealed record TelemetryCapture
{
    /// <summary>"real" or "synthetic". Synthetic data can never promote fidelity.</summary>
    public string Source { get; init; } = "";

    /// <summary>Capture date, YYYY-MM-DD.</summary>
    public string Date { get; init; } = "";

    public string? FieldId { get; init; }

    public string? Condition { get; init; }

    public string? Notes { get; init; }

    public IEnumerable<string> Validate()
    {
        if (Source != "real" && Source != "synthetic")
        {
            yield return "telemetry: capture.source must be \"real\" or \"synthetic\".";
        }
        if (Date.Length == 0
            || !DateOnly.TryParseExact(Date, "yyyy-MM-dd", out _))
        {
            yield return "telemetry: capture.date must be YYYY-MM-DD.";
        }
    }
}

/// <summary>A planar pose sample (x, y in metres; th in radians; any part optional per kind).</summary>
public sealed record TelemetryPose
{
    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Th { get; init; }
}

/// <summary>A planar velocity sample (m/s).</summary>
public sealed record TelemetryVelocity
{
    public double? Vx { get; init; }

    public double? Vy { get; init; }
}

/// <summary>Drive command sample (v m/s, w rad/s).</summary>
public sealed record TelemetryCommand
{
    public double? V { get; init; }

    public double? W { get; init; }
}

/// <summary>One timestamped telemetry frame; payload relevance is kind-specific.</summary>
public sealed record TelemetryFrame
{
    public double? T { get; init; }

    public TelemetryPose? Robot { get; init; }

    public TelemetryPose? Block { get; init; }

    public TelemetryPose? Opponent { get; init; }

    public TelemetryCommand? Command { get; init; }

    /// <summary>Stall trials: measured |velocity| (m/s).</summary>
    public double? Speed { get; init; }

    /// <summary>Stall trials: measured stall label.</summary>
    public bool? Stalled { get; init; }
}

/// <summary>A planar vector/point (metres or unit direction).</summary>
public sealed record TelemetryPoint
{
    public double? X { get; init; }

    public double? Y { get; init; }
}

/// <summary>Collision impact velocities for the two bodies (missing opponent = fixed wall).</summary>
public sealed record TelemetryImpactVelocities
{
    public TelemetryVelocity? Robot { get; init; }

    public TelemetryVelocity? Opponent { get; init; }
}

/// <summary>Explicit pre/post impact velocity pair (alternative to frame-derived velocities).</summary>
public sealed record TelemetryImpact
{
    public TelemetryImpactVelocities? Pre { get; init; }

    public TelemetryImpactVelocities? Post { get; init; }
}

/// <summary>Mount approach kinematics measured at first wall contact (m/s, body frame).</summary>
public sealed record TelemetryMountApproach
{
    /// <summary>Normal velocity into the stage wall.</summary>
    public double? Vn { get; init; }

    /// <summary>Tangential velocity along the wall.</summary>
    public double? Vt { get; init; }
}

/// <summary>
/// One physical experiment. <see cref="Set"/> splits trials into fit and holdout;
/// promotion of a subsystem requires the holdout error to meet the agreed target.
/// </summary>
public sealed record TelemetryTrial
{
    public string Id { get; init; } = "";

    public TelemetryTrialKind Kind { get; init; }

    /// <summary>"fit" or "holdout" (default "fit").</summary>
    public string Set { get; init; } = "fit";

    public List<TelemetryFrame> Frames { get; init; } = [];

    /// <summary>Collision: contact normal; else derived from <see cref="Wall"/> or robot/opponent geometry.</summary>
    public TelemetryPoint? Normal { get; init; }

    /// <summary>Collision: wall tag "north"/"south"/"east"/"west" as an alternative normal source.</summary>
    public string? Wall { get; init; }

    /// <summary>Collision (frame form): impact index between frames[i-1] and frames[i].</summary>
    public int? ImpactIndex { get; init; }

    /// <summary>Collision: explicit impact velocities (preferred over frame form).</summary>
    public TelemetryImpact? Impact { get; init; }

    /// <summary>Mount: measured approach kinematics.</summary>
    public TelemetryMountApproach? Approach { get; init; }

    /// <summary>Mount: measured outcome (true = mounted the stage).</summary>
    public bool? Outcome { get; init; }

    public IEnumerable<string> Validate()
    {
        if (Set != "fit" && Set != "holdout")
        {
            yield return $"trial '{Id}': set must be \"fit\" or \"holdout\", got '{Set}'.";
        }

        var frames = Frames ?? [];
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i]?.T is not { } t || !double.IsFinite(t))
            {
                yield return $"trial '{Id}' frame {i}: t must be a finite time in seconds.";
                continue;
            }
            if (i > 0 && frames[i - 1]?.T is { } prev && t <= prev)
            {
                yield return $"trial '{Id}' frame {i}: timestamps must be strictly increasing.";
            }
        }

        switch (Kind)
        {
            case TelemetryTrialKind.LateralCoast:
            case TelemetryTrialKind.AngularCoast:
                if (frames.Count < 2)
                {
                    yield return $"trial '{Id}' ({Kind}): needs at least 2 frames with robot poses.";
                }
                foreach (var error in FramesRequire(frames, requirePose: true, requireTh: true, "robot"))
                {
                    yield return $"trial '{Id}': {error}";
                }
                break;

            case TelemetryTrialKind.BlockPush:
                if (frames.Count < 2)
                {
                    yield return $"trial '{Id}' (block_push): needs at least 2 block frames.";
                }
                foreach (var error in FramesRequire(frames, requirePose: true, requireTh: false, "block"))
                {
                    yield return $"trial '{Id}': {error}";
                }
                break;

            case TelemetryTrialKind.Collision:
                foreach (var error in ValidateCollision(frames))
                {
                    yield return $"trial '{Id}': {error}";
                }
                break;

            case TelemetryTrialKind.Stall:
                if (frames.Count == 0)
                {
                    yield return $"trial '{Id}' (stall): needs at least 1 labeled frame.";
                }
                for (var i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    if (frame is null
                        || frame.Speed is not { } speed || !double.IsFinite(speed) || speed < 0
                        || frame.Stalled is null)
                    {
                        yield return $"trial '{Id}' frame {i}: stall frames need a finite speed >= 0 and a boolean stalled label.";
                    }
                }
                break;

            case TelemetryTrialKind.Mount:
                if (Approach?.Vn is not { } vn || !double.IsFinite(vn))
                {
                    yield return $"trial '{Id}' (mount): approach.vn must be a finite normal velocity.";
                }
                if (Approach?.Vt is not { } vt || !double.IsFinite(vt))
                {
                    yield return $"trial '{Id}' (mount): approach.vt must be a finite tangential velocity.";
                }
                if (Outcome is null)
                {
                    yield return $"trial '{Id}' (mount): a boolean outcome label is required.";
                }
                break;
        }
    }

    private IEnumerable<string> ValidateCollision(List<TelemetryFrame> frames)
    {
        var errors = new List<string>();
        if (Normal is { } normal
            && (normal.X is not { } nx || normal.Y is not { } ny
                || !double.IsFinite(nx) || !double.IsFinite(ny)
                || Math.Sqrt(nx * nx + ny * ny) < 1e-9))
        {
            errors.Add("normal, when present, needs finite x/y and non-zero length.");
        }
        if (Wall is { } wall && wall.ToLowerInvariant() is not ("north" or "south" or "east" or "west"))
        {
            errors.Add($"wall tag '{wall}' must be north/south/east/west.");
        }
        if (Impact is { Pre: not null, Post: not null })
        {
            errors.AddRange(ValidateImpactPair("pre", Impact.Pre));
            errors.AddRange(ValidateImpactPair("post", Impact.Post));
            return errors;
        }
        if (errors.Count > 0)
        {
            return errors;
        }
        // Frame form: robot + opponent poses, resolvable impact index.
        if (frames.Count < 4)
        {
            errors.Add("collision needs impact{pre,post} or at least 4 robot/opponent frames.");
            return errors;
        }
        errors.AddRange(FramesRequire(frames, requirePose: true, requireTh: false, "robot"));
        errors.AddRange(FramesRequire(frames, requirePose: true, requireTh: false, "opponent"));
        var impactIndex = ImpactIndex ?? -1;
        if (impactIndex < 1 || impactIndex > frames.Count - 2)
        {
            errors.Add("impactIndex must be within 1..frames.Count-2 for the frame form.");
        }
        return errors;
    }

    private static IEnumerable<string> ValidateImpactPair(string label, TelemetryImpactVelocities pair)
    {
        if (pair.Robot is not { } robot || robot.Vx is not { } rvx || robot.Vy is not { } rvy
            || !double.IsFinite(rvx) || !double.IsFinite(rvy))
        {
            yield return $"impact.{label}.robot needs finite vx/vy.";
        }
        if (pair.Opponent is { } opponent
            && (opponent.Vx is not { } ovx || opponent.Vy is not { } ovy
                || !double.IsFinite(ovx) || !double.IsFinite(ovy)))
        {
            yield return $"impact.{label}.opponent, when present, needs finite vx/vy.";
        }
    }

    private static IEnumerable<string> FramesRequire(
        List<TelemetryFrame> frames, bool requirePose, bool requireTh, string key)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            var pose = key switch
            {
                "robot" => frames[i]?.Robot,
                "block" => frames[i]?.Block,
                _ => frames[i]?.Opponent,
            };
            if (requirePose && (pose?.X is not { } x || pose?.Y is not { } y
                    || !double.IsFinite(x) || !double.IsFinite(y)))
            {
                yield return $"frame {i}: {key}.x/{key}.y must be finite metres.";
                continue;
            }
            if (requireTh && (pose?.Th is not { } th || !double.IsFinite(th)))
            {
                yield return $"frame {i}: {key}.th must be a finite heading in radians.";
            }
        }
    }
}

/// <summary>
/// A telemetry capture file. Validate once at load; the calibration tool must
/// not run on unvalidated input.
/// </summary>
public sealed record TelemetryFile : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Contract tag; must be "telemetry-v1".</summary>
    public string? Schema { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public TelemetryUnits Units { get; init; } = new();

    public TelemetryVehicle Vehicle { get; init; } = new();

    public TelemetryCapture Capture { get; init; } = new();

    public List<TelemetryTrial> Trials { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "telemetry: protocolVersion must not be empty.";
        }
        if (Schema != ProtocolVersion.TelemetryFormat)
        {
            yield return $"telemetry: schema must be \"{ProtocolVersion.TelemetryFormat}\".";
        }
        if (SchemaVersion != 1)
        {
            yield return $"telemetry: unsupported schemaVersion {SchemaVersion}.";
        }
        if (string.IsNullOrWhiteSpace(Vehicle?.Id))
        {
            yield return "telemetry: vehicle.id must be present (patch targeting).";
        }
        foreach (var error in Units?.Validate() ?? [])
        {
            yield return error;
        }
        foreach (var error in Capture?.Validate() ?? [])
        {
            yield return error;
        }
        if (Trials is null || Trials.Count == 0)
        {
            yield return "telemetry: trials must be a non-empty array.";
            yield break;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Trials.Count; i++)
        {
            var trial = Trials[i];
            if (trial is null)
            {
                yield return $"telemetry: trials[{i}] must be an object.";
                continue;
            }
            var id = string.IsNullOrWhiteSpace(trial.Id) ? $"trial-{i + 1}" : trial.Id;
            if (!ids.Add(id))
            {
                yield return $"telemetry: duplicate trial id '{id}'.";
            }
            foreach (var error in trial.Validate())
            {
                yield return error;
            }
        }
    }
}
