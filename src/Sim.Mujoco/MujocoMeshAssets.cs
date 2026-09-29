using System.Reflection;

namespace Sim.Mujoco;

/// <summary>一份随模型编译进入 MuJoCo 内存 VFS 的网格资产(逻辑文件名 + 原始字节)。</summary>
/// <param name="Name">XML 里出现的逻辑文件名(不含任何机器路径)。</param>
/// <param name="Bytes">STL 原始字节, 参与 <see cref="MujocoModel"/> 的 ModelSha256。</param>
internal sealed record MujocoMeshAsset(string Name, byte[] Bytes);

/// <summary>
/// 物理车体的网格资产来源。资产以 <c>EmbeddedResource</c> 编进 Sim.Mujoco.dll
/// (见 Sim.Mujoco.csproj 与 tools/mesh/README.md), 因此:
/// <list type="bullet">
/// <item>XML 里只出现逻辑文件名, 运行时不需要任何磁盘路径 —— 跨机哈希稳定;</item>
/// <item>CLI / Godot / 训练进程只要加载了本程序集就一定能拿到字节(不依赖部署时拷贝的目录布局)。</item>
/// </list>
/// 字节与 <c>tools/mesh/extract_vehicle_mesh.py</c> 的产出逐字节一致(sha256 记录在
/// tools/mesh/README.md), 换资产即换物理身份。
/// </summary>
internal static class MujocoMeshAssets
{
    /// <summary>车体主结构(底板 + 上板合并凸体)的逻辑文件名。</summary>
    internal const string Chassis = "robot_chassis.stl";

    /// <summary>后铲的逻辑文件名。后铲必须与车体分开做 geom, 否则凹角被凸包填平。</summary>
    internal const string RearShovel = "robot_rear_shovel.stl";

    internal const string ResourcePrefix = "Sim.Mujoco.assets.";

    /// <summary>v2 物理实际引用的资产(按名字升序, 与 ModelSha256 的排序口径一致)。</summary>
    internal static readonly string[] Required = [Chassis, RearShovel];

    private static readonly Lazy<IReadOnlyList<MujocoMeshAsset>> Cached =
        new(() => Read(Required), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>读取 v2 车体资产(进程内缓存; 返回的字节数组视为只读)。</summary>
    internal static IReadOnlyList<MujocoMeshAsset> Load() => Cached.Value;

    /// <summary>按给定逻辑文件名读取嵌入资源; 缺失即抛(资产与二进制不同步是硬错误)。</summary>
    internal static IReadOnlyList<MujocoMeshAsset> Read(IEnumerable<string> names)
    {
        var assembly = typeof(MujocoMeshAssets).Assembly;
        var result = new List<MujocoMeshAsset>();
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
                ?? throw new InvalidOperationException(
                    $"Embedded mesh asset '{ResourcePrefix}{name}' is missing from {assembly.GetName().Name}; "
                    + "rebuild with the assets in src/Sim.Mujoco/assets (see tools/mesh/README.md).");
            using var buffer = new MemoryStream((int)stream.Length);
            stream.CopyTo(buffer);
            result.Add(new MujocoMeshAsset(name, buffer.ToArray()));
        }
        return result;
    }

    /// <summary>诊断用: 嵌入资产清单(名字/字节数)。</summary>
    internal static IReadOnlyList<string> Manifest(Assembly assembly)
        => assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
