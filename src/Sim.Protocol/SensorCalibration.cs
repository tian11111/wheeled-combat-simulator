using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>
/// sensor-calibration-v1: offline, versioned evidence for imported MBri sensor
/// judgment models (gray four-channel references/hysteresis, front dual-ADC
/// alignment, shovel hang/retract). Produced by the CLI import from explicitly
/// selected CSVs, validated once at entry, and replayed against the raw logs.
/// This contract NEVER enters Scenario/Snapshot/replay traffic; runtime
/// integration is a separate future task.
/// </summary>
public static class SensorCalibrationStatus
{
    /// <summary>Import succeeded but drift/mixed-batch/replay reasons bar runtime use.</summary>
    public const string EvidenceOnly = "evidence_only";

    /// <summary>Replay gate or source consistency failed; values are kept as evidence only.</summary>
    public const string Rejected = "rejected";

    public static bool IsValid(string? status) => status is EvidenceOnly or Rejected;
}

/// <summary>One source file consumed by the import, with content hash.</summary>
public sealed record SensorCalibrationFile
{
    /// <summary>Path relative to the data root, forward slashes (portable hashing).</summary>
    public string Path { get; init; } = "";

    /// <summary>Role tag: gray_model|gray_summary|gray_raw|front_adc_model|front_adc_summary|front_adc_raw|shovel_model|shovel_raw|config_snapshot.</summary>
    public string Role { get; init; } = "";

    public string Sha256 { get; init; } = "";

    public long Bytes { get; init; }

    /// <summary>Vehicle/session label recorded in the selection manifest.</summary>
    public string? CaptureLabel { get; init; }

    /// <summary>Explicit provenance group from the selection manifest.</summary>
    public string? Model { get; init; }

    public string? BatchId { get; init; }

    public string? CaptureDate { get; init; }

    /// <summary>Signal semantics used by this source, never inferred from a value.</summary>
    public string? Semantics { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            yield return "source file: path must be non-empty.";
        }
        if (Path.Contains('\\') || Path.Contains(":") || Path.StartsWith("/"))
        {
            yield return $"source file '{Path}': path must be root-relative with forward slashes.";
        }
        if (string.IsNullOrWhiteSpace(Role))
        {
            yield return $"source file '{Path}': role must be set.";
        }
        if (Sha256.Length is not (64) || !Sha256.All(Uri.IsHexDigit))
        {
            yield return $"source file '{Path}': sha256 must be 64 hex chars.";
        }
        if (Bytes < 0)
        {
            yield return $"source file '{Path}': bytes must be >= 0.";
        }
    }
}

