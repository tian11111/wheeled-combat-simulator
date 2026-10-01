// One-shot external-controller probe for the desktop shell: launch the
// configured command, perform a single JSONL handshake, reap the process.
// Orchestration only — no Sim.Core, no match state, no protocol changes.

using Sim.Controller;
using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>预检失败类别（装配决策据此区分"确定性坏命令"与"可能只是慢"）。</summary>
public static class ControllerPreflightKinds
{
    /// <summary>命令起不来/进程当场退出/管道不可用 —— 确定性失败，应用时应回退。</summary>
    public const string Launch = "launch";

    /// <summary>进程活着但协议应答非法（坏行/错 requestId/非有限动作）—— 确定性失败。</summary>
    public const string Protocol = "protocol";

    /// <summary>TimeoutMs 内无合法应答 —— 可能只是首帧模型加载慢（RL 桥先加载后服务），保留并告警。</summary>
    public const string Timeout = "timeout";
}

public sealed record ControllerPreflightResult(bool Ok, string Message)
{
    /// <summary>失败类别（见 <see cref="ControllerPreflightKinds"/>）；通过时为空串。</summary>
    public string FailureKind { get; init; } = "";
}

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
                // 被丢弃的应答(非法动作行/答了别的 requestId)优先: 它比随后的应答超时
                // 更能说明控制器坏了。
                if (bridge.LastProtocolFault.Length > 0)
                {
                    return new(false, bridge.LastProtocolFault) { FailureKind = ControllerPreflightKinds.Protocol };
                }
                // 进程已退出(开始写之后才死)也算启动类确定性失败, 不能落进"加载慢"的超时桶。
                if (!bridge.IsRunning)
                {
                    var dead = bridge.LastFault.Length > 0 ? bridge.LastFault : "控制器进程已退出";
                    return new(false, dead) { FailureKind = ControllerPreflightKinds.Launch };
                }
                var message = bridge.LastFault.Length > 0 ? bridge.LastFault : "控制器未按协议应答";
                return new(false, message) { FailureKind = ClassifyFault(bridge.LastFault) };
            }
            if (!double.IsFinite(action.V) || !double.IsFinite(action.W))
            {
                return new(false, "控制器返回非有限动作") { FailureKind = ControllerPreflightKinds.Protocol };
            }
            return new(true, $"握手成功 v={action.V:0.###} w={action.W:0.###}");
        }
        catch (Exception error)
        {
            // 启动失败（坏命令行/文件不存在/无权限）是确定性失败。
            return new(false, error.Message) { FailureKind = ControllerPreflightKinds.Launch };
        }
        finally
        {
            bridge?.Dispose();
        }
    }

    /// <summary>
    /// 桥的 fault 文案 → 失败类别（纯函数，供装配决策与单测；文案前缀由
    /// <see cref="ExternalControllerBridge"/> 固定产出）。
    /// </summary>
    public static string ClassifyFault(string? lastFault)
    {
        var fault = lastFault ?? "";
        if (fault.StartsWith("controller response timeout", StringComparison.Ordinal))
        {
            return ControllerPreflightKinds.Timeout;
        }
        if (fault.StartsWith("controller process is not running", StringComparison.Ordinal)
            || fault.StartsWith("controller stdin failed", StringComparison.Ordinal)
            || fault.StartsWith("controller stdout failed", StringComparison.Ordinal))
        {
            return ControllerPreflightKinds.Launch;
        }
        return ControllerPreflightKinds.Protocol;
    }
}
