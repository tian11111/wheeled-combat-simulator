using Sim.Core;

namespace Sim.Mujoco;

/// <summary>
/// 训练专用 physics factory: 同一场景内容哈希的 mjModel 会话级复用(编译一次),
/// 每集创建独立 backend/mjData。普通比赛 factory 每场独立编译, 不受影响。
/// 模型句柄由本 factory 持有, 经 ReleaseAll 统一释放。
/// </summary>
public sealed class MujocoTrainingPhysicsBackendFactory : IPhysicsBackendFactory
{
    private readonly Dictionary<string, IntPtr> _models = new();

    public int CompileCount { get; private set; }

    public IPhysicsBackend Create(PhysicsBackendContext context)
    {
        // 每集生成一次 MJCF + 哈希: 缓存未命中时用同一份 xml/资产 编译, 复用构造
        // 直接携带哈希, 不再重复生成。
        var (xml, assets, hash) = MujocoModel.Generate(context);
        if (!_models.TryGetValue(hash, out var model))
        {
            model = MujocoNative.CreateModel(xml, assets);
            _models[hash] = model;
            CompileCount++;
        }
        return new MujocoPhysicsBackend(context, model, hash);
    }

    public void ReleaseAll()
    {
        foreach (var model in _models.Values)
        {
            MujocoNative.DeleteModel(model);
        }
        _models.Clear();
    }
}
