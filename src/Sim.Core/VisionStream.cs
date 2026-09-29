using System.Text.Json;
using System.Text.Json.Serialization;
using Sim.Protocol;

namespace Sim.Core;

/// <summary>
/// One detection inside a <see cref="VisionStreamFrame"/>: the model output of one
/// detection of a vision service frame (never world truth). The raw YOLO class
/// (good/bad) and the manifest-mapped label (buff/debuff) are both kept so the
/// mapping stays auditable; bbox/center/offset are passed through verbatim.
/// </summary>
public sealed record VisionStreamDetection
{
    /// <summary>Raw YOLO class id: 0=good, 1=bad in the MBri pipeline.</summary>
    [JsonPropertyName("class_id")]
    public int ClassId { get; init; }

    /// <summary>Raw target_type string from the source, e.g. "good"/"bad".</summary>
    [JsonPropertyName("target_type")]
    public string? TargetType { get; init; }

    /// <summary>Manifest-mapped label: "buff"|"debuff" (opponent does not exist in Phase A evidence).</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    /// <summary>Bounding box in pixels: x1, y1, x2, y2 (frame coordinates), when reported.</summary>
    [JsonPropertyName("bbox_x1")]
    public double? BboxX1 { get; init; }

    [JsonPropertyName("bbox_y1")]
    public double? BboxY1 { get; init; }

    [JsonPropertyName("bbox_x2")]
    public double? BboxX2 { get; init; }

    [JsonPropertyName("bbox_y2")]
    public double? BboxY2 { get; init; }

    [JsonPropertyName("center_x")]
    public double? CenterX { get; init; }

    [JsonPropertyName("center_y")]
    public double? CenterY { get; init; }

    /// <summary>Normalized lateral offset (center − width/2)/(width/2), MBri config.py semantics.</summary>
    [JsonPropertyName("offset_x")]
    public double? OffsetX { get; init; }

    /// <summary>Normalized vertical offset (center − height/2)/(height/2).</summary>
    [JsonPropertyName("offset_y")]
    public double? OffsetY { get; init; }
}

/// <summary>
/// One vision-stream frame (one JSONL line): the normalized detection stream the
/// live bridge consumes — from a real-car CSV row group, an external YOLO
/// process' stdout, or a sidecar evidence package. Field names mirror the real-car
/// MBri CSV columns (see <c>MbriVisionDialect</c>) so all three speak the same
/// vocabulary; no image data ever enters the contract.
///
/// Audit-only fields (<see cref="T"/>, <see cref="ReceivedAgeMs"/>, fps,
/// inference_ms, frame size, arrival_sim_t) are dropped by
/// <see cref="ToReplayFrame"/>: the bridge derives age from SimT itself and never
/// trusts a service-reported age.
///
/// <see cref="SelectedTargetIndex"/> is the index of the selected detection (the
/// CSV's <c>selected_target</c> marks the selected ROW as 0/1; this contract
/// carries the resolved index instead, matching
/// <see cref="VisionReplayFrame.SelectedTargetIndex"/>). The selected detection
/// itself must be present in <see cref="Detections"/> — the frame-level
/// <see cref="Label"/>/<see cref="Confidence"/> are that row's copy for audit and
/// for flat consumers, not a substitute for it.
///
/// <see cref="Validate"/> mirrors the CSV dialect's hard rejections, and
/// <see cref="ParseLine"/> enforces them: a data-contract error must fail loudly
/// at the boundary instead of degrading into an <c>unknown</c>/<c>error</c>
/// detection the FSM would happily consume.
/// </summary>
public sealed record VisionStreamFrame
{
    /// <summary>Allowed vision_status values (identical vocabulary to the MBri CSV dialect).</summary>
    public static readonly string[] Statuses = ["target", "no_target", "error", "no_data_or_stale"];

    /// <summary>Allowed detection labels: the normalized vocabulary the core's vision normalization consumes.</summary>
    public static readonly string[] Labels = ["buff", "debuff", "opponent", "unknown"];

    /// <summary>Service-side control-loop time (s) — audit only.</summary>
    [JsonPropertyName("t")]
    public double? T { get; init; }

    /// <summary>Service-reported frame age (ms) — audit only; staleness is computed from SimT.</summary>
    [JsonPropertyName("received_age_ms")]
    public double? ReceivedAgeMs { get; init; }

    /// <summary>Vision service frame number (unique per session).</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    /// <summary>Capture-host epoch ms; the source scales it to an arrival SimT (session first frame = SimT 0).</summary>
    [JsonPropertyName("vision_timestamp_ms")]
    public double TimestampMs { get; init; }

    /// <summary>Verbatim service status: target|no_target|error|no_data_or_stale.</summary>
    [JsonPropertyName("vision_status")]
    public string Status { get; init; } = "";

