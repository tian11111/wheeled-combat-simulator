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
