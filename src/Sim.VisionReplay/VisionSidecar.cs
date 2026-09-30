using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>
/// Provenance of a live-session sidecar package. Every field is explicit — the
/// class mapping especially: silent defaults are forbidden (a wrong mapping would
/// silently relabel the evidence).
/// </summary>
public sealed record VisionSidecarRequest
{
    /// <summary>Frames the session's source actually delivered; the FIRST one must be the session anchor.</summary>
    public required IReadOnlyList<VisionFrameRecord> Frames { get; init; }

    /// <summary>
    /// Epoch ms of the SOURCE SESSION's first frame — the SimT 0 the package must be
    /// anchored at. Stated explicitly so a delivered prefix whose anchor was dropped
    /// is refused instead of silently shifting every age at replay time.
    /// </summary>
    public required double AnchorTimestampMs { get; init; }

    /// <summary>Import-style statistics of the parsed session (archived as the package's file entry).</summary>
    public required VisionImportFileStat Stat { get; init; }

    /// <summary>Explicit raw → label mapping, e.g. good→buff / bad→debuff.</summary>
    public required IReadOnlyDictionary<string, string> ClassMapping { get; init; }

    /// <summary>Batch/session label for the archived report (audit only, never semantics).</summary>
    public required string Label { get; init; }

    public required string ToolVersion { get; init; }

    /// <summary>Provenance tag of the producing pipeline ("mbri-csv", "yolo-bridge-process", ...).</summary>
    public string Source { get; init; } = "mbri-csv";

    /// <summary>Volatile timestamp; defaults to now and is excluded from the content hash.</summary>
    public string? GeneratedAt { get; init; }

    /// <summary>Extra honesty notes appended to the always-present live-bridge limitations.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>
/// vision-replay-v1 sidecar writer for live bridge sessions: the frames a live
/// source delivered become a canonical evidence package (`frames.jsonl` +
/// `import-report.json`), so a live session stays reproducible with
/// <see cref="VisionReplayAdapter"/> after the fact — an action-stream replay
/// cannot reproduce it, because live frame arrival depends on external timing.
///
/// What the writer guarantees before a single byte is written: every frame passes
/// <see cref="VisionFrameRecord.Validate"/> (the read side's own check, so the
/// package cannot be rejected by <see cref="VisionReplayIO.ParseFrames"/> later),
/// (timestampMs, sequence) keys are unique (the adapter refuses duplicates), the
/// session is uniform, and the package's first frame equals the caller-declared
/// session anchor (<see cref="VisionSidecarRequest.AnchorTimestampMs"/>) — a
/// shifted anchor would silently move SimT 0 for every replay of the package.
/// </summary>
public static class VisionSidecar
{
    /// <summary>
    /// Validates, then writes the package: each artifact (frames.jsonl,
    /// import-report.json) is written atomically ON ITS OWN — a crash between the
    /// two writes can leave a frames-only package, which the read side refuses
    /// (both files are required), so a half-written package can never be silently
    /// consumed. Returns the archived import report. Any validation failure
    /// produces NO output.
    /// </summary>
    public static VisionImportReport Write(string directory, VisionSidecarRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var frames = request.Frames;
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new VisionEvidenceException("sidecar: 帧流为空, 无法写出证据包(缺少 SimT 0 基准锚点)");
        }
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            throw new VisionEvidenceException("sidecar: label 必须显式给出");
        }
        if (string.IsNullOrWhiteSpace(request.ToolVersion))
        {
            throw new VisionEvidenceException("sidecar: toolVersion 必须显式给出");
        }
        if (request.ClassMapping.Count == 0)
        {
            throw new VisionEvidenceException("sidecar: classMapping 必须显式给出(禁止沉默默认)");
        }