    /// <summary>Verbatim service error (null when empty).</summary>
    [JsonPropertyName("vision_error")]
    public string? Error { get; init; }

    /// <summary>Detection index of the selected target, when any.</summary>
    [JsonPropertyName("selected_target")]
    public int? SelectedTargetIndex { get; init; }

    /// <summary>Detection count reported by the source (0 on no_target/no_data frames).</summary>
    [JsonPropertyName("detection_count")]
    public int DetectionCount { get; init; }

    [JsonPropertyName("frame_width")]
    public int? FrameWidth { get; init; }

    [JsonPropertyName("frame_height")]
    public int? FrameHeight { get; init; }

    /// <summary>Service-reported frame rate — link-quality metric source.</summary>
    [JsonPropertyName("fps")]
    public double? Fps { get; init; }

    /// <summary>Service-reported inference time (ms) — link-quality metric source.</summary>
    [JsonPropertyName("inference_ms")]
    public double? InferenceMs { get; init; }

    /// <summary>Arrival SimT (s) the source computed: (vision_timestamp_ms − session first frame)/1000.</summary>
    [JsonPropertyName("arrival_sim_t")]
    public double? ArrivalSimT { get; init; }

    /// <summary>Per-detection rows of this frame, in detection_index order (empty when the frame has none).</summary>
    [JsonPropertyName("detections")]
    public List<VisionStreamDetection> Detections { get; init; } = [];

    /// <summary>
    /// The selected detection, i.e. the CSV's selected row; null when
    /// <see cref="SelectedTargetIndex"/> is absent or out of range. The status
    /// gate (only <c>target</c> frames carry detections) is applied by the frame
    /// selector, not here.
    /// </summary>
    [JsonIgnore]
    public VisionStreamDetection? SelectedDetection
        => SelectedTargetIndex is { } index && index >= 0 && index < Detections.Count
            ? Detections[index]
            : null;

    /// <summary>Label of the selected detection (the CSV's selected-row label); null when nothing is selected.</summary>
    [JsonPropertyName("label")]
    public string? Label => SelectedDetection?.Label;

    /// <summary>Confidence of the selected detection; null when nothing is selected.</summary>
    [JsonPropertyName("confidence")]
    public double? Confidence => SelectedDetection?.Confidence;

    /// <summary>
    /// Adapter working set: audit-only fields are stripped and each detection keeps
    /// label/confidence/offset_x, so a streamed frame and a replayed package frame
    /// are the same thing to the bridge.
    /// </summary>
    public VisionReplayFrame ToReplayFrame() => new()
    {
        Sequence = Sequence,
        TimestampMs = TimestampMs,
        Status = Status,
        Error = Error,
        SelectedTargetIndex = SelectedTargetIndex,
        Detections = Detections
            .Select(d => new VisionReplayFrameDetection
            {
                Label = d.Label,
                Confidence = d.Confidence,
                OffsetX = d.OffsetX,
            })
            .ToList(),
    };

    /// <summary>One compact JSONL line using the canonical <see cref="ProtocolJson"/> conventions.</summary>
    public string ToJsonLine() => ProtocolJson.Serialize(this);

    /// <summary>
    /// Contract errors of this frame; empty means it is usable. Mirrors the CSV
    /// dialect's rejection matrix — unknown status, sequence/timestamp sanity,
    /// detection_count vs detection rows, a non-target frame carrying detections,
    /// out-of-range selection, class_id/target_type disagreement, confidence
    /// outside [0,1] and non-finite geometry — so the live stream cannot silently
    /// turn a data-contract error into a served detection.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        static bool NotFinite(double? value) => value is { } v && !double.IsFinite(v);
        static bool OutOfRange(double? value, double min, double max)
            => value is { } v && (!double.IsFinite(v) || v < min || v > max);

