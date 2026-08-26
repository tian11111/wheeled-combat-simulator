using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// The controller action message — the wire form of the external
/// <c>decide(obs) -&gt; {v, w}</c> contract.
///
/// Semantics (CONTRACT.md sections 2 and 4):
/// <list type="bullet">
///   <item><c>v</c> is the requested linear velocity (m/s), <c>w</c> the requested
///   angular velocity (rad/s). Both must be finite numbers; lines without both
///   finite values are dropped by the bridge and treated as a zero action.</item>
///   <item>Requested actions are clamped symmetrically to the current vehicle
///   profile's <c>maxSpeed</c>/<c>maxTurnRate</c> before entering the dynamics.</item>
///   <item><c>requestId</c> echoes the observation's request id; late actions are
///   matched by id and dropped, never applied to a later frame.</item>
/// </list>
/// Named <c>RobotAction</c> (instead of <c>Action</c>) to avoid colliding with
/// <see cref="System.Action"/> in consuming projects.
/// </summary>
public sealed record RobotAction : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Requested linear velocity (m/s).</summary>
    public double V { get; init; }

    /// <summary>Requested angular velocity (rad/s).</summary>
    public double W { get; init; }

    /// <summary>
    /// Echo of the observation request id. Accepts both numeric and string ids
    /// on the wire; integral ids are written back as numbers.
    /// </summary>
    [JsonConverter(typeof(RequestIdConverter))]
    public string? RequestId { get; init; }

    /// <summary>The neutral fallback action used on timeout/protocol failure (v=0, w=0).</summary>
    public static RobotAction Zero { get; } = new();

    /// <summary>True when both <see cref="V"/> and <see cref="W"/> are finite numbers.</summary>
    [JsonIgnore]
    public bool IsFinite => double.IsFinite(V) && double.IsFinite(W);

    /// <summary>
    /// Returns this action clamped symmetrically to <paramref name="limits"/>,
    /// matching the legacy "requested action" semantics (profile-limited policy request).
    /// Non-finite values are left untouched; use <see cref="IsFinite"/> to reject those first.
    /// </summary>
    public RobotAction ClampTo(ActionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var v = double.IsFinite(V) ? Math.Clamp(V, -limits.MaxSpeed, limits.MaxSpeed) : V;
        var w = double.IsFinite(W) ? Math.Clamp(W, -limits.MaxTurnRate, limits.MaxTurnRate) : W;
        return (v == V && w == W) ? this : this with { V = v, W = w };
    }

    /// <summary>
    /// Returns this action clamped using the limits of a vehicle profile
    /// (its <c>maxSpeed</c>/<c>maxTurnRate</c>).
    /// </summary>
    public RobotAction ClampTo(VehicleProfile vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return ClampTo(new ActionLimits { MaxSpeed = vehicle.MaxSpeed, MaxTurnRate = vehicle.MaxTurnRate });
    }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "action: protocolVersion must not be empty.";
        }
        if (!double.IsFinite(V))
        {
            yield return "action: v must be a finite number.";
        }
        if (!double.IsFinite(W))
        {
            yield return "action: w must be a finite number.";
        }
    }
}

/// <summary>
/// Symmetric action limits derived from a vehicle profile
/// (<see cref="VehicleProfile.MaxSpeed"/> / <see cref="VehicleProfile.MaxTurnRate"/>).
/// Defaults mirror the legacy DEFAULT_VEHICLE.
/// </summary>
public sealed record ActionLimits
{
    /// <summary>Maximum absolute linear velocity (m/s). Legacy default: 1.5.</summary>
    public double MaxSpeed { get; init; } = 1.5;

    /// <summary>Maximum absolute angular velocity (rad/s). Legacy default: 4.0.</summary>
    public double MaxTurnRate { get; init; } = 4.0;

    public static ActionLimits Default { get; } = new();

    /// <summary>Clamps an action to these limits (see <see cref="RobotAction.ClampTo"/>).</summary>
    public RobotAction Clamp(RobotAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.ClampTo(this);
    }

    public IEnumerable<string> Validate()
    {
        if (!(MaxSpeed > 0) || !double.IsFinite(MaxSpeed))
        {
            yield return "action limits: maxSpeed must be a positive finite number.";
        }
        if (!(MaxTurnRate > 0) || !double.IsFinite(MaxTurnRate))
        {
            yield return "action limits: maxTurnRate must be a positive finite number.";
        }
    }
}