        var session = frames[0].Session;
        if (string.IsNullOrWhiteSpace(session))
        {
            throw new VisionEvidenceException("sidecar: 帧记录缺少 session");
        }
        if (!string.Equals(request.Stat.Path, session, StringComparison.Ordinal))
        {
            throw new VisionEvidenceException(
                $"sidecar: 文件统计 path '{request.Stat.Path}' 必须等于帧 session '{session}'"
                + " (vision evaluate 的默认会话选择依赖它)");
        }
        if (!double.IsFinite(request.AnchorTimestampMs) || frames[0].TimestampMs != request.AnchorTimestampMs)
        {
            throw new VisionEvidenceException(
                $"sidecar: 包内首帧时间戳 {frames[0].TimestampMs} 不等于源会话锚点 {request.AnchorTimestampMs}"
                + " (锚点右移会让复现时 SimT 0 漂移, 拒绝写出)");
        }
        var width = frames[0].FrameWidth;
        var height = frames[0].FrameHeight;
        var keys = new HashSet<(double TimestampMs, long Sequence)>();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            // 逐帧跑既有 Validate: 写出的包必须能被读取侧(VisionReplayIO.ParseFrames)
            // 原样接受, 否则 live 现场"能跑"而事后无法复现 —— 这类失败必须在写出前暴露。
            var errors = frame.Validate().ToList();
            if (errors.Count > 0)
            {
                throw new VisionEvidenceException($"sidecar: 第 {i} 帧无效 — {string.Join(" ", errors)}");
            }
            if (i > 0 && !string.Equals(frame.Session, session, StringComparison.Ordinal))
            {
                throw new VisionEvidenceException(
                    $"sidecar: 第 {i} 帧 session '{frame.Session}' 与首帧 '{session}' 不一致");
            }
            // 时间戳必须单调非降: 重放适配器的二分选帧依赖升序。
            if (i > 0 && frame.TimestampMs < frames[i - 1].TimestampMs)
            {
                throw new VisionEvidenceException(
                    $"sidecar: 第 {i} 帧时间戳 {frame.TimestampMs} 早于前一帧 {frames[i - 1].TimestampMs}"
                    + " (必须单调非降)");
            }
            // (timestampMs, sequence) 必须唯一: VisionReplayAdapter 构造会拒绝重复键,
            // 重复帧在这里就要拦下而不是写出一个不可加载的包。
            if (!keys.Add((frame.TimestampMs, frame.Sequence)))
            {
                throw new VisionEvidenceException(
                    $"sidecar: 第 {i} 帧 (timestampMs={frame.TimestampMs}, sequence={frame.Sequence}) 与前面的帧重复");
            }
            if (frame.FrameWidth != width || frame.FrameHeight != height)
            {
                throw new VisionEvidenceException(
                    $"sidecar: 第 {i} 帧尺寸 {frame.FrameWidth}x{frame.FrameHeight} 与首帧 {width}x{height} 不一致");
            }
        }

        var framesBytes = VisionReplayIO.SerializeFrames(frames);
        var evidenceSha256 = VisionReplayIO.Sha256Hex(framesBytes);
        var report = new VisionImportReport
        {
            ToolVersion = request.ToolVersion,
            Label = request.Label,
            Source = request.Source,
            ClassMapping = new Dictionary<string, string>(request.ClassMapping, StringComparer.Ordinal),
            FrameWidth = width,
            FrameHeight = height,
            TimeBase = "SimT 0 = 源会话首帧; 到达 = (vision_timestamp_ms − 首帧)/1000 s; live 桥按 SimT 释放",
            Files = [request.Stat],
            GroundTruth = false,
            Grade = VisionReplaySchemas.EvidenceOnly,
            EvidenceId = VisionReplayIO.EvidenceId(evidenceSha256),
            EvidenceSha256 = evidenceSha256,
            FramesFile = VisionReplayIO.FramesFileName,
            Limitations =
            [
                "live 桥 sidecar: 包内为本场次源实际交付的帧流(源会话帧的前缀), 含源会话首帧作为 SimT 0 基准;"
                    + " files[0] 统计描述源会话的全量解析结果, 未被交付的帧不在包内",
                "live 桥帧到达依赖外部时序: 本包用 VisionReplayAdapter 复现, 不承诺动作流回放复现",
                "label 列为采集批次而非逐帧真值; 本报告恒 groundTruth=false / evidence_only",
                .. request.Limitations,
            ],
        };
        // 唯一的墙钟默认值(有意例外, 如实标注): generatedAt 是挥发审计字段, Fingerprint
        // 已把它排除在 contentSha256 之外 —— 同输入的内容指纹仍然确定(只有该时间戳逐次
        // 不同)。"库内不做时钟"纪律针对仿真内核路径与语义计算, 不含审计时间戳的兜底。
        report = VisionReplayIO.Fingerprint(
            report, request.GeneratedAt ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        // Everything validated — now write both artifacts atomically.
        VisionReplayIO.WriteAtomically(Path.Combine(directory, VisionReplayIO.FramesFileName), framesBytes);
        VisionReplayIO.WriteAtomically(
            Path.Combine(directory, VisionReplayIO.ImportReportFileName), ProtocolJson.Serialize(report));
        return report;
    }
}
