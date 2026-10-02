namespace Sim.Core;

/// <summary>
/// MBri 灰度标定层（批1 建立，A1 重标）：仿真 FieldGrayLocal(0-1000) → 真车 ADC 域
/// 逐通道仿射，让真车 GrayRiskModel 的阈值原样生效。
///
/// 移植依据（真车源码只读参考 D:/project/robocup/2026/MBri）：
/// - ADC 域目标参考值取 config.py:44-73（新车 2026-08-14 重采重算；ring_patrol.py:15-25
///   注入的是 cfg.* 值，不是 gray.py:22-29 的旧默认值），本层保持原值不变。
/// - 暗外圈域（zone）：ADC = edge_ref + anchor(g)·(center_ref − edge_ref)。
///   规划定稿公式（design.md 移植映射表）。
/// - 白边域（white）：同理仿射 ADC = center_ref + (g/1000)·(white_ref − center_ref)。
///
/// A1 重标（2026-10-02，治"early-fire"——report.md §4.2/§6 已知缺陷）：批1 把
/// anchor(g) 钉成 g/1000，等价于宣称"仿真灰度 0=台沿、1000=台心"；而官方手绘场
/// 台面灰度只有 300（台沿黑带）→ 825（内环最亮带），台心红区平顶 650
/// （FieldModel.FieldGrayLocal，FieldModel.cs:80-88）→ 巡台 zone 恒 ≤0.825，
/// 低于真车 early-front 阈值（0.88/0.76）→ 几乎全程 EDGE_AVOID、MEDIUM_CRUISE
/// 不可达。重标做法对标真车 dev/calibrate_gray.py（在真场上按 edge/center 分组
/// 采样、取中位数重算参考值）的仿真场等价物——对官方场 FieldGrayLocal 经真实
/// SensorSampler（legacy14 四路灰度探点 ±0.11 m、光斑半径 0.025 m、灰度噪声 ±30、
/// 确定性种子 42）离线采样，把 anchor 的 0/1 端点重锚到官方场实测读数上：
///
/// - EdgeGrayReference[ch]（zone=0 锚）＝台沿组读数中位数：车中心压台沿黑带
///   （4 边 × 向台内 d∈{0, 0.05} m × 4 朝向 × 9 次噪声抽取），只统计台面读数
///   （g ≥ FallFloorGray，剔除悬空探点的走道 0 值——真车标定跑车探点不离台，
///   此为等价处理）。对标真车 edge 组（"竖向边缘.csv"等）中位数。
/// - CenterGrayReference[ch]（zone=1 锚）＝台心组读数中位数：红区方框内
///   （中心 ±{0,0.1,0.12} m × 4 朝向 × 5 次抽取）。对标真车 center 组
///   （"武字中间旋转2圈.csv"等）中位数。
/// - 采样结果（tmp/mbri-recal/ 诊断程序，不入库）：E = front 329.5 / rear 328.5 /
///   left 329.0 / right 327.5；C = front 650.9 / rear 650.4 / left 652.3 /
///   right 652.1。逐通道差异仅来自探点几何（仿真四路同场函数），量级与真车
///   逐路硬件差不同属正常，语义等价。
///
/// 重标后 zone 语义（zone=(g−E)/(C−E)，与真车"zone 0=台沿参考、1=中心参考"对齐）：
/// 台心红区 ≈1.0、内环最亮带 ≈1.54、走道 ≈−1.03（掉台判定可达）、early-front
/// 0.76/0.88 ↔ 前探点距台沿约 0.46/0.55 m、near-edge 0.35 ↔ 约 0.24 m、
/// FAST_ZONE 1.10 ↔ 内环带（可达，CRUISE 恢复）。逐点核对表见
/// MbriGrayCalibrationTests.OfficialField_ZoneSemantics（纯常量断言）。
///
/// White 不重推：官方手绘场无白边域（FieldGrayLocal 上限 825，无白输出），
/// WhiteReference 无法从本场采样，保持真车 ADC 参考与批1 白域仿射不变；
/// 重标后白域结构性不可达复核（含 g=1000 外推端点）见
/// MbriGrayCalibrationTests.WhiteEnter_UnreachableOnOfficialField。
///
/// 其余结构披露（沿批1）：真车传感器非线性/噪声不建模（仿真灰度本身是确定性
/// 模型）；真车阈值在重标后的 zone 域上原样生效。
/// </summary>
public static class MbriGrayCalibration
{
    /// <summary>真车灰度通道名（gray.py GrayRiskModel.NAMES 顺序）。</summary>
    public static readonly string[] Names = ["front", "rear", "left", "right"];

    /// <summary>黑色渐变外圈参考值（真车 ADC 域，config.py GRAY_EDGE_REFERENCE，2026-08-14 重采；A1 重标保持不变）。</summary>
    public static readonly IReadOnlyDictionary<string, double> EdgeReference =
        new Dictionary<string, double> { ["front"] = 494.0, ["rear"] = 632.0, ["left"] = 747.0, ["right"] = 675.0 };

    /// <summary>台面中心参考值（真车 ADC 域，config.py GRAY_CENTER_REFERENCE；A1 重标保持不变）。</summary>
    public static readonly IReadOnlyDictionary<string, double> CenterReference =
        new Dictionary<string, double> { ["front"] = 817.0, ["rear"] = 1136.0, ["left"] = 1159.0, ["right"] = 973.0 };

