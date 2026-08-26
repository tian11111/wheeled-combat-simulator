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

        // decimal conversion of a double is exact (double has ≤17 significant
        // decimal digits), so decimal arithmetic reproduces the ECMAScript
        // "closest n / 10^digits, ties → larger n" rule.
        var m = (decimal)x;
        var scaled = m * (decimal)Math.Pow(10, digits);
        var floor = Math.Floor(scaled);
        var rounded = scaled - floor >= 0.5m ? floor + 1 : floor;
        return rounded.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    /// <summary>JS number-to-string for integral score counters.</summary>
    public static string Num(double value) => ((long)value).ToString(CultureInfo.InvariantCulture);

    /// <summary>ECMAScript <c>Math.imul</c>: 32-bit integer multiply (low 32 bits, wraparound).</summary>
    public static int Imul(int a, int b) => unchecked((int)((long)a * b));
}