/// <summary>
/// Explicit source grouping from the import manifest. A group may contain a
/// model file and its raw files, but files must belong to exactly one group.
/// This is deliberately manifest-owned: filesystem timestamps and filenames
/// are not treated as batch identity.
/// </summary>
public sealed record SensorCaptureGroup
{
    public string Model { get; init; } = "";
    public string BatchId { get; init; } = "";
    public string CaptureDate { get; init; } = "";
    public string VehicleId { get; init; } = "";
    public string Semantics { get; init; } = "";
    public List<string> Files { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (Model is not ("gray" or "frontAdc" or "shovel"))
        {
            yield return $"capture group: unknown model '{Model}'.";
        }
        if (string.IsNullOrWhiteSpace(BatchId))
        {
            yield return $"capture group '{Model}': batchId is required.";
        }
        if (!DateOnly.TryParseExact(CaptureDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
        {
            yield return $"capture group '{BatchId}': captureDate must be yyyy-MM-dd.";
        }
        if (string.IsNullOrWhiteSpace(VehicleId))
        {
            yield return $"capture group '{BatchId}': vehicleId is required; use 'unknown' when unavailable.";
        }
        if (string.IsNullOrWhiteSpace(Semantics))
        {
            yield return $"capture group '{BatchId}': semantics is required.";
        }
        if (Files.Count == 0)
        {
            yield return $"capture group '{BatchId}': files must be non-empty.";
        }
    }
}

/// <summary>Rejected input file with the reason it was excluded (R2 transparency).</summary>
public sealed record SensorCalibrationRejection
{
    public string Path { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>Per-channel gray judgment model (MBri GrayRiskModel parameters).</summary>
public sealed record GrayChannelModel
{
    /// <summary>"front"/"rear"/"left"/"right".</summary>
    public string Sensor { get; init; } = "";

    /// <summary>Median filter window: positive odd.</summary>
    public int FilterWindow { get; init; }

    public double NearEdgeEnter { get; init; }
    public double NearEdgeClear { get; init; }
    public double EdgeReference { get; init; }
    public double CenterReference { get; init; }
    public double WhiteReference { get; init; }
    public double SafeUpper { get; init; }
    public double WhiteLower { get; init; }
    public double WhiteEnter { get; init; }
    public double WhiteClear { get; init; }

    public IEnumerable<string> Validate()
    {
        if (Sensor is not ("front" or "rear" or "left" or "right"))
        {
            yield return $"gray channel: sensor must be front/rear/left/right, got '{Sensor}'.";
        }
        if (FilterWindow <= 0 || FilterWindow % 2 == 0)
        {
            yield return $"gray channel '{Sensor}': filterWindow must be a positive odd integer, got {FilterWindow}.";
        }
        var values = new[]
        {
            NearEdgeEnter, NearEdgeClear, EdgeReference, CenterReference,
            WhiteReference, SafeUpper, WhiteLower, WhiteEnter, WhiteClear,
        };
        if (values.Any(v => !double.IsFinite(v)))
        {
            yield return $"gray channel '{Sensor}': all references/thresholds must be finite.";
        }
        if (!(NearEdgeEnter > 0 && NearEdgeEnter < NearEdgeClear))
        {
            yield return $"gray channel '{Sensor}': near-edge thresholds must satisfy 0 < enter < clear.";
        }
        if (!(WhiteClear < WhiteEnter))
        {
            yield return $"gray channel '{Sensor}': white hysteresis requires clear < enter.";
        }
    }
}

/// <summary>Front dual-ADC alignment model (stored absolute-diff fields + ratio decision).</summary>
public sealed record FrontAdcModel
{
    public int FilterWindow { get; init; }
    public double SignalMin { get; init; }
    /// <summary>Stored-model left-minus-right diff bounds (kept as evidence).</summary>
    public double DiffLow { get; init; }
    public double DiffHigh { get; init; }
    /// <summary>Production ratio-model threshold from config snapshot, if supplied.
    /// The STORED imported model is the diff band (DiffLow/DiffHigh/SignalMin);
    /// replay gates run against the band model, and the ratio model is evidence
    /// of drift only (they encode opposite sign conventions). Null if no config.</summary>
    public double? RatioThreshold { get; init; }

    public IEnumerable<string> Validate()
    {
        if (FilterWindow <= 0 || FilterWindow % 2 == 0)
        {
            yield return $"front ADC: filterWindow must be positive odd, got {FilterWindow}.";
        }
        if (!double.IsFinite(SignalMin) || !(SignalMin >= 0))
        {
            yield return "front ADC: signalMin must be finite and >= 0.";
        }
        if (!double.IsFinite(DiffLow) || !double.IsFinite(DiffHigh) || !(DiffLow < DiffHigh))
        {
            yield return "front ADC: stored diff band must be finite with diffLow < diffHigh.";
        }
        if (RatioThreshold is { } ratio && (!double.IsFinite(ratio) || !(ratio > 0)))
        {
            yield return "front ADC: ratioThreshold, when given, must be finite and > 0.";
        }
    }
}

/// <summary>Under-shovel dual-ADC hang/retract model.</summary>
public sealed record ShovelModel
{
    public int FilterWindow { get; init; }
    /// <summary>Filtered min above this => hanging (悬空).</summary>
    public double HangEnter { get; init; }
    /// <summary>Filtered max below this => cleared (收回).</summary>
    public double HangClear { get; init; }

    public IEnumerable<string> Validate()
    {
        if (FilterWindow <= 0 || FilterWindow % 2 == 0)
        {
            yield return $"shovel: filterWindow must be positive odd, got {FilterWindow}.";
        }
        if (!double.IsFinite(HangEnter) || !double.IsFinite(HangClear) || !(HangEnter < HangClear))
        {
            yield return "shovel: finite thresholds with hangEnter < hangClear required.";
        }
    }
}

/// <summary>Raw-log replay outcome for one model family.</summary>
public sealed record ReplayMetrics
{
    /// <summary>Evaluated decision points (after median warm-up).</summary>
    public int Samples { get; init; }

    public int InvalidRows { get; init; }

    public double? FirstT { get; init; }

    public double? LastT { get; init; }

    /// <summary>Decision label → count (zones/white flags, left/forward/right, hang/clear).</summary>
    public Dictionary<string, int> DecisionCounts { get; init; } = new();

    /// <summary>Files whose replay failed the gate (empty when passing).</summary>
    public List<string> FailedFiles { get; init; } = [];

    /// <summary>Mislabeled/confusion mismatches on rows with recorded decisions, when available.</summary>
    public int LabeledMismatch { get; init; }

    public int LabeledTotal { get; init; }

    /// <summary>Whether this replay meets the declared gate for runtime candidacy.</summary>
    public bool Passed { get; init; }

    /// <summary>Per-file replay evidence retained for auditability.</summary>
    public List<ReplayFileMetrics> FileResults { get; init; } = [];
}

public sealed record ReplayFileMetrics
{
    public string File { get; init; } = "";
    public string? Expected { get; init; }
    public int TotalRows { get; init; }
    public int InvalidRows { get; init; }
    public int Samples { get; init; }
    public int LabeledMismatch { get; init; }
    public int LabeledTotal { get; init; }
    public Dictionary<string, int> DecisionCounts { get; init; } = new();
    public bool Passed { get; init; }
    public string? Reason { get; init; }
}

/// <summary>One model block: typed parameters + provenance + replay + status.</summary>
public sealed record SensorCalibrationModelBlock
{
    /// <summary>"gray" | "frontAdc" | "shovel".</summary>
    public string Model { get; init; } = "";

    public string Status { get; init; } = SensorCalibrationStatus.EvidenceOnly;

    /// <summary>Why this status; required for rejected.</summary>
    public string? Reason { get; init; }

    /// <summary>True when replay gates passed and stored/recomputed/config agree within tolerance.</summary>
    public bool RuntimeCandidate { get; init; }

    public List<string> SourceFiles { get; init; } = [];

    public ReplayMetrics? Replay { get; init; }

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (Model is not ("gray" or "frontAdc" or "shovel"))
        {
            yield return $"model block: unknown model '{Model}'.";
        }
        if (!SensorCalibrationStatus.IsValid(Status))
        {
            yield return $"model '{Model}': status must be evidence_only or rejected.";
        }
        if (Status == SensorCalibrationStatus.Rejected && string.IsNullOrWhiteSpace(Reason))
        {
            yield return $"model '{Model}': rejected requires a reason.";
        }
    }
}

/// <summary>One stored-vs-recomputed-vs-config field comparison.</summary>
public sealed record CalibrationDelta
{
    public string Model { get; init; } = "";
    public string Field { get; init; } = "";
    public double? Stored { get; init; }
    public double? Recomputed { get; init; }
    public double? Config { get; init; }
    public double? MaxDelta { get; init; }
    public bool Consistent { get; init; }

    /// <summary>Source files and explicit batch provenance for this finding.</summary>
    public List<string> SourceFiles { get; init; } = [];

    public List<string> BatchIds { get; init; } = [];

    public List<string> CaptureDates { get; init; } = [];

    public int? SampleCount { get; init; }

    public string? Semantics { get; init; }

    /// <summary>Primary audited cause: model_recompute_error, batch_mixing, semantic_difference, or data_quality_insufficient.</summary>
    public string? CauseCategory { get; init; }

    public string? Uncertainty { get; init; }

    public string? Decision { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Gray-only flag: coordinates were absent so a GrayGridMap cannot be built (R1/AC5).</summary>
public sealed record GrayModelData
{
    public List<GrayChannelModel> Channels { get; init; } = [];

    /// <summary>Always false for MBri gray CSVs (no x/y/th columns).</summary>
    public bool CoordinateData { get; init; }

    public IEnumerable<string> Validate()
    {
        if (CoordinateData)
        {
            yield return "gray: coordinateData must be false for MBri gray CSVs (no field coordinates).";
        }
        var sensors = Channels.Select(c => c.Sensor).ToList();
        foreach (var expected in new[] { "front", "rear", "left", "right" })
        {
            if (!sensors.Contains(expected))
            {
                yield return $"gray: channel '{expected}' missing (四路必须齐全).";
            }
        }
        foreach (var channel in Channels)
        {
            foreach (var error in channel.Validate())
            {
                yield return error;
            }
        }
    }
}

/// <summary>
/// The full sensor-calibration-v1 report. Validate once at load; the CLI must
/// produce it from an explicit selection manifest only.
/// </summary>
public sealed record SensorCalibrationReport : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Contract tag; must be "sensor-calibration-v1".</summary>
    public string? Schema { get; init; }

    public int SchemaVersion { get; init; } = 1;

    /// <summary>Volatile timestamp; excluded from <see cref="ContentSha256"/>.</summary>
    public string? GeneratedAt { get; init; }

    public string? ContentSha256 { get; init; }

    public string ToolVersion { get; init; } = "";

    /// <summary>SHA-256 of the exact selection manifest bytes, when imported by CLI.</summary>
    public string? ManifestSha256 { get; init; }

    /// <summary>Manifest vehicle/session label.</summary>
    public string Label { get; init; } = "";

    public List<SensorCalibrationFile> Files { get; init; } = [];

    public List<string> IgnoredFiles { get; init; } = [];

    public List<SensorCalibrationRejection> RejectedFiles { get; init; } = [];

    public List<SensorCaptureGroup> CaptureGroups { get; init; } = [];

    public GrayModelData? Gray { get; init; }

    public FrontAdcModel? FrontAdc { get; init; }

    public ShovelModel? Shovel { get; init; }

    public List<SensorCalibrationModelBlock> Blocks { get; init; } = [];

    public List<CalibrationDelta> Comparison { get; init; } = [];

    /// <summary>True when stored == recomputed within tolerance for every field.</summary>
    public bool BatchConsistent { get; init; }

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "sensor-calibration: protocolVersion must not be empty.";
        }
        if (Schema != ProtocolVersion.SensorCalibrationFormat)
        {
            yield return $"sensor-calibration: schema must be \"{ProtocolVersion.SensorCalibrationFormat}\".";
        }
        if (SchemaVersion != 1)
        {
            yield return $"sensor-calibration: unsupported schemaVersion {SchemaVersion}.";
        }
        if (string.IsNullOrWhiteSpace(ToolVersion))
        {
            yield return "sensor-calibration: toolVersion must be recorded.";
        }
        if (ManifestSha256 is { } manifestHash && (manifestHash.Length != 64 || !manifestHash.All(Uri.IsHexDigit)))
        {
            yield return "sensor-calibration: manifestSha256 must be 64 hex chars when present.";
        }
        if (Files is null || Files.Count == 0)
        {
            yield return "sensor-calibration: files must list every consumed input.";
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Files)
            {
                if (file is null)
                {
                    yield return "sensor-calibration: files must not contain null entries.";
                    continue;
                }
                foreach (var error in file.Validate())
                {
                    yield return error;
                }
                if (!seen.Add(file.Path))
                {
                    yield return $"sensor-calibration: duplicate source file '{file.Path}'.";
                }
            }
        }
        foreach (var rejection in RejectedFiles ?? [])
        {
            if (rejection is null || string.IsNullOrWhiteSpace(rejection.Path) || string.IsNullOrWhiteSpace(rejection.Reason))
            {
                yield return "sensor-calibration: rejected files need path and reason.";
            }
        }
        foreach (var group in CaptureGroups ?? [])
        {
            if (group is null)
            {
                yield return "sensor-calibration: capture groups must not contain null entries.";
                continue;
            }
            foreach (var error in group.Validate())
            {
                yield return error;
            }
        }
        foreach (var error in Gray?.Validate() ?? [])
        {
            yield return error;
        }
        foreach (var error in FrontAdc?.Validate() ?? [])
        {
            yield return error;
        }
        foreach (var error in Shovel?.Validate() ?? [])
        {
            yield return error;
        }
        var models = Blocks?.Select(b => b.Model).ToList() ?? [];
        foreach (var expected in new[] { "gray", "frontAdc", "shovel" })
        {
            if (!models.Contains(expected))
            {
                yield return $"sensor-calibration: model block '{expected}' missing.";
            }
        }
        foreach (var block in Blocks ?? [])
        {
            foreach (var error in block.Validate())
            {
                yield return error;
            }
        }
        foreach (var delta in Comparison ?? [])
        {
            if (delta is null || string.IsNullOrWhiteSpace(delta.Model) || string.IsNullOrWhiteSpace(delta.Field))
            {
                yield return "sensor-calibration: comparison entries need model and field.";
            }
        }
    }
}
