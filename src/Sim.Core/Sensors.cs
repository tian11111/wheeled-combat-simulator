using Sim.Protocol;

namespace Sim.Core;

/// <summary>A single IR probe result (nearest reflector within the beam).</summary>
public sealed record SensorProbe
{
    public required double D { get; init; }

    /// <summary>Block, opponent robot, or a string tag ("台壁"/"地面"); null = no reflector.</summary>
    public object? Obj { get; init; }

    /// <summary>Incidence-angle attenuation; null means 1 (legacy undefined).</summary>
    public double? Atten { get; init; }
}

/// <summary>
/// Per-robot dynamic sensor sampling, ported from the legacy CORE
/// (irProbeFor / edgeProbeFor / graySpotSample / hysteresis / logical aliases).
/// </summary>
public sealed class SensorSampler
{
    private static readonly string[] LogicalKeys =
    [
        "gF", "gB", "gL", "gR", "uL", "uR", "sFL", "sFR",
        "dLF", "dRF", "dLB", "dRB", "f", "r",
    ];

    private readonly FieldModel _field;
    private readonly SimParameters _params;
    private readonly RobotRuntime _us;
    private readonly RobotRuntime _them;
    private readonly List<BlockRuntime> _blocks;
    private readonly long _seed;
    private readonly Func<long> _stepIndex;
    private readonly IPhysicsBackend _physics;

    // 3D 地面类通道的高度差量程 (m, 工程默认, 非真机标定): 探点世界 Z 与承载面
    // (台面 0.06 / 走道 0)之差超过量程 → 无反射。工况矩阵:
    //   站立贴地: 灰度 diff≈0.0025 / 铲下 ≈0.0413 → 都放行;
    //   20° 台沿倒角爬坡(倒车登台, 车尾 gB 探点随姿态抬升): diff ≈
    //     sin20°×0.06 + cos20°×0.0025 ≈ 0.0229 → 灰度量程必须放行 (0.02 会
    //     拦截爬坡段的登台信号, 0.03 放行);
    //   翘头 30°(半悬前兆): 前灰度 diff ≈ 0.0025+0.06×sin30° = 0.0325 → 0.03
    //     拦截 (拦截起点 pitch ≈ 27°);
    //   铲下 forward≈-0.002 几乎无 pitch 耦合, 半悬由 XY 出台 + 翻覆由光轴
    //   朝向(DownZ)拦截, 量程 0.06 只需覆盖常态 0.0413。
    private const double GrayHeightRange = 0.03;
    private const double IrGroundHeightRange = 0.06;

    public SensorSampler(FieldModel field, SimParameters parameters, RobotRuntime us, RobotRuntime them,
        List<BlockRuntime> blocks, long seed, Func<long> stepIndex, IPhysicsBackend physics)
    {
        _field = field;
        _params = parameters;
        _us = us;
        _them = them;
        _blocks = blocks;
        _seed = seed;
        _stepIndex = stepIndex;
        _physics = physics;
    }

    private RobotRuntime Other(RobotRuntime r) => r.IsUs ? _them : _us;

    /// <summary>
    /// 探点世界坐标。通道未配置 Height 时为 (x, y, 0) —— Z 无效果的旧 2D 平面语义,
    /// 与升级前 SensorPoint 逐位一致; 配置后本地 (forward, lateral, height) 经
    /// Rz(yaw)·Ry(pitch)·Rx(roll) 完整旋转加到车体原点(与 RobotRuntime roll/pitch
    /// 的 ZYX 欧拉分解一致), 半悬/翻覆姿态下探点随姿态抬升/翻转。(曾把平面旋转
    /// 结果当车体原点再叠加一次旋转, 造成 3D 通道探点 XY 双算偏移。)
    /// internal 供测试直接断言姿态矩阵。
    /// </summary>
    internal static (double X, double Y, double Z) SensorPoint(RobotRuntime r, SensorChannel ch)
    {
        var c = Math.Cos(r.Th);
        var s = Math.Sin(r.Th);
        if (ch.Height is not { } h)
        {
            return (r.X + c * ch.Forward - s * ch.Lateral, r.Y + s * ch.Forward + c * ch.Lateral, 0);
        }
        var cr = Math.Cos(r.Roll);
        var sr = Math.Sin(r.Roll);
        var cp = Math.Cos(r.Pitch);
        var sp = Math.Sin(r.Pitch);
        var ry = cr * ch.Lateral - sr * h;
        var rz = sr * ch.Lateral + cr * h;
        var rx = cp * ch.Forward + sp * rz;
        rz = -sp * ch.Forward + cp * rz;
        return (r.X + c * rx - s * ry, r.Y + s * rx + c * ry, r.ZG + rz);
    }

