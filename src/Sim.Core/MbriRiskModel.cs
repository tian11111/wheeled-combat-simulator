namespace Sim.Core;

/// <summary>四路灰度采样（按真车语义名 front/rear/left/right；单位=ADC 或仿真灰度视层而定）。</summary>
public readonly record struct MbriGraySample(double Front, double Rear, double Left, double Right)
{
    /// <summary>按 gray.py NAMES 顺序取通道值。</summary>
    public double Get(string name) => name switch
    {
        "front" => Front,
        "rear" => Rear,
        "left" => Left,
        "right" => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(name), $"unknown gray channel '{name}'."),
    };
}

/// <summary>单次 GrayRiskModel.update 的观测输出（gray.py update 返回 dict 的 C# 投影）。</summary>
public sealed record MbriGrayObservation
{
    public bool Ready { get; init; }

    public bool Valid { get; init; }

    /// <summary>清洗后的原始值（坏值已按 0 处理）。</summary>
    public MbriGraySample Raw { get; init; }

    public MbriGraySample Filtered { get; init; }

    /// <summary>逐通道 zone：(filtered−edge)/(center−edge)，越小越靠近暗外圈。</summary>
    public MbriGraySample Zone { get; init; }

    /// <summary>逐通道 white：(filtered−center)/(white−center)。</summary>
    public MbriGraySample White { get; init; }

    /// <summary>四路 zone 的中位数（偶数个取中间两值均值，同 Python statistics.median）。</summary>
    public double ZoneScore { get; init; }

    /// <summary>zone_score &lt; near_edge_enter（config 0.35）。</summary>
    public bool NearEdge { get; init; }

    /// <summary>zone_score &gt; near_edge_clear（config 0.65）。</summary>
    public bool NearClear { get; init; }

    /// <summary>压到白边的通道名（filtered ≥ white_enter，按 NAMES 序）。</summary>
    public string[] WhiteHits { get; init; } = [];

    /// <summary>四路 filtered 均 &lt; white_clear。</summary>
    public bool WhiteClear { get; init; }
}

/// <summary>
/// 真车 GrayRiskModel 逐行移植（gray.py:82-172，纯函数状态机，无 IO/时钟/随机）：
/// 三点中值滤波 + zone/white 双层风险 + near_edge 滞回阈值。
/// 参数取 config.py:43-76（新车 2026-08-14 重采；ring_patrol.py:15-25 注入 cfg.*），
/// 不用 gray.py:20-29 的独立默认值。
/// </summary>
public sealed class MbriRiskModel
{
    /// <summary>通道名与 gray.py GrayRiskModel.NAMES 一致（顺序影响 white_hits/主风险判定）。</summary>
    public static readonly string[] Names = MbriGrayCalibration.Names;

    private readonly int _window;
    private readonly IReadOnlyDictionary<string, double> _edgeReference;
    private readonly IReadOnlyDictionary<string, double> _centerReference;
    private readonly IReadOnlyDictionary<string, double> _whiteReference;
    private readonly IReadOnlyDictionary<string, double> _whiteEnter;
    private readonly IReadOnlyDictionary<string, double> _whiteClear;
    private readonly double _nearEdgeEnter;
    private readonly double _nearEdgeClear;
    private readonly double _adcMax;
    private readonly Queue<double>[] _samples;

    public MbriRiskModel(
        int window,
        IReadOnlyDictionary<string, double>? edgeReference = null,
        IReadOnlyDictionary<string, double>? centerReference = null,
        IReadOnlyDictionary<string, double>? whiteReference = null,
        IReadOnlyDictionary<string, double>? whiteEnter = null,
        IReadOnlyDictionary<string, double>? whiteClear = null,
        double nearEdgeEnter = 0.35,
        double nearEdgeClear = 0.65,
        double adcMax = 10000.0)
    {
        if (window < 1 || window % 2 == 0)
        {
            throw new ArgumentException("滤波窗口必须为正奇数", nameof(window));
        }
        _window = window;
        _edgeReference = edgeReference ?? MbriGrayCalibration.EdgeReference;
        _centerReference = centerReference ?? MbriGrayCalibration.CenterReference;
        _whiteReference = whiteReference ?? MbriGrayCalibration.WhiteReference;
        _whiteEnter = whiteEnter ?? DefaultWhiteEnter;
        _whiteClear = whiteClear ?? DefaultWhiteClear;
        _nearEdgeEnter = nearEdgeEnter;
        _nearEdgeClear = nearEdgeClear;
        _adcMax = adcMax;
        _samples = new Queue<double>[Names.Length];
        for (var i = 0; i < Names.Length; i++)
        {
            _samples[i] = new Queue<double>(window);
        }
    }

