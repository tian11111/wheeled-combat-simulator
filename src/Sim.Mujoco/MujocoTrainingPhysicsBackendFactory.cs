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
        var (_, hash) = MujocoModel.Generate(context);
        if (!_models.TryGetValue(hash, out var model))
        {
            var (xml, _) = MujocoModel.Generate(context);
            model = MujocoNative.CreateModel(xml);
            _models[hash] = model;
            CompileCount++;
        }
        return new MujocoPhysicsBackend(context, model);
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