        if (!Statuses.Contains(Status, StringComparer.Ordinal))
        {
            yield return $"vision stream frame: vision_status '{Status}' 不在允许枚举内 [{string.Join("|", Statuses)}]";
        }
        if (Sequence < 0)
        {
            yield return "vision stream frame: sequence 必须 >= 0";
        }
        if (!double.IsFinite(TimestampMs) || TimestampMs < 0)
        {
            yield return "vision stream frame: vision_timestamp_ms 必须是有限非负数值";
        }
        if (OutOfRange(ReceivedAgeMs, 0, double.PositiveInfinity))
        {
            yield return "vision stream frame: received_age_ms 必须是有限非负数值(存在时)";
        }
        if (NotFinite(Fps) || NotFinite(InferenceMs))
        {
            yield return "vision stream frame: fps/inference_ms 必须是有限数值(存在时)";
        }
        if (DetectionCount < 0)
        {
            yield return "vision stream frame: detection_count 必须 >= 0";
        }
        // 显式 "detections": null 会在反序列化时覆盖属性初始化器 —— 这是数据契约错误
        // (无检测帧用空数组), 必须在边界上拒绝: 让它穿透会以 NullReferenceException
        // 的形式炸掉读侧(进程源/桥), 而不是被 ParseLine 包成 JsonException。
        if (Detections is null)
        {
            yield return "vision stream frame: detections 不能为 null(无检测帧用空数组 [])";
        }
        var detections = Detections ?? [];
        if (DetectionCount != detections.Count)
        {
            yield return $"vision stream frame: detection_count {DetectionCount} 与 detections 行数 {detections.Count} 不一致";
        }
        if (Status == "target" && DetectionCount == 0)
        {
            yield return "vision stream frame: vision_status=target 但 detection_count=0";
        }
        if (Status != "target" && DetectionCount != 0)
        {
            yield return $"vision stream frame: vision_status={Status} 但 detection_count={DetectionCount} (非 target 帧不允许携带检测)";
        }
        if (SelectedTargetIndex is { } selected && (selected < 0 || selected >= detections.Count))
        {
            yield return $"vision stream frame: selected_target {selected} 超出 detections 范围";
        }
        for (var i = 0; i < detections.Count; i++)
        {
            var detection = detections[i];
            if (detection is null)
            {
                yield return $"vision stream frame: detections[{i}] 为空";
                continue;
            }
            if (!Labels.Contains(detection.Label, StringComparer.Ordinal))
            {
                yield return $"vision stream frame: detections[{i}].label '{detection.Label}' 不在允许枚举内 [{string.Join("|", Labels)}]";
            }
            if (OutOfRange(detection.Confidence, 0, 1))
            {
                yield return $"vision stream frame: detections[{i}].confidence 超出 [0,1]";
            }
            if (detection.ClassId is not (0 or 1))
            {
                yield return $"vision stream frame: detections[{i}].class_id {detection.ClassId} 只允许 0(good)/1(bad)";
            }
            if (detection.TargetType is { } rawType && ((detection.ClassId == 0) != (rawType == "good")))
            {
                yield return $"vision stream frame: detections[{i}].class_id {detection.ClassId} 与 target_type '{rawType}' 不一致";
            }
            if (NotFinite(detection.BboxX1) || NotFinite(detection.BboxY1)
                || NotFinite(detection.BboxX2) || NotFinite(detection.BboxY2)
                || NotFinite(detection.CenterX) || NotFinite(detection.CenterY))
            {
                yield return $"vision stream frame: detections[{i}] bbox/center 必须是有限数值";
            }
            if (OutOfRange(detection.OffsetX, -1, 1) || OutOfRange(detection.OffsetY, -1, 1))
            {
                yield return $"vision stream frame: detections[{i}] offset 超出 [-1,1]";
            }
        }
    }

    /// <summary>
    /// Parses one JSONL line and enforces the wire contract (see
    /// <see cref="Validate"/>). Malformed JSON and contract violations both throw
    /// <see cref="JsonException"/>: a stream source turns that into a fault record
    /// (never a crash, and never a silently mislabeled detection).
    /// </summary>
    public static VisionStreamFrame ParseLine(string line)
    {
        // ProtocolJson.Deserialize 对空载荷与 JSON 字面量 null 已显式抛 JsonException
        // ("JSON payload deserialized to null."), 所以这里拿到的必是帧对象; 而
        // "对象内的空形状"(如 "detections": null) 由 Validate 拒绝 —— 两条路径都不得
        // 以 NullReferenceException 的形式穿出边界。
        var frame = ProtocolJson.Deserialize<VisionStreamFrame>(line);
        var errors = frame.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new JsonException($"vision stream frame 不符合契约: {string.Join(" ", errors)}");
        }
        return frame;
    }
}

/// <summary>
/// Frame source of the live vision bridge (R1). Pure interface: no IO, no clock
/// and no RNG of its own — the caller drives it with simulation time, so the same
/// scenario always releases the same frames at the same SimT. Implementations
/// (CSV at scaled SimT, external YOLO process at wall clock) live outside the
/// deterministic core and only refresh the bridge's cache.
/// </summary>
public interface IVisionStreamSource
{
    /// <summary>
    /// Hands every frame that has arrived by <paramref name="simTimeSeconds"/> to
    /// <see cref="Released"/> and returns how many frames that added. Must be
    /// non-blocking: it never waits for the next frame, it only releases what
    /// arrival time has already made available.
    /// </summary>
    int PumpUntil(double simTimeSeconds);

    /// <summary>
    /// Frames delivered so far, ascending by (TimestampMs, Sequence). The session
    /// anchor — SimT 0 — is the first frame released here.
    /// </summary>
    IReadOnlyList<VisionReplayFrame> Released { get; }
}
