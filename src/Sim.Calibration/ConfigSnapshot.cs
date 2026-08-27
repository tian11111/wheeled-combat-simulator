using System.Text.RegularExpressions;
using Sim.Protocol;

namespace Sim.Calibration;

/// <summary>Read-only extraction of MBri config.py decision constants (no eval, no writes).</summary>
public static class ConfigSnapshot
{
    private static readonly Regex Scalar = new(
        @"^(?<name>[A-Z][A-Z0-9_]*)\s*=\s*(?<value>[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*(?:#.*)?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static SensorConfigSnapshot Parse(string configPyText)
    {
        var scalars = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match m in Scalar.Matches(configPyText))
        {
            if (double.TryParse(m.Groups["value"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)
                && double.IsFinite(v))
            {
                scalars.TryAdd(m.Groups["name"].Value, v);
            }
        }
        return new SensorConfigSnapshot(
            Get(scalars, "GRAY_NEAR_EDGE_ENTER"),
            Get(scalars, "IR_DIRECTION_RATIO_THRESHOLD"),
            Get(scalars, "IR_DIRECTION_SIGNAL_MIN"),
            Get(scalars, "SHOVEL_HANG_ENTER"),
            Get(scalars, "SHOVEL_HANG_CLEAR"));

        static double? Get(Dictionary<string, double> values, string key)
            => values.TryGetValue(key, out var v) ? v : null;
    }
}

/// <summary>Canonical serialization + content fingerprint for the evidence report.</summary>
public static class SensorEvidence
{
    public static SensorCalibrationReport Fingerprint(SensorCalibrationReport report, string? generatedAt)
    {
        var content = report with { GeneratedAt = null, ContentSha256 = null };
        var hash = CalibrationMath.Sha256Hex(ProtocolJson.Serialize(content));
        return report with { GeneratedAt = generatedAt, ContentSha256 = hash };
    }

    public static string Serialize(SensorCalibrationReport report) => ProtocolJson.Serialize(report);
}
