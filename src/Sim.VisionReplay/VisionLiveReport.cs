using System.Text.Json.Serialization;
using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>
/// vision-live-bridge-report-v1 — one `vision live` session (a CSV-simulated
/// live source feeding <c>LiveVisionBridge</c> through a whole match).
///
/// The report is honest by construction: the link layer only describes the
/// frame stream (ages, stale rate, consumption), the equivalence layer proves
/// the same frames through <see cref="Core.VisionReplayAdapter"/> consume
/// identically (a structural claim, not a similarity score), and the
/// baseline/diff layer is explicitly SUMMARY-LEVEL — the two runs cannot be
/// compared bit-for-bit because only the default classifyRate stub draws
/// <see cref="Core.VisionContext.Random"/>, so the two matches consume different
/// random streams. Grade stays evidence_only: this proves strategy behaviour
/// under real vision quality, never recognition accuracy.
/// </summary>
public sealed record VisionLiveSource
{
    /// <summary>Source kind: "csv" (SimT-scaled CSV stream) or "process" (wall-clock JSONL subprocess).</summary>
    public string Kind { get; init; } = "csv";

    /// <summary>Provenance: absolute path of the loaded CSV, or the external process command line.</summary>
    public string Path { get; init; } = "";

    /// <summary>Session key of the evidence package (CSV file name, or the process session label).</summary>
    public string Session { get; init; } = "";

    /// <summary>SHA-256 of the source bytes; empty for a process source (no source file exists).</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Source byte count; 0 for a process source (no source file exists).</summary>
    public long Bytes { get; init; }

    /// <summary>Recognized dialect, e.g. "mbri-hunt-detections".</summary>
    public string Dialect { get; init; } = "";

    /// <summary>Frames of the whole session (before SimT release).</summary>
    public int Frames { get; init; }

    public int Detections { get; init; }

    /// <summary>Warm-up rows dropped before the first frame (counted, never fabricated).</summary>
    public int WarmupRows { get; init; }

    public int DuplicateReceives { get; init; }

    /// <summary>Session length in seconds: (last − first) timestamp.</summary>
    public double DurationSeconds { get; init; }

    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    /// <summary>Explicit raw → label mapping; silent defaults are never applied.</summary>
    public Dictionary<string, string> ClassMapping { get; init; } = new();

    /// <summary>SimT scaling semantics of this source (audit string).</summary>
    public string? TimeBase { get; init; }
}

/// <summary>
/// External vision process facts (present only when <see cref="VisionLiveSource.Kind"/>
/// is "process"): the command line, how the child ended, and the stream faults it
/// produced. A dead stream is NOT an exception — the bridge keeps serving stale
/// frames and records the reason — so these counters are how a session reports it.
/// </summary>
public sealed record VisionLiveProcess
{
    /// <summary>Full command line the session started.</summary>
    public string Command { get; init; } = "";

    /// <summary>Session label of the process source (the sidecar's frames[0].session).</summary>
    public string Session { get; init; } = "";

    /// <summary>Lifecycle state at report time: running|exited|faulted.</summary>
    public string State { get; init; } = "running";

    /// <summary>Child exit code; null when it had not exited (a live inference process is killed at the end).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Stream faults: malformed JSONL lines plus a non-zero exit / read failure.</summary>
    public long Faults { get; init; }

    /// <summary>Rejected JSONL lines (contract violations — never turned into frames).</summary>
    public int RejectedLines { get; init; }

    /// <summary>
    /// Re-received (timestampMs, sequence) frames whose content differed from the
    /// first delivery. The bridge keeps the first delivery (never kills the
    /// engine); the import path hard-rejects the same input, so non-zero here is
    /// a real source-side protocol violation.
    /// </summary>
    public int ConflictingDuplicates { get; init; }

    /// <summary>Most recent fault description; null when the stream stayed clean.</summary>
    public string? LastFault { get; init; }

    /// <summary>
    /// Wall-clock alignment of the engine stepping: 1.0 when `--realtime 1x` was used.
    /// 0 means the engine ran faster than the external stream, so most frames arrive
    /// after the classify calls that could have served them (the report stays honest).
    /// </summary>
    public double Realtime { get; init; }
}

/// <summary>
/// Link quality of the live session: how fresh the frames the camera cache
/// served were, how often the window had gone stale, and how the FSM consumed
/// them. Frame ages are computed from SimT by the bridge — the service's
/// self-reported age is never trusted.
/// </summary>
public sealed record VisionLiveLink
{
    /// <summary>Total classify calls across both roles.</summary>
    public int ClassifyCalls { get; init; }

    /// <summary>Calls served by an in-window frame (the camera cache located a frame, stale excluded).</summary>
    public int ServedCalls { get; init; }

    /// <summary>Calls that returned unknown with a reason code (stale/error/no_target/no_selection/no_frame).</summary>
    public int UnknownCalls { get; init; }

    /// <summary>Calls whose located frame was out of the maxAge window.</summary>
    public int StaleCalls { get; init; }

