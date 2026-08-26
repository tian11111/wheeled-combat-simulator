using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// Immutable committed state view of one tick. The core commits exactly one
/// snapshot per tick after referee scoring; renderers interpolate between
/// snapshots and must never mutate them.
/// </summary>
public sealed record Snapshot : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Tick index since match start (fixed 0.05 s steps).</summary>
    public long Tick { get; init; }

    /// <summary>Simulation time in seconds.</summary>
    public double T { get; init; }

    /// <summary>Remaining match time in seconds.</summary>
    public double Timer { get; init; }

    /// <summary>Referee phase (PREP / RUN / DONE).</summary>
    public MatchPhase Phase { get; init; } = MatchPhase.Prep;

    /// <summary>True while the referee has paused the match.</summary>
    public bool Paused { get; init; }

    /// <summary>True once the match reached a terminal state.</summary>
    public bool Done { get; init; }

    /// <summary>Terminal reason, required when <see cref="Done"/> (e.g. "比赛时间结束").</summary>
    public string? DoneReason { get; init; }

    public Scores Scores { get; init; } = new();

    /// <summary>Restart penalties per role (legacy restartPenalties: {us, them}).</summary>
    public Scores RestartPenalties { get; init; } = new();

    /// <summary>Full robot states keyed by role ("us"/"them").</summary>
    public Dictionary<string, RobotState> Robots { get; init; } = new();

    /// <summary>Legacy logical sensor aliases keyed by role.</summary>
    public Dictionary<string, LegacySensors>? Sensors { get; init; }

    /// <summary>Real sensor channels keyed by role, then by channel id.</summary>
    public Dictionary<string, Dictionary<string, double>>? RawSensors { get; init; }

    /// <summary>Sensor profiles keyed by role.</summary>
    public Dictionary<string, SensorProfile>? SensorLayout { get; init; }

    /// <summary>Perception implementation metadata.</summary>
    public Perception? Perception { get; init; }

    /// <summary>Energy blocks in play.</summary>
    public ObjectSet? Objects { get; init; }

    /// <summary>Events committed since the previous snapshot (monotonic seq).</summary>
    public List<Event>? Events { get; init; }

    /// <summary>Score delta since the previous snapshot (gym-style per-step reward).</summary>
    public Scores? Reward { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "snapshot: protocolVersion must not be empty.";
        }
        if (Tick < 0)
        {
            yield return "snapshot: tick must be >= 0.";
        }
        if (!(T >= 0) || !double.IsFinite(T))
        {
            yield return "snapshot: t must be a non-negative finite number.";
        }
        if (!(Timer >= 0) || !double.IsFinite(Timer))
        {
            yield return "snapshot: timer must be a non-negative finite number.";
        }
        if (Done && string.IsNullOrWhiteSpace(DoneReason))
        {
            yield return "snapshot: doneReason is required when done is true.";
        }
        if (Robots is null)
        {
            yield return "snapshot: robots must be present.";
        }
        else
        {
            if (!Robots.ContainsKey(RoleNames.Us))
            {
                yield return $"snapshot: robots must contain role '{RoleNames.Us}'.";
            }
            if (!Robots.ContainsKey(RoleNames.Them))
            {
                yield return $"snapshot: robots must contain role '{RoleNames.Them}'.";
            }
            foreach (var (role, robot) in Robots)
            {
                if (!RoleNames.IsKnownRole(role))
                {
                    yield return $"snapshot: unknown robot role '{role}'.";
                }
                if (robot is null)
                {
                    yield return $"snapshot: robots['{role}'] must not be null.";
                }
            }
        }

        if (Events is { Count: > 0 })
        {
            var previousSeq = 0L;
            foreach (var evt in Events)
            {
                if (evt is null)
                {
                    yield return "snapshot: events must not contain null entries.";
                    continue;
                }
                foreach (var error in evt.Validate())
                {
                    yield return $"snapshot: {error}";
                }
                if (evt.Seq <= previousSeq)
                {
                    yield return $"snapshot: event seq must be strictly increasing, got {evt.Seq} after {previousSeq}.";
                }
                previousSeq = evt.Seq;
            }
        }
    }
}

/// <summary>
/// Full dynamic state of one robot (legacy state.robots.&lt;role&gt; shape,
/// including the non-ideal dynamics fields vx/vy/speed/omega/pitch/roll/zG/
/// isStalled/wedgedFront/frontLoad).
/// </summary>
public sealed record RobotState
{
    public double X { get; init; }

    public double Y { get; init; }

    public double Th { get; init; }

    /// <summary>Commanded/integrated linear velocity (m/s).</summary>
    public double V { get; init; }

    /// <summary>Commanded/integrated angular velocity (rad/s).</summary>
    public double W { get; init; }

    /// <summary>Actual integrated X velocity (m/s).</summary>
    public double Vx { get; init; }

    /// <summary>Actual integrated Y velocity (m/s).</summary>
    public double Vy { get; init; }

    /// <summary>Actual speed magnitude (m/s).</summary>
    public double Speed { get; init; }

    /// <summary>Actual angular velocity (rad/s).</summary>
    public double Omega { get; init; }

    /// <summary>Pitch on the step edge (rad) — display/diagnostic only.</summary>
    public double Pitch { get; init; }

    /// <summary>Roll on the step edge (rad) — display/diagnostic only.</summary>
    public double Roll { get; init; }

    /// <summary>Height above ground (m) — display/diagnostic only.</summary>
    [JsonPropertyName("zG")]
    public double ZG { get; init; }

    /// <summary>True while the drive is stalled (overcurrent semantics).</summary>
    public bool IsStalled { get; init; }

    /// <summary>True while an opponent shovel is wedged under this robot.</summary>
    public bool WedgedFront { get; init; }

    /// <summary>Front wheel load factor (drops toward 0 when wedged).</summary>
    public double FrontLoad { get; init; } = 1;

    public bool OnPlatform { get; init; }

    public bool Hang { get; init; }

    /// <summary>FSM state name.</summary>
    public string? State { get; init; }

    /// <summary>Human-readable action label.</summary>
    public string? Action { get; init; }

    /// <summary>True once armed for this match.</summary>
    public bool Armed { get; init; }

    /// <summary>True while under manual control.</summary>
    public bool Manual { get; init; }

    /// <summary>Per-robot countdown timer in seconds (e.g. mount countdown).</summary>
    public double Timer { get; init; }

    /// <summary>This robot's vehicle profile.</summary>
    public VehicleProfile? Vehicle { get; init; }
}