    /// <summary>传感器光轴(车体系朝下 (0,0,-1))在世界系的 Z 分量: 直立 -1, 倒扣 +1。
    /// internal 供测试直接断言姿态矩阵。</summary>
    internal static double SensorDownZ(RobotRuntime r) => -Math.Cos(r.Pitch) * Math.Cos(r.Roll);

    private static double SensorAngle(RobotRuntime r, SensorChannel ch) => r.Th + ch.Angle;

    private static (double X, double Y) FrontPt(RobotRuntime r, double d)
        => (r.X + Math.Cos(r.Th) * d, r.Y + Math.Sin(r.Th) * d);

    private double SensorNoiseFor(SensorChannel ch)
        => ch.Noise is { } noise ? noise : (ch.Type == SensorType.Gray ? _params.GrayNoise : _params.IrNoise);

    private double SensorNoiseRandom(RobotRuntime r, SensorChannel ch)
        => DeterministicRandom.SensorNoiseRandom(_seed, r.IsUs, _stepIndex(), ch.Id);

    private static double ClampSensorValue(SensorChannel ch, double v)
    {
        var n = double.IsFinite(v) ? v : 0;
        var max = ch.Max != 0 ? ch.Max : (ch.Type == SensorType.Gray ? 1000 : 1.2);
        return Js.Clamp(n, ch.Min != 0 ? ch.Min : 0, max);
    }

    /// <summary>Opponent rectangle body face cosine for IR incidence attenuation.</summary>
    public static double RobotFaceCos(RobotRuntime o, double ox, double oy, double beamX, double beamY)
    {
        var c = Math.Cos(o.Th);
        var s = Math.Sin(o.Th);
        var dx = ox - o.X;
        var dy = oy - o.Y;
        var lx = dx * c + dy * s;    // 传感器在对手车身前向坐标
        var ly = -dx * s + dy * c;   // 传感器在对手车身左侧坐标
        double fnx, fny;
        if (Math.Abs(lx) >= Math.Abs(ly))
        {
            // 前/后面
            var sg = lx >= 0 ? 1 : -1;
            fnx = sg * c;
            fny = sg * s;
        }
        else
        {
            // 左/右侧面
            var sg = ly >= 0 ? 1 : -1;
            fnx = -sg * s;
            fny = sg * c;
        }
        return Math.Max(0, Math.Abs(beamX * fnx + beamY * fny));
    }

    /// <summary>IR 探测经后端接口取命中: 平面查询由 backend 退化到 PlanarSensors(逐位
    /// 兼容), 3D 查询由 MuJoCo 用 mj_ray 打真实碰撞几何。</summary>
    private SensorProbe? ProbeRayFor(RobotRuntime r, SensorChannel ch, (double X, double Y, double Z) p,
        double ang, double half, double range, bool inclEdge, bool inclFence)
        => _physics.ProbeRay(new ProbeQuery
        {
            Robot = r,
            X = p.X,
            Y = p.Y,
            Z = p.Z,
            Angle = ang,
            HalfFov = half,
            Range = range,
            IncludeEdge = inclEdge,
            IncludeFence = inclFence,
            Planar = ch.Height is null,
        });

    /// <summary>地面类探测经后端接口: SpotRadius>0 灰度采样, 否则台面反射 0/1;
    /// 3D 查询附加高度差量程与光轴朝向判定(半悬/翻覆不再凭 XY 在台内误报)。</summary>
    private double ProbeGroundFor(RobotRuntime r, SensorChannel ch, (double X, double Y, double Z) p, bool spot)
        => _physics.ProbeGround(new GroundQuery
        {
            X = p.X,
            Y = p.Y,
            Z = p.Z,
            Planar = ch.Height is null,
            SpotRadius = spot ? (_params.GraySpotRadius != 0 ? _params.GraySpotRadius : 0.025) : 0,
            MaxHeight = ch.Type == SensorType.Gray ? GrayHeightRange : IrGroundHeightRange,
            DownZ = SensorDownZ(r),
        });

    private static double IrVal(SensorProbe? pr, double range)
        => pr is null ? 0 : Js.Clamp((pr.Atten ?? 1) * (1 - pr.D / range), 0, 1);