    /// <summary>Calls that handed the FSM a real detection (buff/debuff).</summary>
    public int DetectionCalls { get; init; }

    /// <summary>stale calls / classify calls (0 when there was no call).</summary>
    public double StaleRate { get; init; }

    /// <summary>unknown reason code → count.</summary>
    public Dictionary<string, int> UnknownReasons { get; init; } = new();

    /// <summary>Normalized labels handed to the FSM → count (buff/debuff/unknown).</summary>
    public Dictionary<string, int> FsmDetections { get; init; } = new();

    /// <summary>Age (ms) distribution over every call that located a frame — stale ages included (the camera's actual age).</summary>
    public VisionDistribution? FrameAgeMs { get; init; }

    /// <summary>Age (ms) distribution over calls that handed the FSM a real detection.</summary>
    public VisionDistribution? DetectionAgeMs { get; init; }

    /// <summary>Service self-reported fps distribution (audit; never used for staleness).</summary>
    public VisionDistribution? ReportedFps { get; init; }

    public VisionDistribution? ReportedInferenceMs { get; init; }

    /// <summary>Distinct delivered frames actually served in-window at least once.</summary>
    public int ServedFrames { get; init; }

    /// <summary>Frames the source delivered during the match (the sidecar working set).</summary>
    public int DeliveredFrames { get; init; }

    /// <summary>Delivered frames never served in-window — the difference decision③ reports.</summary>
    public int UnservedFrames { get; init; }

    /// <summary>SimT (s) of the first served frame; null when nothing was served.</summary>
    public double? FirstServeSimT { get; init; }

    public double? LastServeSimT { get; init; }
}

/// <summary>
/// Equivalence confirmation: the very frames the live session delivered, read
/// back from the sidecar package and replayed by <see cref="Core.VisionReplayAdapter"/>,
/// must produce the same consumption ledger and the same match. This is the
/// structural guarantee of the shared <c>VisionFrameSelector</c>, evaluated on
/// the real fixture instead of a synthetic stream.
/// </summary>
public sealed record VisionLiveEquivalence
{
    /// <summary>Reference implementation the live ledger is compared against.</summary>
    public string Reference { get; init; } = Core.VisionReplayAdapter.ModeName;

    /// <summary>Live and replayed classify calls must agree record for record.</summary>
    public bool ConsumptionSequenceMatches { get; init; }

    public bool EventFingerprintMatches { get; init; }

    public bool ScoresMatch { get; init; }

    public int LiveCalls { get; init; }

    public int ReplayCalls { get; init; }

    /// <summary>SHA-256 over the ordered ledger lines of the live session.</summary>
    public string LiveLedgerSha256 { get; init; } = "";

    /// <summary>SHA-256 over the ordered ledger lines of the replayed sidecar session.</summary>
    public string ReplayLedgerSha256 { get; init; } = "";

    /// <summary>First record that diverged ("#i live=… replay=…"), null when the ledgers match.</summary>
    public string? FirstDivergence { get; init; }

    /// <summary>Honest one-line verdict.</summary>
    public string Conclusion { get; init; } = "";
}

/// <summary>Sidecar evidence package written for this live session (decision③: the frames the source delivered).</summary>
public sealed record VisionLiveSidecar
{
    /// <summary>Absolute package directory.</summary>
    public string Directory { get; init; } = "";

    public string FramesFile { get; init; } = VisionReplayIO.FramesFileName;

    public string ImportReportFile { get; init; } = VisionReplayIO.ImportReportFileName;

    public string EvidenceId { get; init; } = "";

    public string EvidenceSha256 { get; init; } = "";

    /// <summary>Frames inside the package (= frames the source delivered; the session first frame is always included).</summary>
    public int Frames { get; init; }

    /// <summary>Package frames that were served in-window at least once.</summary>
    public int ServedFrames { get; init; }

    public int UnservedFrames { get; init; }

    /// <summary>SimT 0 anchor the package is locked to (the source session's first frame).</summary>
    public double AnchorTimestampMs { get; init; }
}

/// <summary>Summary of one full match run (the live session or the default-vision baseline).</summary>
public sealed record VisionLiveRun
{
    /// <summary>Vision mode of this run: "liveBridge" or "default(classifyRate)".</summary>
    public string VisionMode { get; init; } = "";

    public long Seed { get; init; }

    public long Ticks { get; init; }

    public Scores FinalScores { get; init; } = new();

    public string? DoneReason { get; init; }

    /// <summary>Committed events by kind (snake_case EventKind → count).</summary>
    public Dictionary<string, int> EventKinds { get; init; } = new();

    public int EventCount { get; init; }

    /// <summary>SHA-256 over the ordered "seq|tick|type|cls|msg" event fingerprint lines.</summary>
    public string EventFingerprint { get; init; } = "";
}

/// <summary>
/// Summary-level baseline diff. <see cref="VisionLiveRun"/> diff values are
/// computed as baseline − live; the note states why bit-for-bit comparison is
/// not possible (different RNG consumption).
/// </summary>
public sealed record VisionLiveBaselineDiff
{
    /// <summary>baseline.Us − live.Us.</summary>
    public double ScoreUs { get; init; }

