using System.Text;
using Sim.Core;
using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>
/// Hash-locked read of one vision-replay-v1 evidence directory
/// (<see cref="VisionReplayIO.FramesFileName"/> + <see cref="VisionReplayIO.ImportReportFileName"/>).
/// The pre-flight and the canonical-frame → adapter-mapping live here exactly once,
/// so `vision evaluate` (CLI) and the desktop `visionReplay` vision source consume
/// the same package with the same rules — reuse, not a second copy:
/// missing directory/files, an invalid import report, a report without
/// evidenceSha256 or a hash mismatch all throw <see cref="VisionEvidenceException"/>;
/// nothing ever falls back silently to a different vision source.
///
/// Loaded frames are immutable; the adapter carries a per-match consumption ledger
/// and the SimT 0 anchor, so <see cref="CreateAdapter"/> builds a NEW adapter for
/// every match — the same "never reuse across matches" discipline as the live bridge.
/// </summary>
public sealed record VisionEvidencePackage
{
    /// <summary>Directory the package was read from (audit/provenance).</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>SHA-256 of the exact frames.jsonl bytes — the package's identity.</summary>
    public required string EvidenceSha256 { get; init; }

    /// <summary>SHA-256 of the archived import report bytes (provenance).</summary>
    public required string ImportReportSha256 { get; init; }

    public required VisionImportReport ImportReport { get; init; }

    /// <summary>Every canonical frame of the package, in file order.</summary>
    public required IReadOnlyList<VisionFrameRecord> Frames { get; init; }

    /// <summary>Session names in first-appearance order (the `--session` choices).</summary>
    public required IReadOnlyList<string> Sessions { get; init; }

    /// <summary>Evidence id: taken from the archived report, derived from the content hash when absent.</summary>
    public string EvidenceId => ImportReport.EvidenceId ?? VisionReplayIO.EvidenceId(EvidenceSha256);

    /// <summary>
    /// Default session selection (shared with the CLI): the first file the import
    /// report lists, else the package's first session. An empty package yields ""
    /// and <see cref="SelectSession"/> reports it instead of crashing.
    /// </summary>
    public string DefaultSession => ImportReport.Files.FirstOrDefault()?.Path ?? Sessions.FirstOrDefault() ?? "";

    /// <summary>
    /// Loads and verifies one evidence directory. Failures throw
    /// <see cref="VisionEvidenceException"/> with the CLI's message wording.
    /// </summary>
    public static VisionEvidencePackage Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!System.IO.Directory.Exists(directory))
        {
            throw new VisionEvidenceException($"证据目录不存在: {directory}");
        }
        var framesPath = Path.Combine(directory, VisionReplayIO.FramesFileName);
        var importReportPath = Path.Combine(directory, VisionReplayIO.ImportReportFileName);
        if (!File.Exists(framesPath) || !File.Exists(importReportPath))
        {
            throw new VisionEvidenceException(
                $"证据目录缺少 {VisionReplayIO.FramesFileName} / {VisionReplayIO.ImportReportFileName}: {directory}");
        }
        var framesBytes = File.ReadAllBytes(framesPath);
        var evidenceSha256 = VisionReplayIO.Sha256Hex(framesBytes);
        var importReport = ProtocolJson.Deserialize<VisionImportReport>(File.ReadAllText(importReportPath));
        if (importReport is null)
        {
            throw new VisionEvidenceException($"导入报告为空: {importReportPath}");
        }
        var importErrors = importReport.Validate().ToList();
        if (importErrors.Count > 0)
        {
            throw new VisionEvidenceException($"导入报告无效: {string.Join(" ", importErrors)}");
        }
        if (importReport.EvidenceSha256 is null)
        {
            throw new VisionEvidenceException("导入报告缺少 evidenceSha256, 无法哈希锁定证据包");
        }
        if (importReport.EvidenceSha256 != evidenceSha256)
        {
            throw new VisionEvidenceException(
                $"证据包哈希不一致: 报告 {importReport.EvidenceSha256} 实际 {evidenceSha256}");
        }
        var frames = VisionReplayIO.ParseFrames(Encoding.UTF8.GetString(framesBytes));
        return new VisionEvidencePackage
        {
            DirectoryPath = directory,
            EvidenceSha256 = evidenceSha256,
            ImportReportSha256 = VisionReplayIO.Sha256Hex(File.ReadAllText(importReportPath)),
            ImportReport = importReport,
            Frames = frames,
            Sessions = frames.Select(f => f.Session).Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>
    /// Resolves a session name to one the package actually contains. A null
    /// <paramref name="session"/> takes <see cref="DefaultSession"/>; an unknown one
    /// (or a package without frames) is rejected with the available choices listed.
    /// </summary>
    public string SelectSession(string? session)
    {
        if (Sessions.Count == 0)
        {
            throw new VisionEvidenceException($"证据包不含任何帧: {DirectoryPath}");
        }
        var requested = session ?? DefaultSession;
        if (!Sessions.Contains(requested, StringComparer.Ordinal))
        {
            throw new VisionEvidenceException(
                $"会话 '{requested}' 不在证据包内 (可用: {string.Join(", ", Sessions)})");
        }
        return requested;
    }

    /// <summary>
    /// Builds a fresh <see cref="VisionReplayAdapter"/> over one session's canonical
    /// frames (the same mapping the CLI evaluates with). One adapter per match:
    /// the consumption ledger and the SimT 0 anchor are not shareable across matches.
    /// </summary>
    public VisionReplayAdapter CreateAdapter(string session, double maxAgeMs)
        => new(
            VisionReplayFrames.ToReplayFrames(Frames.Where(f => f.Session == session)),
            EvidenceId,
            EvidenceSha256,
            maxAgeMs);
}
