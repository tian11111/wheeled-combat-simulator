using Sim.Core;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Hosting;

/// <summary>One backend selection and replay identity boundary for both hosts.</summary>
public static class MatchEngineHost
{
    public static MatchEngine Create(Scenario scenario, IVisionAdapter? visionAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        // Legacy construction never touches the native loader.
        return scenario.Physics?.Backend == PhysicsSpec.Mujoco
            ? new MatchEngine(scenario, visionAdapter, MujocoPhysicsBackendFactory.Instance)
            : new MatchEngine(scenario, visionAdapter);
    }

    public static MatchEngine CreateForReplay(ReplayFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var errors = file.Header.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid replay header: {string.Join(" ", errors)}");
        }
        var engine = Create(file.Scenario);
        try
        {
            var current = engine.BuildReplayHeader();
            var recordedBackend = file.Header.PhysicsBackend ?? PhysicsSpec.Legacy;
            var currentBackend = current.PhysicsBackend ?? PhysicsSpec.Legacy;
            if (recordedBackend != currentBackend
                || (currentBackend == PhysicsSpec.Mujoco
                    && (file.Header.PhysicsEngineVersion != current.PhysicsEngineVersion
                        || file.Header.PhysicsModelSha256 != current.PhysicsModelSha256)))
            {
                throw new InvalidOperationException(
                    $"replay physics identity mismatch: recorded {recordedBackend}/{file.Header.PhysicsEngineVersion}/{file.Header.PhysicsModelSha256}, "
                    + $"current {currentBackend}/{current.PhysicsEngineVersion}/{current.PhysicsModelSha256}.");
            }
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }
}
