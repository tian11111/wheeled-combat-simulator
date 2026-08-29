namespace Sim.VisionReplay;

/// <summary>
/// Layer 1 — link quality metrics over normalized frames. Pure functions:
/// no clock, no IO, no randomness. Median/percentile semantics match Python
/// statistics (median = midpoint of the two middle values, percentile with
/// linear interpolation) so the numbers can be cross-checked offline.
/// </summary>
public static class VisionLinkMetrics
{
    public static VisionLinkQuality Compute(IReadOnlyList<VisionFrameRecord> frames)
    {
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            statusCounts[frame.Status] = statusCounts.GetValueOrDefault(frame.Status) + 1;
        }
        var total = frames.Count;

        var gaps = new Dictionary<long, int>();
        double? firstValidDetectionMs = null;
        foreach (var sessionFrames in GroupBySession(frames))
        {
            for (var i = 1; i < sessionFrames.Count; i++)
            {
                var gap = sessionFrames[i].Sequence - sessionFrames[i - 1].Sequence;
                gaps[gap] = gaps.GetValueOrDefault(gap) + 1;
            }
            var sessionStart = sessionFrames[0].TimestampMs;
            var first = sessionFrames.FirstOrDefault(f
                => f.Status == "target" && f.SelectedTargetIndex is not null);
            if (first is not null)
            {
                var relative = first.TimestampMs - sessionStart;
                firstValidDetectionMs = firstValidDetectionMs is { } current
                    ? Math.Min(current, relative)
                    : relative;
            }
        }

        var retention = TargetRetention(frames);
        var flips = SelectionFlips(frames);
        return new VisionLinkQuality
        {
            Sessions = GroupBySession(frames).Count,
            Frames = total,
            StatusCounts = statusCounts,
            ValidRate = total == 0 ? 0 : statusCounts.GetValueOrDefault("target") / (double)total,
            NoTargetRate = total == 0 ? 0 : statusCounts.GetValueOrDefault("no_target") / (double)total,
            ErrorRate = total == 0 ? 0 : statusCounts.GetValueOrDefault("error") / (double)total,
            NoDataOrStaleRate = total == 0 ? 0 : statusCounts.GetValueOrDefault("no_data_or_stale") / (double)total,
            SequenceGapHistogram = gaps,
            Fps = Distribution(frames.Where(f => f.Fps is { }).Select(f => f.Fps!.Value).ToList()),
            InferenceMs = Distribution(frames.Where(f => f.InferenceMs is { }).Select(f => f.InferenceMs!.Value).ToList()),
            TargetRetention = retention,
            SelectionFlips = flips,
            FirstValidDetectionMs = firstValidDetectionMs,
        };
    }

    /// <summary>Frames grouped by session in file order; order inside each session is preserved.</summary>
    public static List<List<VisionFrameRecord>> GroupBySession(IReadOnlyList<VisionFrameRecord> frames)
    {
        var groups = new List<List<VisionFrameRecord>>();
        var byName = new Dictionary<string, List<VisionFrameRecord>>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            if (!byName.TryGetValue(frame.Session, out var list))
            {
                list = [];
                byName[frame.Session] = list;
                groups.Add(list);
            }
            list.Add(frame);
        }
        return groups;
    }

    private static VisionTargetRetention TargetRetention(IReadOnlyList<VisionFrameRecord> frames)
    {
        var runLengths = new List<int>();
        var runSeconds = new List<double>();
        foreach (var sessionFrames in GroupBySession(frames))
        {
            var runStart = -1;
            for (var i = 0; i <= sessionFrames.Count; i++)
            {
                var isTarget = i < sessionFrames.Count && sessionFrames[i].Status == "target";
                if (isTarget && runStart < 0)
                {
                    runStart = i;
                }
                else if (!isTarget && runStart >= 0)
                {
                    runLengths.Add(i - runStart);
                    runSeconds.Add(sessionFrames[i - 1].TimestampMs - sessionFrames[runStart].TimestampMs);
                    runStart = -1;
                }
            }
        }
        return new VisionTargetRetention
        {
            Runs = runLengths.Count,
            LongestRunFrames = runLengths.Count == 0 ? 0 : runLengths.Max(),
            MeanRunFrames = runLengths.Count == 0 ? 0 : runLengths.Average(),
            MeanRunSeconds = runSeconds.Count == 0 ? 0 : runSeconds.Average(),
        };
    }

    private static int SelectionFlips(IReadOnlyList<VisionFrameRecord> frames)
    {
        var flips = 0;
        foreach (var sessionFrames in GroupBySession(frames))
        {
            string? previous = null;
            foreach (var frame in sessionFrames)
            {
                if (frame.Status != "target" || frame.SelectedTargetIndex is not { } index
                    || index >= frame.Detections.Count)
                {
                    continue;
                }
                var label = frame.Detections[index].Label;
                if (previous is not null && label != previous)
                {
                    flips++;
                }
                previous = label;
            }
        }
        return flips;
    }

    private static VisionDistribution Distribution(List<double> values)
    {
        if (values.Count == 0)
        {
            return new VisionDistribution { Count = 0 };
        }
        values.Sort();
        return new VisionDistribution
        {
            Count = values.Count,
            Min = values[0],
            P50 = Percentile(values, 0.5),
            P95 = Percentile(values, 0.95),
            Max = values[^1],
        };
    }

    /// <summary>Python-style percentile with linear interpolation (NaN for empty input).</summary>
    public static double Percentile(List<double> sorted, double q)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }
        if (sorted.Count == 1)
        {
            return sorted[0];
        }
        var position = q * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }
        return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    /// <summary>Python statistics.median semantics.</summary>
    public static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }
}
