using System.Globalization;

namespace Sim.Core;

/// <summary>
/// Math and formatting helpers that reproduce the legacy JavaScript core's
/// observable behavior (clamp/norm conventions, <c>Number.toFixed</c> output
/// used inside log messages).
/// </summary>
public static class Js
{
    /// <summary>Legacy <c>clamp(v,a,b)</c>: NaN-unsafe, boundary-inclusive.</summary>
    public static double Clamp(double v, double a, double b) => v < a ? a : (v > b ? b : v);

    /// <summary>Normalize an angle to (-π, π] via the legacy while-loop.</summary>
    public static double Norm(double a)
    {
        while (a > Math.PI)
        {
            a -= 2 * Math.PI;
        }
        while (a < -Math.PI)
        {
            a += 2 * Math.PI;
        }
        return a;
    }

    /// <summary>Legacy <c>Math.hypot</c> equivalent (naive form; identical for the axis-aligned and well-scaled values used by the core).</summary>
    public static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    /// <summary>
    /// ECMAScript <c>Number.prototype.toFixed(digits)</c>: decimal rounding of
    /// the exact binary value, ties resolved toward the larger integer n.
    /// Used only for log-message text parity with the old core.
    /// </summary>
    public static string ToFixed(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || Math.Abs(x) >= 1e21)
        {
            return x.ToString(CultureInfo.InvariantCulture);
        }
        if (x == 0.0)
        {
            // ECMAScript 保留负零: (-0.4).toFixed(0) === "-0"。
            return (double.IsNegative(x) ? "-" : "") + "0" + (digits > 0 ? "." + new string('0', digits) : "");
        }

        // 取 G17 (17 位有效数字, double 的规范精确展开) 而非 "R"/直接 (decimal)x:
        // "R" 会给出最短往返串 (1.2345 → "1.2345"), 直接 decimal 展开成 50 位十进制
        // 后乘 10^digits 会丢尾差 —— 两者都不能复现 ECMAScript 按**二进制真值**
        // 舍入的行为。G17 的 17 位对 1.23 类量级足够承载真值尾差, 且 decimal 的
        // 28-29 位有效数字能容纳它。期望值经 Node 对照: 1.2345→"1.234",
        // 1.005→"1.00", 2.675→"2.67", 0.25→"0.3", 0.05→"0.1", -1.25→"-1.3"。
        var m = decimal.Parse(x.ToString("G17", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var negative = m < 0;
        var abs = negative ? -m : m;
        var scale = (decimal)Math.Pow(10, digits);
        var scaled = abs * scale;
        // 在绝对值域上做 floor+0.5 进位 (Math.Floor 对负数向 -∞, 直接用会反向),
        // 最后补负号 —— 与 ECMAScript "取最近的 n, 平手取较大 n (绝对值域)" 等价。
        var floor = Math.Floor(scaled);
        var rounded = scaled - floor >= 0.5m ? floor + 1 : floor;
        var text = (rounded / scale).ToString("F" + digits, CultureInfo.InvariantCulture);
        // 2026-10-14 修复: 原实现把放大后的整数直接按 F{digits} 打印, digits>0 时
        // 输出比真值大 10^digits 倍 (5.0 → "50.0"; 事件行 t= 显示值=10×秒的成因,
        // 也是 oracle.py "0.1s 单位" 口径的根源)。JS toFixed(5.0,1)==="5.0" 无放大,
        // 故除回 scale 再格式化。
        return negative ? "-" + text : text;
    }

    /// <summary>JS number-to-string for integral score counters.</summary>
    public static string Num(double value) => ((long)value).ToString(CultureInfo.InvariantCulture);

    /// <summary>ECMAScript <c>Math.imul</c>: 32-bit integer multiply (low 32 bits, wraparound).</summary>
    public static int Imul(int a, int b) => unchecked((int)((long)a * b));
}
