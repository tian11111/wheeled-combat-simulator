namespace Sim.Protocol;

/// <summary>
/// 桌面"传感器覆盖"的克隆器: 以内置/场景 profile 为基底, 应用通道开关与
/// 挂点/朝向偏移, 产出新的自定义 profile —— 不改基底、不改逻辑别名映射
/// (Logical 仍指向原通道 id, 禁用通道经 SensorSampler 缺读数容错自然降级)。
/// 偏移语义与实车"挪探头"同构: 车体系 dx=前向(Forward)、dy=横向(Lateral)、
/// dz=高度(Height)、dyaw=朝向(Angle, rad)。
/// </summary>
public static class SensorProfileCustomizer
{
    /// <summary>单通道偏移 (m / rad)。</summary>
    public sealed record ChannelOffset(double Dx, double Dy, double Dz, double Yaw);

    /// <summary>
    /// 产出应用覆盖后的克隆。偏移与禁用都未给出的通道保持原值逐位不变;
    /// 2D 语义通道 (Height=null) 在 Dz=0 时保持 null (不因克隆升格 3D)。
    /// </summary>
    public static SensorProfile Apply(
        SensorProfile baseProfile,
        string newId,
        IReadOnlyCollection<string>? disabledIds = null,
        IReadOnlyDictionary<string, ChannelOffset>? offsets = null)
    {
        if (string.IsNullOrWhiteSpace(newId))
        {
            throw new ArgumentException("custom sensor profile id must not be empty.", nameof(newId));
        }
        var disabled = disabledIds is { Count: > 0 } ? new HashSet<string>(disabledIds) : null;
        var channels = new List<SensorChannel>(baseProfile.Channels.Count);
        foreach (var ch in baseProfile.Channels)
        {
            var next = ch;
            if (offsets is not null && offsets.TryGetValue(ch.Id, out var off))
            {
                next = next with
                {
                    Forward = ch.Forward + off.Dx,
                    Lateral = ch.Lateral + off.Dy,
                    Angle = ch.Angle + off.Yaw,
                    Height = ch.Height is { } h ? h + off.Dz : (off.Dz == 0 ? null : off.Dz),
                };
            }
            if (disabled is not null && disabled.Contains(ch.Id))
            {
                next = next with { Disabled = true };
            }
            channels.Add(next);
        }
        return baseProfile with { Id = newId, Channels = channels };
    }
}
