// 外观模型绑定配置 (robot-models.json 的单条记录)。
// 单独成文件是为了保持 Godot 依赖边界: RobotModelLoader.cs 需要 Godot 的
// GltfDocument/Node3D, 而这条纯数据契约要能被 Sim.Tests 链接(配置包 bundle 的
// 序列化/往返回归), 不能在测试程序集里拖进 Godot 运行时。

namespace Sim.GodotShell;

/// <summary>Render-only model binding for one robot role.</summary>
public sealed record RobotModelConfig
{
    /// <summary>res:// 路径或文件系统路径 (.glb/.gltf)。空 = 使用 primitive。</summary>
    public string Path { get; init; } = "";

    /// <summary>均匀缩放 (渲染层)。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>朝向偏移 (rad)。</summary>
    public double YawOffset { get; init; }

    /// <summary>高度偏移 (m)。</summary>
    public double HeightOffset { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Path);
}
