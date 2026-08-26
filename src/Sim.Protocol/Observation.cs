using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>Per-role score pair (also used for restart penalties and reward deltas).</summary>
public sealed record Scores
{
    public double Us { get; init; }

    public double Them { get; init; }
}

/// <summary>
/// The observation message handed to controllers each tick — the <c>obs</c> of
/// <c>decide(obs) -&gt; {v, w}</c>.
///
/// Field order and names follow the legacy wire format (CONTRACT.md section 2,
/// SIMULATOR.md "obs 结构"): <c>requestId, t, role, timer, scores, robot,
/// sensors, rawSensors, sensorLayout, perception, opponent, objects</c>, plus
/// the new <c>tick</c> and <c>protocolVersion</c> fields (additive, ignored by
/// legacy Python controllers).
/// </summary>
public sealed record Observation : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>
    /// Monotonic request id for this decide() call. The controller must echo it
    /// back on the action; late answers are matched by this id and dropped.
    /// </summary>
    public long? RequestId { get; init; }

    /// <summary>Tick index since match start (fixed 0.05 s steps). New field.</summary>
    public long Tick { get; init; }

    /// <summary>Simulation time in seconds since match start.</summary>
    public double T { get; init; }

    /// <summary>Role of the observing robot ("us" or "them").</summary>
    public string Role { get; init; } = RoleNames.Us;

    /// <summary>Remaining match time in seconds (120 s countdown).</summary>
    public double Timer { get; init; }

    public Scores Scores { get; init; } = new();

    /// <summary>The observing robot's own view.</summary>
    public RobotView Robot { get; init; } = new();

    /// <summary>
    /// Legacy compatibility aliases (gF/gB/gL/gR, uL/uR, sFL/sFR, dLF/dRF/dLB/dRB, f, r).
    /// New strategy code must use <see cref="RawSensors"/> and <see cref="SensorLayout"/>.
    /// </summary>
    public LegacySensors? Sensors { get; init; }

    /// <summary>Real channels of this robot's sensor profile, keyed by channel id.</summary>
    public Dictionary<string, double>? RawSensors { get; init; }

    /// <summary>Authoritative channel count/type/layout backing <see cref="RawSensors"/>.</summary>
    public SensorProfile? SensorLayout { get; init; }

    /// <summary>Field-gray and vision implementation metadata.</summary>
    public Perception? Perception { get; init; }

    /// <summary>Opponent view (position/heading/onPlatform/state).</summary>
    public OpponentView? Opponent { get; init; }

    /// <summary>Energy blocks currently in play.</summary>
    public ObjectSet? Objects { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "observation: protocolVersion must not be empty.";
        }
        if (!RoleNames.IsKnownRole(Role))
        {
            yield return $"observation: role must be '{RoleNames.Us}' or '{RoleNames.Them}', got '{Role}'.";
        }
        if (Tick < 0)
        {
            yield return "observation: tick must be >= 0.";
        }
        if (!(T >= 0) || !double.IsFinite(T))
        {
            yield return "observation: t must be a non-negative finite number.";
        }
        if (!(Timer >= 0) || !double.IsFinite(Timer))
        {
            yield return "observation: timer must be a non-negative finite number.";
        }
        if (Robot is null)
        {
            yield return "observation: robot must be present.";
        }
        if (Opponent is null)
        {
            yield return "observation: opponent must be present.";
        }

        if (RawSensors is not null)
        {
            foreach (var (channelId, value) in RawSensors)
            {
                if (string.IsNullOrWhiteSpace(channelId))
                {
                    yield return "observation: rawSensors keys must not be empty.";
                }
                if (!double.IsFinite(value))
                {
                    yield return $"observation: rawSensors['{channelId}'] must be finite.";
                }
            }
        }

        if (SensorLayout is not null)
        {
            foreach (var error in SensorLayout.Validate())
            {
                yield return $"observation: {error}";
            }
        }
    }
}

/// <summary>The observing robot's own dynamic state plus its vehicle profile.</summary>
public sealed record RobotView
{
    /// <summary>Position X (m, field coordinates 0..3.8).</summary>
    public double X { get; init; }

    /// <summary>Position Y (m).</summary>
    public double Y { get; init; }

    /// <summary>Heading (rad).</summary>
    public double Th { get; init; }

    /// <summary>Current commanded/integrated linear velocity (m/s).</summary>
    public double V { get; init; }

    /// <summary>Current commanded/integrated angular velocity (rad/s).</summary>
    public double W { get; init; }

    /// <summary>True while the robot is on the platform.</summary>
    public bool OnPlatform { get; init; }

    /// <summary>True while the robot is hanging on the platform edge.</summary>
    public bool Hang { get; init; }

    /// <summary>FSM state name, e.g. "SEARCH", "SCORE_BLOCK".</summary>
    public string? State { get; init; }

