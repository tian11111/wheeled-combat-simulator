using Sim.Protocol;

namespace Sim.Calibration;

/// <summary>
/// Pure, deterministic re-implementations of the MBri decision models
/// (gray.py GrayRiskModel, ir.py IrDirectionModel, shovel_guard.py
/// ShovelGuard hang/clear signal) for raw-log replay gating.
/// Median semantics follow Python statistics.median (even counts average the
/// two middle values). No clock, no IO, no randomness.
/// </summary>
public static class SensorReplay
{
    /// <summary>Python-compatible median over the sorted buffer.</summary>
    public static double Median(List<double> buffer)
    {
        if (buffer.Count == 0)
        {
            return 0;
        }
        var sorted = buffer.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // ---------- gray ----------

    /// <summary>Gray raw row: t + four channels (missing → invalid).</summary>
    public sealed record GrayRow(double T, double[] Channels, bool Valid);

    public sealed record GrayReplayResult(
        int TotalRows, int InvalidRows, int ReadyRows,
        double ZoneP001, double ZoneP50, double ZoneP999,
        int NearEdgeAsserts, int WhiteHitRows,
        double? FirstT, double? LastT);

    /// <summary>
    /// Replays gray rows through the imported channel model and returns the
    /// zone-score distribution/hysteresis statistics used for drift reporting.
    /// Mirrors GrayRiskModel.update including the "invalid sample still enters
    /// the buffer as 0" behavior.
    /// </summary>
    public static GrayReplayResult ReplayGray(
        GrayModelData model, IReadOnlyList<GrayRow> rows, double adcMax = 10000.0)
    {
        var channels = model.Channels.ToDictionary(c => c.Sensor);
        var names = new[] { "front", "rear", "left", "right" };
        var buffers = names.ToDictionary(n => n, n => new List<double>());
        var zoneScoreSamples = new List<double>();
        var invalid = 0;
        var ready = 0;
        var nearEdge = 0;
        var whiteHits = 0;
        double? firstT = null, lastT = null;
        foreach (var row in rows)
        {
            if (firstT is null)
            {
                firstT = row.T;
            }
            lastT = row.T;
            var rowOk = row.Valid;
            var cleaned = new Dictionary<string, double>();
            foreach (var (name, value) in names.Zip(row.Channels))
            {
                if (!double.IsFinite(value) || value < 0 || value > adcMax)
                {
                    cleaned[name] = 0.0;
                    rowOk = false;
                }
                else
                {
                    cleaned[name] = value;
                }
            }
            if (!rowOk)
            {
                invalid++;
            }
            foreach (var name in names)
            {
                var buffer = buffers[name];
                buffer.Add(cleaned[name]);
                var window = channels[name].FilterWindow;
                if (buffer.Count > window)
                {
                    buffer.RemoveAt(0);
                }
            }
            if (names.Any(n => buffers[n].Count < channels[n].FilterWindow))
            {
                continue;
            }
            ready++;
            var filtered = names.ToDictionary(n => n, n => Median(buffers[n]));
            var zone = names.ToDictionary(n => n, n =>
            {
                var c = channels[n];
                var span = c.CenterReference - c.EdgeReference;
                return span == 0 ? 0.0 : (filtered[n] - c.EdgeReference) / span;
            });
            var zoneScore = Median(zone.Values.ToList());
            zoneScoreSamples.Add(zoneScore);
            var enter = channels[names[0]].NearEdgeEnter;
            if (zoneScore < enter)
            {
                nearEdge++;
            }
            if (names.Any(n => filtered[n] >= channels[n].WhiteEnter))
            {
                whiteHits++;
            }
        }
        var sortedZones = zoneScoreSamples.OrderBy(v => v).ToList();
        return new GrayReplayResult(
            rows.Count, invalid, ready,
            Percentile(sortedZones, 0.001), Percentile(sortedZones, 0.5), Percentile(sortedZones, 0.999),
            nearEdge, whiteHits, firstT, lastT);
    }

    // ---------- front ADC ----------

    public sealed record AdcRow(double T, double Left, double Right, bool Valid);

    public sealed record AdcReplayResult(
        int TotalRows, int InvalidRows, int ReadyRows, int LabeledRows, int Mismatches,
        Dictionary<string, int> DirectionCounts,
        double DiffP05, double DiffP50, double DiffP95,
        double RawDiffP05, double RawDiffP95,
        double SignalP01, double SignalP50, double SignalP99,
        double? FirstT, double? LastT);

    /// <summary>
    /// Replays the STORED band model (imported): valid flag + range check
    /// resets the buffers (MBri "fail-safe 断线"); per-row decision uses the
    /// filtered diff against [DiffLow, DiffHigh] with the signal floor, where
    /// diff < low means 车头左偏 ("left") per the model CSV semantics.
    /// When the model carries a config ratio threshold, rows where the two
    /// decision models disagree are counted as band_ratio_disagree evidence.
    /// Expected direction labels (per selected file) feed the mismatch metric.
    /// </summary>
    public static AdcReplayResult ReplayFrontAdc(
        FrontAdcModel model, IReadOnlyList<AdcRow> rows, string expectedDirection,
        double adcMax = 10000.0)
    {
        var leftBuf = new List<double>();
        var rightBuf = new List<double>();
        var diffs = new List<double>();
        var rawDiffs = new List<double>();
        var rawSignals = new List<double>();
        var signals = new List<double>();
        var counts = new Dictionary<string, int> { ["left"] = 0, ["forward"] = 0, ["right"] = 0 };
        var invalid = 0;
        var ready = 0;
        var labeled = 0;
        var mismatches = 0;
        double? firstT = null, lastT = null;
        foreach (var row in rows)
        {
            if (firstT is null)
            {
                firstT = row.T;
            }
            lastT = row.T;
            var valid = row.Valid
                && double.IsFinite(row.Left) && double.IsFinite(row.Right)
                && row.Left >= 0 && row.Left <= adcMax && row.Right >= 0 && row.Right <= adcMax;
            if (!valid)
            {
                invalid++;
                leftBuf.Clear();
                rightBuf.Clear();
                continue;
            }
            rawDiffs.Add(row.Left - row.Right);
            rawSignals.Add(Math.Max(row.Left, row.Right));
            leftBuf.Add(row.Left);
            rightBuf.Add(row.Right);
            if (leftBuf.Count > model.FilterWindow)
            {
                leftBuf.RemoveAt(0);
                rightBuf.RemoveAt(0);
            }
            if (leftBuf.Count < model.FilterWindow)
            {
                continue;
            }
            ready++;
            var fl = Median(leftBuf);
            var fr = Median(rightBuf);
            var signal = Math.Max(fl, fr);
            var total = fl + fr;
            var ratio = total > 0 ? (fl - fr) / total : 0.0;
            var diff = fl - fr;
            diffs.Add(diff);
            signals.Add(signal);
            var direction = signal < model.SignalMin
                ? "forward"
                : diff < model.DiffLow
                    ? "left"
                    : diff > model.DiffHigh ? "right" : "forward";
            counts[direction]++;
            if (model.RatioThreshold is { } rt)
            {
                var ratioDir = signal < model.SignalMin
                    ? "forward"
                    : ratio > rt ? "left" : ratio < -rt ? "right" : "forward";
                if (ratioDir != direction)
                {
                    counts.TryGetValue("band_ratio_disagree", out var dis);
                    counts["band_ratio_disagree"] = dis + 1;
                }
            }
            labeled++;
            if (direction != expectedDirection)
            {
                mismatches++;
            }
        }
        var sortedDiffs = diffs.OrderBy(v => v).ToList();
        var sortedRawDiffs = rawDiffs.OrderBy(v => v).ToList();
        var sortedSignals = signals.OrderBy(v => v).ToList();
        var sortedRawSignals = rawSignals.OrderBy(v => v).ToList();
        return new AdcReplayResult(rows.Count, invalid, ready, labeled, mismatches, counts,
            Percentile(sortedDiffs, 0.05), Percentile(sortedDiffs, 0.5), Percentile(sortedDiffs, 0.95),
            Percentile(sortedRawDiffs, 0.05), Percentile(sortedRawDiffs, 0.95),
            Percentile(sortedSignals, 0.01), Percentile(sortedSignals, 0.5), Percentile(sortedSignals, 0.99),
            firstT, lastT);
    }

    // ---------- shovel ----------

    public sealed record ShovelRow(double T, double Left, double Right, bool Valid);

    public sealed record ShovelReplayResult(
        int TotalRows, int InvalidRows, int ReadyRows,
        int HangAsserts, int HangTransitions, int ClearTransitions,
        double MinP01, double MinP99, double MaxP01, double MaxP99,
        double? FirstT, double? LastT);

    /// <summary>
    /// Replays ShovelGuard's filtered hang signal (enter = filtered-min above
    /// HangEnter, clear = filtered-max below HangClear) and its transitions,
    /// without motor commands or wall-clock timing (state-machine timing is
    /// out of sensor-evidence scope).
    /// </summary>
    public static ShovelReplayResult ReplayShovel(
        ShovelModel model, IReadOnlyList<ShovelRow> rows, double adcMax = 10000.0)
    {
        var minBuf = new List<double>();
        var maxBuf = new List<double>();
        var mins = new List<double>();
        var maxs = new List<double>();
        var invalid = 0;
        var ready = 0;
        var hang = 0;
        var transitions = 0;
        var clears = 0;
        var wasHang = false;
        double? firstT = null, lastT = null;
        foreach (var row in rows)
        {
            if (firstT is null)
            {
                firstT = row.T;
            }
            lastT = row.T;
            if (!row.Valid || !double.IsFinite(row.Left) || !double.IsFinite(row.Right)
                || row.Left < 0 || row.Left > adcMax || row.Right < 0 || row.Right > adcMax)
            {
                invalid++;
                minBuf.Clear();
                maxBuf.Clear();
                wasHang = false;
                continue;
            }
            minBuf.Add(Math.Min(row.Left, row.Right));
            maxBuf.Add(Math.Max(row.Left, row.Right));
            if (minBuf.Count > model.FilterWindow)
            {
                minBuf.RemoveAt(0);
                maxBuf.RemoveAt(0);
            }
            if (minBuf.Count < model.FilterWindow)
            {
                continue;
            }
            ready++;
            var fmin = Median(minBuf);
            var fmax = Median(maxBuf);
            mins.Add(fmin);
            maxs.Add(fmax);
            var nowHang = fmin > model.HangEnter;
            if (nowHang)
            {
                hang++;
                if (!wasHang)
                {
                    transitions++;
                }
            }
            else if (wasHang && fmax < model.HangClear)
            {
                clears++;
            }
            wasHang = nowHang;
        }
        var sortedMins = mins.OrderBy(v => v).ToList();
        var sortedMaxs = maxs.OrderBy(v => v).ToList();
        return new ShovelReplayResult(rows.Count, invalid, ready, hang, transitions, clears,
            Percentile(sortedMins, 0.01), Percentile(sortedMins, 0.99),
            Percentile(sortedMaxs, 0.01), Percentile(sortedMaxs, 0.99), firstT, lastT);
    }

    /// <summary>Linear-interpolation percentile on a sorted list (documented method).</summary>
    public static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }
        if (sorted.Count == 1)
        {
            return sorted[0];
        }
        var idx = (sorted.Count - 1) * p;
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        var frac = idx - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}
