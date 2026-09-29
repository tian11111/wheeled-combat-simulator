namespace Sim.Core;

/// <summary>
/// 平面投影传感器语义 (legacy CORE 移植) 的单一实现。legacy 二维物理后端直接
/// 使用; MuJoCo 后端对未配置 <see cref="Sim.Protocol.SensorChannel.Height"/> 的
/// 通道同样退化到本实现 (逐位兼容, 与 <c>IPhysicsBackend.IsFlipped => false</c>
/// 同一模式)。全部函数是 (x, y, th) 平面几何 + 场模型, 与三维姿态无关。
/// </summary>
public static class PlanarSensors
{
    /// <summary>
    /// 平面 IR 探测: 能量块 + 对手车圆模型 + 台沿 + 围栏的最近命中。
    /// 与 SensorSampler 旧 irProbeFor 逐位等价。
    /// </summary>
    public static SensorProbe? IrProbe(FieldModel field, RobotRuntime self, RobotRuntime other,
        List<BlockRuntime> blocks, double ox, double oy, double ang, double half, double range,
        bool includeEdge, bool includeFence)
    {
        SensorProbe? best = null;
        var targets = new List<object>(blocks.Count + 1);
        targets.AddRange(blocks);
        targets.Add(other);
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
                atten = SensorSampler.RobotFaceCos((RobotRuntime)o, ox, oy, beamX, beamY);
            }
            if (best is null || dd < best.D)
            {
                best = new SensorProbe { D = dd, Obj = o, Atten = atten };
            }
        }
        if (includeEdge && !field.OnPlatform(ox, oy))
        {
            // 从台下探测台沿
            for (var s = 0.05; s <= range; s += 0.05)
            {
                var px = ox + Math.Cos(ang) * s;
                var py = oy + Math.Sin(ang) * s;
                if (field.OnPlatform(px, py))
                {
                    if (best is null || s < best.D)
                    {
                        best = new SensorProbe { D = s, Obj = null, Atten = 1 };
                    }
                    break;
                }
            }
        }
        if (includeFence)
        {
            // 围栏(后向)
            var s = FenceDist(field, ox, oy, ang, range);
            if (s is { } fence && (best is null || fence < best.D))
            {
                best = new SensorProbe { D = fence, Obj = null, Atten = 1 };
            }
        }
        return best;
    }

    /// <summary>场局部轴对齐围栏方框的射线距离 (平面语义)。</summary>
    public static double? FenceDist(FieldModel field, double ox, double oy, double ang, double range)
    {
        var t = field.Transform;
        var (x, y) = t.WorldToLocalPoint(ox, oy);
        var lang = t.WorldToLocalHeading(ang);
        var hi = field.Field.FieldSize - 0.05;
        for (var s = 0.05; s <= range; s += 0.05)
        {
            var px = x + Math.Cos(lang) * s;
            var py = y + Math.Sin(lang) * s;
            if (px < 0.05 || px > hi || py < 0.05 || py > hi)
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>台壁反射: 走道上铲前红外对白台壁的反射距离 (平面语义)。</summary>
    public static double? WallProbe(FieldModel field, double px, double py, double ang, double range)
    {
        if (field.OnPlatform(px, py))
        {
            return null; // 起点已在台上 → 无台壁可反射
        }
        for (var s = 0.06; s <= range; s += 0.06)
        {
            if (field.OnPlatform(px + Math.Cos(ang) * s, py + Math.Sin(ang) * s))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>灰度近地圆形光斑加权采样 (中心 + 四个方向边缘点)。</summary>
    public static double GraySpot(FieldModel field, double x, double y, double spotRadius)
    {
        double sum = 0;
        sum += field.FieldGray(x, y);
        sum += field.FieldGray(x + spotRadius, y);
        sum += field.FieldGray(x - spotRadius, y);
        sum += field.FieldGray(x, y + spotRadius);
        sum += field.FieldGray(x, y - spotRadius);
        return sum / 5;
    }
}
