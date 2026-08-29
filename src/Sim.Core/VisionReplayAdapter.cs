using System.Text.Json;
using Sim.Protocol;

namespace Sim.Core;

/// <summary>One normalized detection of a replayed vision frame (model output only — never world truth).</summary>
public sealed record VisionReplayFrameDetection
{
    /// <summary>Manifest-mapped label: "buff"|"debuff" ("opponent" does not exist in Phase A evidence).</summary>
    public string Label { get; init; } = "";

    public double Confidence { get; init; }

    public double? OffsetX { get; init; }
}

/// <summary>
/// One recorded vision frame handed to <see cref="VisionReplayAdapter"/>. The
/// list is the ONLY vision input of the replay path: built from a hash-locked
/// evidence package (vision-replay-v1) by the caller, never from world state.
/// </summary>
public sealed record VisionReplayFrame
{
    /// <summary>Vision service frame number (evidence session order).</summary>
    public long Sequence { get; init; }

    /// <summary>Frame timestamp in ms; session start = first frame, mapped to SimT = 0.</summary>
    public double TimestampMs { get; init; }

    /// <summary>Recorded service status: target|no_target|error|no_data_or_stale.</summary>
    public string Status { get; init; } = "";

    public string? Error { get; init; }

    /// <summary>detection index of the recorded selected target, when any.</summary>
    public int? SelectedTargetIndex { get; init; }

    public IReadOnlyList<VisionReplayFrameDetection> Detections { get; init; } = [];
}

/// <summary>One classify-call record for the consumption registry (evaluation/audit).</summary>
public sealed record VisionReplayConsumeRecord
{
    public string Role { get; init; } = "";

    /// <summary>Sim time (s) of the classify call.</summary>
    public double SimT { get; init; }

    /// <summary>Consumed frame sequence; null when the call returned unknown.</summary>
    public long? FrameSequence { get; init; }

    /// <summary>Age of the consumed frame in ms; null when no frame was consumed.</summary>
    public double? AgeMs { get; init; }

    /// <summary>Reason code for unknown results: no_frame|stale|error|no_target|no_selection. Null on consumption.</summary>
    public string? Reason { get; init; }

    /// <summary>Label handed to the FSM: buff|debuff|unknown.</summary>
    public string Label { get; init; } = "";

    /// <summary>Confidence of the handed detection; 0 for unknown.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Deterministic real-vision replay adapter (R3): serves the recorded frame
/// sequence exactly as captured — missing/old/error frames return explicit
/// <c>unknown</c> results, never the random stub, and the simulator's target
/// truth (<see cref="VisionContext.Target"/>) is never read to fabricate
/// "correct answers". The adapter never draws <see cref="VisionContext.Random"/>:
/// the shared Mulberry32 match stream must not shift between the default and
/// replay paths.
///
/// Frame selection is a pure function of SimT: the NEWEST frame at or before
/// the current time whose age is within the fixed maxAgeMs window — i.e. the
/// robot's camera cache semantics. (Design note: design.md sketched "first
/// frame after the last consumed one in the window"; newest-in-window keeps the
/// same determinism while mirroring the real cache, which re-serves the current
/// frame until a fresher one arrives.) Repeated classifies of the same frame
/// are legitimate: the recorded evidence shows the same behavior.
/// </summary>
public sealed class VisionReplayAdapter : IVisionAdapter
{
    public const string ModeName = "visionReplay";

    private readonly VisionReplayFrame[] _frames;
    private readonly double _sessionStartMs;
    private readonly string _evidenceId;
    private readonly string _evidenceSha256;
    private readonly double _maxAgeMs;
    private readonly Dictionary<string, VisionReplayConsumeRecord> _lastByRole = new();
    private readonly List<VisionReplayConsumeRecord> _consumes = [];