    /// <summary>白边参考值（真车 ADC 域，config.py GRAY_WHITE_REFERENCE；官方场无白边可采样，A1 重标保持不变）。</summary>
    public static readonly IReadOnlyDictionary<string, double> WhiteReference =
        new Dictionary<string, double> { ["front"] = 1704.0, ["rear"] = 2144.0, ["left"] = 1920.0, ["right"] = 1858.0 };

    /// <summary>
    /// 官方场台沿黑带实测灰度锚点（A1 重标，zone=0；采样配方见类型注释）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> EdgeGrayReference =
        new Dictionary<string, double> { ["front"] = 329.5, ["rear"] = 328.5, ["left"] = 329.0, ["right"] = 327.5 };

    /// <summary>
    /// 官方场台心红区实测灰度锚点（A1 重标，zone=1；采样配方见类型注释）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> CenterGrayReference =
        new Dictionary<string, double> { ["front"] = 650.9, ["rear"] = 650.4, ["left"] = 652.3, ["right"] = 652.1 };

    /// <summary>仿真灰度量程上限（FieldGrayLocal 官方语义 0-1000）。</summary>
    public const double SimGrayMax = 1000.0;

    /// <summary>
    /// 仿真灰度（0-1000）→ 真车 ADC 域（暗外圈/中心域）逐通道仿射（A1 重标）：
    /// <c>edge_ref + anchor(g)·(center_ref − edge_ref)</c>，其中
    /// <c>anchor(g) = (g − EdgeGrayReference)/(CenterGrayReference − EdgeGrayReference)</c>
    /// 是官方场位置锚（台沿 0、台心 1，越界线性外推）。纯函数。
    /// 端点：g=台沿锚 → edge_ref；g=台心锚 → center_ref；g=0（走道）→ 低于
    /// edge_ref（zone&lt;0，掉台判定可达）；g=1000 → 高于 center_ref（外推，
    /// 官方场实际不产生该灰度）。真车 zone 公式作用其上恒等于 anchor(g)。
    /// </summary>
    public static double SimToAdc(string channel, double g)
    {
        var e = EdgeGrayReference[channel];
        var c = CenterGrayReference[channel];
        var anchor = (g - e) / (c - e);
        return EdgeReference[channel] + anchor * (CenterReference[channel] - EdgeReference[channel]);
    }

    /// <summary>
    /// 仿真灰度（0-1000）→ 真车 ADC 域（白边域）逐通道仿射（"白边域同理"，批1 原样保留）：
    /// <c>center_ref + (g/1000)·(white_ref − center_ref)</c>。纯函数。
    /// 端点：g=0 → center_ref；g=1000 → white_ref。官方手绘场无白边域，该域
    /// 结构性不可达（重标后复核见 MbriGrayCalibrationTests）；供实测灰度图带
    /// 白边值（GrayGridMap 0-1000 编码）时使用。
    /// </summary>
    public static double SimToAdcWhite(string channel, double g)
        => CenterReference[channel] + (g / SimGrayMax) * (WhiteReference[channel] - CenterReference[channel]);

    /// <summary>
    /// 四通道整组换算（仿真灰度 → ADC 域采样），通道序与 gray.py NAMES 一致：
    /// front/rear/left/right ↔ 仿真 gF/gB/gL/gR。纯函数。
    /// </summary>
    public static MbriGraySample SimSampleToAdc(double gFront, double gRear, double gLeft, double gRight)
        => new(
            SimToAdc("front", gFront),
            SimToAdc("rear", gRear),
            SimToAdc("left", gLeft),
            SimToAdc("right", gRight));

    /// <summary>
    /// 掉台判定域的地板阈值（仿真灰度）：与内核 SimParameters.FallThreshold 同值
    /// （SimParameters.cs:11 "掉台判定(灰度)"，默认 150）。官方手绘场走道恒 0
    /// （±30 灰度噪声）、台面 ≥270（黑带 300−噪声），阈值取中保证两侧裕量。
    /// </summary>
    public const double FallFloorGray = 150.0;

    /// <summary>
    /// 掉台判定域映射（批2，解决批1 披露的 zone&lt;0 域差）：真车掉台判定是
    /// "四路 zone 全&lt;0"（reentry.py:177），即四路 ADC 均暗于边缘参考值——真车
    /// 走道地板的物理读数确实暗于边缘参考。仿真走道灰度恒 0，经 A1 重标仿射本已
    /// 落在 zone≈−1.03，但为保持批2 合同（走道段显式映射到 ADC=0，"地板暗于
    /// 边缘参考"的强等效物）本映射仍把走道段（g&lt;FallFloorGray）直接映射到
    /// ADC=0：zone=(0−edge)/(center−edge)&lt;0，掉台判定可达；台面段（g≥150，
    /// 即 ≥270 含噪声下界）走 A1 重标仿射不变。仅用于 reentry 的灰度模型输入；
    /// 巡台仍用重标仿射（其合同与单测对应更新，真车两模型本就各自实例化）。
    /// 纯函数。
    /// </summary>
    public static double SimToAdcFallDomain(string channel, double g)
        => g < FallFloorGray ? 0.0 : SimToAdc(channel, g);

    /// <summary>四通道整组掉台判定域换算（同 <see cref="SimToAdcFallDomain"/>）。纯函数。</summary>
    public static MbriGraySample SimSampleToAdcFallDomain(double gFront, double gRear, double gLeft, double gRight)
        => new(
            SimToAdcFallDomain("front", gFront),
            SimToAdcFallDomain("rear", gRear),
            SimToAdcFallDomain("left", gLeft),
            SimToAdcFallDomain("right", gRight));
}
