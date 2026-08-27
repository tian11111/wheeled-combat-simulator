namespace Sim.Calibration;

/// <summary>
/// Agreed holdout error targets and coverage requirements per subsystem
/// (PRD R3/R4). Promotion of a fidelity subsystem requires ALL of: a
/// successful fit, holdout samples meeting the per-kind minimum, the holdout
/// metric within target, and capture source "real" (synthetic never promotes).
/// </summary>
public static class CalibrationTargets
{
    /// <summary>Holdout log-ratio residual RMSE bound for coast decays.</summary>
    public const double ExponentialHoldoutRmse = 0.05;

    /// <summary>Holdout speed residual RMSE bound (m/s) for the block friction model.</summary>
    public const double BlockHoldoutRmse = 0.05;

    /// <summary>Holdout relative-normal-velocity RMSE bound (m/s) for restitution.</summary>
    public const double RestitutionHoldoutRmse = 0.05;

    /// <summary>Holdout classification accuracy floor for the stall threshold.</summary>
    public const double StallHoldoutAccuracy = 0.95;

    /// <summary>Holdout misclassification ceiling for the mount gate.</summary>
    public const double MountHoldoutMaxErrorRate = 0.10;

    /// <summary>Holdout trials required per subsystem kind.</summary>
    public const int MinHoldoutTrialsPerKind = 2;

    /// <summary>Holdout samples required per subsystem (fall back to trial minima).</summary>
    public const int MinHoldoutStallSamples = 6;

    /// <summary>Mount validation: holdout trials, both outcomes, bucket coverage.</summary>
    public const int MinMountHoldoutTrials = 12;
    public const int MinMountBucketsWithTrials = 3;
    public const int MinMountTrialsPerCoveredBucket = 2;
}

/// <summary>One speed×angle bucket in the mount confusion matrix.</summary>
public sealed record MountBucket(
    string SpeedBand,
    string AngleBand,
    int FitTrials,
    int HoldoutTrials,
    int Correct,
    int Misclassified);

/// <summary>Full mount evaluation: prediction correctness split fit/holdout + coverage.</summary>
public sealed record MountEvaluation(
    int FitTrials,
    int HoldoutTrials,
    int FitCorrect,
    int HoldoutCorrect,
    int HoldoutPositives,
    int HoldoutNegatives,
    double? HoldoutErrorRate,
    IReadOnlyList<MountBucket> Buckets,
    string? Reason);

/// <summary>
/// Evaluates the deterministic kernel mount gate against measured mount trials.
/// The gate is NOT fitted — mismatch above target is reported as model
/// insufficiency (PRD R4), keeping mount uncalibrated rather than overfitting.
/// </summary>
public static class MountEvaluator
{
    /// <summary>Exactly mirrors PhysicsWorld.StageWall single-wall acceptance.</summary>
    public static bool PredictAccepted(double vn, double vt, double mountVMin, double mountAngleMax)
        => vn > mountVMin && Math.Abs(vt) < vn * Math.Tan(mountAngleMax);

    private static readonly (double Lo, double Hi, string Label)[] SpeedBands =
    [
        (0.0, 0.3, "<0.30"),
        (0.3, 0.5, "0.30-0.50"),
        (0.5, 0.75, "0.50-0.75"),
        (0.75, 1.0, "0.75-1.00"),
        (1.0, double.PositiveInfinity, ">=1.00"),
    ];

    private static readonly (double DegLo, double DegHi, string Label)[] AngleBands =
    [
        (0.0, 10.0, "<=10deg"),
        (10.0, 15.0, "10-15deg"),
        (15.0, 20.0, "15-20deg"),
        (20.0, 25.0, "20-25deg"),
        (25.0, double.PositiveInfinity, ">25deg"),
    ];

    public static MountBucketKey BucketOf(double vn, double vt)
    {
        var angleDeg = Math.Abs(Math.Atan2(vt, Math.Max(0.0, vn))) * 180.0 / Math.PI;
        var speed = SpeedBands.First(b => vn >= b.Lo && vn < b.Hi).Label;
        var angle = AngleBands.First(b => angleDeg >= b.DegLo && angleDeg < b.DegHi).Label;
        return new MountBucketKey(speed, angle);
    }

