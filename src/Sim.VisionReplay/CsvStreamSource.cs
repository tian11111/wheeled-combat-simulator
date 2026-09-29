using System.Text;
using Sim.Core;

namespace Sim.VisionReplay;

/// <summary>
/// Live frame source over ONE real-car MBri hunt-dialect CSV (73 columns), for the
/// live vision bridge (`vision live --source &lt;csv&gt;` and the desktop liveBridge
/// source).
///
/// Normalization is never re-implemented: <see cref="VisionEvidenceBuilder.ParseSession"/>
/// is shared with `vision import`, so warm-up rows, receive-group collapse,
/// multi-detection aggregation, the selected target and the whole validation
/// matrix behave exactly as in the offline evidence path. The live-vs-replay
/// equivalence is therefore structural, not two parsers agreeing by luck.
///
/// SimT scaling: the session's first frame is arrival 0 s and a frame's arrival is
/// (vision_timestamp_ms − first frame)/1000 s, so the first delivered frame is the
/// source's SimT 0 anchor (the bridge's session start and the sidecar's anchor rule
/// both fall out of that by construction). The file is read once in
/// <see cref="Load"/>; <see cref="PumpUntil"/> is pure memory — never IO, never a
/// clock, never random.
/// </summary>
public sealed class CsvStreamSource : IVisionStreamSource
{
    private readonly List<VisionReplayFrame> _released = [];
    private readonly List<VisionFrameRecord> _releasedEvidence = [];
    private readonly double _sessionFirstTimestampMs;
    private int _cursor;

    private CsvStreamSource(
        string sourcePath,
        string session,
        VisionReplayManifest manifest,
        IReadOnlyList<VisionFrameRecord> evidenceFrames,
        VisionImportFileStat stat)
    {
        if (evidenceFrames.Count == 0)
        {
            throw new VisionEvidenceException($"{session}: 不含任何帧行, 无法建立 SimT 0 基准");
        }
        SourcePath = sourcePath;
        Session = session;
        Manifest = manifest;
        EvidenceFrames = evidenceFrames;
        Stat = stat;
        _sessionFirstTimestampMs = evidenceFrames[0].TimestampMs;
        Frames = evidenceFrames.Select(f => VisionReplayFrames.ToStreamFrame(f, _sessionFirstTimestampMs)).ToList();
        DurationSeconds = (evidenceFrames[^1].TimestampMs - _sessionFirstTimestampMs) / 1000.0;
    }

    /// <summary>Absolute path of the loaded CSV (audit/report provenance).</summary>
    public string SourcePath { get; }

    /// <summary>Session key of the evidence package (the CSV file name).</summary>
    public string Session { get; }

    /// <summary>
    /// Manifest synthesized for the single-file stream: explicit class mapping
    /// good→buff / bad→debuff and the frame size reported by the first frame row
    /// (there is no manifest to contradict for a single-file live stream; every
    /// later row is still validated against that size).
    /// </summary>
    public VisionReplayManifest Manifest { get; }

    /// <summary>Every frame of the session in the live-stream wire contract (ascending).</summary>
    public IReadOnlyList<VisionStreamFrame> Frames { get; }

    /// <summary>Every frame of the session in the canonical evidence form (ascending).</summary>
    public IReadOnlyList<VisionFrameRecord> EvidenceFrames { get; }

    /// <summary>Import-style statistics of the parsed session (the sidecar report's file entry).</summary>
    public VisionImportFileStat Stat { get; }

    /// <summary>Session length in seconds: (last − first) timestamp.</summary>
    public double DurationSeconds { get; }

    /// <summary>
    /// Epoch ms of the session's first frame — the SimT 0 anchor a sidecar package
    /// must carry (<see cref="VisionSidecarRequest.AnchorTimestampMs"/>).
    /// </summary>
    public double SessionFirstTimestampMs => _sessionFirstTimestampMs;

    public IReadOnlyList<VisionReplayFrame> Released => _released;

    /// <summary>Canonical form of the frames delivered so far — the sidecar working set.</summary>
    public IReadOnlyList<VisionFrameRecord> ReleasedEvidenceFrames => _releasedEvidence;

