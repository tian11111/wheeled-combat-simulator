// 机器人外观模型导入 (纯渲染层, design.md "Robot Model Import"):
// - res:// 资源走 ResourceLoader (编辑器已导入的 .glb/.gltf);
// - 外部文件走 GltfDocument.AppendFromFile 运行时解析;
// - 校验扩展名/存在性/文件大小/节点数, 任一失败即回退 primitive 并报错,
//   绝不允许导入失败改变仿真结果 (权威参数仍来自 VehicleProfile)。
// 模型配置是本地桌面状态 (robot-models.json), 永不进入 Scenario/Snapshot/回放。

using Godot;

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

public static class RobotModelLoader
{
    /// <summary>保守的运行时导入上限。</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;
    public const int MaxNodes = 5000;

    private const string ModelNodeName = "ImportedModel";

    /// <summary>
    /// 把 config 指定的模型挂到机器人根节点 (成功时隐藏 primitive 车身,
    /// 保留登台环)。返回 null 表示成功; 错误信息表示保持 primitive 回退。
    /// 重复调用同一路径时复用已导入节点, 只更新变换。
    /// </summary>
    public static string? Apply(Node3D? robotRoot, RobotModelConfig? config)
    {
        if (robotRoot is null)
        {
            return "机器人节点不存在";
        }
        var existing = robotRoot.GetNodeOrNull<Node3D>(ModelNodeName);
        if (config is null || config.IsEmpty)
        {
            if (existing is not null)
            {
                DetachAndFree(existing);
            }
            ShowPrimitive(robotRoot, true);
            return null;
        }

        var loaded = existing;
        if (loaded is not null && (!loaded.HasMeta("modelPath") || loaded.GetMeta("modelPath").AsString() != config.Path))
        {
            // Free immediately: QueueFree would keep the name occupied this
            // frame and the replacement would be auto-renamed.
            DetachAndFree(loaded);
            loaded = null;
        }
        if (loaded is null)
        {
            var error = LoadInto(robotRoot, config.Path, out loaded);
            if (error is not null)
            {
                return error;
            }
        }

        loaded!.Position = new Vector3(0, (float)config.HeightOffset, 0);
        loaded.Rotation = new Vector3(0, (float)config.YawOffset, 0);
        var s = (float)(config.Scale > 0 ? config.Scale : 1.0);
        loaded.Scale = new Vector3(s, s, s);
        ShowPrimitive(robotRoot, false);
        return null;
    }

    private static string? LoadInto(Node3D robotRoot, string path, out Node3D? model)
    {
        model = null;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".glb" && ext != ".gltf")
        {
            return $"不支持的模型格式 '{ext}' (仅 .glb/.gltf)";
        }

        Node3D? scene;
        if (path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal))
        {
            // 已导入资源走资源加载快速路径。
            var packed = GD.Load<PackedScene>(path);
            if (packed is null)
            {
                return $"无法加载 res 资源: {path}";
            }
            scene = packed.Instantiate() as Node3D;
        }
        else
        {
            if (!System.IO.File.Exists(path))
            {
                return $"模型文件不存在: {path}";
            }
            var info = new System.IO.FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                return $"模型文件过大 ({info.Length} B > {MaxFileBytes} B): {path}";
            }
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(path, state, 0);
            if (err != Error.Ok)
            {
                return $"GLTF 解析失败 ({err}): {path}";
            }
            var gen = doc.GenerateScene(state, 0);
            scene = gen as Node3D;
        }

        if (scene is null)
        {
            return $"模型根节点不是 Node3D: {path}";
        }
        if (CountNodes(scene) > MaxNodes)
        {
            scene.Free();
            return $"模型节点数超限: {path}";
        }
        scene.Name = ModelNodeName;
        scene.SetMeta("modelPath", path);
        robotRoot.AddChild(scene);
        EnsureNormals(scene);
        model = scene;
        return null;
    }

    private static void DetachAndFree(Node node)
    {
        node.GetParent()?.RemoveChild(node);
        node.Free();
    }

    private static void ShowPrimitive(Node3D robotRoot, bool visible)
    {
        foreach (var part in new[] { "Body", "Nose", "Shovel" })
        {
            if (robotRoot.GetNodeOrNull<Node3D>(part) is { } node)
            {
                node.Visible = visible;
            }
        }
    }

    /// <summary>
    /// Runtime GltfDocument import does not synthesize normals the way the
    /// editor importer does; without them the Forward+ renderer draws black.
    /// Rebuild any surface that has no normal array with flat normals.
    /// </summary>
    private static void EnsureNormals(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is MeshInstance3D instance && instance.Mesh is ArrayMesh array)
            {
                for (var i = 0; i < array.GetSurfaceCount(); i++)
                {
                    var normals = array.SurfaceGetArrays(i)[(int)Mesh.ArrayType.Normal];
                    if (normals.VariantType == Variant.Type.Nil || normals.As<Vector3[]>().Length == 0)
                    {
                        var tool = new SurfaceTool();
                        tool.AppendFrom(array, i, Transform3D.Identity);
                        tool.GenerateNormals();
                        var fixedMesh = tool.Commit();
                        if (fixedMesh is not null)
                        {
                            instance.Mesh = fixedMesh;
                        }
                    }
                }
            }
            if (child is Node childNode)
            {
                EnsureNormals(childNode);
            }
        }
    }

    private static int CountNodes(Node node)
    {
        var total = 1;
        foreach (var child in node.GetChildren())
        {
            if (child is Node childNode)
            {
                total += CountNodes(childNode);
            }
        }
        return total;
    }
}
