using System.Text.Json.Serialization;
using Sim.Core;
using Sim.Protocol;

namespace Sim.Calibration;

/// <summary>One subsystem's fit + holdout evidence in the report.</summary>
public sealed record SubsystemFit(
    [property: JsonPropertyName("calibrated")] bool Calibrated,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("fitSamples")] int FitSamples,
    [property: JsonPropertyName("fitRmse")] double? FitRmse,
    [property: JsonPropertyName("holdoutSamples")] int HoldoutSamples,
    [property: JsonPropertyName("holdoutRmse")] double? HoldoutRmse,
    [property: JsonPropertyName("holdoutAccuracy")] double? HoldoutAccuracy,
    [property: JsonPropertyName("target")] double Target,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>Recommended patch targeting scenario/profile files.</summary>
public sealed record RecommendedPatch(
    Dictionary<string, VehiclePatchEntry> Vehicles,
    Dictionary<string, double> Parameters);

public sealed record VehiclePatchEntry(
    string Id,
    double? LatFrictionK,
    double? AngDamping);

/// <summary>
/// Full calibration report. <see cref="ContentSha256"/> covers every field
/// except <see cref="GeneratedAt"/> so the same input always yields the same
/// fingerprint (PRD AC3).
/// </summary>
public sealed record CalibrationReport
{
    public int SchemaVersion { get; init; } = 1;
    public string? GeneratedAt { get; init; }
    public string? ContentSha256 { get; init; }
    public string ToolVersion { get; init; } = $"sim-core-{MatchEngine.CoreVersion}+calibration-v1";
    public required TelemetrySummary Telemetry { get; init; }
    public required Dictionary<string, SubsystemFit> Fits { get; init; }
    public MountEvaluation? Mount { get; init; }
    public required RecommendedPatch RecommendedPatch { get; init; }
    public required FidelityEligibility Eligibility { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed record TelemetrySummary(
    string Sha256,
    string Source,
    string Date,
    string VehicleId,
    Dictionary<string, int> TrialCounts,
    Dictionary<string, int> HoldoutCounts);

public sealed record FidelityEligibility(bool Friction, bool Collision, bool Stall, bool Mount);

/// <summary>
/// Runs the whole pipeline on a VALIDATED telemetry file (the caller must
/// <see cref="TelemetryFile.Validate"/> first). Deterministic: no clocks, no
/// randomness, no IO; <c>generatedAt</c> and the input SHA are supplied by
/// the caller so identical content hashes identically.
/// </summary>
public static class Calibrator
{
    public static CalibrationReport Calibrate(
        TelemetryFile telemetry,
        string inputSha256,
        string? generatedAt = null,
        double mountVMin = 0.3,
        double mountAngleMax = 0.26)
    {
        var byKind = telemetry.Trials.GroupBy(t => KindKey(t.Kind))
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Id, StringComparer.Ordinal).ToList());
        var counts = byKind.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var holdoutCounts = byKind.ToDictionary(
            kv => kv.Key, kv => kv.Value.Count(t => t.Set == "holdout"));

        var lateralFit = FitCoast(byKind, TelemetryTrialKind.LateralCoast, CalibrationTargets.ExponentialHoldoutRmse);
        var angularFit = FitCoast(byKind, TelemetryTrialKind.AngularCoast, CalibrationTargets.ExponentialHoldoutRmse);
        var blockFit = FitBlock(byKind);
        var collisionFit = FitCollision(byKind);
        var stallFit = FitStall(byKind);

        var mountTrials = byKind.TryGetValue("mount", out var mountList)
            ? mountList
                .Where(t => t.Approach is not null && t.Outcome is not null)
                .Select(t => (new MountSample(t.Approach!.Vn!.Value, t.Approach!.Vt!.Value, t.Outcome!.Value), t.Set))
                .ToList()
            : [];
        var mount = MountEvaluator.Evaluate(mountTrials, mountVMin, mountAngleMax);
        var mountEligible = mount.Reason is null
            && telemetry.Capture.Source == "real";

        var real = telemetry.Capture.Source == "real";
        var friction = lateralFit.Eligible && blockFit.Eligible && real;
        var collision = angularFit.Eligible && collisionFit.Eligible && real;
        var stall = stallFit.Eligible && real;

        // Data-side eligible but source not real: record why promotion is blocked.
        static SubsystemFit MarkSource(SubsystemFit fit, bool real)
            => !fit.Eligible || real
                ? fit
                : fit with { Eligible = false, Reason = "合成/自测数据不得晋升 fidelity (capture.source != real)" };
        lateralFit = MarkSource(lateralFit, real);
        angularFit = MarkSource(angularFit, real);
        blockFit = MarkSource(blockFit, real);
        collisionFit = MarkSource(collisionFit, real);
        stallFit = MarkSource(stallFit, real);

        var vehicles = new Dictionary<string, VehiclePatchEntry>();
        var anyVehicle = lateralFit.Eligible || angularFit.Eligible;
        if (anyVehicle)
        {
            vehicles["us"] = new VehiclePatchEntry(
                telemetry.Vehicle.Id,
                lateralFit.Eligible ? lateralFit.Value : null,
                angularFit.Eligible ? angularFit.Value : null);
        }
        var parameters = new Dictionary<string, double>();
        if (blockFit.Eligible)
        {
            parameters["BLOCK_MU_K"] = blockFit.Value!.Value;
        }
        if (collisionFit.Eligible)
        {
            parameters["COLLISION_RESTITUTION"] = collisionFit.Value!.Value;
        }
        if (stallFit.Eligible)
        {
            parameters["STALL_SPEED"] = stallFit.Value!.Value;
        }

        var report = new CalibrationReport
        {
            GeneratedAt = generatedAt,
            Telemetry = new TelemetrySummary(
                inputSha256, telemetry.Capture.Source, telemetry.Capture.Date,
                telemetry.Vehicle.Id, counts, holdoutCounts),
            Fits = new Dictionary<string, SubsystemFit>
            {
                ["latFrictionK"] = lateralFit,
                ["angDamping"] = angularFit,
                ["BLOCK_MU_K"] = blockFit,
                ["COLLISION_RESTITUTION"] = collisionFit,
                ["STALL_SPEED"] = stallFit,
            },
            Mount = mount,
            RecommendedPatch = new RecommendedPatch(vehicles, parameters),
            Eligibility = new FidelityEligibility(friction, collision, stall, mountEligible),
            Limitations =
            [
                "仅决策逻辑仿真参数建议; 需固定 seed 回归与真机复测后方可用于策略结论。",
                "合成/自测数据可验证拟合器, 但 capture.source != real 时永不晋升 fidelity。",
                "mount 门控只评估不拟合; 误差超标时如实报告模型不足。",
            ],
        };
        return report;
    }