    /// <summary>Human-readable current action label.</summary>
    public string? Action { get; init; }

    /// <summary>This side's full vehicle profile (controllers may adapt to it).</summary>
    public VehicleProfile? Vehicle { get; init; }
}

/// <summary>Opponent view as visible to the observing controller.</summary>
public sealed record OpponentView
{
    public double X { get; init; }

    public double Y { get; init; }

    public double Th { get; init; }

    public bool OnPlatform { get; init; }

    /// <summary>Opponent FSM state name.</summary>
    public string? State { get; init; }
}

/// <summary>
/// Legacy logical sensor aliases kept for old strategies. All values are
/// optional; absent aliases are simply omitted. Gray values are 0–1000
/// (walkway 0, black band ~300, platform white ~1000), IR values ~0–1.
/// </summary>
public sealed record LegacySensors
{
    [JsonPropertyName("gF")] public double? GrayFront { get; init; }
    [JsonPropertyName("gB")] public double? GrayRear { get; init; }
    [JsonPropertyName("gL")] public double? GrayLeft { get; init; }
    [JsonPropertyName("gR")] public double? GrayRight { get; init; }
    [JsonPropertyName("uL")] public double? ShovelUnderLeft { get; init; }
    [JsonPropertyName("uR")] public double? ShovelUnderRight { get; init; }
    [JsonPropertyName("sFL")] public double? ShovelFrontLeft { get; init; }
    [JsonPropertyName("sFR")] public double? ShovelFrontRight { get; init; }
    [JsonPropertyName("dLF")] public double? DiagLeftFront { get; init; }
    [JsonPropertyName("dRF")] public double? DiagRightFront { get; init; }
    [JsonPropertyName("dLB")] public double? DiagLeftRear { get; init; }
    [JsonPropertyName("dRB")] public double? DiagRightRear { get; init; }

    /// <summary>Front distance/trigger channel (virtual in the 11-channel profile).</summary>
    [JsonPropertyName("f")] public double? Front { get; init; }

    /// <summary>Rear channel; 0 when the profile has no rear IR.</summary>
    [JsonPropertyName("r")] public double? Rear { get; init; }
}

/// <summary>Perception implementation metadata (fidelity evidence, not physics).</summary>
public sealed record Perception
{
    /// <summary>Field gray-scale implementation metadata.</summary>
    public FieldGrayInfo? FieldGray { get; init; }

    /// <summary>Vision/classification implementation metadata.</summary>
    public VisionInfo? Vision { get; init; }
}

/// <summary>Metadata about the active field-gray model.</summary>
public sealed record FieldGrayInfo
{
    /// <summary>Map id, e.g. "hand_drawn" or a measured-map identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Fidelity mode: "hand_drawn" or "measured".</summary>
    public string? Mode { get; init; }

    /// <summary>Hash (e.g. SHA-256) of the loaded gray table, when one is loaded.</summary>
    public string? Hash { get; init; }

    /// <summary>Interpolation of the loaded table: "bilinear" or "nearest".</summary>
    public string? Interpolation { get; init; }
}

/// <summary>Metadata about the active vision/classification implementation.</summary>
public sealed record VisionInfo
{
    /// <summary>Vision mode: "default" (classifyRate stub) or "external".</summary>
    public string Mode { get; init; } = "default";

    /// <summary>Simulated classification rate when running the random stub.</summary>
    public double? ClassifyRate { get; init; }

    /// <summary>Number of vision errors observed so far.</summary>
    public long? ErrorCount { get; init; }

    /// <summary>Last vision error message.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Verbatim "external" sub-object (roles → frameId/detection/ageMs), kept as
    /// raw JSON so newer external-vision payloads round-trip without protocol changes.
    /// Values obtained from a JsonDocument must be cloned before assignment.
    /// </summary>
    public JsonElement? External { get; init; }
}

/// <summary>Energy blocks visible in the observation.</summary>
public sealed record ObjectSet
{
    /// <summary>The two buff blocks (15x15x15 cm, simulation radius 0.075 m).</summary>
    public List<EnergyBlockView> Buffs { get; init; } = [];

    /// <summary>The single debuff block.</summary>
    public EnergyBlockView? Debuff { get; init; }
}

/// <summary>A single energy block.</summary>
public sealed record EnergyBlockView
{
    public double X { get; init; }

    public double Y { get; init; }

    /// <summary>True while the block is still on the platform.</summary>
    public bool OnPlatform { get; init; }

    /// <summary>
    /// True once the block has been pushed off the platform — it is out for the
    /// rest of the match (no respawn; only a full reset re-places it).
    /// </summary>
    [JsonPropertyName("out")]
    public bool? Out { get; init; }

    /// <summary>Role that last touched this block (scoring attribution).</summary>
    public string? LastTouch { get; init; }
}
