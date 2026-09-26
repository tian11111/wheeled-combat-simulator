using Sim.Core;
using Sim.Protocol;

namespace Sim.Mujoco;

/// <summary>Host injection point for explicitly selected MuJoCo scenarios.</summary>
public sealed class MujocoPhysicsBackendFactory : IPhysicsBackendFactory
{
    public static MujocoPhysicsBackendFactory Instance { get; } = new();

    public IPhysicsBackend Create(PhysicsBackendContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Scenario.Physics?.Backend != PhysicsSpec.Mujoco)
        {
            throw new ArgumentException("The MuJoCo factory requires physics.backend='mujoco'.", nameof(context));
        }
        return new MujocoPhysicsBackend(context);
    }
}
