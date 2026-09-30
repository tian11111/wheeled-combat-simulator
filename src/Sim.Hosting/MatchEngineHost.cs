using Sim.Core;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Hosting;

/// <summary>One backend selection and replay identity boundary for both hosts.</summary>
public static class MatchEngineHost
{
    /// <summary>
    /// Refusal for live-bridge sessions: their frame arrival depends on external
    /// timing, so an action-stream replay cannot reproduce them. The message must
    /// point at the way out (the sidecar evidence package), never just say "no".
    /// </summary>
    public const string LiveBridgeReplayRefusal =
        "vision mode 'liveBridge' 不能作为普通 replay 录制/复现: live 桥的帧到达依赖外部时序, 动作流回放无法复现; "
        + "请改用 live 场次写出的 sidecar 证据包做确定性复现 (vision evaluate --evidence <sidecar 目录>)。";

    /// <summary>
    /// Recording/reproduction gate shared by the recording paths and
    /// <see cref="CreateForReplay"/>: a live-bridge header is refused before any
    /// file is written or replayed.
    /// </summary>
    public static void EnsureRecordable(ReplayHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.VisionMode == LiveVisionBridge.ModeName)
        {
            throw new InvalidOperationException(LiveBridgeReplayRefusal);
        }
    }

    public static MatchEngine Create(Scenario scenario, IVisionAdapter? visionAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        // Legacy construction never touches the native loader.
        return scenario.Physics?.Backend == PhysicsSpec.Mujoco
            ? new MatchEngine(scenario, visionAdapter, MujocoPhysicsBackendFactory.Instance)
            : new MatchEngine(scenario, visionAdapter);
    }

    /// <summary>训练/评测专用: 显式注入 physics factory(如 mjModel 会话级复用)。
    /// 普通比赛入口 Create(scenario, visionAdapter) 语义不变。</summary>
    public static MatchEngine Create(Scenario scenario, IVisionAdapter? visionAdapter,
        IPhysicsBackendFactory factory)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(factory);
        return new MatchEngine(scenario, visionAdapter, factory);
    }

    public static MatchEngine CreateForReplay(ReplayFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var errors = file.Header.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid replay header: {string.Join(" ", errors)}");
        }
        // 决策⑥ 门禁: live 桥场次不可按动作流复现(物理身份比较之前先明确拒绝并指路 sidecar)。
        EnsureRecordable(file.Header);
        var engine = Create(file.Scenario);
        try
        {
            var current = engine.BuildReplayHeader();
            var recordedBackend = file.Header.PhysicsBackend ?? PhysicsSpec.Legacy;
            var currentBackend = current.PhysicsBackend ?? PhysicsSpec.Legacy;
            // 09-25 SEARCH 索敌闭环: 控制映射变更以 CoreVersion 标识, MuJoCo 回放
            // 额外比较该字段(纯 C# 变更不进 MJCF 哈希); legacy 回放不加此门禁。
            if (recordedBackend != currentBackend
                || (currentBackend == PhysicsSpec.Mujoco
                    && (file.Header.PhysicsEngineVersion != current.PhysicsEngineVersion
                        || file.Header.PhysicsModelSha256 != current.PhysicsModelSha256
                        || file.Header.CoreVersion != current.CoreVersion)))
            {
                throw new InvalidOperationException(
                    $"replay physics identity mismatch: recorded {recordedBackend}/{file.Header.PhysicsEngineVersion}/{file.Header.PhysicsModelSha256}/{file.Header.CoreVersion}, "
                    + $"current {currentBackend}/{current.PhysicsEngineVersion}/{current.PhysicsModelSha256}/{current.CoreVersion}.");
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
