using System.Text.Json.Serialization;
using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>Per-source-file import statistics (audit trail).</summary>
public sealed record VisionImportFileStat
{
    public string Path { get; init; } = "";

    public string Sha256 { get; init; } = "";

    public long Bytes { get; init; }

    /// <summary>Recognized dialect, e.g. "mbri-hunt-detections".</summary>
    public string Dialect { get; init; } = "";

    public int Rows { get; init; }

    /// <summary>Rows before the first frame arrives (no sequence, no_data_or_stale); counted, never fabricated.</summary>
    public int WarmupRows { get; init; }

    /// <summary>Receive groups (sequence × receive time), including re-receives.</summary>
    public int ReceiveGroups { get; init; }

    /// <summary>Unique frames after collapsing re-receives.</summary>
    public int Frames { get; init; }

    public int Detections { get; init; }

    public int DuplicateReceives { get; init; }

    public double? FirstTimestampMs { get; init; }

    public double? LastTimestampMs { get; init; }

    /// <summary>vision_status census for this file.</summary>
    public Dictionary<string, int> StatusCounts { get; init; } = new();
}

/// <summary>Rejected input file with the reason it was excluded (R2 transparency).</summary>
public sealed record VisionRejection
{
    public string Path { get; init; } = "";

    public string Reason { get; init; } = "";
}

/// <summary>
/// vision-replay-v1 import report: used/ignored/rejected files with hashes,
/// dialects, per-file stats, the explicit class mapping and the honest
/// groundTruth=false / evidence_only grade. Archived in the repo; the
/// normalized frames stay in the local (uncommitted) evidence directory.
/// </summary>
public sealed record VisionImportReport : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    public string Schema { get; init; } = VisionReplaySchemas.VisionReplayFormat;

    public int SchemaVersion { get; init; } = 1;

    /// <summary>Volatile timestamp; excluded from <see cref="ContentSha256"/>.</summary>
    public string? GeneratedAt { get; init; }

    public string? ContentSha256 { get; init; }

    public string ToolVersion { get; init; } = "";

    /// <summary>SHA-256 of the exact selection manifest bytes, when imported by CLI.</summary>
    public string? ManifestSha256 { get; init; }

    public string Label { get; init; } = "";

    public string Source { get; init; } = "mbri-csv";

    public VisionModelRef? Model { get; init; }

    public Dictionary<string, string> ClassMapping { get; init; } = new();

    public string? Opponent { get; init; }

    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    public string? TimeBase { get; init; }

    public List<VisionImportFileStat> Files { get; init; } = [];

    public List<string> IgnoredFiles { get; init; } = [];

    public List<VisionRejection> RejectedFiles { get; init; } = [];

    /// <summary>Always false in Phase A: MBri CSVs carry no per-frame ground truth.</summary>
    public bool GroundTruth { get; init; }

    /// <summary>Always "evidence_only" in Phase A.</summary>
    public string Grade { get; init; } = VisionReplaySchemas.EvidenceOnly;

    /// <summary>Stable id of the produced evidence package (derived from its content hash).</summary>
    public string? EvidenceId { get; init; }

    /// <summary>SHA-256 of the canonical frames.jsonl bytes.</summary>
    public string? EvidenceSha256 { get; init; }

    /// <summary>Artifact name of the normalized frames inside the evidence directory.</summary>
    public string? FramesFile { get; init; }

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "vision import report: protocolVersion must not be empty.";
        }
        if (Schema != VisionReplaySchemas.VisionReplayFormat)
        {
            yield return $"vision import report: schema must be \"{VisionReplaySchemas.VisionReplayFormat}\".";
        }
        if (SchemaVersion != 1)
        {
            yield return $"vision import report: unsupported schemaVersion {SchemaVersion}.";
        }
        if (string.IsNullOrWhiteSpace(ToolVersion))
        {
            yield return "vision import report: toolVersion must be recorded.";
        }
        if (ManifestSha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            yield return "vision import report: manifestSha256 must be 64 hex chars when present.";
        }
        if (Files.Count == 0)
        {
            yield return "vision import report: files must list every consumed input.";
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            if (string.IsNullOrWhiteSpace(file?.Path))
            {
                yield return "vision import report: files need a path.";
                continue;
            }
            if (!seen.Add(file.Path))
            {
                yield return $"vision import report: duplicate source file '{file.Path}'.";
            }
        }
        foreach (var rejection in RejectedFiles ?? [])
        {
            if (rejection is null || string.IsNullOrWhiteSpace(rejection.Path) || string.IsNullOrWhiteSpace(rejection.Reason))
            {
                yield return "vision import report: rejected files need path and reason.";
            }
        }
        if (GroundTruth)
        {
            yield return "vision import report: groundTruth must be false for MBri CSVs (no per-frame labels).";
        }
        if (Grade != VisionReplaySchemas.EvidenceOnly)
        {
            yield return $"vision import report: grade must be \"{VisionReplaySchemas.EvidenceOnly}\" in Phase A.";
        }
        if (EvidenceSha256 is { } evidence && (evidence.Length != 64 || !evidence.All(Uri.IsHexDigit)))
        {
            yield return "vision import report: evidenceSha256 must be 64 hex chars when present.";
        }
        if (EvidenceId is { } id && string.IsNullOrWhiteSpace(id))
        {
            yield return "vision import report: evidenceId must not be empty when present.";
        }
    }
}
