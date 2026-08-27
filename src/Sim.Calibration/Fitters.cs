namespace Sim.Calibration;

/// <summary>
/// Parameter fitters ported from the legacy sim_calibrate.js, operating on
/// pre-decomposed sample lists so the numerics are unit-testable in isolation.
/// Model shapes match the kernel exactly (see Sim.Core PhysicsWorld constants).
/// </summary>
public static class Fitters
{
    /// <summary>Legacy minimum usable sample counts per fit family.</summary>
    public const int MinExponentialSamples = 4;
    public const int MinBlockSamples = 4;
    public const int MinRestitutionSamples = 3;
    public const int MinStallSamples = 6;

    /// <summary>
    /// Least-squares exponential decay: minimises Σ(logRatio + k·dt)²,
    /// i.e. k = -Σ dt·logRatio / Σ dt².
    /// </summary>
    public static FitResult FitExponentialDecay(IReadOnlyList<ExponentialPair> pairs, string methodLabel)
    {
        if (pairs.Count < MinExponentialSamples)
        {
            return FitResult.Insufficient("样本不足", pairs.Count, MinExponentialSamples);
        }
        var denominator = pairs.Sum(p => p.Dt * p.Dt);
        if (!(denominator > 1e-9))
        {
            return FitResult.Insufficient("有效时间跨度不足", pairs.Count, MinExponentialSamples);
        }
        var value = -pairs.Sum(p => p.Dt * p.LogRatio) / denominator;
        var rmse = ExponentialRmse(pairs, value);
        return FitResult.Ok(value, pairs.Count, rmse, $"最小二乘: log(|v(t+dt)|/|v(t)|) = -k·dt [{methodLabel}]");
    }

    /// <summary>RMSE of the log-ratio residuals for a fixed decay constant (holdout scoring).</summary>
    public static double ExponentialRmse(IReadOnlyList<ExponentialPair> pairs, double k)
        => CalibrationMath.Rmse(pairs.Select(p => p.LogRatio + k * p.Dt).ToList());

    /// <summary>
    /// Bounded 1-D least squares for the block friction model
    /// v' = max(0, v·exp(-damping·dt) - mu·g·dt) (identical to the kernel step).
    /// </summary>
    public static FitResult FitBlockFriction(IReadOnlyList<BlockPair> pairs)
    {
        if (pairs.Count < MinBlockSamples)
        {
            return FitResult.Insufficient("样本不足", pairs.Count, MinBlockSamples);
        }
        var value = CalibrationMath.MinimiseBounded(mu => BlockLoss(pairs, mu), 0.01, 3.0);
        var rmse = BlockRmse(pairs, value);
        return FitResult.Ok(value, pairs.Count, rmse,
            $"一维最小二乘: v′=max(0,v·exp(-{Sim.Core.PhysicsWorld.BlockLinearDamping}dt)-μgdt)");
    }

    public static double PredictBlockSpeed(BlockPair pair, double mu)
        => Math.Max(0, pair.Before * Math.Exp(-Sim.Core.PhysicsWorld.BlockLinearDamping * pair.Dt)
            - mu * Sim.Core.PhysicsWorld.Gravity * pair.Dt);

    public static double BlockRmse(IReadOnlyList<BlockPair> pairs, double mu)
        => CalibrationMath.Rmse(pairs.Select(p => p.After - PredictBlockSpeed(p, mu)).ToList());

    private static double BlockLoss(IReadOnlyList<BlockPair> pairs, double mu)
        => pairs.Sum(p => Math.Pow(p.After - PredictBlockSpeed(p, mu), 2));

    /// <summary>
    /// Restitution least squares over pairs with before&gt;0.05 and after≤0:
    /// e = clamp(-Σ before·after / Σ before², 0, 0.9).
    /// </summary>
    public static FitResult FitRestitution(IReadOnlyList<CollisionSample> samples)
    {
        var usable = samples.Where(s => s.Before > 0.05 && s.After <= 0).ToList();
        if (usable.Count < MinRestitutionSamples)
        {
            return FitResult.Insufficient("样本不足或缺失入射法线", usable.Count, MinRestitutionSamples);
        }
        var denominator = usable.Sum(s => s.Before * s.Before);
        if (!(denominator > 1e-12))
        {
            return FitResult.Insufficient("有效时间跨度不足", usable.Count, MinRestitutionSamples);
        }
        var value = CalibrationMath.Clamp(
            -usable.Sum(s => s.Before * s.After) / denominator, 0, 0.9);
        var rmse = RestitutionRmse(usable, value);
        return FitResult.Ok(value, usable.Count, rmse, "最小二乘: v_rel,after = -e·v_rel,before");
    }

    public static double RestitutionRmse(IReadOnlyList<CollisionSample> samples, double e)
        => CalibrationMath.Rmse(samples.Select(s => s.After + e * s.Before).ToList());

    /// <summary>
    /// Stall threshold: classifies isStalled ≈ (speed ≤ threshold) over candidate
    /// thresholds {0, values, midpoints}; ties prefer the lower threshold.
    /// Requires both positive and negative labels.
    /// </summary>
    public static FitResult FitStallThreshold(IReadOnlyList<StallSample> samples)
    {
        var positives = samples.Count(s => s.Stalled);
        var negatives = samples.Count - positives;
        if (samples.Count < MinStallSamples || positives == 0 || negatives == 0)
        {
            return FitResult.Insufficient("需要至少 6 个带 commanded/stalled 正反标签的速度样本", samples.Count, MinStallSamples);
        }
        var values = samples.Select(s => s.Speed).Distinct().OrderBy(v => v).ToList();
        var candidates = new List<double> { 0 };
        candidates.AddRange(values);
        for (var i = 0; i < values.Count - 1; i++)
        {
            candidates.Add((values[i] + values[i + 1]) / 2);
        }
        var best = (threshold: 0.0, squaredError: double.PositiveInfinity);
        foreach (var threshold in candidates)
        {
            var squaredError = samples.Sum(s => Math.Pow(
                (s.Speed <= threshold ? 1 : 0) - (s.Stalled ? 1 : 0), 2));
            if (squaredError < best.squaredError
                || (squaredError == best.squaredError && threshold < best.threshold))
            {
                best = (threshold, squaredError);
            }
        }
        var accuracy = 1 - best.squaredError / samples.Count;
        var rmse = Math.Sqrt(best.squaredError / samples.Count);
        return FitResult.Ok(best.threshold, samples.Count, rmse,
            "阈值二分类最小二乘: isStalled≈[speed≤STALL_SPEED]", CalibrationMath.Round6(accuracy));
    }

    /// <summary>Holdout classification metrics for a fixed stall threshold.</summary>
    public static (double Rmse, double Accuracy) EvaluateStall(IReadOnlyList<StallSample> samples, double threshold)
    {
        if (samples.Count == 0)
        {
            return (double.NaN, 0);
        }
        var squaredError = samples.Sum(s => Math.Pow(
            (s.Speed <= threshold ? 1 : 0) - (s.Stalled ? 1 : 0), 2));
        return (Math.Sqrt(squaredError / samples.Count), 1 - squaredError / samples.Count);
    }
}
