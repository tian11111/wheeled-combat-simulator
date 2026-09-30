using Sim.Core;

namespace Sim.VisionReplay;

/// <summary>
/// Canonical evidence frame → adapter working set / live-stream contract. These
/// mappings live here exactly once so the offline path (`vision import` →
/// frames.jsonl → <see cref="VisionReplayAdapter"/>), the live CSV stream
/// (<see cref="CsvStreamSource"/>) and a re-read sidecar all feed the adapters the
/// same frames. Audit-only fields (t/fps/inference_ms/frame size/bbox/class_id/
/// target_type) stay in the evidence record and never enter the adapters' working
/// set — the adapters only ever see label/confidence/offset_x.
/// </summary>
public static class VisionReplayFrames
{
    /// <summary>Maps one canonical evidence frame onto the adapters' working frame.</summary>
    public static VisionReplayFrame ToReplayFrame(VisionFrameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new VisionReplayFrame
        {
            Sequence = record.Sequence,
            TimestampMs = record.TimestampMs,
            Status = record.Status,
            Error = record.Error,
            SelectedTargetIndex = record.SelectedTargetIndex,
            Detections = record.Detections.Select(ToReplayDetection).ToList(),
        };
    }

    /// <summary>Maps a frame list in order (the adapters sort by (timestamp, sequence) themselves).</summary>
    public static IReadOnlyList<VisionReplayFrame> ToReplayFrames(IEnumerable<VisionFrameRecord> records)
        => records.Select(ToReplayFrame).ToList();

