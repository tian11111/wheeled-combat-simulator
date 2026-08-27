using Sim.Protocol;

namespace Sim.Calibration;

/// <summary>
/// Canonical report serialization + content fingerprint, and the safe
/// application of a report's recommended patch onto a scenario file.
/// Same input bytes always yield the same <see cref="CalibrationReport.ContentSha256"/>
/// because the fingerprint is computed over the report with volatile
/// bookkeeping (generatedAt, contentSha256 itself) removed.
/// </summary>
public static class ReportWriter
{
    /// <summary>Serialize with the content fingerprint set (generatedAt excluded from the hash).</summary>
    public static CalibrationReport Fingerprint(CalibrationReport report, string? generatedAt)
    {
        var content = report with { GeneratedAt = null, ContentSha256 = null };
        var hash = CalibrationMath.Sha256Hex(ProtocolJson.Serialize(content));
        return report with { GeneratedAt = generatedAt, ContentSha256 = hash };
    }

    public static string Serialize(CalibrationReport report) => ProtocolJson.Serialize(report);

    /// <summary>
    /// Apply the report's recommendedPatch onto <paramref name="base"/>:
    /// vehicle overrides for "us" (id + lateral/angular), scenario parameter
    /// overrides for block/collision/stall. Everything else (layout, seed,
    /// ruleset id, blocks) is preserved verbatim. The result must still pass
    /// <see cref="Scenario.Validate"/>.
    /// </summary>
    public static Scenario ApplyPatch(Scenario baseScenario, CalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(baseScenario);
        ArgumentNullException.ThrowIfNull(report);
        var scenario = baseScenario;

        if (report.RecommendedPatch.Vehicles.TryGetValue("us", out var vehicle) && vehicle is not null)
        {
            var vehicles = new Dictionary<string, VehicleProfile>(scenario.Vehicles);
            var profile = vehicles.TryGetValue(RoleNames.Us, out var existing) && existing is not null
                ? existing
                : new VehicleProfile();
            profile = profile with
            {
                Id = string.IsNullOrWhiteSpace(vehicle.Id) ? profile.Id : vehicle.Id,
                LatFrictionK = vehicle.LatFrictionK ?? profile.LatFrictionK,
                AngDamping = vehicle.AngDamping ?? profile.AngDamping,
            };
            vehicles[RoleNames.Us] = profile;
            scenario = scenario with { Vehicles = vehicles };
        }

        var parameters = report.RecommendedPatch.Parameters;
        if (parameters.Count > 0)
        {
            var merged = scenario.Parameters is null
                ? new Dictionary<string, double>()
                : new Dictionary<string, double>(scenario.Parameters);
            foreach (var (name, value) in parameters)
            {
                merged[name] = value;
            }
            scenario = scenario with { Parameters = merged };
        }

        var errors = scenario.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"patched scenario invalid: {string.Join(" ", errors)}");
        }
        return scenario;
    }

    /// <summary>Atomic write (temp + move) for the emitted scenario file.</summary>
    public static void EmitScenario(string path, Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = full + ".tmp";
        File.WriteAllText(temp, ProtocolJson.Serialize(scenario));
        File.Move(temp, full, overwrite: true);
    }
}
