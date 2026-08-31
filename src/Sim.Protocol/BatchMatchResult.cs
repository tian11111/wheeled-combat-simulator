using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>Per-role external-controller fault counts (timeouts, bad lines, dead process).</summary>
public sealed record BatchFaults
{
    public long Us { get; init; }

    public long Them { get; init; }
}

/// <summary>
/// Failure descriptor for a <see cref="BatchMatchResult"/> row with status
/// "failed". <see cref="Kind"/> is a stable machine-readable category
/// (e.g. "controller_start_failed", "match_error", "batch_scheduler");
/// <see cref="Message"/> is a human-readable reason. Neither fakes partial
/// match data — the row's result fields stay null.
/// </summary>
public sealed record BatchFailure
{
    public string Kind { get; init; } = "";

    public string Message { get; init; } = "";
}

/// <summary>
/// One headless batch row: the machine-readable result of a single input seed
/// from the `batch` CLI command (schema "sim-batch-result-v1", one JSON object
/// per line, emitted in input order after all workers finish).
///
/// Determinism boundary: the wire shape excludes creation timestamps, thread /
/// scheduler data and machine paths. Completed rows carry only stable match
/// facts (ticks, scores, penalties, done reason, faults, event count and the
/// two SHA-256 fingerprints computed by the CLI over the same stable fields).
/// Failed rows keep inputIndex/seed/status/faults plus failure.kind/message;
/// every unfinished result field is null (no fake partial success).
///
/// This DTO describes the cross-process output contract only — it references
/// no CLI, process, thread or file types. Wire evolution is additive: existing
/// field names and enum spellings never change.
///
/// Note: intentionally NOT an <see cref="IProtocolMessage"/> — the batch row's
/// version tag is `schemaVersion` (design-fixed shape), not the per-message
/// `protocolVersion` stamp, and the wire shape must not gain a second version
/// field.
/// </summary>
public sealed record BatchMatchResult
{
    /// <summary>status value: the match ran to a terminal state.</summary>
    public const string StatusCompleted = "completed";

    /// <summary>status value: the match did not produce a usable result.</summary>
    public const string StatusFailed = "failed";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = ProtocolVersion.BatchResultFormat;

    /// <summary>Zero-based position of this seed in the batch input list.</summary>
    public long InputIndex { get; init; }

    /// <summary>Deterministic seed the job ran with (duplicates allowed; distinguished by inputIndex).</summary>
    public long Seed { get; init; }

    /// <summary>"completed" or "failed".</summary>
    public string Status { get; init; } = StatusCompleted;

    /// <summary>Ruleset identifier of the scenario (null on failed rows).</summary>
    public string? ScenarioId { get; init; }

    /// <summary>Committed tick count (null on failed rows).</summary>
    public long? Ticks { get; init; }

    /// <summary>Final referee scores (null on failed rows).</summary>
    public Scores? Scores { get; init; }

    /// <summary>Final restart penalties per role (null on failed rows).</summary>
    public Scores? Penalties { get; init; }

    /// <summary>Terminal reason (null on failed rows).</summary>
    public string? DoneReason { get; init; }

    /// <summary>External-controller faults per role; present on both statuses.</summary>
    public BatchFaults? Faults { get; init; }

    /// <summary>Number of committed events (null on failed rows).</summary>
    public long? EventCount { get; init; }

    /// <summary>SHA-256 over the ordered "seq|tick|type|cls|message" event lines (null on failed rows).</summary>
    public string? EventFingerprint { get; init; }

    /// <summary>SHA-256 over seed/ticks/scores/penalties/doneReason plus the event lines (null on failed rows).</summary>
    public string? ResultFingerprint { get; init; }

    /// <summary>Failure details; null on completed rows.</summary>
    public BatchFailure? Failure { get; init; }

    public IEnumerable<string> Validate()
    {
        if (SchemaVersion != ProtocolVersion.BatchResultFormat)
        {
            yield return $"batch result: schemaVersion must be '{ProtocolVersion.BatchResultFormat}', got '{SchemaVersion}'.";
        }
        if (InputIndex < 0)
        {
            yield return "batch result: inputIndex must be >= 0.";
        }
        if (Seed < 0)
        {
            yield return "batch result: seed must be >= 0.";
        }
        if (Status != StatusCompleted && Status != StatusFailed)
        {
            yield return $"batch result: status must be '{StatusCompleted}' or '{StatusFailed}', got '{Status}'.";
        }
        if (Faults is null)
        {
            yield return "batch result: faults must be present.";
        }
        else if (Faults.Us < 0 || Faults.Them < 0)
        {
            yield return "batch result: faults must be >= 0.";
        }

        if (Status == StatusCompleted)
        {
            if (string.IsNullOrWhiteSpace(ScenarioId))
            {
                yield return "batch result: completed rows require scenarioId.";
            }
            if (Ticks is null || Ticks < 0)
            {
                yield return "batch result: completed rows require a non-negative ticks value.";
            }
            if (Scores is null || Penalties is null)
            {
                yield return "batch result: completed rows require scores and penalties.";
            }
            if (string.IsNullOrWhiteSpace(DoneReason))
            {
                yield return "batch result: completed rows require doneReason.";
            }
            if (EventCount is null || EventCount < 0)
            {
                yield return "batch result: completed rows require a non-negative eventCount.";
            }
            foreach (var (name, fingerprint) in new[] { ("eventFingerprint", EventFingerprint), ("resultFingerprint", ResultFingerprint) })
            {
                if (string.IsNullOrWhiteSpace(fingerprint) || !IsSha256Hex(fingerprint))
                {
                    yield return $"batch result: completed rows require a lowercase sha-256 {name}.";
                }
            }
            if (Failure is not null)
            {
                yield return "batch result: completed rows must not carry failure details.";
            }
        }
        else if (Status == StatusFailed)
        {
            if (Failure is null || string.IsNullOrWhiteSpace(Failure.Kind) || string.IsNullOrWhiteSpace(Failure.Message))
            {
                yield return "batch result: failed rows require failure.kind and failure.message.";
            }
            if (Ticks is not null || Scores is not null || Penalties is not null || DoneReason is not null
                || EventCount is not null || EventFingerprint is not null || ResultFingerprint is not null)
            {
                yield return "batch result: failed rows must not carry partial match results (ticks/scores/penalties/doneReason/fingerprints stay null).";
            }
        }
    }

    private static bool IsSha256Hex(string value)
        => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