    private SensorProbe? EdgeProbeFor(RobotRuntime r, SensorChannel ch, (double X, double Y, double Z) p)
    {
        var ang = SensorAngle(r, ch);
        var range = ch.Range != 0 ? ch.Range : 0.9;
        var half = ch.Fov != 0 ? ch.Fov : 0.30;
        // `edge` measures the raised platform wall; `fence` measures the outer perimeter.
        var fenceMode = ch.Mode == "fence";
        var target = ProbeRayFor(r, ch, p, ang, half, range,
            !fenceMode && !_field.OnPlatform(p.X, p.Y), fenceMode);
        // 台面反射候选: 平面语义看 XY 投影; 3D 语义用探点垂线的地面判定
        // (OnPlatform + 高度差量程 + 光轴朝向) —— 半悬/翻覆不再误报"铲前仍在
        // 台面"。该候选不可省: 水平光束打不到台面顶, 铲前红外(探点在车尾,
        // 语义模式 angle=0 朝车头, 设计⑦不改)在台面上依赖此反射维持高电平,
        // 砍掉会使 FSM 的 rush(前冲找墙)永不退出而冲出场外。
        double ground;
        if (!_field.OnPlatform(r.X, r.Y))
        {
            ground = 0.3;              // 走道地面有弱反射
        }
        else if (ProbeGroundFor(r, ch, p, spot: false) > 0)
        {
            ground = 1;                // 铲前仍在台面
        }
        else
        {
            ground = 0;                // 3D: 探点离面超量程/朝向不对 → 无反射
        }
        var wall = fenceMode ? null
            : (ch.Height is null ? PlanarSensors.WallProbe(_field, p.X, p.Y, ang, range) : null);
        var candidates = new List<SensorProbe?>
        {
            target,
            wall is { } wallDist ? new SensorProbe { D = wallDist, Obj = "台壁" } : null,
            ground != 0 ? new SensorProbe { D = range * (1 - ground), Obj = "地面" } : null,
        };
        SensorProbe? best = null;
        foreach (var cdt in candidates)
        {
            if (cdt is not null && (best is null || cdt.D < best.D))
            {
                best = cdt;
            }
        }
        return best;
    }

    /// <summary>施密特触发器: 数字红外二值输出, 进入/释放阈值分离。</summary>
    private double IrHysteresis(RobotRuntime r, string id, double value)
    {
        var band = _params.IrHystBand != 0 ? _params.IrHystBand : 0.10;
        var trig = _params.IrTrigger != 0 ? _params.IrTrigger : 0.35;
        var on = trig + band;
        var off = Math.Max(0, trig - band);
        if (!r.IrHyst.TryGetValue(id, out var st))
        {
            st = new HysteresisState();
            r.IrHyst[id] = st;
        }
        if (value >= on)
        {
            st.Bit = 1;
        }
        else if (value <= off)
        {
            st.Bit = 0;
        }
        return st.Bit;
    }

    /// <summary>连续红外值级迟滞(死区): 变化小于死区时保持上一值。</summary>
    private double ValueHysteresis(RobotRuntime r, string id, double value)
    {
        var band = (_params.IrHystBand != 0 ? _params.IrHystBand : 0.10) * 0.2;
        if (!r.IrHyst.TryGetValue(id, out var st))
        {
            st = new HysteresisState { Value = value };
            r.IrHyst[id] = st;
        }
        if (Math.Abs(value - st.Value) > band)
        {
            st.Value = value;
        }
        return st.Value;
    }

    private (double Value, SensorProbe? Probe) SampleSensorChannelFor(RobotRuntime r, SensorChannel ch)
    {
        var p = SensorPoint(r, ch);
        var ang = SensorAngle(r, ch);
        var range = ch.Range != 0 ? ch.Range : 0.9;
        var half = ch.Fov != 0 ? ch.Fov : 0.35;
        var value = 0.0;
        SensorProbe? probe = null;
        if (ch.Type == SensorType.Gray)
        {
            value = ProbeGroundFor(r, ch, p, spot: true);
        }
        else if (ch.Type == SensorType.IrGround)
        {
            // 台面/地面反射=1, 悬空/离面超量程/光轴朝向不对=0
            value = ProbeGroundFor(r, ch, p, spot: false);
            if (ch.Mode == "target")
            {
                probe = ProbeRayFor(r, ch, p, ang, half, range, false, false);
                value = Math.Max(value, IrVal(probe, range));
            }
        }
        else if (ch.Type == SensorType.IrEdge)
        {
            probe = EdgeProbeFor(r, ch, p);
            value = IrVal(probe, range);
            if (_field.OnPlatform(r.X, r.Y))
            {
                var sp = FrontPt(r, 0.35);
                value = Math.Max(value, _field.OnPlatform(sp.X, sp.Y) ? 1 : 0.12);
            }
        }
        else if (ch.Type is SensorType.Digital or SensorType.IrDistance)
        {
            var inclEdge = ch.Mode is "edge_target" or "edge";
            var inclFence = ch.Mode == "fence";
            probe = ProbeRayFor(r, ch, p, ang, half, range, inclEdge, inclFence);
            value = IrVal(probe, range);
            if (ch.Mode == "edge_target" && _field.OnPlatform(r.X, r.Y))
            {
                var sp = FrontPt(r, 0.25);
                value = Math.Max(value, _field.OnPlatform(sp.X, sp.Y) ? 1 : 0.12);
            }
        }
        value += (SensorNoiseRandom(r, ch) * 2 - 1) * SensorNoiseFor(ch);
        // 非理想特性滤波: 数字红外施密特二值 / 连续红外值级迟滞。
        if (ch.Type == SensorType.Digital)
        {
            value = IrHysteresis(r, ch.Id, value);
        }
        else if (ch.Type is SensorType.IrDistance or SensorType.IrGround or SensorType.IrEdge)
        {
            value = ValueHysteresis(r, ch.Id, value);
        }
        return (ClampSensorValue(ch, value), probe);
    }

