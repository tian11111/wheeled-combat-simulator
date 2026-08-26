using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// Vehicle and sensor profile normalization, ported from the legacy CORE
/// <c>normalizeVehicle</c>/<c>normalizeSensorProfile</c>. The protocol DTOs
/// already carry the legacy defaults for missing JSON fields, so normalization
/// here is the CONTRACT.md 5.1 range clamping plus the footprint minimums
/// that prevent tunneling (extents must cover body + shovel).
/// </summary>
public static class VehicleNormalizer
{
    /// <summary>
    /// Normalizes a vehicle profile. A null sensor profile becomes the legacy
    /// 14-channel compatibility profile (headless/API default of the old core).
    /// </summary>
    public static VehicleProfile Normalize(VehicleProfile input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var length = Js.Clamp(input.Length, 0.08, 0.8);
        var width = Js.Clamp(input.Width, 0.08, 0.8);
        var height = Js.Clamp(input.Height, 0.02, 0.4);
        var shovelLength = Js.Clamp(input.ShovelLength, 0, 0.5);
        var shovelWidth = Js.Clamp(input.ShovelWidth, 0.02, 0.8);
        var bodyRadius = Math.Max(length, width) / 2;
        var collisionRadius = Math.Max(Js.Clamp(input.CollisionRadius, 0.04, 0.6), bodyRadius);
        var frontExtent = Math.Max(Js.Clamp(input.FrontExtent, 0.04, 0.9), length / 2 + shovelLength);
        var rearExtent = Math.Max(Js.Clamp(input.RearExtent, 0.04, 0.9), length / 2);
        var sideExtent = Math.Max(Math.Max(Js.Clamp(input.SideExtent, 0.04, 0.9), width / 2), shovelWidth / 2);

        return input with
        {
            Length = length,
            Width = width,
            Height = height,
            ShovelLength = shovelLength,
            ShovelWidth = shovelWidth,
            CollisionRadius = collisionRadius,
            FrontExtent = frontExtent,
            RearExtent = rearExtent,
            SideExtent = sideExtent,
            MaxSpeed = Js.Clamp(input.MaxSpeed, 0.05, 3.0),
            MaxTurnRate = Js.Clamp(input.MaxTurnRate, 0.1, 12.0),
            AccelK = Js.Clamp(input.AccelK, 1, 40),
            Mass = Js.Clamp(input.Mass, 0.05, 10),
            PushFactor = Js.Clamp(input.PushFactor, 0.1, 3),
            WheelBase = Js.Clamp(input.WheelBase, 0.02, 0.8),
            TrackWidth = Js.Clamp(input.TrackWidth, 0.02, 0.8),
            LatFrictionK = Js.Clamp(input.LatFrictionK, 0.5, 60),
            AngDamping = Js.Clamp(input.AngDamping, 0, 40),
            ShovelHeight = Js.Clamp(input.ShovelHeight, 0, 0.3),
            Sensors = NormalizeSensors(input.Sensors),
        };
    }

    /// <summary>
    /// Normalizes a sensor profile. Channel positions/angles/ranges are clamped
    /// to the legacy bounds; output bounds default by type (gray 1000, digital 1,
    /// others 1.2). The protocol DTO default of 1.2 for <c>max</c> is corrected
    /// for gray/digital channels when the caller did not override it.
    /// </summary>
    public static SensorProfile NormalizeSensors(SensorProfile? input)
    {
        var seed = input ?? SensorProfiles.Legacy14;
        var channels = new List<SensorChannel>(seed.Channels.Count);
        var index = 0;
        foreach (var channel in seed.Channels)
        {
            index++;
            var max = channel.Max;
            if (max == new SensorChannel().Max)
            {
                // 1.2 is the DTO default; derive the type-specific legacy default.
                max = channel.Type switch
                {
                    SensorType.Gray => 1000,
                    SensorType.Digital => 1,
                    _ => 1.2,
                };
            }
            channels.Add(channel with
            {
                Id = string.IsNullOrWhiteSpace(channel.Id) ? $"sensor_{index}" : channel.Id,
                Forward = Js.Clamp(channel.Forward, -1, 1),
                Lateral = Js.Clamp(channel.Lateral, -1, 1),
                Angle = Js.Clamp(channel.Angle, -Math.PI, Math.PI),
                Range = Js.Clamp(channel.Range, 0.05, 3),
                Fov = Js.Clamp(channel.Fov, 0.01, Math.PI),
                Min = Js.Clamp(channel.Min, 0, 100000),
                Max = Js.Clamp(max, 0.0001, 100000),
                Noise = channel.Noise is null ? null : Math.Max(0, channel.Noise.Value),
            });
        }
        return new SensorProfile
        {
            Id = string.IsNullOrWhiteSpace(seed.Id) ? "custom" : seed.Id,
            Label = seed.Label,
            Channels = channels,
            Logical = seed.Logical is null || seed.Logical.Count == 0
                ? new Dictionary<string, LogicalSensorMap>(SensorProfiles.Legacy14.Logical!)
                : new Dictionary<string, LogicalSensorMap>(seed.Logical),
        };
    }
}
