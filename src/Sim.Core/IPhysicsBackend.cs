using Sim.Protocol;

namespace Sim.Core;

/// <summary>One match's physics state. Implementations update the shared runtime entities.</summary>
public interface IPhysicsBackend : IDisposable
{
    string BackendId { get; }
    string? EngineVersion { get; }
    string? ModelSha256 { get; }

    /// <summary>Advances one referee tick; returns true if the two robots touched during it.</summary>
    bool Step(double dt);
    bool OnStage(RobotRuntime robot);
    bool HangOn(RobotRuntime robot);
    bool FullOn(RobotRuntime robot);

    /// <summary>The referee has already reset the managed robot to its start pose.</summary>
    void ResetRobot(RobotRuntime robot);

    /// <summary>Optional display-only poses; legacy physics returns null.</summary>
    PhysicsPoses? BuildPhysicsPoses();
}

/// <summary>The host creates an external backend after core runtime entities exist.</summary>
public interface IPhysicsBackendFactory
{
    IPhysicsBackend Create(PhysicsBackendContext context);
}

/// <summary>Per-match references needed to build a physics backend.</summary>
public sealed record PhysicsBackendContext(
    Scenario Scenario,
    FieldModel Field,
    SimParameters Parameters,
    RobotRuntime Us,
    RobotRuntime Them,
    List<BlockRuntime> Blocks,
    EventBus Events,
    double AntiStallPhaseUs,
    double AntiStallPhaseThem);
