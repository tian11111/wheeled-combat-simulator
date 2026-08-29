namespace Sim.VisionReplay;

/// <summary>
/// MBri CSV dialect registry. Dialects are recognized by EXACT header
/// column-name set equality — semantics are never guessed from individual
/// column names. In Phase A only the full hunt dialect (per-detection rows)
/// is importable; the simplified main_* dialect and any unknown header are
/// rejected with an explicit missing-column list instead of silent degrade.
/// </summary>
public static class MbriVisionDialect
{
    /// <summary>The importable dialect id.</summary>
    public const string HuntDetections = "mbri-hunt-detections";

    /// <summary>
    /// Exact header column-name set of the full MBri hunt dialect (73 columns,
    /// per-detection rows). Column ORDER is irrelevant; the SET must match.
    /// </summary>
    public static readonly string[] HuntDetectionColumns =
    [
        "t", "label", "measured_distance_cm", "sequence", "vision_timestamp_ms",
        "received_age_ms", "vision_valid", "vision_status", "vision_error",
        "frame_width", "frame_height", "action", "fps", "inference_ms",
        "detection_count", "detection_index", "selected_target", "class_id",
        "target_type", "confidence", "bbox_x1", "bbox_y1", "bbox_x2", "bbox_y2",
        "bbox_width", "bbox_height", "bbox_area", "bbox_area_ratio",
        "center_x", "center_y", "offset_x", "offset_y", "model_distance_cm",
        "sensor_valid", "sensor_error", "io_mask", "hunt_mode", "hunt_state",
        "hunt_reason", "hunt_owns_control", "near_direction", "good_offset_x",
        "bad_offset_x", "good_confidence", "good_acquire_count", "good_miss_count",
        "good_locked", "shovel_left", "shovel_right", "shovel_state",
        "shovel_active", "shovel_hang", "left_cmd", "right_cmd", "motor_enabled",
        "adc0", "adc1", "adc2", "adc3", "adc4", "adc5", "adc6", "adc7", "adc8",
        "adc9", "io0", "io1", "io2", "io3", "io4", "io5", "io6", "io7",
    ];

    /// <summary>
    /// Columns the frame normalization actually consumes; used to build the
    /// explicit missing-column list for rejected headers.
    /// </summary>
    public static readonly string[] RequiredVisionColumns =
    [
        "sequence", "vision_timestamp_ms", "received_age_ms", "vision_status",
        "vision_error", "frame_width", "frame_height", "fps", "inference_ms",
        "detection_count", "detection_index", "selected_target", "class_id",
        "target_type", "confidence", "bbox_x1", "bbox_y1", "bbox_x2", "bbox_y2",
        "center_x", "center_y", "offset_x", "offset_y",
    ];

    /// <summary>Detects the dialect by exact header set; null means "not importable".</summary>
    public static string? Detect(IReadOnlyList<string> headers)
    {
        var set = headers.ToHashSet(StringComparer.Ordinal);
        if (set.Count == HuntDetectionColumns.Length
            && HuntDetectionColumns.All(set.Contains))
        {
            return HuntDetections;
        }
        return null;
    }

    /// <summary>Builds the explicit reason (missing required vision columns) for a rejected header.</summary>
    public static string RejectionReason(IReadOnlyList<string> headers)
    {
        var set = headers.ToHashSet(StringComparer.Ordinal);
        var missing = RequiredVisionColumns.Where(c => !set.Contains(c)).ToList();
        var extra = set.Except(HuntDetectionColumns, StringComparer.Ordinal).ToList();
        var parts = new List<string>
        {
            $"表头 {headers.Count} 列与 {HuntDetections} 方言({HuntDetectionColumns.Length} 列)不匹配",
        };
        if (missing.Count > 0)
        {
            parts.Add($"缺少必需视觉列 [{string.Join(",", missing)}]");
        }
        if (extra.Count > 0)
        {
            parts.Add($"未知列 [{string.Join(",", extra)}]");
        }
        return string.Join("; ", parts) + " (Phase A 不导入无逐检测明细的简化方言)";
    }
}
