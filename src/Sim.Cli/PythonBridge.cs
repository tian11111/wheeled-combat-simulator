using Sim.Core;
using Sim.Protocol;
using Sim.Controller;

namespace Sim.Cli;

/// <summary>
/// Backward-compatible CLI name for the shared external controller bridge.
/// The implementation lives in Sim.Controller so Godot and CLI use the same
/// JSONL, timeout and process-lifecycle behavior.
/// </summary>
public sealed class PythonBridge : IControllerAdapter, IDisposable
{
    private readonly ExternalControllerBridge _bridge;

    private PythonBridge(ExternalControllerBridge bridge) => _bridge = bridge;

    /// <summary>Fault count: timeouts, dead-pipe writes and unmatched deadlines.</summary>
    public long Faults => _bridge.Faults;

    /// <summary>Launches <paramref name="command"/> (executable plus optional arguments).</summary>
    public static PythonBridge Start(string command, double timeoutMs)
        => new(ExternalControllerBridge.Start(command, timeoutMs));

    public RobotAction Decide(Observation observation)
        => _bridge.Decide(observation);

    public void Dispose()
        => _bridge.Dispose();
}
