using Sim.Core;

namespace Sim.Tests;

/// <summary>
/// 批1 换算层端点/单调/锚点单测（implement.md 批1 第 1 条）。
/// 锚点与披露见 MbriUnits 类型注释：k=0.000896（config.py:145 400×0.6s 实测
/// 21.5cm）、TrackWidth=0.229 实测（MujocoModel.cs:96-97；旧桥猜测 0.18 差 21.4%
/// &lt; 30% 停止阈值，按实测执行并披露）、时长→tick 四舍五入（design.md）。
/// </summary>
public sealed class MbriUnitsTests
{
    // ---------- 轮速 → m/s ----------

    [Fact]
    public void WheelToMs_Endpoints()
    {
        Assert.Equal(0.0, MbriUnits.WheelToMs(0), 12);
        // 锚点：400 单位 ≈ 0.3584 m/s（0.215 m / 0.6 s 实测）。
        Assert.Equal(0.3584, MbriUnits.WheelToMs(400), 9);
        // 开局后退 1000 单位 ≈ 0.896 m/s（PRD Confirmed Baseline）。
        Assert.Equal(0.896, MbriUnits.WheelToMs(1000), 9);
        // 电机死区下限（config.py:88 注 400 以下不能可靠驱动）不改变纯换算。
        Assert.Equal(0.3584, MbriUnits.WheelToMs(400.0), 9);
    }

    [Fact]
    public void WheelToMs_AnchorReproducesMeasuredStep()
    {
        // 400 单位 × 0.6 s × k 应还原实测退离距离 21.5 cm（config.py:145）。
        var distance = MbriUnits.WheelToMs(400) * 0.6;
        Assert.Equal(0.215, distance, 3); // 0.21504 m，误差 < 0.1 mm
    }

    [Fact]
    public void WheelToMs_Monotonic()
    {
        for (var unit = 0; unit <= 1023; unit += 1)
        {
            Assert.True(MbriUnits.WheelToMs(unit) <= MbriUnits.WheelToMs(unit + 1) + 1e-15,
                $"WheelToMs must be monotonic at {unit}");
        }
    }

    // ---------- 差速 → v/w ----------

    [Fact]
    public void DifferentialToVW_PureLinear_HasZeroYaw()
    {
        var (v, w) = MbriUnits.DifferentialToVW(400, 400);
        Assert.Equal(0.3584, v, 9);
        Assert.Equal(0.0, w, 12);

        // 开局后退：(-1000, -1000) → v=-0.896, w=0。
        var (rv, rw) = MbriUnits.DifferentialToVW(-1000, -1000);
        Assert.Equal(-0.896, rv, 9);
        Assert.Equal(0.0, rw, 12);
    }

    [Fact]
    public void DifferentialToVW_PureSpin_MatchesTurnCalibrationTable()
    {
        // 原地转 ±625（135° 表项）：|w| = 2·625·k/(2·0.229) = 2.4454 rad/s。
        var (_, wRight) = MbriUnits.DifferentialToVW(-625, 625);
        Assert.Equal(2.4454, Math.Abs(wRight), 3);
        // 规划分母 2·TrackWidth 对齐真车标定表：135° 实测 1.0s / 90° 实测 0.65s
        // （config.py MOTOR_TURN_CALIBRATION），代入应在 ±5% 内
        // （理想差速公式会快一倍：135° 仅 0.48s，与实测不符）。
        var t135 = Math.PI * 3.0 / 4.0 / Math.Abs(wRight);
        Assert.InRange(t135, 0.95, 1.05); // 0.964s, 偏差 3.6%
        var (_, w600) = MbriUnits.DifferentialToVW(-600, 600);
        var t90 = Math.PI / 2.0 / Math.Abs(w600);
        Assert.InRange(t90, 0.65 * 0.95, 0.65 * 1.05); // 0.669s, 偏差 2.9%
    }

    [Fact]
    public void DifferentialToVW_TrackWidthIsMeasured229mm()
    {
        // 实测轮心 y=+0.11635/-0.11265（MujocoModel.cs:96-97）；旧桥 0.18 差 21.4%。
        Assert.Equal(0.229, MbriUnits.TrackWidthMeters, 9);
        var deviation = Math.Abs(MbriUnits.TrackWidthMeters - 0.18) / MbriUnits.TrackWidthMeters;
        Assert.True(deviation < 0.30, $"旧桥差异 {deviation:P1} 须 < 30% 停止阈值 (implement.md)");
        // 单位差速 (0,1) → w = k/(2·TrackWidth)（闭式对照）。
        Assert.Equal(MbriUnits.WheelSpeedK / (2 * MbriUnits.TrackWidthMeters),
            MbriUnits.DifferentialToVW(0, 1).W, 12);
    }

    [Fact]
    public void DifferentialToVW_RejectsBadTrackWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MbriUnits.DifferentialToVW(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MbriUnits.DifferentialToVW(1, 1, double.NaN));
    }

    // ---------- 时长 → tick ----------

    [Fact]
    public void SecondsToTicks_Endpoints()
    {
        // 开局后退 1.8s → 36 tick（批1 指令定稿数字）。
        Assert.Equal(36, MbriUnits.SecondsToTicks(1.8));
        Assert.Equal(0, MbriUnits.SecondsToTicks(0));
        // 巡台各时长（config.py）：0.6→12、2→40、转向表 0.5→10/0.55→11/0.65→13/1.2→24。
        Assert.Equal(12, MbriUnits.SecondsToTicks(0.60));
        Assert.Equal(40, MbriUnits.SecondsToTicks(2.0));
        Assert.Equal(10, MbriUnits.SecondsToTicks(0.5));
        Assert.Equal(11, MbriUnits.SecondsToTicks(0.55));
        Assert.Equal(13, MbriUnits.SecondsToTicks(0.65));
        Assert.Equal(24, MbriUnits.SecondsToTicks(1.2));
        Assert.Equal(0, MbriUnits.SecondsToTicks(-1.0));
    }

    [Fact]
    public void SecondsToTicks_MonotonicAndRoundsHalfAwayFromZero()
    {
        long prev = 0;
        for (var s = 0.05; s <= 3.0 + 1e-9; s += 0.05)
        {
            var ticks = MbriUnits.SecondsToTicks(s);
            Assert.True(ticks >= prev, $"SecondsToTicks must be monotonic at {s}");
            prev = ticks;
        }
        Assert.Equal(1, MbriUnits.SecondsToTicks(0.025, 0.05)); // 半 tick 四舍五入进位
        Assert.Equal(0, MbriUnits.SecondsToTicks(0.024, 0.05));
        Assert.Equal(1, MbriUnits.SecondsToTicks(0.075, 0.05));
    }

    [Fact]
    public void SecondsToTicks_RejectsBadTickSeconds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MbriUnits.SecondsToTicks(1.0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MbriUnits.SecondsToTicks(1.0, -0.05));
    }
}
