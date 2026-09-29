namespace Sim.Core;

/// <summary>
/// Camera-cache frame selection shared by every vision adapter (design 决策②:
/// equivalence is a structural fact, not two copies of the same code kept in
/// sync by hand). Serves the newest frame at or before the current stream time
/// whose age is within <c>maxAgeMs</c>, maps the service status onto the
/// <c>no_frame|stale|error|no_target|no_selection</c> reason codes and builds the
/// <see cref="VisionReplayConsumeRecord"/> ledger entry.
///
/// Pure by construction — no RNG, no IO, no clock — so a live stream and a
/// replayed evidence package yield bit-identical records for the same frame set.
/// </summary>
internal static class VisionFrameSelector
{
    /// <summary>Binary search: index of the newest frame with TimestampMs &lt;= tMs, or -1 when none.</summary>
    internal static int FindLastAtOrBefore(IReadOnlyList<VisionReplayFrame> frames, double tMs)
    {
        var low = 0;
        var high = frames.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (frames[mid].TimestampMs <= tMs)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return result;
    }

    /// <summary>
    /// Selects the frame serving one classify call and builds its consume record.
    /// <paramref name="tMs"/> is the stream clock the call maps to (SimT 0 = the
    /// session's first frame, caller's business), <paramref name="simT"/> is the
    /// SimT recorded in the ledger, <paramref name="source"/> the detection source
    /// name (the adapter's mode).
    /// </summary>
    internal static (VisionReplayConsumeRecord Record, VisionDetection? Detection) Select(
        IReadOnlyList<VisionReplayFrame> frames,
        double tMs,
        double maxAgeMs,
        string role,
        double simT,
        string source)
    {
        var index = FindLastAtOrBefore(frames, tMs);
        if (index < 0)
        {
            return (Unknown(role, simT, "no_frame"), null);
        }
        var frame = frames[index];
        var ageMs = tMs - frame.TimestampMs;
        // Closed window: age == maxAgeMs is still fresh (the camera cache's last
        // valid frame); only strictly older frames are stale.
        if (ageMs > maxAgeMs)
        {
            return (Unknown(role, simT, "stale", frame, ageMs), null);
        }
        return Consume(frame, ageMs, role, simT, source);
    }

    /// <summary>Maps a served frame onto its ledger record plus the detection handed to the FSM.</summary>
    private static (VisionReplayConsumeRecord Record, VisionDetection? Detection) Consume(
        VisionReplayFrame frame, double ageMs, string role, double simT, string source)
    {
        string? reason = frame.Status switch
        {
            "target" => null,
            "error" => "error",
            "no_data_or_stale" => "stale",
            "no_target" => "no_target",
            _ => "error",
        };
        VisionReplayFrameDetection? selected = null;
        if (reason is null)
        {
            if (frame.SelectedTargetIndex is { } index && index >= 0 && index < frame.Detections.Count)
            {
                selected = frame.Detections[index];
            }
            else
            {
                reason = "no_selection";
            }
        }
        var detection = selected is null
            ? null
            : new VisionDetection
            {
                Label = selected.Label,
                Confidence = selected.Confidence,
                Source = source,
                OffsetX = selected.OffsetX,
            };
        return (new VisionReplayConsumeRecord
        {
            Role = role,
            SimT = simT,
            FrameSequence = frame.Sequence,
            AgeMs = ageMs,
            Reason = reason,
            Label = selected?.Label ?? "unknown",
            Confidence = selected?.Confidence ?? 0,
        }, detection);
    }

    private static VisionReplayConsumeRecord Unknown(string role, double simT, string reason)
        => new()
        {
            Role = role,
            SimT = simT,
            FrameSequence = null,
            AgeMs = null,
            Reason = reason,
            Label = "unknown",
        };

    private static VisionReplayConsumeRecord Unknown(
        string role, double simT, string reason, VisionReplayFrame frame, double ageMs)
        => new()
        {
            Role = role,
            SimT = simT,
            FrameSequence = frame.Sequence,
            AgeMs = ageMs,
            Reason = reason,
            Label = "unknown",
        };
}
