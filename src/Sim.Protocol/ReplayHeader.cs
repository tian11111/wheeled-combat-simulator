using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// Reference to the field-gray model used for a recorded match: its id and a
/// content hash so replays can prove they ran against the same gray table.
/// </summary>
public sealed record FieldGrayRef
{
    /// <summary>Gray model id, e.g. "hand_drawn" or a measured-map identifier.</summary>
    public string Id { get; init; } = "hand_drawn";

    /// <summary>Content hash (e.g. SHA-256) of the gray table, when applicable.</summary>
    public string? Hash { get; init; }

    /// <summary>Fidelity mode: "hand_drawn" or "measured".</summary>
    public string? Mode { get; init; }
}

/// <summary>
/// Accepted controller actions and core commands for one recorded tick.
/// Only actions that were actually accepted (after request-id matching and
/// validation) are recorded; process faults and timeouts are recorded as
/// commands/diagnostics by the CLI, not as actions.
/// </summary>
public sealed record ReplayTick
{
    /// <summary>Tick index.</summary>
    public long Tick { get; init; }

    /// <summary>Simulation time in seconds (optional, header-decidable sampling).</summary>
    public double? T { get; init; }

    /// <summary>Accepted action per role.</summary>
    public Dictionary<string, RobotAction> Actions { get; init; } = new();

    /// <summary>Core commands issued on this tick (arm/pause/resume/restart/scene/step/loadReplay, ...).</summary>
    public List<string>? Commands { get; init; }
}

/// <summary>
/// Header of a versioned replay file. Replaying the same header (seed,
/// parameters, vehicle profiles, field-gray id/hash, vision mode, core version)
/// and the recorded tick inputs must reproduce the same event sequence and
/// final scores.
/// </summary>
public sealed record ReplayHeader : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Replay file format version (currently "replay-v1").</summary>
    [JsonPropertyName("replayVersion")]
    public string ReplayVersion { get; init; } = ProtocolVersion.ReplayFormat;

    /// <summary>Ruleset identifier the match ran under.</summary>
    public string RulesetId { get; init; } = "wushu-ring-2026";

    /// <summary>Deterministic seed the match ran with.</summary>
    public long Seed { get; init; }

    /// <summary>Version of the simulation core that produced the replay.</summary>
    public string CoreVersion { get; init; } = "";

    /// <summary>Active vision mode ("default", "random_stub", "external", ...).</summary>
    public string VisionMode { get; init; } = "default";

    /// <summary>Simulation parameters the match ran with, keyed by name.</summary>
    public Dictionary<string, double>? Parameters { get; init; }

    /// <summary>Vehicle profiles per role.</summary>
    public Dictionary<string, VehicleProfile> Vehicles { get; init; } = new()
    {
        [RoleNames.Us] = new(),
        [RoleNames.Them] = new(),
    };

    /// <summary>Field-gray model reference (id/hash).</summary>
    public FieldGrayRef FieldGray { get; init; } = new();

    /// <summary>Accepted actions/commands by tick, in tick order.</summary>
    public List<ReplayTick> Ticks { get; init; } = [];

    /// <summary>Creation timestamp (informational only; never part of determinism).</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Vision evidence package id when the match ran the visionReplay adapter
    /// (additive; null on the default classifyRate path, so old headers stay
    /// byte-identical). Evidence reproduction = hash-locked package + scenario
    /// + recorded actions; per-frame detections are replayed deterministically.
    /// </summary>
    public string? VisionEvidenceId { get; init; }

    /// <summary>SHA-256 of the vision evidence package (additive; null on the default path).</summary>
    public string? VisionEvidenceSha256 { get; init; }

    /// <summary>Number of recorded ticks.</summary>
    [JsonIgnore]
    public int TickCount => Ticks.Count;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "replay header: protocolVersion must not be empty.";
        }
        if (string.IsNullOrWhiteSpace(ReplayVersion))
        {
            yield return "replay header: replayVersion must not be empty.";
        }
        if (string.IsNullOrWhiteSpace(RulesetId))
        {
            yield return "replay header: rulesetId must not be empty.";
        }
        if (Seed < 0)
        {
            yield return "replay header: seed must be >= 0.";
        }
        if (string.IsNullOrWhiteSpace(CoreVersion))
        {
            yield return "replay header: coreVersion must not be empty.";
        }
        if (string.IsNullOrWhiteSpace(VisionMode))
        {
            yield return "replay header: visionMode must not be empty.";
        }
        if (VisionEvidenceId is { } evidenceId && string.IsNullOrWhiteSpace(evidenceId))
        {
            yield return "replay header: visionEvidenceId must not be empty when present.";
        }
        if (VisionEvidenceSha256 is { } evidenceSha
            && (evidenceSha.Length != 64 || !evidenceSha.All(Uri.IsHexDigit)))
        {
            yield return "replay header: visionEvidenceSha256 must be 64 hex chars when present.";
        }
        if ((VisionEvidenceId is null) != (VisionEvidenceSha256 is null))
        {
            yield return "replay header: visionEvidenceId and visionEvidenceSha256 must be set together.";
        }
        if (FieldGray is null)
        {
            yield return "replay header: fieldGray must be present.";
        }
        else if (string.IsNullOrWhiteSpace(FieldGray.Id))
        {
            yield return "replay header: fieldGray.id must not be empty.";
        }

        if (Vehicles is null)
        {
            yield return "replay header: vehicles must be present.";
        }
        else
        {
            foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
            {
                if (!Vehicles.TryGetValue(role, out var vehicle) || vehicle is null)
                {
                    yield return $"replay header: vehicles must contain '{role}'.";
                }
            }
            foreach (var (role, vehicle) in Vehicles)
            {
                if (vehicle is null)
                {
                    yield return $"replay header: vehicles['{role}'] must not be null.";
                    continue;
                }
                foreach (var error in vehicle.Validate())
                {
                    yield return $"replay header: vehicles['{role}']: {error}";
                }
            }
        }

        if (Ticks is null)
        {
            yield return "replay header: ticks must be present.";
        }
        else
        {
            var previousTick = -1L;
            foreach (var tick in Ticks)
            {
                if (tick is null)
                {
                    yield return "replay header: ticks must not contain null entries.";
                    continue;
                }
                if (tick.Tick <= previousTick)
                {
                    yield return $"replay header: tick indices must be strictly increasing, got {tick.Tick} after {previousTick}.";
                }
                previousTick = tick.Tick;

                foreach (var (role, action) in tick.Actions)
                {
                    if (!RoleNames.IsKnownRole(role))
                    {
                        yield return $"replay header: tick {tick.Tick} has unknown action role '{role}'.";
                    }
                    if (action is null)
                    {
                        yield return $"replay header: tick {tick.Tick} has a null action for '{role}'.";
                        continue;
                    }
                    foreach (var error in action.Validate())
                    {
                        yield return $"replay header: tick {tick.Tick} action '{role}': {error}";
                    }
                }
            }
        }
    }
}
