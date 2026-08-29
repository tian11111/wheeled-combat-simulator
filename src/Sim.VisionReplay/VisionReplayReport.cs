using System.Text.Json.Serialization;
using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>Min/p50/p95/max distribution summary (Python-style percentile semantics).</summary>
public sealed record VisionDistribution
{
    public int Count { get; init; }

    public double? Min { get; init; }

    public double? P50 { get; init; }

    public double? P95 { get; init; }

    public double? Max { get; init; }
}

/// <summary>Target retention across consecutive target frames (keep/保持时长).</summary>
public sealed record VisionTargetRetention
{
    /// <summary>Number of consecutive target-frame runs.</summary>
    public int Runs { get; init; }

    public int LongestRunFrames { get; init; }

    public double MeanRunFrames { get; init; }

    /// <summary>Mean run duration in seconds, estimated from frame timestamps.</summary>
    public double MeanRunSeconds { get; init; }
}

/// <summary>
/// Layer 1 — link quality metrics computable WITHOUT ground truth. These only
/// describe the evidence itself (rates, gaps, latency, jitter); they never
/// imply recognition accuracy.
/// </summary>
public sealed record VisionLinkQuality
{
    public int Sessions { get; init; }

    public int Frames { get; init; }

    /// <summary>vision_status census across all evaluated frames.</summary>
    public Dictionary<string, int> StatusCounts { get; init; } = new();

    /// <summary>Fractions of frames per status relative to all frames.</summary>
    public double ValidRate { get; init; }

    public double NoTargetRate { get; init; }

    public double ErrorRate { get; init; }

    public double NoDataOrStaleRate { get; init; }

    /// <summary>sequence gap (frame[i+1] − frame[i]) → count; gap 1 means consecutive.</summary>
    public Dictionary<long, int> SequenceGapHistogram { get; init; } = new();

    public VisionDistribution? Fps { get; init; }

    public VisionDistribution? InferenceMs { get; init; }

    public VisionTargetRetention? TargetRetention { get; init; }

    /// <summary>Count of consecutive target frames where the selected label flipped (buff↔debuff jitter).</summary>
    public int SelectionFlips { get; init; }

    /// <summary>Session-relative ms of the first target frame with a selection (min across sessions).</summary>
    public double? FirstValidDetectionMs { get; init; }
}

/// <summary>One evidence frame's consumption ledger entry from the policy replay.</summary>
public sealed record VisionFrameConsumption
{
    public string Session { get; init; } = "";

    public long Sequence { get; init; }

    public string Status { get; init; } = "";

    /// <summary>How many classify calls (both roles) consumed this frame.</summary>
    public int Consumed { get; init; }

    /// <summary>First consuming role, when any.</summary>
    public string? FirstConsumedBy { get; init; }

    /// <summary>Sim time (s) of the first consumption, when any.</summary>
    public double? FirstConsumedSimT { get; init; }

    /// <summary>Detection label handed to the FSM on first consumption (or the unknown reason code).</summary>
    public string? FirstResult { get; init; }
}

/// <summary>
/// Layer 3 — policy consumption: proves the vision→FSM data flow with a
/// deterministic injected-engine replay. This is NOT a claim of real-match
/// performance; the fingerprint exists to prove same-evidence replays are
/// bit-identical.
/// </summary>
public sealed record VisionPolicyConsumption
{
    public string ScenarioId { get; init; } = "";

    public long Seed { get; init; }

    public long Ticks { get; init; }

    public Scores FinalScores { get; init; } = new();

    public string? DoneReason { get; init; }

    public string VisionMode { get; init; } = VisionReplaySchemas.VisionMode;

    public double MaxAgeMs { get; init; }

    /// <summary>Which session of the evidence package the adapter replayed.</summary>
    public string Session { get; init; } = "";

    /// <summary>Total classify calls across both roles.</summary>
    public int ClassifyCalls { get; init; }

    /// <summary>Calls that consumed an in-window frame.</summary>
    public int ConsumedCalls { get; init; }

    /// <summary>Calls that returned unknown with a reason code (stale/error/no_frame/no_target/no_selection).</summary>
    public int UnknownCalls { get; init; }

    /// <summary>unknown reason code → count.</summary>
    public Dictionary<string, int> UnknownReasons { get; init; } = new();

    /// <summary>Normalized detection labels handed to the FSM → count (buff/debuff/unknown).</summary>
    public Dictionary<string, int> FsmDetections { get; init; } = new();

    public List<VisionFrameConsumption> Frames { get; init; } = [];

    /// <summary>Key FSM state transitions: "role:FROM→TO" in order.</summary>
    public List<string> StateTransitions { get; init; } = [];

    /// <summary>Number of committed events.</summary>
    public int EventCount { get; init; }

