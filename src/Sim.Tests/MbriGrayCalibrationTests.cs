using Sim.Core;

namespace Sim.Tests;

/// <summary>
/// 灰度标定层端点/单调/语义单测（批1 建立，A1 重标更新断言）。
/// A1 重标（2026-10-02）：批1 仿射把 zone 钉在 g/1000（宣称仿真灰度 0=台沿、
/// 1000=台心），官方场台面灰度只达 ~825（红区平顶 650）→ zone 恒 &lt; early-front
/// 0.88 → 巡台几乎全程 EDGE_AVOID（report.md §4.2/§6 缺陷）。重标把 zone=0/1
/// 锚到官方场 SensorSampler 实测读数（台沿黑带 E≈329 / 台心红区 C≈651，采样配方
/// 见 MbriGrayCalibration 类型注释，对标真车 dev/calibrate_gray.py 流程）。
/// 本文件钉：锚点端点、单调性、ADC 域合法性、官方场位置语义（台心≈1、边沿→0、
/// 阈值落位）与白域不可达复核。
/// </summary>
public sealed class MbriGrayCalibrationTests
{
    [Fact]
    public void SimToAdc_AnchoredEndpoints_EdgeGrayToEdgeRef_CenterGrayToCenterRef()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            Assert.Equal(MbriGrayCalibration.EdgeReference[channel],
                MbriGrayCalibration.SimToAdc(channel, MbriGrayCalibration.EdgeGrayReference[channel]), 9);
            Assert.Equal(MbriGrayCalibration.CenterReference[channel],
                MbriGrayCalibration.SimToAdc(channel, MbriGrayCalibration.CenterGrayReference[channel]), 9);
        }
        // 抽查具体通道数值（真车 ADC 参考 × 官方场采样锚点）。
        Assert.Equal(494.0, MbriGrayCalibration.SimToAdc("front", 329.5), 9);
        Assert.Equal(817.0, MbriGrayCalibration.SimToAdc("front", 650.9), 9);
        Assert.Equal(1159.0, MbriGrayCalibration.SimToAdc("left", 652.3), 9);
    }

    [Fact]
    public void SimToAdc_AnchorMidpointMapsToAdcMidpoint()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            var e = MbriGrayCalibration.EdgeGrayReference[channel];
            var c = MbriGrayCalibration.CenterGrayReference[channel];
            Assert.Equal((MbriGrayCalibration.EdgeReference[channel] + MbriGrayCalibration.CenterReference[channel]) / 2.0,
                MbriGrayCalibration.SimToAdc(channel, (e + c) / 2.0), 9);
        }
    }

    [Fact]
    public void SimToAdc_WalkwayBelowEdgeRef_RawMaxExtrapolatesAboveCenterRef()
    {
        // 走道 g=0 → 低于 edge_ref（zone<0，掉台判定语义保持，批1 openIssue 解法不回退）；
        // g=1000 → 高于 center_ref（位置锚线性外推；官方场实际不产生该灰度）。
        foreach (var channel in MbriGrayCalibration.Names)
        {
            Assert.True(MbriGrayCalibration.SimToAdc(channel, 0.0)
                        < MbriGrayCalibration.EdgeReference[channel],
                        $"SimToAdc({channel},0) must land below edge_ref.");
            Assert.True(MbriGrayCalibration.SimToAdc(channel, 1000.0)
                        > MbriGrayCalibration.CenterReference[channel],
                        $"SimToAdc({channel},1000) must extrapolate above center_ref.");
        }
    }

    [Fact]
    public void SimToAdc_MonotonicPerChannel()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            for (var g = 0.0; g < 1000.0; g += 1.0)
            {
                Assert.True(
                    MbriGrayCalibration.SimToAdc(channel, g) <= MbriGrayCalibration.SimToAdc(channel, g + 1.0) + 1e-9,
                    $"SimToAdc({channel}) must be monotonic at g={g}");
            }
        }
    }

    /// <summary>真车 zone 公式（gray.py:-zone=(adc−edge)/(center−edge)）作用在标定值上。</summary>
    private static double CarZone(string channel, double g)
    {
        var adc = MbriGrayCalibration.SimToAdc(channel, g);
        return (adc - MbriGrayCalibration.EdgeReference[channel])
               / (MbriGrayCalibration.CenterReference[channel] - MbriGrayCalibration.EdgeReference[channel]);
    }

    [Fact]
    public void SimToAdc_ZoneEqualsOfficialFieldAnchorNormalization()
    {
        // 结构性推论（A1 重标后）：zone 恒等于官方场位置锚 (g−E)/(C−E)。
        foreach (var channel in MbriGrayCalibration.Names)
        {
            var e = MbriGrayCalibration.EdgeGrayReference[channel];
            var c = MbriGrayCalibration.CenterGrayReference[channel];
            for (var g = 0.0; g <= 1000.0; g += 50.0)
            {
                Assert.Equal((g - e) / (c - e), CarZone(channel, g), 12);
            }
        }
    }

    [Fact]
    public void OfficialField_ZoneSemantics_CenterOne_EdgeZero_WalkwayNegative()
    {
        // 官方手绘场特征灰度（FieldModel.FieldGrayLocal，FieldModel.cs:80-88：
        // 走道 0、台沿黑带 300、台心红区平顶 650、内环最亮带 825）→ 重标后 zone 语义：
        foreach (var channel in MbriGrayCalibration.Names)
        {
            // 走道：zone≈−1.03，深于掉台深暗阈值（PATROL_DEEP_ZONE=−0.45）。
            var walkway = CarZone(channel, 0);
            Assert.True(walkway < MbriPatrol.DeepZone, $"walkway zone {walkway} must be deep-dark.");
            // 台沿黑带：zone≈0（|zone|≤0.15），且不落深暗域（台上不误报掉台/深暗）。
            var edgeBand = CarZone(channel, 300);
            Assert.InRange(edgeBand, -0.15, 0.05);
            Assert.True(edgeBand > MbriPatrol.DeepZone, "edge band must not read deep-dark on platform.");
            // 台心红区：zone≈1（"台心≈1"语义）。
            Assert.InRange(CarZone(channel, 650), 0.95, 1.05);
            // 内环最亮带：zone≈1.54 ≥ FAST_ZONE_SCORE（CRUISE 高速档可达）。
            Assert.True(CarZone(channel, 825) > MbriPatrol.FastZoneScore,
                        "inner ring must reach FAST_ZONE (CRUISE tier reachable).");
        }
    }

    [Fact]
    public void OfficialField_ThresholdGrays_LandInsideReachablePlatformRange()
    {
        // 阈值→灰度落位核对（A1 "同步核对 early-front/FAST_ZONE 等阈值语义"；
        // A3 重校：early-front 前路线 0.76→0.35，与 near-edge 同一条官方场 danger
        // 边界——near-edge 0.35 与 early-front 0.35/0.88 的灰度须落在台面渐变带
        // （300–650），FAST_ZONE 1.10 的灰度须落在内环带（650–825]——即各阈值在
        // 官方场均可达且顺序正确（对应位置：台沿内 ~0.24m / ~0.24m / ~0.55m /
        // 内环 ~0.66m 起环带）。
        foreach (var channel in MbriGrayCalibration.Names)
        {
            var e = MbriGrayCalibration.EdgeGrayReference[channel];
            var c = MbriGrayCalibration.CenterGrayReference[channel];
            var gNear = e + MbriRiskModel.NearEdgeEnter * (c - e);      // 0.35
            var gEarly = e + MbriPatrol.EarlyFrontAbs * (c - e);        // 0.35（A3）
            var gEarlyScore = e + MbriPatrol.EarlyFrontZone * (c - e);  // 0.88
            var gFast = e + MbriPatrol.FastZoneScore * (c - e);         // 1.10
            Assert.InRange(gNear, 300.0, 650.0);
            Assert.Equal(gNear, gEarly, 6.0);
            Assert.InRange(gEarlyScore, gEarly, 650.0);
            Assert.InRange(gFast, 650.0, 825.0);
        }
    }

    [Fact]
    public void SimToAdc_StayWithinAdcDomain()
    {
        // 仿射输出必须落在真车 ADC 合法域 [0,10000]（gray.py ADC_MAX），
        // 保证喂入 MbriRiskModel 后 valid=true（坏值路径不因标定层引入）。
        foreach (var channel in MbriGrayCalibration.Names)
        {
            for (var g = 0.0; g <= 1000.0; g += 1.0)
            {
                var adc = MbriGrayCalibration.SimToAdc(channel, g);
                Assert.InRange(adc, 0.0, 10000.0);
            }
        }
    }

    [Fact]
    public void SimToAdcWhite_Endpoints()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            Assert.Equal(MbriGrayCalibration.CenterReference[channel],
                MbriGrayCalibration.SimToAdcWhite(channel, 0), 9);
            Assert.Equal(MbriGrayCalibration.WhiteReference[channel],
                MbriGrayCalibration.SimToAdcWhite(channel, 1000), 9);
        }
        Assert.Equal(2144.0, MbriGrayCalibration.SimToAdcWhite("rear", 1000), 9);
    }

    [Fact]
    public void SimToAdcWhite_MonotonicAndDominatesZoneDomainAtSameGray()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            for (var g = 0.0; g < 1000.0; g += 1.0)
            {
                Assert.True(
                    MbriGrayCalibration.SimToAdcWhite(channel, g) <= MbriGrayCalibration.SimToAdcWhite(channel, g + 1.0) + 1e-9,
                    $"SimToAdcWhite({channel}) must be monotonic at g={g}");
                // 白边域在相同灰度下整体高于暗外圈域（A1 重标后 zone 域在台心锚以上
                // 外推，逐点同灰度支配是两域不交的正确不变量）。
                Assert.True(MbriGrayCalibration.SimToAdcWhite(channel, g)
                            >= MbriGrayCalibration.SimToAdc(channel, g) - 1e-9);
            }
        }
    }

    [Fact]
    public void WhiteEnter_UnreachableOnOfficialField()
    {
        // 批1 披露复核（A1 重标后仍成立）：官方手绘场台面灰度上限 825（红区平顶
        // 650，FieldModel.cs:83-87），加灰度噪声界 855；重标仿射在该范围内（乃至
        // 理论外推端点 g=1000）的输出均低于 config GRAY_WHITE_ENTER →
        // white_hits/WHITE_ESCAPE 结构性不可达。
        foreach (var channel in MbriGrayCalibration.Names)
        {
            Assert.True(MbriGrayCalibration.SimToAdc(channel, 855.0)
                        < MbriRiskModel.DefaultWhiteEnter[channel],
                        $"white_enter must stay unreachable at platform max for {channel}.");
            Assert.True(MbriGrayCalibration.SimToAdc(channel, 1000.0)
                        < MbriRiskModel.DefaultWhiteEnter[channel],
                        $"white_enter must stay unreachable even at extrapolated g=1000 for {channel}.");
        }
    }

    [Fact]
    public void SimSampleToAdc_MapsSemanticChannels()
    {
        // gF/gB/gL/gR → front/rear/left/right（MbriFsm.ReadGraySample 的通道对应）。
        var sample = MbriGrayCalibration.SimSampleToAdc(0, 500, 1000, 250);
        Assert.Equal(MbriGrayCalibration.SimToAdc("front", 0), sample.Front, 9);
        Assert.Equal(MbriGrayCalibration.SimToAdc("rear", 500), sample.Rear, 9);
        Assert.Equal(MbriGrayCalibration.SimToAdc("left", 1000), sample.Left, 9);
        Assert.Equal(MbriGrayCalibration.SimToAdc("right", 250), sample.Right, 9);
    }
}