    private sealed record LogicalSpec(string[] Ids, string Reducer, bool Virtual);

    private static LogicalSpec GetLogicalSpec(SensorProfile profile, string key)
    {
        if (profile.Logical is null || !profile.Logical.TryGetValue(key, out var raw) || raw is null || raw.IsNull)
        {
            return new LogicalSpec([], "none", false);
        }
        if (raw.Channel is not null)
        {
            return new LogicalSpec([raw.Channel], "first", false);
        }
        if (raw.Channels is { Count: > 0 } channels)
        {
            return new LogicalSpec(channels.ToArray(), string.IsNullOrEmpty(raw.Reducer) ? "first" : raw.Reducer!, raw.Virtual);
        }
        return new LogicalSpec([], string.IsNullOrEmpty(raw.Reducer) ? "first" : raw.Reducer!, raw.Virtual);
    }

    private static double LogicalValueFor(SensorProfile profile, string key, Dictionary<string, double> raw)
    {
        var spec = GetLogicalSpec(profile, key);
        var values = spec.Ids.Where(id => raw.TryGetValue(id, out var v) && double.IsFinite(v)).Select(id => raw[id]).ToList();
        if (values.Count == 0)
        {
            return 0;
        }
        return spec.Reducer switch
        {
            "min" => values.Min(),
            "mean" or "avg" => values.Sum() / values.Count,
            "sum" => values.Sum(),
            "max" => values.Max(),
            _ => values[0],
        };
    }

    private static SensorProbe? LogicalProbeFor(SensorProfile profile, string key, Dictionary<string, SensorProbe> probes)
    {
        var spec = GetLogicalSpec(profile, key);
        SensorProbe? best = null;
        foreach (var id in spec.Ids)
        {
            // 3D 化后无命中通道的 probe 为 null (probes 字典里存 null), 必须跳过;
            // best 为 null 时的短路曾掩盖这一点。
            if (probes.TryGetValue(id, out var p) && p is not null && (best is null || p.D < best.D))
            {
                best = p;
            }
        }
        return best;
    }

    /// <summary>Samples all real channels and refreshes the legacy logical aliases.</summary>
    public void SampleSensorsFor(RobotRuntime r)
    {
        var profile = r.Vehicle.Sensors ?? SensorProfiles.Legacy14;
        var raw = new Dictionary<string, double>();
        var probes = new Dictionary<string, SensorProbe>();
        foreach (var ch in profile.Channels)
        {
            var (value, probe) = SampleSensorChannelFor(r, ch);
            raw[ch.Id] = value;
            probes[ch.Id] = probe!;
        }

        // 兼容逻辑别名
        var compat = new Dictionary<string, double>();
        foreach (var key in LogicalKeys)
        {
            compat[key] = LogicalValueFor(profile, key, raw);
        }
        // 没有正前独立红外时, 用两路前对角的最大值作为"前方有目标"提示。
        if (GetLogicalSpec(profile, "f").Ids.Length == 0)
        {
            compat["f"] = Math.Max(compat.GetValueOrDefault("dLF"), compat.GetValueOrDefault("dRF"));
        }

        r.Sens = compat;
        // Probes keyed by channel id plus legacy logical names (FSM uses the logical names).
        var logicalProbes = new Dictionary<string, SensorProbe>(probes);
        foreach (var key in LogicalKeys)
        {
            var p = LogicalProbeFor(profile, key, probes);
            if (p is not null)
            {
                logicalProbes[key] = p;
            }
        }
        r.Probe = logicalProbes;
        r.RawSens = raw;
    }
}