    /// <summary>SHA-256 over the ordered "seq|tick|type|cls|msg" event fingerprint lines.</summary>
    public string PolicyFingerprint { get; init; } = "";
}

/// <summary>
/// Layer 2 — detection quality vs. human ground truth. Phase A has no
/// per-frame labels, so this layer is always not_run(no_ground_truth); the
/// confusion/IoU fields are reserved for Phase B.
/// </summary>
public sealed record VisionDetectionQuality
{
    public string Status { get; init; } = "not_run(no_ground_truth)";

    /// <summary>Phase B reserved: per-predicted-label confusion row.</summary>
    public Dictionary<string, VisionConfusionRow>? Confusion { get; init; }

    public double? Precision { get; init; }

    public double? Recall { get; init; }

    public double? F1 { get; init; }

    public double? MeanIoU { get; init; }

    public double? MeanCenterErrorPx { get; init; }

    public double? MeanOffsetError { get; init; }

    public string? Note { get; init; }
}

public sealed record VisionConfusionRow
{
    public int TruePositive { get; init; }

    public int FalsePositive { get; init; }

    public int FalseNegative { get; init; }
}

/// <summary>Session-level development/holdout split status (R4: never rename development data).</summary>
public sealed record VisionHoldout
{
    public string Status { get; init; } = "not_run(no_ground_truth)";

    public string? DevelopmentSessions { get; init; }

    public string? HoldoutSessions { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// vision-replay-report-v1: the offline evaluation report. Conclusion is
/// always vision=random_stub (evidence_only) in Phase A — replaying the
/// model's own CSV output never validates recognition accuracy, so
/// fidelity.json stays untouched.
/// </summary>
public sealed record VisionReplayReport : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    public string Schema { get; init; } = VisionReplaySchemas.VisionReplayReportFormat;

    public int SchemaVersion { get; init; } = 1;

    /// <summary>Volatile timestamp; excluded from <see cref="ContentSha256"/>.</summary>
    public string? GeneratedAt { get; init; }

    public string? ContentSha256 { get; init; }

    public string ToolVersion { get; init; } = "";

    public string EvidenceId { get; init; } = "";

    public string EvidenceSha256 { get; init; } = "";

    public string? ImportReportSha256 { get; init; }

    public string Label { get; init; } = "";

    public string Source { get; init; } = "mbri-csv";

    public VisionModelRef? Model { get; init; }

    public Dictionary<string, string> ClassMapping { get; init; } = new();

    public VisionLinkQuality? Link { get; init; }

    public VisionPolicyConsumption? Policy { get; init; }

    public VisionDetectionQuality Detection { get; init; } = new();

    public VisionHoldout Holdout { get; init; } = new();

    /// <summary>Always "evidence_only" in Phase A.</summary>
    public string Grade { get; init; } = VisionReplaySchemas.EvidenceOnly;

    /// <summary>Honest conclusion line, e.g. "vision=random_stub (evidence_only)".</summary>
    public string Conclusion { get; init; } = "";

    /// <summary>Always false in Phase A.</summary>
    public bool GroundTruth { get; init; }

    /// <summary>Minimal Phase B re-capture / re-label checklist (采集/标注/门槛建议).</summary>
    public IReadOnlyList<string> PhaseB { get; init; } = [];

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "vision replay report: protocolVersion must not be empty.";
        }
        if (Schema != VisionReplaySchemas.VisionReplayReportFormat)
        {
            yield return $"vision replay report: schema must be \"{VisionReplaySchemas.VisionReplayReportFormat}\".";
        }
        if (SchemaVersion != 1)
        {
            yield return $"vision replay report: unsupported schemaVersion {SchemaVersion}.";
        }
        if (EvidenceSha256.Length != 64 || !EvidenceSha256.All(Uri.IsHexDigit))
        {
            yield return "vision replay report: evidenceSha256 must be 64 hex chars.";
        }
        if (string.IsNullOrWhiteSpace(EvidenceId))
        {
            yield return "vision replay report: evidenceId must be recorded.";
        }
        if (Link is null)
        {
            yield return "vision replay report: link quality layer must be present.";
        }
        if (Policy is null)
        {
            yield return "vision replay report: policy consumption layer must be present.";
        }
        if (Detection.Status != "not_run(no_ground_truth)" && Detection.Status != "pass" && Detection.Status != "fail")
        {
            yield return "vision replay report: detection quality status must be not_run(no_ground_truth), pass or fail.";
        }
        if (GroundTruth)
        {
            yield return "vision replay report: groundTruth must be false in Phase A.";
        }
        if (Grade != VisionReplaySchemas.EvidenceOnly)
        {
            yield return $"vision replay report: grade must be \"{VisionReplaySchemas.EvidenceOnly}\" in Phase A.";
        }
        if (string.IsNullOrWhiteSpace(Conclusion))
        {
            yield return "vision replay report: conclusion must be recorded.";
        }
    }
}
