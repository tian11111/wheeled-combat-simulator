// MakeOption 的 id 取义规则 (批4 R4.5)。无 Godot 依赖, 链进 Sim.Tests 回归
// (见 Sim.Tests.csproj) —— 下标只表示 UI 顺序, 语义一律按"id 表 + 下标 → id"取,
// 加/删/换序选项不再错位。

namespace Sim.GodotShell;

/// <summary>
/// OptionButton 选项的 id 语义: <c>SettingsPanel.MakeOption</c> 把每条选项的 id 按
/// AddItem 顺序登记到控件 Meta (<see cref="MetaKey"/>), 读取端先取当前下标再映射回
/// id (<c>GetSelectedId</c>), 不再对下标 switch —— UI 顺序不承担语义。
/// </summary>
public static class SettingsOptionIds
{
    /// <summary>OptionButton.Meta 里存 id 串表 (按 AddItem 顺序) 的键。</summary>
    public const string MetaKey = "settings_option_ids";

    /// <summary>
    /// 下标 → id; 空表/越界返回 <paramref name="fallback"/> (控件未建好或表丢失时的
    /// 防御: 退回该下拉的既有默认档语义)。
    /// </summary>
    public static string IdAt(IReadOnlyList<string>? ids, int index, string fallback)
        => ids is not null && index >= 0 && index < ids.Count ? ids[index] : fallback;

    /// <summary>
    /// id → 下标; 空值/未收录返回 0。首档即既有默认档 (跟随场景/窗口化/内置 FSM 等),
    /// 故旧设置缺省字段与"显式选了首档"都落到 0, 与行为不变的缺省语义一致。
    /// </summary>
    public static int IndexOf(IReadOnlyList<string>? ids, string? id)
    {
        if (ids is null || id is null)
        {
            return 0;
        }
        for (var i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return 0;
    }
}
