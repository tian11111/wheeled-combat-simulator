namespace Sim.Protocol;

/// <summary>
/// Protocol versioning constants for the RobotSimulator wire contracts.
///
/// These identifiers are stamped on every serialized message so that old
/// traces, replay files and controllers can be checked against the core that
/// produced them (see the compatibility rules in the task design).
/// </summary>
public static class ProtocolVersion
{
    /// <summary>Current wire-format version for all protocol messages.</summary>
    public const string Current = "v1";

    /// <summary>
    /// Version tag of the diagnostic trace format. Distinguishes
    /// requested actions / applied actions / actual velocity / raw sensors /
    /// rewards exactly as described by CONTRACT.md section 2.2.
    /// </summary>
    public const string DiagnosticTrace = "diagnostic-v1";

    /// <summary>Version tag of the replay file format (ReplayHeader).</summary>
    public const string ReplayFormat = "replay-v1";

    /// <summary>
    /// Version tag of the arena layout extension (Scenario.layoutVersion and
    /// the optional field pose in Scenario.field.pose). Scenario files without
    /// any layoutVersion tag are legacy identity layouts and stay valid.
    /// </summary>
    public const string ArenaLayoutV1 = "arena-layout-v1";

    /// <summary>
    /// Version tag of the offline telemetry contract (telemetry-v1): strict
    /// SI units, typed trials per physical experiment kind, consumed by the
    /// calibration tool. Never part of match/replay traffic.
    /// </summary>
    public const string TelemetryFormat = "telemetry-v1";

    /// <summary>
    /// Version tag of the offline sensor-evidence contract (sensor-calibration-v1):
    /// imported MBri gray/front-ADC/shovel judgment models with source hashes and
    /// raw-log replay gates. Offline artifact only — never enters Scenario/Snapshot/replays.
    /// </summary>
    public const string SensorCalibrationFormat = "sensor-calibration-v1";

    /// <summary>
    /// Version tag of the headless batch result stream (one
    /// <see cref="BatchMatchResult"/> JSON object per input seed). Additive-only
    /// wire contract consumed by AI agents; never part of Scenario/Snapshot/replays.
    /// </summary>
    public const string BatchResultFormat = "sim-batch-result-v1";
}
