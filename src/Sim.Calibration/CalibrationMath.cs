using System.Security.Cryptography;
using System.Text;

namespace Sim.Calibration;

/// <summary>Numerically stable, deterministic helpers shared by the fitters.</summary>
public static class CalibrationMath
{
    /// <summary>Rounds to 6 decimals for report-stable output (matches legacy).</summary>
    public static double? Round6(double value)
    {
        if (!double.IsFinite(value))
        {
            return null;
        }
        return Math.Round(value, 6, MidpointRounding.ToEven);
    }

    public static double Clamp(double value, double lo, double hi)
        => Math.Max(lo, Math.Min(hi, value));

    /// <summary>Wraps an angle difference into [-pi, pi] (shortest arc).</summary>
    public static double AngleDelta(double a, double b)
    {
        var delta = b - a;
        while (delta > Math.PI)
        {
            delta -= 2 * Math.PI;
        }
        while (delta < -Math.PI)
        {
            delta += 2 * Math.PI;
        }
        return delta;
    }

    /// <summary>RMSE of residual samples.</summary>
    public static double Rmse(IReadOnlyList<double> residuals)
    {
        if (residuals.Count == 0)
        {
            return double.NaN;
        }
        var sum = 0.0;
        foreach (var r in residuals)
        {
            sum += r * r;
        }
        return Math.Sqrt(sum / residuals.Count);
    }

    /// <summary>Golden-section (ternary) search for a 1-D bounded least-squares minimum.</summary>
    public static double MinimiseBounded(Func<double, double> loss, double lo, double hi, int iterations = 80)
    {
        var left = lo;
        var right = hi;
        for (var i = 0; i < iterations; i++)
        {
            var a = left + (right - left) / 3;
            var b = right - (right - left) / 3;
            if (loss(a) <= loss(b))
            {
                right = b;
            }
            else
            {
                left = a;
            }
        }
        return (left + right) / 2;
    }

    /// <summary>Lowercase hex SHA-256 of raw bytes (input fingerprint).</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));
}

/// <summary>A consecutive-decay observation: log(|v(t+dt)|/|v(t)|) over dt seconds.</summary>
public readonly record struct ExponentialPair(double Dt, double LogRatio);

/// <summary>A block-glide observation: speed before/after a dt interval.</summary>
public readonly record struct BlockPair(double Before, double After, double Dt);

/// <summary>A collision observation: relative normal velocity before/after impact.</summary>
public readonly record struct CollisionSample(double Before, double After);

/// <summary>A stall-labeled speed sample.</summary>
public readonly record struct StallSample(double Speed, bool Stalled);

/// <summary>A mount trial: measured approach normal/tangential velocity + outcome.</summary>
public readonly record struct MountSample(double Vn, double Vt, bool Outcome);

/// <summary>
/// Outcome of one parameter fit. <see cref="Calibrated"/> is false with a
/// <see cref="Reason"/> when sample counts are insufficient (the tool never guesses).
/// </summary>
public sealed record FitResult(
    bool Calibrated,
    double? Value,
    int Samples,
    double? Rmse,
    string Method,
    string? Reason = null,
    double? Accuracy = null)
{
    public static FitResult Insufficient(string reason, int samples, int required)
        => new(false, null, samples, null, "insufficient", reason + $" (样本 {samples} < 最少 {required})");

    public static FitResult Ok(double value, int samples, double rmse, string method, double? accuracy = null)
        => new(true, CalibrationMath.Round6(value), samples, CalibrationMath.Round6(rmse), method, null, accuracy);
}