    /// <summary>
    /// Loads one MBri hunt-dialect CSV. Validation failures (missing file, unknown
    /// header set, malformed/inconsistent rows, no frame row at all) throw
    /// <see cref="VisionEvidenceException"/> / <see cref="MbriCsvException"/> before
    /// any simulation runs — loading is the pre-flight, pumping never throws.
    /// </summary>
    public static CsvStreamSource Load(string csvPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        var full = Path.GetFullPath(csvPath);
        if (!File.Exists(full))
        {
            throw new VisionEvidenceException($"CSV 源不存在: {full}");
        }
        var session = Path.GetFileName(full);
        // 与 import 路径同一上限: 超大输入显式拒绝, 绝不整体读入内存。
        var size = new FileInfo(full).Length;
        if (size > VisionReplayIO.MaxSourceFileBytes)
        {
            throw new VisionEvidenceException(
                $"文件超过 {VisionReplayIO.MaxSourceFileBytesLabel} 上限: {session} ({size} B)");
        }
        // 与 vision import 同一读取路径(bytes → UTF-8 字符串), 保证两边解析同一文本。
        var bytes = File.ReadAllBytes(full);
        var table = MbriCsvTable.Parse(session, Encoding.UTF8.GetString(bytes));
        if (MbriVisionDialect.Detect(table.Headers) is null)
        {
            throw new VisionEvidenceException($"{session}: {MbriVisionDialect.RejectionReason(table.Headers)}");
        }
        var manifest = InferManifest(session, table);
        var (frames, stat) = VisionEvidenceBuilder.ParseSession(session, table, manifest);
        // 源文件身份如实入账: import 的分文件统计不填 sha/bytes(选择清单已持有),
        // live 源本来就要读文件, 顺手把哈希/字节数补齐供报告与 sidecar 复算。
        stat = stat with
        {
            Dialect = MbriVisionDialect.HuntDetections,
            Sha256 = VisionReplayIO.Sha256Hex(bytes),
            Bytes = bytes.Length,
        };
        return new CsvStreamSource(full, session, manifest, frames, stat);
    }

    /// <summary>
    /// Releases every frame that has arrived by <paramref name="simTimeSeconds"/>.
    /// The gate uses the same arithmetic as the adapters' frame selection —
    /// `timestampMs &lt;= first + simT*1000` — so a frame can never be selected as
    /// "in window" while still unreleased (no rounding drift at the boundary).
    /// </summary>
    public int PumpUntil(double simTimeSeconds)
    {
        var deadlineMs = _sessionFirstTimestampMs + simTimeSeconds * 1000.0;
        var added = 0;
        while (_cursor < EvidenceFrames.Count && EvidenceFrames[_cursor].TimestampMs <= deadlineMs)
        {
            _releasedEvidence.Add(EvidenceFrames[_cursor]);
            _released.Add(VisionReplayFrames.ToReplayFrame(EvidenceFrames[_cursor]));
            _cursor++;
            added++;
        }
        return added;
    }

    private static VisionReplayManifest InferManifest(string session, MbriCsvTable table)
    {
        var sequence = table.IndexOf("sequence");
        var width = table.IndexOf("frame_width");
        var height = table.IndexOf("frame_height");
        for (var row = 0; row < table.Rows.Count; row++)
        {
            if (table.Text(row, sequence).Length == 0)
            {
                continue; // 预热行(无 sequence): 与 import 一致地丢弃
            }
            var frameWidth = (int)table.Number(row, width);
            var frameHeight = (int)table.Number(row, height);
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                throw new VisionEvidenceException(
                    $"{session} 行 {table.Rows[row].Line}: 帧尺寸 {frameWidth}x{frameHeight} 必须为正");
            }
            return new VisionReplayManifest
            {
                Label = session,
                ClassMapping = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["good"] = "buff",
                    ["bad"] = "debuff",
                },
                TimeBase = "vision_timestamp_ms 为采集主机 epoch 毫秒; live 桥按 (帧时间戳 − 首帧)/1000 s 缩放为 SimT",
                FrameWidth = frameWidth,
                FrameHeight = frameHeight,
            };
        }
        throw new VisionEvidenceException($"{session}: 不含任何帧行(全部是预热行), 无法建立 SimT 0 基准");
    }
}