    /// <param name="frames">Recorded frames; sorted by (timestampMs, sequence) into an internal copy.</param>
    /// <param name="evidenceId">Evidence package id (written to the replay header).</param>
    /// <param name="evidenceSha256">SHA-256 of the evidence package (written to the replay header).</param>
    /// <param name="maxAgeMs">Fixed staleness window; older frames return unknown("stale").</param>
    public VisionReplayAdapter(
        IReadOnlyList<VisionReplayFrame> frames,
        string evidenceId,
        string evidenceSha256,
        double maxAgeMs)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("vision replay adapter requires at least one frame.", nameof(frames));
        }
        if (string.IsNullOrWhiteSpace(evidenceId))
        {
            throw new ArgumentException("vision replay adapter requires an evidence id.", nameof(evidenceId));
        }
        if (evidenceSha256.Length != 64 || !evidenceSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("vision replay adapter requires the 64-hex evidence sha256.", nameof(evidenceSha256));
        }
        if (!double.IsFinite(maxAgeMs) || maxAgeMs <= 0)
        {
            throw new ArgumentException("vision replay adapter requires a positive finite maxAgeMs.", nameof(maxAgeMs));
        }
        _frames = frames.OrderBy(f => f.TimestampMs).ThenBy(f => f.Sequence).ToArray();
        for (var i = 1; i < _frames.Length; i++)
        {
            if (_frames[i].TimestampMs == _frames[i - 1].TimestampMs
                && _frames[i].Sequence == _frames[i - 1].Sequence)
            {
                throw new ArgumentException(
                    $"vision replay adapter requires unique frames; duplicate sequence {_frames[i].Sequence}.",
                    nameof(frames));
            }
        }
        // Fixed time mapping (deterministic contract): SimT = 0 is the first
        // frame of the session; evidence epoch ms are never compared raw.
        _sessionStartMs = _frames[0].TimestampMs;
        _evidenceId = evidenceId;
        _evidenceSha256 = evidenceSha256;
        _maxAgeMs = maxAgeMs;
    }

    public string Id => ModeName;

    public string EvidenceId => _evidenceId;

    public string EvidenceSha256 => _evidenceSha256;

    public double MaxAgeMs => _maxAgeMs;

    /// <summary>Consumption ledger in call order (deterministic).</summary>
    public IReadOnlyList<VisionReplayConsumeRecord> Consumes => _consumes;

    /// <summary>Last classify result per role (the external-vision registry snapshot).</summary>
    public IReadOnlyDictionary<string, VisionReplayConsumeRecord> LastByRole => _lastByRole;

    public VisionDetection Classify(VisionContext context)
    {
        // Discipline (R3): never read context.Target, never call context.Random.
        var tMs = _sessionStartMs + context.T * 1000.0;

        // Newest frame at or before SimT (binary search; T is monotonic per role).
        var index = FindLastAtOrBefore(tMs);
        VisionReplayConsumeRecord record;
        VisionDetection? detection = null;
        if (index < 0)
        {
            record = Unknown(context, "no_frame");
        }
        else
        {
            var frame = _frames[index];
            var ageMs = tMs - frame.TimestampMs;
            record = ageMs > _maxAgeMs
                ? Unknown(context, "stale", frame, ageMs)
                : Consume(context, frame, ageMs, ref detection);
        }
        _lastByRole[context.Role] = record;
        _consumes.Add(record);
        return detection ?? new VisionDetection
        {
            Label = "unknown",
            Confidence = 0,
            Source = record.Reason ?? ModeName,
        };
    }

    /// <summary>
    /// External consumption registry for <see cref="VisionInfo.External"/>:
    /// roles.{us,them}.{frameId, detection, ageMs}, matching the legacy
    /// external-vision sub-object shape (LegacyAliasTests). Roles without any
    /// classify yet keep the object shape with null fields.
    /// </summary>
    public JsonElement? BuildExternalSnapshot()
    {
        object RoleView(string role) => _lastByRole.TryGetValue(role, out var record)
            ? (object)new
            {
                frameId = record.FrameSequence,
                ageMs = record.AgeMs,
                reason = record.Reason,
                detection = record.FrameSequence is null
                    ? null
                    : (object?)new { label = record.Label, confidence = record.Confidence, source = ModeName },
            }
            : new
            {
                frameId = (long?)null,
                ageMs = (double?)null,
                reason = (string?)null,
                detection = (object?)null,
            };
        var json = ProtocolJson.Serialize(new
        {
            roles = new
            {
                us = RoleView(RoleNames.Us),
                them = RoleView(RoleNames.Them),
            },
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private VisionReplayConsumeRecord Consume(
        VisionContext context, VisionReplayFrame frame, double ageMs, ref VisionDetection? detection)
    {
        string? reason = frame.Status switch
        {
            "target" => null,
            "error" => "error",
            "no_data_or_stale" => "stale",
            "no_target" => "no_target",
            _ => "error",
        };
        VisionReplayFrameDetection? selected = null;
        if (reason is null)
        {
            if (frame.SelectedTargetIndex is { } index && index >= 0 && index < frame.Detections.Count)
            {
                selected = frame.Detections[index];
            }
            else
            {
                reason = "no_selection";
            }
        }
        if (selected is not null)
        {
            detection = new VisionDetection
            {
                Label = selected.Label,
                Confidence = selected.Confidence,
                Source = ModeName,
                OffsetX = selected.OffsetX,
            };
        }
        return new VisionReplayConsumeRecord
        {
            Role = context.Role,
            SimT = context.T,
            FrameSequence = frame.Sequence,
            AgeMs = ageMs,
            Reason = reason,
            Label = selected?.Label ?? "unknown",
            Confidence = selected?.Confidence ?? 0,
        };
    }

    private VisionReplayConsumeRecord Unknown(VisionContext context, string reason)
        => new()
        {
            Role = context.Role,
            SimT = context.T,
            FrameSequence = null,
            AgeMs = null,
            Reason = reason,
            Label = "unknown",
        };

    private VisionReplayConsumeRecord Unknown(VisionContext context, string reason, VisionReplayFrame frame, double ageMs)
        => new()
        {
            Role = context.Role,
            SimT = context.T,
            FrameSequence = frame.Sequence,
            AgeMs = ageMs,
            Reason = reason,
            Label = "unknown",
        };

    private int FindLastAtOrBefore(double tMs)
    {
        var low = 0;
        var high = _frames.Length - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (_frames[mid].TimestampMs <= tMs)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return result;
    }
}
