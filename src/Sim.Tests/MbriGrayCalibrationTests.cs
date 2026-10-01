using Sim.Core;

namespace Sim.Tests;

/// <summary>
/// 批1 灰度标定层端点/单调单测（implement.md 批1 第 2 条）。
/// 仿射公式与参考值来源见 MbriGrayCalibration 类型注释（config.py:44-73，
/// 2026-08-14 重采；gray.py:22-29 旧默认值不采用）。
/// </summary>
public sealed class MbriGrayCalibrationTests
{
    [Fact]
    public void SimToAdc_Endpoints_EdgeRefAtZero_CenterRefAtThousand()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            Assert.Equal(MbriGrayCalibration.EdgeReference[channel],
                MbriGrayCalibration.SimToAdc(channel, 0), 9);
            Assert.Equal(MbriGrayCalibration.CenterReference[channel],
                MbriGrayCalibration.SimToAdc(channel, 1000), 9);
        }
        // 抽查具体通道数值（config.py GRAY_EDGE/CENTER_REFERENCE）。
        Assert.Equal(494.0, MbriGrayCalibration.SimToAdc("front", 0), 9);
        Assert.Equal(817.0, MbriGrayCalibration.SimToAdc("front", 1000), 9);
        Assert.Equal(1159.0, MbriGrayCalibration.SimToAdc("left", 1000), 9);
    }

    [Fact]
    public void SimToAdc_MidpointIsLinearInterpolation()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            var edge = MbriGrayCalibration.EdgeReference[channel];
            var center = MbriGrayCalibration.CenterReference[channel];
            Assert.Equal((edge + center) / 2.0, MbriGrayCalibration.SimToAdc(channel, 500), 9);
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

    [Fact]
    public void SimToAdc_MakesCarZoneIdentity_GradientOver1000()
    {
        // 结构性推论（交付披露）：真车 zone 公式作用在仿射值上恒等于 g/1000。
        foreach (var channel in MbriGrayCalibration.Names)
        {
            for (var g = 0.0; g <= 1000.0; g += 50.0)
            {
                var adc = MbriGrayCalibration.SimToAdc(channel, g);
                var zone = (adc - MbriGrayCalibration.EdgeReference[channel])
                           / (MbriGrayCalibration.CenterReference[channel] - MbriGrayCalibration.EdgeReference[channel]);
                Assert.Equal(g / 1000.0, zone, 12);
            }
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
    public void SimToAdcWhite_MonotonicAndAboveZoneDomain()
    {
        foreach (var channel in MbriGrayCalibration.Names)
        {
            for (var g = 0.0; g < 1000.0; g += 1.0)
            {
                Assert.True(
                    MbriGrayCalibration.SimToAdcWhite(channel, g) <= MbriGrayCalibration.SimToAdcWhite(channel, g + 1.0) + 1e-9,
                    $"SimToAdcWhite({channel}) must be monotonic at g={g}");
                // 白边域整体高于暗外圈域上界（center_ref），两域不重叠。
                Assert.True(MbriGrayCalibration.SimToAdcWhite(channel, g)
                            >= MbriGrayCalibration.SimToAdc(channel, 1000) - 1e-9);
            }
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
