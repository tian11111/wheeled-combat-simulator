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

    /// <summary>Optional world-space 3D poses from the MuJoCo backend, for rendering only.</summary>
    public PhysicsPoses? PhysicsPoses { get; init; }

    /// <summary>Events committed since the previous snapshot (monotonic seq).</summary>
    public List<Event>? Events { get; init; }

    /// <summary>Score delta since the previous snapshot (gym-style per-step reward).</summary>
    public Scores? Reward { get; init; }

    /// <summary>
    /// Per-source score breakdown keyed by role ("us"/"them") then by source
    /// ("drop"/"clock"/"block_buff"/"block_debuff"/"penalty"/"restart"/
    /// "inactivity"), mirroring the 2026 评分表. Additive (old JSON decodes
    /// unchanged); the per-source values always sum to <see cref="Scores"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, double>>? ScoreBreakdown { get; init; }

    /// <summary>Active referee score-clock phase ("us_only"/"them_only"); null when both sides share a state.</summary>
    public string? ScoreClockPhase { get; init; }

    /// <summary>Seconds accumulated toward the next score-clock point (0–10).</summary>
    public double? ScoreClockSeconds { get; init; }

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

        if (PhysicsPoses is not null)
        {
            foreach (var error in PhysicsPoses.Validate())
            {
                yield return $"snapshot: {error}";
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

/// <summary>World-space position in metres and quaternion in x/y/z/w order.</summary>
public sealed record PhysicsPose3
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Qx { get; init; }
    public double Qy { get; init; }
    public double Qz { get; init; }
    public double Qw { get; init; } = 1;

    public IEnumerable<string> Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Z))
        {
            yield return "position x/y/z must be finite.";
        }
        if (!double.IsFinite(Qx) || !double.IsFinite(Qy) || !double.IsFinite(Qz) || !double.IsFinite(Qw)
            || Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw < 1e-12)
        {
            yield return "quaternion qx/qy/qz/qw must be finite and nonzero.";
        }
    }
}

/// <summary>Robot poses by role and block poses in the same order as ObjectSet.</summary>
public sealed record PhysicsPoses
{
    public Dictionary<string, PhysicsPose3> Robots { get; init; } = new();
    public List<PhysicsPose3> Buffs { get; init; } = [];
    public PhysicsPose3? Debuff { get; init; }

    public IEnumerable<string> Validate()
    {
        if (Robots is null)
        {
            yield return "physicsPoses.robots must be present.";
        }
        else
        {
            foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
            {
                if (!Robots.ContainsKey(role))
                {
                    yield return $"physicsPoses.robots must contain '{role}'.";
                }
            }
            foreach (var (role, pose) in Robots)
            {
                if (!RoleNames.IsKnownRole(role) || pose is null)
                {
                    yield return $"physicsPoses.robots['{role}'] is invalid.";
                    continue;
                }
                foreach (var error in pose.Validate())
                {
                    yield return $"physicsPoses.robots['{role}']: {error}";
                }
            }
        }
        if (Buffs is null)
        {
            yield return "physicsPoses.buffs must be present.";
        }
        else
        {
            for (var i = 0; i < Buffs.Count; i++)
            {
                if (Buffs[i] is null)
                {
                    yield return $"physicsPoses.buffs[{i}] must not be null.";
                    continue;
                }
                foreach (var error in Buffs[i].Validate())
                {
                    yield return $"physicsPoses.buffs[{i}]: {error}";
                }
            }
        }
        if (Debuff is not null)
        {
            foreach (var error in Debuff.Validate())
            {
                yield return $"physicsPoses.debuff: {error}";
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