    /// <summary>baseline.Them − live.Them.</summary>
    public double ScoreThem { get; init; }

    /// <summary>baseline.Ticks − live.Ticks.</summary>
    public long Ticks { get; init; }

    /// <summary>baseline.EventCount − live.EventCount.</summary>
    public int EventCount { get; init; }

    /// <summary>Event kind → (baseline − live) count, including kinds present in only one run.</summary>
    public Dictionary<string, int> EventKinds { get; init; } = new();

    /// <summary>Explicit non-bit-comparison note.</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// vision-live-bridge-report-v1 top level: source / link / equivalence / sidecar
/// / live / baseline / diff / honest grade fields.
/// </summary>
public sealed record VisionLiveBridgeReport : IProtocolMessage
{
    [JsonPropertyName("protocolVersion")]
    public string Version { get; init; } = ProtocolVersion.Current;

    public string Schema { get; init; } = VisionReplaySchemas.VisionLiveReportFormat;

    public int SchemaVersion { get; init; } = 1;

    /// <summary>Volatile timestamp; excluded from <see cref="ContentSha256"/>.</summary>
    public string? GeneratedAt { get; init; }

    public string? ContentSha256 { get; init; }

    public string ToolVersion { get; init; } = "";

    public string ScenarioId { get; init; } = "";

    public long Seed { get; init; }

    public double MaxAgeMs { get; init; }

    public VisionLiveSource? Source { get; init; }

    /// <summary>External-process facts; present exactly when the source kind is "process".</summary>
    public VisionLiveProcess? Process { get; init; }

    public VisionLiveLink? Link { get; init; }

    public VisionLiveEquivalence? Equivalence { get; init; }

    public VisionLiveSidecar? Sidecar { get; init; }

    public VisionLiveRun? Live { get; init; }

    public VisionLiveRun? Baseline { get; init; }

    public VisionLiveBaselineDiff? Diff { get; init; }

    /// <summary>Always "evidence_only" (Phase A honesty rule).</summary>
    public string Grade { get; init; } = VisionReplaySchemas.EvidenceOnly;

    /// <summary>Always false: MBri CSVs carry no per-frame ground truth.</summary>
    public bool GroundTruth { get; init; }

    /// <summary>Honest conclusion line.</summary>
    public string Conclusion { get; init; } = "";

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Version))
        {
            yield return "vision live report: protocolVersion must not be empty.";
        }
        if (Schema != VisionReplaySchemas.VisionLiveReportFormat)
        {
            yield return $"vision live report: schema must be \"{VisionReplaySchemas.VisionLiveReportFormat}\".";
        }
        if (SchemaVersion != 1)
        {
            yield return $"vision live report: unsupported schemaVersion {SchemaVersion}.";
        }
        if (string.IsNullOrWhiteSpace(ToolVersion))
        {
            yield return "vision live report: toolVersion must be recorded.";
        }
        if (Source is null)
        {
            yield return "vision live report: source section must be present.";
        }
        else if (string.IsNullOrWhiteSpace(Source.Session) || Source.Frames <= 0)
        {
            yield return "vision live report: source must name a session with at least one frame.";
        }
        else if (Source.ClassMapping.Count == 0)
        {
            yield return "vision live report: source classMapping must be explicit.";
        }
        else if (Source.Kind is not ("csv" or "process"))
        {
            yield return $"vision live report: source kind '{Source.Kind}' 未知 (csv|process)。";
        }
        else if (Source.Kind == "process" && Process is null)
        {
            yield return "vision live report: 进程源必须带 process 分区(命令/状态/故障如实入账)。";
        }
        if (Link is null)
        {
            yield return "vision live report: link layer must be present.";
        }
        if (Equivalence is null)
        {
            yield return "vision live report: equivalence layer must be present.";
        }
        if (Sidecar is null)
        {
            yield return "vision live report: sidecar section must be present.";
        }
        else if (Sidecar.EvidenceSha256.Length != 64 || !Sidecar.EvidenceSha256.All(Uri.IsHexDigit))
        {
            yield return "vision live report: sidecar evidenceSha256 must be 64 hex chars.";
        }
        if (Live is null)
        {
            yield return "vision live report: live run summary must be present.";
        }
        if (Baseline is null)
        {
            yield return "vision live report: baseline run summary must be present.";
        }
        if (Diff is null)
        {
            yield return "vision live report: baseline diff must be present.";
        }
        if (GroundTruth)
        {
            yield return "vision live report: groundTruth must be false in Phase A.";
        }
        if (Grade != VisionReplaySchemas.EvidenceOnly)
        {
            yield return $"vision live report: grade must be \"{VisionReplaySchemas.EvidenceOnly}\" in Phase A.";
        }
        if (string.IsNullOrWhiteSpace(Conclusion))
        {
            yield return "vision live report: conclusion must be recorded.";
        }
    }
}