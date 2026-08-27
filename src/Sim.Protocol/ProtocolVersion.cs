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
}
