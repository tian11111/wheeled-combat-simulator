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

    /// <summary>
    /// 车体是否倾覆(失去行动能力, 例如底朝天)。MuJoCo 由三维姿态判定;
    /// legacy 没有 roll/pitch 自由度, 恒 false —— 该路径的行为必须逐位不变。
    /// </summary>
    bool IsFlipped(RobotRuntime robot);

    /// <summary>
    /// IR 探测: 从探点世界坐标沿光束方向的最近命中; 无命中返回 null。
    /// <paramref name="query"/>.Planar=true 的查询走平面投影语义(逐位兼容);
    /// 3D 查询由 MuJoCo 用 mj_ray 打真实碰撞几何(传感器所见 = 物理所碰),
    /// legacy 后端对 3D 查询同样退化为平面语义(legacy 没有 Z 自由度)。
    /// </summary>
    SensorProbe? ProbeRay(ProbeQuery query);

    /// <summary>
    /// 地面类探测(灰度/铲下 IR): 探点下方承载面的反射值。SpotRadius>0 时返回
    /// 颜色场灰度(0..1000), 否则返回台面反射 0/1。3D 查询附加高度差量程判定
    /// (探点高于承载面超过 MaxHeight 或低于它 → 无反射); 平面查询与 legacy
    /// 一样只看 XY。
    /// </summary>
    double ProbeGround(GroundQuery query);

    /// <summary>The referee has already reset the managed robot to its start pose.</summary>
    void ResetRobot(RobotRuntime robot);

    /// <summary>Optional display-only poses; legacy physics returns null.</summary>
    PhysicsPoses? BuildPhysicsPoses();
}

/// <summary>IR 探测查询(世界系)。发射车的几何/集合由 backend 自身持有。</summary>
public sealed record ProbeQuery
{
    /// <summary>发射者(命中目标 = 能量块 + 对手车, 自身几何被排除)。</summary>
    public required RobotRuntime Robot { get; init; }

    /// <summary>探点世界坐标。平面语义下 Z 无效果(传 0 即可)。</summary>
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Z { get; init; }

    /// <summary>光束水平方位角(世界系, rad)。平面实现直接消费该值以保持逐位兼容。</summary>
    public required double Angle { get; init; }

    /// <summary>光束俯仰角(rad, 上正)。当前通道恒 0; 3D 实现构造方向向量用。</summary>
    public double Elevation { get; init; }

    /// <summary>半视场角(rad): 目标中心偏离光束轴超过该值不算命中(静态面不受限)。</summary>
    public required double HalfFov { get; init; }

    /// <summary>最大量程(m)。</summary>
    public required double Range { get; init; }

    /// <summary>含台沿候选(从台下探测台沿, 平面语义)。</summary>
    public bool IncludeEdge { get; init; }

    /// <summary>含围栏候选(平面语义)。</summary>
    public bool IncludeFence { get; init; }

    /// <summary>true = 通道未配置 Height, 走旧 2D 平面投影语义(逐位兼容)。</summary>
    public required bool Planar { get; init; }
}

/// <summary>地面类探测查询(世界系)。</summary>
public sealed record GroundQuery
{
    /// <summary>探点世界坐标。</summary>
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Z { get; init; }

    /// <summary>true = 平面语义(只看 XY); false = 附加高度差量程判定。</summary>
    public required bool Planar { get; init; }

    /// <summary>颜色场光斑采样半径(m); >0 返回灰度值, 0 返回台面反射 0/1。</summary>
    public double SpotRadius { get; init; }

    /// <summary>高度差量程(m): 探点世界 Z 与承载面高度之差超过该值 → 无反射。</summary>
    public double MaxHeight { get; init; }

    /// <summary>传感器光轴在世界的 Z 分量(下正为负): 姿态翻转后朝上即读不到地面。</summary>
    public double DownZ { get; init; } = -1;
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
