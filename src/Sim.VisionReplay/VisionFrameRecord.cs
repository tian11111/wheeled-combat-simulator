using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>
/// One normalized detection inside a <see cref="VisionFrameRecord"/>. The raw
/// YOLO class (good/bad) and the manifest-mapped label (buff/debuff) are both
/// kept so the mapping stays auditable; confidence/bbox/center/offset are the
/// verbatim model outputs (validated, never fabricated).
/// </summary>
public sealed record VisionFrameDetection
{
    /// <summary>Raw YOLO class id: 0=good, 1=bad in the MBri pipeline.</summary>
    public int ClassId { get; init; }

    /// <summary>Raw target_type string from the CSV, e.g. "good"/"bad".</summary>
    public string RawType { get; init; } = "";

    /// <summary>Manifest-mapped label: "buff"|"debuff" (opponent does not exist in Phase A).</summary>
    public string Label { get; init; } = "";

    public double Confidence { get; init; }

    /// <summary>Bounding box in pixels: x1, y1, x2, y2 (frame coordinates).</summary>
    public double[] Bbox { get; init; } = [0, 0, 0, 0];

    public double CenterX { get; init; }

    public double CenterY { get; init; }

    /// <summary>Normalized lateral offset (center − width/2)/(width/2), MBri config.py semantics.</summary>
    public double OffsetX { get; init; }

    /// <summary>Normalized vertical offset (center − height/2)/(height/2).</summary>
    public double OffsetY { get; init; }
}

/// <summary>
/// One normalized vision frame of the vision-replay-v1 evidence package
/// (one JSONL line). Aggregated from the per-detection CSV rows of one receive
/// group; re-received duplicates of the same sequence collapse into
/// <see cref="DuplicateReceives"/> and keep the FIRST (freshest-age) receive.
/// </summary>
public sealed record VisionFrameRecord
{
    /// <summary>Source file (session) this frame belongs to.</summary>
    public string Session { get; init; } = "";

    /// <summary>Vision service frame number; unique per session (validated).</summary>
    public long Sequence { get; init; }

    /// <summary>Capture-host epoch milliseconds.</summary>
    public double TimestampMs { get; init; }

    /// <summary>Age of the frame (ms) when the control loop received it (first receive).</summary>
    public double ReceivedAgeMs { get; init; }

    /// <summary>Verbatim vision_status: target|no_target|error|no_data_or_stale.</summary>
    public string Status { get; init; } = "";

    /// <summary>Verbatim vision_error (null when empty).</summary>
    public string? Error { get; init; }

    public double? Fps { get; init; }

    public double? InferenceMs { get; init; }

    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    /// <summary>Receive groups beyond the first for the same sequence (stale cache re-polls).</summary>
    public int DuplicateReceives { get; init; }

    /// <summary>detection_index of the selected target in the first receive, when any.</summary>
    public int? SelectedTargetIndex { get; init; }

    public List<VisionFrameDetection> Detections { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Session))
        {
            yield return "vision frame: session must be recorded.";
        }
        if (Sequence < 0)
        {
            yield return "vision frame: sequence must be >= 0.";
        }
        if (!double.IsFinite(TimestampMs))
        {
            yield return "vision frame: timestampMs must be finite.";
        }
        if (!double.IsFinite(ReceivedAgeMs) || ReceivedAgeMs < 0)
        {
            yield return "vision frame: receivedAgeMs must be finite and >= 0.";
        }
        if (!VisionReplaySchemas.Statuses.Contains(Status))
        {
            yield return $"vision frame: unknown status '{Status}'.";
        }
        if (FrameWidth <= 0 || FrameHeight <= 0)
        {
            yield return "vision frame: frameWidth/frameHeight must be positive.";
        }
        if (DuplicateReceives < 0)
        {
            yield return "vision frame: duplicateReceives must be >= 0.";
        }
        foreach (var detection in Detections)
        {
            if (detection is null)
            {
                yield return "vision frame: detections must not contain null entries.";
                continue;
            }
            if (detection.Confidence is < 0 or > 1)
            {
                yield return $"vision frame {Sequence}: confidence must be within [0,1].";
            }
            if (detection.Bbox.Length != 4 || detection.Bbox.Any(v => !double.IsFinite(v)))
            {
                yield return $"vision frame {Sequence}: bbox must be four finite numbers.";
            }
        }
        if (SelectedTargetIndex is { } selected && (selected < 0 || selected >= Detections.Count))
        {
            yield return $"vision frame {Sequence}: selectedTargetIndex {selected} out of range.";
        }
    }
}
