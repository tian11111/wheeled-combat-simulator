// 传感器通道行编辑的合并规则 (批3 R3.2)。无 Godot 依赖, 链进 Sim.Tests 回归
// (见 Sim.Tests.csproj) —— 面板控件本身不可单测, 但"同名保留/异名重置 + 保存档打底"
// 的语义在此。

using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>
/// 一条传感器通道行上的编辑值: 启用勾选 + 车体系挂点偏移 (dx 前向 / dy 横向 /
/// dz 高度 / dyaw 朝向)。
/// </summary>
public readonly record struct SensorChannelEdit(bool Enabled, double Dx, double Dy, double Dz, double Yaw)
{
    /// <summary>基底默认: 启用 + 零偏移 (未编辑通道回落到这里)。</summary>
    public static SensorChannelEdit Default => new(true, 0, 0, 0, 0);
}

/// <summary>
/// 切换传感器预设时的通道值合成 (批3 R3.2): 以当前控件上的未应用编辑为最高优先,
/// 同名通道保留编辑值; 没有同名编辑的通道回上次保存档 (仍无则基底默认)。旧基底独有
/// 的编辑随行消失 (异名重置, 与下拉框只展示新基底通道一致), 但已保存的覆盖不会因为
/// 在中间基底的控件上"看不见"就被静默清空 (切走再切回仍回保存档)。
/// </summary>
public static class SensorChannelEdits
{
    /// <summary>
    /// 按新基底通道清单产出每通道生效值, 优先级:
    /// <paramref name="edited"/> (当前控件未应用编辑) → <paramref name="saved"/>
    /// (上次保存档) → <see cref="SensorChannelEdit.Default"/> (基底默认)。
    /// 返回值键集合 = 新基底通道集合 (两份来源的异名键都不进入结果)。
    /// </summary>
    public static Dictionary<string, SensorChannelEdit> Merge(
        IReadOnlyDictionary<string, SensorChannelEdit>? saved,
        IReadOnlyDictionary<string, SensorChannelEdit>? edited,
        SensorProfile newBase)
    {
        ArgumentNullException.ThrowIfNull(newBase);
        var merged = new Dictionary<string, SensorChannelEdit>(StringComparer.Ordinal);
        foreach (var channel in newBase.Channels)
        {
            if (edited is not null && edited.TryGetValue(channel.Id, out var kept))
            {
                merged[channel.Id] = kept;
            }
            else if (saved is not null && saved.TryGetValue(channel.Id, out var previous))
            {
                merged[channel.Id] = previous;
            }
            else
            {
                merged[channel.Id] = SensorChannelEdit.Default;
            }
        }
        return merged;
    }
}