    /// <summary>config.py GRAY_WHITE_ENTER（2026-08-14 重采）。</summary>
    public static readonly IReadOnlyDictionary<string, double> DefaultWhiteEnter =
        new Dictionary<string, double> { ["front"] = 1560.0, ["rear"] = 1946.0, ["left"] = 1790.0, ["right"] = 1720.0 };

    /// <summary>config.py GRAY_WHITE_CLEAR（2026-08-14 重采）。</summary>
    public static readonly IReadOnlyDictionary<string, double> DefaultWhiteClear =
        new Dictionary<string, double> { ["front"] = 1479.0, ["rear"] = 1835.0, ["left"] = 1718.0, ["right"] = 1645.0 };

    /// <summary>config.py GRAY_NEAR_EDGE_ENTER/CLEAR（新车 2026-08-14 调参）。</summary>
    public const double NearEdgeEnter = 0.35;
    public const double NearEdgeClear = 0.65;

    /// <summary>config.py GRAY_ADC_MAX / gray.py ADC_MAX。</summary>
    public const double AdcMax = 10000.0;

    /// <summary>滤波窗口（config.py GRAY_FILTER_WINDOW=3）。</summary>
    public const int FilterWindow = 3;

    /// <summary>窗口已满（= ready）；reset 语义：重建实例或 Clear()。</summary>
    public bool Ready
    {
        get
        {
            foreach (var q in _samples)
            {
                if (q.Count != _window)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>清空滤波窗口（真车 rearm/角色重启语义，design.md 第 4 节）。</summary>
    public void Clear()
    {
        foreach (var q in _samples)
        {
            q.Clear();
        }
    }

    /// <summary>
    /// gray.py _clean 逐行移植：非有限值或越出 [0, adc_max] → 0.0 且 valid=false。
    /// </summary>
    private static (MbriGraySample Cleaned, bool Valid) Clean(MbriGraySample raw, double adcMax)
    {
        static double CleanOne(double v, double adcMax, ref bool valid)
        {
            if (!double.IsFinite(v) || v < 0.0 || v > adcMax)
            {
                valid = false;
                return 0.0;
            }
            return v;
        }

        var valid = true;
        var front = CleanOne(raw.Front, adcMax, ref valid);
        var rear = CleanOne(raw.Rear, adcMax, ref valid);
        var left = CleanOne(raw.Left, adcMax, ref valid);
        var right = CleanOne(raw.Right, adcMax, ref valid);
        return (new MbriGraySample(front, rear, left, right), valid);
    }

    /// <summary>
    /// Python statistics.median：奇数取中位，偶数取中间两值均值。
    /// 纯函数——Python 版不修改输入序列，这里必须 Clone 后排序，
    /// 否则 zone 数组会被就地排序、逐通道 Zone 输出失真（迁移矩阵单测钉住此语义）。
    /// </summary>
    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("median requires at least one value.");
        }
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>gray.py update 逐行移植（时间由调用方按 tick 驱动，模型自身无时钟）。</summary>
    public MbriGrayObservation Update(MbriGraySample raw)
    {
        var (cleaned, valid) = Clean(raw, _adcMax);
        var filtered = new double[Names.Length];
        for (var i = 0; i < Names.Length; i++)
        {
            var q = _samples[i];
            q.Enqueue(cleaned.Get(Names[i]));
            if (q.Count > _window)
            {
                q.Dequeue();
            }
            filtered[i] = Median(q.ToArray());
        }

        var zone = new double[Names.Length];
        var white = new double[Names.Length];
        for (var i = 0; i < Names.Length; i++)
        {
            var name = Names[i];
            zone[i] = (filtered[i] - _edgeReference[name]) / (_centerReference[name] - _edgeReference[name]);
            white[i] = (filtered[i] - _centerReference[name]) / (_whiteReference[name] - _centerReference[name]);
        }

        var zoneScore = Median(zone);
        var whiteHits = new List<string>();
        var whiteClear = true;
        for (var i = 0; i < Names.Length; i++)
        {
            var name = Names[i];
            if (filtered[i] >= _whiteEnter[name])
            {
                whiteHits.Add(name);
            }
            if (filtered[i] >= _whiteClear[name])
            {
                whiteClear = false;
            }
        }

        return new MbriGrayObservation
        {
            Ready = Ready,
            Valid = valid,
            Raw = cleaned,
            Filtered = new MbriGraySample(filtered[0], filtered[1], filtered[2], filtered[3]),
            Zone = new MbriGraySample(zone[0], zone[1], zone[2], zone[3]),
            White = new MbriGraySample(white[0], white[1], white[2], white[3]),
            ZoneScore = zoneScore,
            NearEdge = zoneScore < _nearEdgeEnter,
            NearClear = zoneScore > _nearEdgeClear,
            WhiteHits = whiteHits.ToArray(),
            WhiteClear = whiteClear,
        };
    }
}
