using Sim.Core;
using Sim.Protocol;

namespace Sim.Mujoco;

/// <summary>Host injection point for explicitly selected MuJoCo scenarios.</summary>
public sealed class MujocoPhysicsBackendFactory : IPhysicsBackendFactory
{
    private readonly double _inPlaceTurnCompensation;

    /// <summary>
    /// 可选注入原地转向补偿系数; null 用生产默认值
    /// (MujocoPhysicsBackend.DefaultInPlaceTurnCompensation)。候选对照测试各自
    /// 持有 factory 注入自己的取值, 不再改进程级静态状态。
    /// </summary>
    public MujocoPhysicsBackendFactory(double? inPlaceTurnCompensation = null)
    {
        _inPlaceTurnCompensation = inPlaceTurnCompensation
            ?? MujocoPhysicsBackend.DefaultInPlaceTurnCompensation;
    }

    public static MujocoPhysicsBackendFactory Instance { get; } = new();

    public IPhysicsBackend Create(PhysicsBackendContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Scenario.Physics?.Backend != PhysicsSpec.Mujoco)
        {
            throw new ArgumentException("The MuJoCo factory requires physics.backend='mujoco'.", nameof(context));
        }
        return new MujocoPhysicsBackend(context, _inPlaceTurnCompensation);
    }
}
