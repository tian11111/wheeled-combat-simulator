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

    public SensorSampler(FieldModel field, SimParameters parameters, RobotRuntime us, RobotRuntime them,
        List<BlockRuntime> blocks, long seed, Func<long> stepIndex)
    {
        _field = field;
        _params = parameters;
        _us = us;
        _them = them;
        _blocks = blocks;
        _seed = seed;
        _stepIndex = stepIndex;
    }

    private RobotRuntime Other(RobotRuntime r) => r.IsUs ? _them : _us;

    private static (double X, double Y) SensorPoint(RobotRuntime r, SensorChannel ch)
    {
        var c = Math.Cos(r.Th);
        var s = Math.Sin(r.Th);
        return (r.X + c * ch.Forward - s * ch.Lateral, r.Y + s * ch.Forward + c * ch.Lateral);
    }

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

    private SensorProbe? IrProbeFor(RobotRuntime r, double ox, double oy, double ang, double half, double range, bool inclEdge, bool inclFence)
    {
        SensorProbe? best = null;
        var targets = new List<object>(_blocks.Count + 1);
        targets.AddRange(_blocks);
        targets.Add(Other(r));
        var beamX = Math.Cos(ang);
        var beamY = Math.Sin(ang);
        foreach (var o in targets)
        {
            double oxo, oyo, oro;
            if (o is BlockRuntime b)
            {
                oxo = b.X; oyo = b.Y; oro = b.R;
            }
            else
            {
                var rb = (RobotRuntime)o;
                oxo = rb.X; oyo = rb.Y; oro = rb.R;
            }
            var dx = oxo - ox;
            var dy = oyo - oy;
            var d = Js.Hypot(dx, dy);
            if (d > range + oro)
            {
                continue;
            }
            var a = Js.Norm(Math.Atan2(dy, dx) - ang);
            if (Math.Abs(a) > half + Math.Asin(Math.Min(1, oro / Math.Max(0.05, d))))
            {
                continue;
            }
            var dd = d - oro;
            // 入射角余弦衰减: 能量块最近点法线沿径向 → cosθ≈1; 对手按矩形车身取面法线。
            double atten = 1;
            if (o is RobotRuntime)
            {
                atten = RobotFaceCos((RobotRuntime)o, ox, oy, beamX, beamY);
            }
            if (best is null || dd < best.D)
            {
                best = new SensorProbe { D = dd, Obj = o, Atten = atten };
            }
        }
        if (inclEdge && !_field.OnPlatform(ox, oy))
        {
            // 从台下探测台沿
            for (var s = 0.05; s <= range; s += 0.05)
            {
                var px = ox + Math.Cos(ang) * s;
                var py = oy + Math.Sin(ang) * s;
                if (_field.OnPlatform(px, py))
                {
                    if (best is null || s < best.D)
                    {
                        best = new SensorProbe { D = s, Obj = null, Atten = 1 };
                    }
                    break;
                }
            }
        }
        if (inclFence)
        {
            // 围栏(后向)
            var s = FenceDist(ox, oy, ang, range);
            if (s is { } fence && (best is null || fence < best.D))
            {
                best = new SensorProbe { D = fence, Obj = null, Atten = 1 };
            }
        }
        return best;
    }

    private double? FenceDist(double ox, double oy, double ang, double range)
    {
        var hi = _field.Field.FieldSize - 0.05;
        for (var s = 0.05; s <= range; s += 0.05)
        {
            var px = ox + Math.Cos(ang) * s;
            var py = oy + Math.Sin(ang) * s;
            if (px < 0.05 || px > hi || py < 0.05 || py > hi)
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>台壁反射: 走道上铲前红外对白台壁的反射距离。</summary>
    private double? WallProbe(double px, double py, double ang, double range)
    {
        if (_field.OnPlatform(px, py))
        {
            return null; // 起点已在台上 → 无台壁可反射
        }
        for (var s = 0.06; s <= range; s += 0.06)
        {
            if (_field.OnPlatform(px + Math.Cos(ang) * s, py + Math.Sin(ang) * s))
            {
                return s;
            }
        }
        return null;
    }

    private static double IrVal(SensorProbe? pr, double range)
        => pr is null ? 0 : Js.Clamp((pr.Atten ?? 1) * (1 - pr.D / range), 0, 1);

    private SensorProbe? EdgeProbeFor(RobotRuntime r, SensorChannel ch, (double X, double Y) p)
    {
        var ang = SensorAngle(r, ch);
        var range = ch.Range != 0 ? ch.Range : 0.9;
        var half = ch.Fov != 0 ? ch.Fov : 0.30;
        // `edge` measures the raised platform wall; `fence` measures the outer perimeter.
        var fenceMode = ch.Mode == "fence";
        var target = IrProbeFor(r, p.X, p.Y, ang, half, range,
            !fenceMode && !_field.OnPlatform(p.X, p.Y), fenceMode);
        var wall = fenceMode ? null : WallProbe(p.X, p.Y, ang, range);
        var ground = 0.0;
        if (!_field.OnPlatform(r.X, r.Y))
        {
            ground = 0.3;              // 走道地面有弱反射
        }
        else if (_field.OnPlatform(p.X, p.Y))
        {
            ground = 1;                // 铲前仍在台面
        }
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

    /// <summary>灰度近地圆形光斑加权采样 (中心 + 四个方向边缘点)。</summary>
    private double GraySpotSample(double x, double y)
    {
        var radius = _params.GraySpotRadius != 0 ? _params.GraySpotRadius : 0.025;
        double sum = 0;
        sum += _field.FieldGray(x, y);
        sum += _field.FieldGray(x + radius, y);
        sum += _field.FieldGray(x - radius, y);
        sum += _field.FieldGray(x, y + radius);
        sum += _field.FieldGray(x, y - radius);
        return sum / 5;
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
            value = GraySpotSample(p.X, p.Y);
        }
        else if (ch.Type == SensorType.IrGround)
        {
            // 台面/地面反射=1, 悬空=0
            value = _field.OnPlatform(p.X, p.Y) ? 1 : 0;
            if (ch.Mode == "target")
            {
                probe = IrProbeFor(r, p.X, p.Y, ang, half, range, false, false);
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
            probe = IrProbeFor(r, p.X, p.Y, ang, half, range, inclEdge, inclFence);
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
            if (probes.TryGetValue(id, out var p) && (best is null || p.D < best.D))
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