    public readonly record struct MountBucketKey(string Speed, string Angle);

    /// <summary>
    /// Splits mount samples by trial set, predicts acceptance with the current
    /// gate parameters and builds the bucketed confusion matrix.
    /// </summary>
    public static MountEvaluation Evaluate(
        IReadOnlyList<(MountSample Sample, string Set)> trials,
        double mountVMin,
        double mountAngleMax)
    {
        var holdout = trials.Where(t => t.Set == "holdout").Select(t => t.Sample).ToList();
        var fit = trials.Where(t => t.Set == "fit").Select(t => t.Sample).ToList();
        var buckets = new Dictionary<MountBucketKey, (int Fit, int Holdout, int Correct, int Wrong)>();
        foreach (var (sample, set) in trials.OrderBy(t => t.Sample.Vn).ThenBy(t => t.Sample.Vt))
        {
            var key = BucketOf(sample.Vn, sample.Vt);
            var predicted = PredictAccepted(sample.Vn, sample.Vt, mountVMin, mountAngleMax);
            var correct = predicted == sample.Outcome;
            var current = buckets.GetValueOrDefault(key);
            buckets[key] = set == "holdout"
                ? (current.Fit, current.Holdout + 1, current.Correct + (correct ? 1 : 0), current.Wrong + (correct ? 0 : 1))
                : (current.Fit + 1, current.Holdout, current.Correct + (correct ? 1 : 0), current.Wrong + (correct ? 0 : 1));
        }
        var matrix = buckets
            .OrderBy(kv => kv.Key.Speed).ThenBy(kv => kv.Key.Angle)
            .Select(kv => new MountBucket(kv.Key.Speed, kv.Key.Angle, kv.Value.Fit, kv.Value.Holdout, kv.Value.Correct, kv.Value.Wrong))
            .ToList();

        var holdoutPositives = holdout.Count(s => s.Outcome);
        var holdoutNegatives = holdout.Count - holdoutPositives;
        double? errorRate = holdout.Count > 0
            ? CalibrationMath.Round6((double)holdout.Count(s =>
                PredictAccepted(s.Vn, s.Vt, mountVMin, mountAngleMax) != s.Outcome) / holdout.Count)
            : null;
        string? reason = null;
        if (holdout.Count < CalibrationTargets.MinMountHoldoutTrials)
        {
            reason = $"holdout mount 试验不足 ({holdout.Count} < {CalibrationTargets.MinMountHoldoutTrials})";
        }
        else if (holdoutPositives == 0 || holdoutNegatives == 0)
        {
            reason = "holdout mount 试验必须同时覆盖成功与失败样本";
        }
        else
        {
            var covered = matrix.Count(b => b.HoldoutTrials >= CalibrationTargets.MinMountTrialsPerCoveredBucket);
            if (covered < CalibrationTargets.MinMountBucketsWithTrials)
            {
                reason = $"速度/角度分桶覆盖不足 ({covered} 个达标桶 < {CalibrationTargets.MinMountBucketsWithTrials})";
            }
            else if (errorRate > CalibrationTargets.MountHoldoutMaxErrorRate)
            {
                reason = $"mount 模型误差超标 (留出错误率 {errorRate:P1} > {CalibrationTargets.MountHoldoutMaxErrorRate:P0}) — 确定性门控不足，保持未标定";
            }
        }
        return new MountEvaluation(
            fit.Count, holdout.Count,
            fit.Count(s => PredictAccepted(s.Vn, s.Vt, mountVMin, mountAngleMax) == s.Outcome),
            holdout.Count(s => PredictAccepted(s.Vn, s.Vt, mountVMin, mountAngleMax) == s.Outcome),
            holdoutPositives, holdoutNegatives,
            errorRate, matrix, reason);
    }
}
