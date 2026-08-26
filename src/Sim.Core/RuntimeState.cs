using System.Text.Json;
using Sim.Protocol;

namespace Sim.Core;

/// <summary>FSM states (legacy names, used verbatim in states/actions/events).</summary>
public enum FsmState
{
    WaitStart,
    MountRing,
    Search,
    Attack,
    ScoreBlock,
    Recover,
    Finished,
    Manual,
}

/// <summary>Legacy wire spelling of an FSM state.</summary>
public static class FsmStateNames
{
    public static string ToWire(FsmState state) => state switch
    {
        FsmState.WaitStart => "WAIT_START",
        FsmState.MountRing => "MOUNT_RING",
        FsmState.Search => "SEARCH",
        FsmState.Attack => "ATTACK",
        FsmState.ScoreBlock => "SCORE_BLOCK",
        FsmState.Recover => "RECOVER",
        FsmState.Finished => "FINISHED",
        FsmState.Manual => "MANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

/// <summary>Mount sub-state (MOUNT_RING phases).</summary>
public sealed class MountState
{
    public string Phase = "posture";
    public double T;
    public int Faces;
    public bool RushSeen;
    public bool Climbed;
    public double BackoffT;
    public bool FwdAltAligned;
}

/// <summary>Recover sub-state (RECOVER phases).</summary>
public sealed class RecoverState
{
    public string Phase = "spin";
    public double T;
    public int Count;
    public double FallDir;
}

/// <summary>Search sub-state (SEARCH phases).</summary>
public sealed class ScanState
{
    public int Dir = 1;
    public string Phase = "scan";
    public TargetInfo? Target;
    public double T;
    public int Side = 1;
}

/// <summary>Sensor target found by the diagonal IR probes.</summary>
public sealed class TargetInfo
{
    public required object Obj; // BlockRuntime or RobotRuntime
    public double D;
    public required string Rel;
}

/// <summary>Per-robot FSM state (legacy <c>r.fsm</c>).</summary>
public sealed class FsmRuntime
{
    public FsmState State = FsmState.WaitStart;
    public string Action = "等待发令";
    public bool Armed;
    public bool Manual;
    public double Timer = 120;
    public double SimT;
    public MountState Mount = new();
    public RecoverState Rec = new();
    public ScanState Scan = new();
    public BlockRuntime? ScoreTarget;
    public double ScoreLastX;
    public double ScoreLastY;
    public double ScoreProgressT;
    public string DoneReason = "";
    public double InactiveT;
    public bool InactiveWarned;
}

/// <summary>Hysteresis state of one sensor channel (per robot, keyed by channel id).</summary>
public sealed class HysteresisState
{
    public double Value;
    public int Bit;
}

/// <summary>Common physics body surface shared by robots and energy blocks.</summary>
public interface IBody
{
    double X { get; set; }

    double Y { get; set; }

    double Vx { get; set; }

    double Vy { get; set; }

    double Mass { get; }
}

/// <summary>
/// Mutable runtime state of one robot. Mirrors the legacy CORE robot object
/// (US/THEM share the same structure; only vehicle profiles differ).
/// </summary>
public sealed class RobotRuntime : IBody
{
    public required string Role;      // "us" | "them"
    public required string Name;      // 我方 | 对手 (legacy log prefix)
    public double X { get; set; }
    public double Y { get; set; }
    public double Th;
    public double V;                  // requested linear velocity (clamped)
    public double W;                  // requested angular velocity (clamped)
    public double Vx { get; set; }
    public double Vy { get; set; }
    public VehicleProfile Vehicle = VehicleProfile.Default;
    public double R = 0.16;           // collisionRadius cache
    public double Omega;              // actual yaw rate
    public double SpinOmega;          // post-collision spin (decays)
    public double ZG;
    public double Pitch;
    public double Roll;
    public bool IsStalled;
    public double StallT;
    public double StallAnchorX;
    public double StallAnchorY;
    public bool WedgedFront;
    public double FrontLoad = 1;
    public Queue<(double V, double W)> CmdQueue = new();
    public double CmdV;
    public double CmdW;
    public Dictionary<string, HysteresisState> IrHyst = new();
    public bool WasOn;
    public bool DropPending;
    public FsmRuntime Fsm = new();
    public Dictionary<string, double> Sens = new();    // legacy logical aliases
    public Dictionary<string, double> RawSens = new(); // real profile channels
    public Dictionary<string, SensorProbe> Probe = new();

    public bool IsUs => Role == RoleNames.Us;

    double IBody.Mass => Math.Max(0.05, Vehicle.Mass);
}

/// <summary>One energy block (buff or debuff).</summary>
public sealed class BlockRuntime : IBody
{
    public BlockKind Kind;
    public string Name = "";        // 增益块 / 减益块
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double R = 0.075;
    public bool WasOn = true;
    public bool Out;
    public List<(string Role, double T)> ContactThisStep = new();
    public string? LastContactRole;

    double IBody.Mass => 0.30;
}

/// <summary>
/// A structured domain event plus its legacy log-line projection. The legacy
/// log line (seq/t/cls/msg) is kept verbatim for trace comparison against the
/// old prototype; the structured fields (kind/data) are the new surface.
/// </summary>
public sealed record CoreEvent
{
    public required long Seq { get; init; }

    public required EventKind Kind { get; init; }

    public required double T { get; init; }

    /// <summary>
    /// Robot the legacy log line was attributed to (drives the message prefix,
    /// timestamp and default class). Neutral referee decisions use the "us"
    /// robot for the log line but <see cref="Neutral"/> for the structured role.
    /// </summary>
    public required RobotRuntime Robot { get; init; }

    /// <summary>True for neutral events (no structured role).</summary>
    public bool Neutral { get; init; }

    /// <summary>Legacy log class: "us"/"them"/"score"/"warn"/"sim".</summary>
    public required string Cls { get; init; }

    /// <summary>Legacy log message (verbatim, including the [我方] prefix).</summary>
    public required string Msg { get; init; }

    /// <summary>Structured payload serialized with the protocol options.</summary>
    public object? Data { get; init; }

    /// <summary>Tick index at which the event was committed.</summary>
    public long Tick { get; init; }

    /// <summary>Converts to the protocol <see cref="Event"/> DTO (message carries the legacy "[我方]/[对手]" prefix).</summary>
    public Event ToProtocolEvent() => new()
    {
        Seq = Seq,
        Tick = Tick,
        T = T,
        Type = Kind,
        Role = Neutral ? null : Robot.Role,
        Cls = Cls,
        Msg = $"[{Robot.Name}] {Msg}",
        Data = Data is null ? null : ToData(Data),
    };

    private static Dictionary<string, JsonElement> ToData(object data)
    {
        var element = JsonSerializer.SerializeToElement(data, ProtocolJson.Options);
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.Deserialize<Dictionary<string, JsonElement>>(ProtocolJson.Options)
                ?? new Dictionary<string, JsonElement>();
        }
        return new Dictionary<string, JsonElement> { ["value"] = element };
    }
}

/// <summary>Monotonic event bus shared by the FSM, physics and referee.</summary>
public sealed class EventBus
{
    private readonly List<CoreEvent> _events = new();

    public IReadOnlyList<CoreEvent> Events => _events;

    public long Tick { get; set; }

    public void Emit(EventKind kind, RobotRuntime robot, string msg, string? cls = null, object? data = null, bool neutral = false)
    {
        _events.Add(new CoreEvent
        {
            Seq = _events.Count + 1,
            Kind = kind,
            T = JsNum(robot.Fsm.SimT),
            Robot = robot,
            Neutral = neutral,
            Cls = cls ?? (robot.IsUs ? "us" : "them"),
            Msg = msg,
            Data = data,
            Tick = Tick,
        });

        static double JsNum(double simT)
        {
            // legacy: +r.fsm.simT.toFixed(1)
            var text = Js.ToFixed(simT, 1);
            return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