    /// <summary>
    /// Stream wire frame → canonical evidence record: the inverse of
    /// <see cref="ToStreamFrame"/>, for a live session whose frames arrived over a
    /// JSONL stream (external process source) instead of a CSV file (decision③:
    /// such a session's sidecar holds the frames the source delivered, so it stays
    /// reproducible through <see cref="VisionReplayAdapter"/>).
    ///
    /// The record is the strict superset: it needs a session label (the stream
    /// contract has no session field) and the audit/geometry fields the stream
    /// may omit. Where a required value is missing this throws — silently writing
    /// 0 would fabricate evidence, and a package with zeros would replay a
    /// different camera cache than the live session served. A process bridge
    /// therefore emits the full CSV-column field set (see
    /// <c>Sim.Core.ExternalProcessStreamSource</c>'s contract note).
    /// </summary>
    public static VisionFrameRecord ToEvidenceFrame(VisionStreamFrame frame, string session)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(session))
        {
            throw new VisionEvidenceException("stream 帧缺会话名: 证据包必须显式给出 session");
        }
        if (frame.FrameWidth is not { } width || frame.FrameHeight is not { } height || width <= 0 || height <= 0)
        {
            throw new VisionEvidenceException(
                $"stream 帧 {frame.Sequence}: frame_width/frame_height 缺失或非正, 无法写成规范证据帧(sidecar 需要帧尺寸)");
        }
        if (frame.ReceivedAgeMs is not { } receivedAge || !double.IsFinite(receivedAge) || receivedAge < 0)
        {
            throw new VisionEvidenceException(
                $"stream 帧 {frame.Sequence}: received_age_ms 缺失或非法(证据帧的审计字段不允许用 0 顶替)");
        }
        var detections = new List<VisionFrameDetection>(frame.Detections.Count);
        for (var i = 0; i < frame.Detections.Count; i++)
        {
            var detection = frame.Detections[i]
                ?? throw new VisionEvidenceException($"stream 帧 {frame.Sequence}: detections[{i}] 为空");
            if (detection.BboxX1 is not { } x1 || detection.BboxY1 is not { } y1
                || detection.BboxX2 is not { } x2 || detection.BboxY2 is not { } y2)
            {
                throw new VisionEvidenceException(
                    $"stream 帧 {frame.Sequence}: detections[{i}] 缺 bbox_x1..bbox_y2(证据帧要求完整几何)");
            }
            if (detection.CenterX is not { } centerX || detection.CenterY is not { } centerY)
            {
                throw new VisionEvidenceException(
                    $"stream 帧 {frame.Sequence}: detections[{i}] 缺 center_x/center_y(证据帧要求完整几何)");
            }
            if (detection.OffsetX is not { } offsetX || detection.OffsetY is not { } offsetY)
            {
                throw new VisionEvidenceException(
                    $"stream 帧 {frame.Sequence}: detections[{i}] 缺 offset_x/offset_y(证据帧要求完整几何)");
            }
            detections.Add(new VisionFrameDetection
            {
                ClassId = detection.ClassId,
                // target_type 缺省时按契约回填(class_id 0=good / 1=bad, 见 VisionStreamDetection 文档)。
                RawType = detection.TargetType is { Length: > 0 } rawType
                    ? rawType
                    : detection.ClassId == 0 ? "good" : "bad",
                Label = detection.Label,
                Confidence = detection.Confidence,
                Bbox = [x1, y1, x2, y2],
                CenterX = centerX,
                CenterY = centerY,
                OffsetX = offsetX,
                OffsetY = offsetY,
            });
        }
        return new VisionFrameRecord
        {
            Session = session,
            Sequence = frame.Sequence,
            TimestampMs = frame.TimestampMs,
            ReceivedAgeMs = receivedAge,
            Status = frame.Status,
            Error = frame.Error,
            Fps = frame.Fps,
            InferenceMs = frame.InferenceMs,
            FrameWidth = width,
            FrameHeight = height,
            // 流契约没有"重收"概念(每帧一行, 重复键由桥按首次交付折叠): 证据帧如实记 0。
            DuplicateReceives = 0,
            SelectedTargetIndex = frame.SelectedTargetIndex,
            Detections = detections,
        };
    }

    /// <summary>
    /// Projects one canonical evidence frame onto the live-stream wire contract
    /// (`VisionStreamFrame`). <see cref="VisionStreamFrame.T"/> stays null: the CSV's
    /// control-loop seconds are not part of the canonical record, and
    /// <see cref="VisionStreamFrame.ArrivalSimT"/> carries the same information
    /// relative to the session's first frame.
    /// </summary>
    public static VisionStreamFrame ToStreamFrame(VisionFrameRecord record, double sessionFirstTimestampMs)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new VisionStreamFrame
        {
            ReceivedAgeMs = record.ReceivedAgeMs,
            Sequence = record.Sequence,
            TimestampMs = record.TimestampMs,
            Status = record.Status,
            Error = record.Error,
            SelectedTargetIndex = record.SelectedTargetIndex,
            DetectionCount = record.Detections.Count,
            FrameWidth = record.FrameWidth,
            FrameHeight = record.FrameHeight,
            Fps = record.Fps,
            InferenceMs = record.InferenceMs,
            ArrivalSimT = (record.TimestampMs - sessionFirstTimestampMs) / 1000.0,
            Detections = record.Detections.Select(ToStreamDetection).ToList(),
        };
    }

    private static VisionReplayFrameDetection ToReplayDetection(VisionFrameDetection detection) => new()
    {
        Label = detection.Label,
        Confidence = detection.Confidence,
        OffsetX = detection.OffsetX,
    };

    private static VisionStreamDetection ToStreamDetection(VisionFrameDetection detection) => new()
    {
        ClassId = detection.ClassId,
        TargetType = detection.RawType,
        Label = detection.Label,
        Confidence = detection.Confidence,
        BboxX1 = detection.Bbox.Length > 0 ? detection.Bbox[0] : null,
        BboxY1 = detection.Bbox.Length > 1 ? detection.Bbox[1] : null,
        BboxX2 = detection.Bbox.Length > 2 ? detection.Bbox[2] : null,
        BboxY2 = detection.Bbox.Length > 3 ? detection.Bbox[3] : null,
        CenterX = detection.CenterX,
        CenterY = detection.CenterY,
        OffsetX = detection.OffsetX,
        OffsetY = detection.OffsetY,
    };
}
