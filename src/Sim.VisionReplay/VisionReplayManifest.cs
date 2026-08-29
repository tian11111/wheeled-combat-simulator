using System.Text.Json.Serialization;
using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>
/// Schema constants for the vision evidence line. Deliberately separate from
/// <see cref="ProtocolVersion"/>: this line is offline evidence only and never
/// enters Scenario/Snapshot/replay traffic (the replay header references the
/// evidence id/hash through additive nullable fields instead).
/// </summary>
public static class VisionReplaySchemas
{
    /// <summary>Evidence package + import report contract tag.</summary>
    public const string VisionReplayFormat = "vision-replay-v1";

    /// <summary>Evaluation (link quality + policy consumption) report contract tag.</summary>
    public const string VisionReplayReportFormat = "vision-replay-report-v1";

    /// <summary>Vision mode written to ReplayHeader.VisionMode when the replay adapter is injected.</summary>
    public const string VisionMode = "visionReplay";

    /// <summary>Allowed MBri vision_status values (kept verbatim from the source CSV).</summary>
    public static readonly string[] Statuses = ["target", "no_target", "error", "no_data_or_stale"];

    /// <summary>The evidence grade of every Phase A import: no per-frame ground truth exists.</summary>
    public const string EvidenceOnly = "evidence_only";
}

/// <summary>One explicitly selected source file in the selection manifest.</summary>
public sealed record VisionReplayFileSelection
{
    /// <summary>File name (or forward-slash relative path) resolved against the manifest directory / data dir.</summary>
    public string Path { get; init; } = "";

    public string Sha256 { get; init; } = "";

    public long Bytes { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            yield return "vision manifest file: path must be non-empty.";
        }
        if (Path.Contains('\\') || Path.Contains(':') || Path.StartsWith("/"))
        {
            yield return $"vision manifest file '{Path}': path must be a plain name or forward-slash relative path.";
        }
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            yield return $"vision manifest file '{Path}': sha256 must be 64 hex chars.";
        }
        if (Bytes < 0)
        {
            yield return $"vision manifest file '{Path}': bytes must be >= 0.";
        }
    }
}

/// <summary>Reference to the YOLO/model that produced the evidence (name + optional hash).</summary>
public sealed record VisionModelRef
{
    public string Name { get; init; } = "";

    public string? Sha256 { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "vision manifest model: name must be non-empty.";
        }
        if (Sha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            yield return "vision manifest model: sha256 must be 64 hex chars when present.";
        }
    }
}

/// <summary>
/// Selection manifest — the ONLY way the vision importer chooses files.
/// The good/bad → buff/debuff mapping must be explicit here; deriving semantics
/// from file names (good_*/bad_*) is forbidden (R2).
/// </summary>
public sealed record VisionReplayManifest
{
    /// <summary>Contract tag; must be "vision-replay-v1" (JSON key "schemaVersion").</summary>
    [JsonPropertyName("schemaVersion")]
    public string? Schema { get; init; } = VisionReplaySchemas.VisionReplayFormat;

    /// <summary>Provenance tag of the producing pipeline, e.g. "mbri-csv".</summary>
    public string Source { get; init; } = "mbri-csv";

    /// <summary>Vehicle/session batch label (audit only, never used for semantics).</summary>
    public string Label { get; init; } = "";

    public VisionModelRef? Model { get; init; }

    /// <summary>Explicit class mapping, e.g. {"good":"buff","bad":"debuff"}. Raw types outside this map are rejected.</summary>
    public Dictionary<string, string> ClassMapping { get; init; } = new();

    /// <summary>How `opponent` detections are produced; Phase A records "unavailable(ir-probe)".</summary>
    public string? Opponent { get; init; }

    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    /// <summary>Human-readable time-base description (audit only).</summary>
    public string? TimeBase { get; init; }

    public List<VisionReplayFileSelection> Files { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (Schema != VisionReplaySchemas.VisionReplayFormat)
        {
            yield return $"vision manifest: schema must be \"{VisionReplaySchemas.VisionReplayFormat}\".";
        }
        if (string.IsNullOrWhiteSpace(Label))
        {
            yield return "vision manifest: label (vehicle/session batch) is required.";
        }
        if (Source != "mbri-csv")
        {
            yield return $"vision manifest: source must be \"mbri-csv\" in Phase A, got '{Source}'.";
        }
        if (ClassMapping.Count == 0)
        {
            yield return "vision manifest: classMapping must be explicit (good→buff, bad→debuff); filename inference is forbidden.";
        }
        else
        {
            foreach (var (raw, mapped) in ClassMapping)
            {
                if (mapped is not ("buff" or "debuff" or "opponent" or "unknown"))
                {
                    yield return $"vision manifest: classMapping['{raw}'] maps to '{mapped}', which is not a normalized label.";
                }
            }
            if (ClassMapping.GetValueOrDefault("good") != "buff" || ClassMapping.GetValueOrDefault("bad") != "debuff")
            {
                yield return "vision manifest: classMapping must map good→buff and bad→debuff for MBri evidence.";
            }
        }
        if (FrameWidth <= 0 || FrameHeight <= 0)
        {
            yield return "vision manifest: frameWidth/frameHeight must be positive.";
        }
        foreach (var file in Files)
        {
            if (file is null)
            {
                yield return "vision manifest: files must not contain null entries.";
                continue;
            }
            foreach (var error in file.Validate())
            {
                yield return error;
            }
        }
        if (Files.Select(f => f?.Path).Distinct(StringComparer.Ordinal).Count() != Files.Count)
        {
            yield return "vision manifest: duplicate selected file.";
        }
        foreach (var error in Model?.Validate() ?? [])
        {
            yield return error;
        }
    }

    /// <summary>Selected file names in deterministic (ordinal) order.</summary>
    public IReadOnlyList<string> SelectedFiles()
        => Files
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Path))
            .Select(f => f.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
