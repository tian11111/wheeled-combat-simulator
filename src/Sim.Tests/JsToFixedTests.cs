using Sim.Core;

namespace Sim.Tests;

/// <summary>
/// Js.ToFixed 的 ECMAScript 语义钉子（期望值经 Node 对照产出）。
///
/// <para>回归背景 (2026-10-14): 原实现先乘 10^digits 再按 F{digits} 格式化,
/// digits&gt;0 时输出比真值大 10^digits 倍 —— <c>ToFixed(5.0, 1)</c> 得 "50.0"
/// (JS 应为 "5.0")。该缺陷同时是事件行 <c>t=</c> 显示值 = 10×秒 (被误读成
/// "0.1s 单位") 的根源, 并连带 oracle/tools 解析与全部回放基线。</para>
/// </summary>
public sealed class JsToFixedTests
{
    /// <summary>(值, 位数, Node (v.toFixed(d)) 的权威输出)。</summary>
    [Theory]
    // 放大回归组 (旧实现全错)
    [InlineData(5.0, 1, "5.0")]
    [InlineData(120.0, 1, "120.0")]
    [InlineData(0.5, 1, "0.5")]
    [InlineData(0.1, 1, "0.1")]
    [InlineData(119.9, 1, "119.9")]
    [InlineData(3.14159, 2, "3.14")]
    [InlineData(9.99, 2, "9.99")]
    // 二进制真值舍入组 (decimal 精度陷阱)
    [InlineData(1.2345, 3, "1.234")]  // 真值 1.2344999999999999307…
    [InlineData(1.005, 2, "1.00")]    // 真值 1.00499999999999989…
    [InlineData(2.675, 2, "2.67")]    // 真值 2.67499999999999982…
    [InlineData(0.125, 2, "0.13")]
    // 平手进位组
    [InlineData(2.5, 0, "3")]
    [InlineData(0.25, 1, "0.3")]
    [InlineData(1.25, 1, "1.3")]
    [InlineData(0.05, 1, "0.1")]
    // 负数 (ECMAScript 对 -0 有特殊输出)
    [InlineData(-1.25, 1, "-1.3")]
    [InlineData(-0.4, 0, "-0")]
    [InlineData(-1.5, 0, "-2")]
    [InlineData(-0.05, 1, "-0.1")]
    public void ToFixed_MatchesEcmaScript(double value, int digits, string expected)
        => Assert.Equal(expected, Js.ToFixed(value, digits));

    [Fact]
    public void ToFixed_DigitsZero_Unchanged()
    {
        // digits=0 时原实现即正确 (scale=1), 作为兼容性对照组。
        Assert.Equal("216", Js.ToFixed(216.0, 0));
        Assert.Equal("0", Js.ToFixed(0.4, 0));
        Assert.Equal("1", Js.ToFixed(0.6, 0));
    }

    [Fact]
    public void ToFixed_HandlesNonFiniteAndHuge()
    {
        Assert.Equal("NaN", Js.ToFixed(double.NaN, 1));
        Assert.Equal("Infinity", Js.ToFixed(double.PositiveInfinity, 1));
        Assert.Equal("-Infinity", Js.ToFixed(double.NegativeInfinity, 2));
        Assert.Equal("1E+21", Js.ToFixed(1e21, 1));
    }

    [Fact]
    public void ToFixed_IsUsedForEventTimeText_InSeconds()
    {
        // 事件行 t= 由 RuntimeState 经 ToFixed(simT, 1) 产出; 5 秒的 SimT 必须
        // 显示 "5.0" 而不是 "50.0" —— 这条同时钉住 oracle.py 的秒单位口径。
        Assert.Equal("5.0", Js.ToFixed(5.0, 1));
        Assert.Equal("0.1", Js.ToFixed(0.1, 1));
        Assert.Equal("119.9", Js.ToFixed(119.9, 1));
        Assert.Equal("0.0", Js.ToFixed(0.0, 1));
    }
}
