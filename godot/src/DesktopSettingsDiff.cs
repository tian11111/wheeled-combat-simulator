// 桌面设置"是否改变对局结果"的深比较 (批3 R3.1)。无 Godot 依赖, 链进 Sim.Tests
// 回归 (见 Sim.Tests.csproj) —— MatchSettingsEqual 原先私有在 Main 里, 无法单测,
// 且遗漏 Vehicle 导致"只改小车却打印显示设置已应用"。

namespace Sim.GodotShell;

/// <summary>
/// 判定两份 <see cref="DesktopSettings"/> 之间是否只有显示项 (窗口/缩放) 变化 ——
/// Main.ApplyDesktopSettings 据此决定"应用设置后自动重开当前对局"还是"仅显示设置
/// 已应用"。比较范围 = 所有进入场景装配的字段: 仿真参数 / 双方控制器 / 视觉源 /
/// 能量块覆盖 / 比赛场景覆盖 / 高级接触开关 / 小车设置 (质量·转速·扭矩·轮径·
/// 传感器预设·禁用清单·挂点偏移)。
///
/// null 与空集合语义一致 (老配置缺省 = null 或空, 行为逐位不变): null 清单/字典
/// 与空清单/字典相等; 缺省接触开关 = 全开。
/// </summary>
public static class DesktopSettingsDiff
{
    /// <summary>True = 除显示项外没有会改变对局结果的差异 (含左右同为 null 的缺省档)。</summary>
    public static bool MatchRelevantEqual(DesktopSettings? left, DesktopSettings? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null)
        {
            return false;
        }
        return DictionaryEqual(left.SimulationParameters, right.SimulationParameters)
            && ControllerEqual(left.UsController, right.UsController)
            && ControllerEqual(left.ThemController, right.ThemController)
            && VisionEqual(left.Vision, right.Vision)
            && BlockLayoutEqual(left.BlockLayout, right.BlockLayout)
            && MatchOverridesEqual(left.MatchOverrides, right.MatchOverrides)
            && DevContactEqual(left.DevContact, right.DevContact)
            // 批3 R3.1: 小车设置 (含传感器覆盖) 也改对局结果, 必须计入; 缺省档两边
            // 都是默认 VehicleSettings/new 语义 = 相等, 老配置行为不变。
            && VehicleEqual(left.Vehicle, right.Vehicle);
    }

    private static bool VehicleEqual(VehicleSettings? left, VehicleSettings? right)
        => (left?.Mass ?? 3.5) == (right?.Mass ?? 3.5)
            && (left?.MotorRpm ?? 120) == (right?.MotorRpm ?? 120)
            && (left?.MotorTorque ?? 1.72) == (right?.MotorTorque ?? 1.72)
            && (left?.WheelRadius ?? 0.0325) == (right?.WheelRadius ?? 0.0325)
            && (left?.SensorProfileId ?? "") == (right?.SensorProfileId ?? "")
            && SetEqual(left?.SensorDisabled, right?.SensorDisabled)
            && SensorOffsetsEqual(left?.SensorOffsets, right?.SensorOffsets);

    private static bool MatchOverridesEqual(MatchOverrides? left, MatchOverrides? right)
        => (left?.PhysicsBackendOverride ?? "") == (right?.PhysicsBackendOverride ?? "")
            && (left?.ScenarioPath ?? "") == (right?.ScenarioPath ?? "")
            && left?.MatchDuration == right?.MatchDuration
            && left?.Seed == right?.Seed;

    private static bool DevContactEqual(DevContact? left, DevContact? right)
        => (left?.L1VehicleVehicleObb ?? true) == (right?.L1VehicleVehicleObb ?? true)
            && (left?.L2VehicleBlockObb ?? true) == (right?.L2VehicleBlockObb ?? true)
            && (left?.L3BlockWallBlock ?? true) == (right?.L3BlockWallBlock ?? true);

    private static bool BlockLayoutEqual(BlockLayoutSettings? left, BlockLayoutSettings? right)
        => left?.BuffCount == right?.BuffCount
            && left?.DebuffCount == right?.DebuffCount
            && left?.RandomPositions == right?.RandomPositions;

    private static bool VisionEqual(VisionSettings? left, VisionSettings? right)
        => left?.Source == right?.Source
            && left?.EvidencePath == right?.EvidencePath
            && left?.CsvPath == right?.CsvPath
            && left?.MaxAgeMs == right?.MaxAgeMs;

    private static bool DictionaryEqual(IReadOnlyDictionary<string, double>? left,
        IReadOnlyDictionary<string, double>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }
        return left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    /// <summary>禁用清单按集合语义比较 (顺序不承载含义; null 与空集相等)。</summary>
    private static bool SetEqual(IReadOnlyCollection<string>? left, IReadOnlyCollection<string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        var leftSet = new HashSet<string>(left ?? [], StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right ?? [], StringComparer.Ordinal);
        return leftSet.SetEquals(rightSet);
    }

    /// <summary>
    /// 挂点偏移字典: 键集合与每条 SensorOffset 值都相等。null 与空字典在这里视为相等
    /// (两者都表示"无覆盖", 与引擎语义一致) —— 避免反序列化把 null/省略档判成"改过小车"
    /// 而触发一次多余的重开。
    /// </summary>
    private static bool SensorOffsetsEqual(IReadOnlyDictionary<string, SensorOffset>? left,
        IReadOnlyDictionary<string, SensorOffset>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        var leftMap = left ?? EmptyOffsets;
        var rightMap = right ?? EmptyOffsets;
        return leftMap.Count == rightMap.Count
            && leftMap.All(pair => rightMap.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static readonly Dictionary<string, SensorOffset> EmptyOffsets = new(StringComparer.Ordinal);

    private static bool ControllerEqual(ControllerProfile? left, ControllerProfile? right)
        => left?.Mode == right?.Mode
            && left?.Command == right?.Command
            && left?.TimeoutMs == right?.TimeoutMs;
}
