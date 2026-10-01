namespace Sim.Core;

/// <summary>
/// MBri 灰度标定层（批1，纯函数）：仿真 FieldGrayLocal(0-1000) → 真车 ADC 域
/// 逐通道仿射，让真车 GrayRiskModel 的阈值原样生效。
///
/// 移植依据（真车源码只读参考 D:/project/robocup/2026/MBri）：
/// - 参考值取 config.py:44-73（新车 2026-08-14 重采重算；ring_patrol.py:15-25
///   注入的是 cfg.* 值，不是 gray.py:22-29 的旧默认值）。
/// - 暗外圈域（zone）：ADC = edge_ref + (g/1000)·(center_ref − edge_ref)。
///   规划定稿公式（design.md 移植映射表 / 本任务批1 指令第 3 条）。
/// - 白边域（white）：同理仿射 ADC = center_ref + (g/1000)·(white_ref − center_ref)。
///
/// 结构披露（design.md 第 5 节"结构忠实、数值近似"）：
/// - 在该仿射下，真车 zone 公式 (adc−edge)/(center−edge) 恒等于 g/1000：
///   仿真灰度 0-1000 一比一成为 zone 0-1，真车阈值（near_edge 0.35/0.65、
///   deep −0.45、release 0.55 等）在仿真灰度上直接生效。
/// - 仿真灰度上限 1000 → ADC 上限 = center_ref &lt; white_enter（config.py:62-67），
///   且 FieldGrayLocal 无白边语义 → white_hits/WHITE_ESCAPE 结构性不可达
///   （默认手绘场）。白边域仿射另行提供，供实测灰度图带白边值时使用。
/// - 真车传感器非线性/噪声不建模（仿真灰度本身是确定性模型）。
/// </summary>
public static class MbriGrayCalibration
{
    /// <summary>真车灰度通道名（gray.py GrayRiskModel.NAMES 顺序）。</summary>
    public static readonly string[] Names = ["front", "rear", "left", "right"];

    /// <summary>黑色渐变外圈参考值（config.py GRAY_EDGE_REFERENCE，2026-08-14 重采）。</summary>
    public static readonly IReadOnlyDictionary<string, double> EdgeReference =
        new Dictionary<string, double> { ["front"] = 494.0, ["rear"] = 632.0, ["left"] = 747.0, ["right"] = 675.0 };

    /// <summary>台面中心参考值（config.py GRAY_CENTER_REFERENCE）。</summary>
    public static readonly IReadOnlyDictionary<string, double> CenterReference =
        new Dictionary<string, double> { ["front"] = 817.0, ["rear"] = 1136.0, ["left"] = 1159.0, ["right"] = 973.0 };

    /// <summary>白边参考值（config.py GRAY_WHITE_REFERENCE）。</summary>
    public static readonly IReadOnlyDictionary<string, double> WhiteReference =
        new Dictionary<string, double> { ["front"] = 1704.0, ["rear"] = 2144.0, ["left"] = 1920.0, ["right"] = 1858.0 };

    /// <summary>仿真灰度量程上限（FieldGrayLocal 官方语义 0-1000）。</summary>
    public const double SimGrayMax = 1000.0;

    /// <summary>
    /// 仿真灰度（0-1000）→ 真车 ADC 域（暗外圈/中心域）逐通道仿射：
    /// <c>edge_ref + (g/1000)·(center_ref − edge_ref)</c>。纯函数。
    /// 端点：g=0 → edge_ref（走道映射到外圈参考）；g=1000 → center_ref（中心）。
    /// </summary>
    public static double SimToAdc(string channel, double g)
        => EdgeReference[channel] + (g / SimGrayMax) * (CenterReference[channel] - EdgeReference[channel]);

    /// <summary>
    /// 仿真灰度（0-1000）→ 真车 ADC 域（白边域）逐通道仿射（"白边域同理"）：
    /// <c>center_ref + (g/1000)·(white_ref − center_ref)</c>。纯函数。
    /// 端点：g=0 → center_ref；g=1000 → white_ref。默认手绘场不会走到该域。
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
    /// 走道地板的物理读数确实暗于边缘参考。仿真走道灰度恒 0，经批1 仿射映射到
    /// ADC=edge_ref（zone=0，不为负），掉台永远触发不了。本映射把走道段
    /// （g&lt;FallFloorGray）直接映射到 ADC=0（"地板暗于边缘参考"的等效物）：
    /// zone=(0−edge)/(center−edge)&lt;0，掉台判定可达；台面段（g≥150，即 ≥270
    /// 含噪声下界）走批1 标准仿射不变。仅用于 reentry 的灰度模型输入；巡台
    /// 仍用批1 仿射（其合同与单测不变，真车两模型本就各自实例化）。
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