    // ---------- per-kind fit + holdout scoring ----------

    /// <summary>Wire-style kind key (snake_case), matching the telemetry JSON.</summary>
    private static string KindKey(TelemetryTrialKind kind) => kind switch
    {
        TelemetryTrialKind.LateralCoast => "lateral_coast",
        TelemetryTrialKind.AngularCoast => "angular_coast",
        TelemetryTrialKind.BlockPush => "block_push",
        TelemetryTrialKind.Collision => "collision",
        TelemetryTrialKind.Stall => "stall",
        TelemetryTrialKind.Mount => "mount",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static SubsystemFit FitCoast(
        Dictionary<string, List<TelemetryTrial>> byKind, TelemetryTrialKind kind, double target)
    {
        var key = KindKey(kind);
        var trials = byKind.TryGetValue(key, out var list) ? list : [];
        var (fit, holdout) = Split(trials, t => TelemetryDecomposer.CoastPairs(t));
        var result = Fitters.FitExponentialDecay(fit, key);
        if (!result.Calibrated)
        {
            return Unavailable(result, target);
        }
        var holdRmse = holdout.Count >= 1
            ? Fitters.ExponentialRmse(holdout, result.Value!.Value)
            : double.NaN;
        var ok = holdout.Count >= 1 && double.IsFinite(holdRmse) && holdRmse <= target;
        return new SubsystemFit(true, result.Value, fit.Count, result.Rmse, holdout.Count,
            double.IsFinite(holdRmse) ? CalibrationMath.Round6(holdRmse) : null, null, target, ok,
            result.Method, ok ? null : (holdout.Count < 1 ? "缺少 holdout 试验" : $"holdout RMSE 超出目标"));
    }

    private static SubsystemFit FitBlock(Dictionary<string, List<TelemetryTrial>> byKind)
    {
        var trials = byKind.TryGetValue("block_push", out var list) ? list : [];
        var (fit, holdout) = Split(trials, t => TelemetryDecomposer.BlockPairs(t));
        var result = Fitters.FitBlockFriction(fit);
        if (!result.Calibrated)
        {
            return Unavailable(result, CalibrationTargets.BlockHoldoutRmse);
        }
        var holdRmse = holdout.Count >= 1 ? Fitters.BlockRmse(holdout, result.Value!.Value) : double.NaN;
        var ok = holdout.Count >= 1 && double.IsFinite(holdRmse) && holdRmse <= CalibrationTargets.BlockHoldoutRmse;
        return new SubsystemFit(true, result.Value, fit.Count, result.Rmse, holdout.Count,
            double.IsFinite(holdRmse) ? CalibrationMath.Round6(holdRmse) : null, null,
            CalibrationTargets.BlockHoldoutRmse, ok, result.Method, ok ? null : "holdout 数据不足或误差超标");
    }

    private static SubsystemFit FitCollision(Dictionary<string, List<TelemetryTrial>> byKind)
    {
        var trials = byKind.TryGetValue("collision", out var list) ? list : [];
        var (fit, holdout) = Split(trials, t =>
        {
            var s = TelemetryDecomposer.CollisionSampleOf(t);
            return s is null ? new List<CollisionSample>() : new List<CollisionSample> { s.Value };
        });
        var result = Fitters.FitRestitution(fit);
        if (!result.Calibrated)
        {
            return Unavailable(result, CalibrationTargets.RestitutionHoldoutRmse);
        }
        var holdRmse = holdout.Count >= 1
            ? Fitters.RestitutionRmse(holdout, result.Value!.Value)
            : double.NaN;
        var ok = holdout.Count >= 1 && double.IsFinite(holdRmse) && holdRmse <= CalibrationTargets.RestitutionHoldoutRmse;
        return new SubsystemFit(true, result.Value, fit.Count, result.Rmse, holdout.Count,
            double.IsFinite(holdRmse) ? CalibrationMath.Round6(holdRmse) : null, null,
            CalibrationTargets.RestitutionHoldoutRmse, ok, result.Method, ok ? null : "holdout 数据不足或误差超标");
    }

    private static SubsystemFit FitStall(Dictionary<string, List<TelemetryTrial>> byKind)
    {
        var trials = byKind.TryGetValue("stall", out var list) ? list : [];
        var (fit, holdout) = Split(trials, t => TelemetryDecomposer.StallSamples(t));
        var result = Fitters.FitStallThreshold(fit);
        if (!result.Calibrated)
        {
            return Unavailable(result, CalibrationTargets.StallHoldoutAccuracy);
        }
        double? accuracy = null;
        var ok = false;
        if (holdout.Count >= CalibrationTargets.MinHoldoutStallSamples)
        {
            var (rmse, acc) = Fitters.EvaluateStall(holdout, result.Value!.Value);
            accuracy = CalibrationMath.Round6(acc);
            ok = acc >= CalibrationTargets.StallHoldoutAccuracy;
        }
        return new SubsystemFit(true, result.Value, fit.Count, result.Rmse, holdout.Count, null, accuracy,
            CalibrationTargets.StallHoldoutAccuracy, ok, result.Method,
            ok ? null : "holdout 标签样本不足或准确率低于目标");
    }

    private static SubsystemFit Unavailable(FitResult result, double target)
        => new(false, null, result.Samples, null, 0, null, null, target, false, result.Method, result.Reason);

    private static (List<T> Fit, List<T> Holdout) Split<T>(
        List<TelemetryTrial> trials, Func<TelemetryTrial, List<T>> project)
    {
        var fit = new List<T>();
        var holdout = new List<T>();
        foreach (var trial in trials)
        {
            var items = project(trial);
            if (trial.Set == "holdout")
            {
                holdout.AddRange(items);
            }
            else
            {
                fit.AddRange(items);
            }
        }
        return (fit, holdout);
    }
}
