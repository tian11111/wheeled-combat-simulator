// One-shot external-controller probe for the desktop shell: launch the
// configured command, perform a single JSONL handshake, reap the process.
// Orchestration only — no Sim.Core, no match state, no protocol changes.

using Sim.Controller;
using Sim.Protocol;

namespace Sim.GodotShell;

public sealed record ControllerPreflightResult(bool Ok, string Message);

public static class ControllerPreflight
{
    /// <summary>
    /// Probes one controller profile with the exact bridge semantics a real
    /// match uses (same JSONL contract, same timeout, same fault categories).
    /// A healthy controller answers one valid action; anything the bridge
    /// counts as a fault (unlaunchable command, bad line, wrong request id,
    /// timeout, dead process) fails the probe. The probe process is always
    /// reaped before returning.
    /// </summary>
    public static ControllerPreflightResult Run(ControllerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsExternal || string.IsNullOrWhiteSpace(profile.Command))
        {
            return new(true, "内置 FSM 无需预检");
        }
        ExternalControllerBridge? bridge = null;
        try
        {
            bridge = ExternalControllerBridge.Start(profile.Command, profile.TimeoutMs);
            var action = bridge.Decide(new Observation { RequestId = 1 });
            if (bridge.Faults > 0)
            {
                return new(false,
                    bridge.LastFault.Length > 0 ? bridge.LastFault : "控制器未按协议应答");
            }
            if (!double.IsFinite(action.V) || !double.IsFinite(action.W))
            {
                return new(false, "控制器返回非有限动作");
            }
            return new(true, $"握手成功 v={action.V:0.###} w={action.W:0.###}");
        }
        catch (Exception error)
        {
            return new(false, error.Message);
        }
        finally
        {
            bridge?.Dispose();
        }
    }
}
