using Sim.Cli;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Wire contract for the `sim-batch-result-v1` batch row (design.md): fixed
/// camelCase shape, null omission, completed/failed variants, Validate() rules
/// and fixed SHA-256 fingerprint vectors for the CLI canonicalizer.
/// </summary>
public class BatchMatchResultTests
{
    private const string Hex64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static BatchMatchResult CompletedRow() => new()
    {
        InputIndex = 0,
        Seed = 42,
        Status = BatchMatchResult.StatusCompleted,
        ScenarioId = "wushu-ring-2026",
        Ticks = 60,
        Scores = new Scores { Us = 3, Them = 0 },
        Penalties = new Scores { Us = 1, Them = 0 },
        DoneReason = "比赛时间结束",
        Faults = new BatchFaults { Us = 2, Them = 0 },
        EventCount = 5,
        EventFingerprint = Hex64,
        ResultFingerprint = Hex64,
    };

    private static BatchMatchResult FailedRow() => new()
    {
        InputIndex = 1,
        Seed = 7,
        Status = BatchMatchResult.StatusFailed,
        Faults = new BatchFaults { Us = 3, Them = 0 },
        Failure = new BatchFailure
        {
            Kind = "controller_start_failed",
            Message = "failed to start controller process: nope",
        },
    };

    // ---------- wire shape ----------

    [Fact]
    public void CompletedRow_KeepsFixedCamelCaseShape_AndOmitsNulls()
    {
        var json = ProtocolJson.Serialize(CompletedRow());
        Assert.StartsWith("{\"schemaVersion\":\"sim-batch-result-v1\"", json);
        Assert.Contains("\"inputIndex\":0", json);
        Assert.Contains("\"seed\":42", json);
        Assert.Contains("\"status\":\"completed\"", json);
        Assert.Contains("\"scenarioId\":\"wushu-ring-2026\"", json);
        Assert.Contains("\"ticks\":60", json);
        Assert.Contains("\"scores\":{\"us\":3,\"them\":0}", json);
        Assert.Contains("\"penalties\":{\"us\":1,\"them\":0}", json);
        Assert.Contains("\"doneReason\":", json);
        Assert.Contains("\"faults\":{\"us\":2,\"them\":0}", json);
        Assert.Contains("\"eventCount\":5", json);
        Assert.Contains("\"eventFingerprint\":\"" + Hex64 + "\"", json);
        Assert.Contains("\"resultFingerprint\":\"" + Hex64 + "\"", json);
        // nulls are omitted: a completed row has no failure member, and the
        // per-message protocolVersion stamp is intentionally absent.
        Assert.DoesNotContain("failure", json);
        Assert.DoesNotContain("protocolVersion", json);
    }

    [Fact]
    public void CompletedRow_RoundTripsLosslessly()
    {
        var row = CompletedRow();
        var json = ProtocolJson.Serialize(row);
        Assert.Equal(json, ProtocolJson.RoundTripJson(row));
        Assert.Equal(row, ProtocolJson.Deserialize<BatchMatchResult>(json));
    }

    [Fact]
    public void FailedRow_KeepsIdentityAndFaults_OmitsPartialResults()
    {
        var json = ProtocolJson.Serialize(FailedRow());
        Assert.Contains("\"schemaVersion\":\"sim-batch-result-v1\"", json);
        Assert.Contains("\"inputIndex\":1", json);
        Assert.Contains("\"seed\":7", json);
        Assert.Contains("\"status\":\"failed\"", json);
        Assert.Contains("\"faults\":{\"us\":3,\"them\":0}", json);
        Assert.Contains("\"failure\":{\"kind\":\"controller_start_failed\"", json);
        // no fake partial success: unfinished result fields stay null/omitted.
        Assert.DoesNotContain("scenarioId", json);
        Assert.DoesNotContain("ticks", json);
        Assert.DoesNotContain("scores", json);
        Assert.DoesNotContain("penalties", json);
        Assert.DoesNotContain("doneReason", json);
        Assert.DoesNotContain("eventCount", json);
        Assert.DoesNotContain("eventFingerprint", json);
        Assert.DoesNotContain("resultFingerprint", json);
    }

    [Fact]
    public void FailedRow_RoundTripsLosslessly()
    {
        var row = FailedRow();
        var json = ProtocolJson.Serialize(row);
        Assert.Equal(json, ProtocolJson.RoundTripJson(row));
        Assert.Equal(row, ProtocolJson.Deserialize<BatchMatchResult>(json));
    }

    // ---------- Validate() rules ----------

    private static string Errors(BatchMatchResult row) => string.Join(" ", row.Validate());

    [Fact]
    public void ValidRows_ProduceNoValidationErrors()
    {
        Assert.Empty(CompletedRow().Validate());
        Assert.Empty(FailedRow().Validate());
    }

    [Fact]
    public void CompletedRow_RequiresStableResultFields()
    {
        Assert.Contains("scores", Errors(CompletedRow() with { Scores = null }));
        Assert.Contains("penalties", Errors(CompletedRow() with { Penalties = null }));
        Assert.Contains("ticks", Errors(CompletedRow() with { Ticks = null }));
        Assert.Contains("doneReason", Errors(CompletedRow() with { DoneReason = null }));
        Assert.Contains("eventCount", Errors(CompletedRow() with { EventCount = null }));
        Assert.Contains("eventFingerprint", Errors(CompletedRow() with { EventFingerprint = null }));
        Assert.Contains("resultFingerprint", Errors(CompletedRow() with { ResultFingerprint = "xyz" }));
        Assert.Contains("lowercase", Errors(CompletedRow() with { ResultFingerprint = Hex64.ToUpperInvariant() }));
        Assert.Contains("failure", Errors(CompletedRow() with { Failure = new BatchFailure { Kind = "x", Message = "y" } }));
    }

    [Fact]
    public void FailedRow_RejectsPartialResults_AndRequiresFailure()
    {
        Assert.Contains("failure", Errors(FailedRow() with { Failure = null }));
        Assert.Contains("must not carry", Errors(FailedRow() with { Ticks = 10 }));
        Assert.Contains("must not carry", Errors(FailedRow() with { Scores = new Scores() }));
        Assert.Contains("must not carry", Errors(FailedRow() with
        {
            EventFingerprint = Hex64,
            ResultFingerprint = Hex64,
            EventCount = 1,
            DoneReason = "比赛时间结束",
        }));
    }

    [Fact]
    public void Validate_RejectsWrongSchema_NegativeIdentity_AndUnknownStatus()
    {
        Assert.Contains("schemaVersion", Errors(new BatchMatchResult { SchemaVersion = "v2" }));
        Assert.Contains("inputIndex", Errors(new BatchMatchResult { InputIndex = -1 }));
        Assert.Contains("seed", Errors(new BatchMatchResult { Seed = -1 }));
        Assert.Contains("status", Errors(new BatchMatchResult { Status = "ok" }));
        Assert.Contains("faults", Errors(new BatchMatchResult { Faults = null }));
    }

    // ---------- fingerprint vectors (design-fixed canonical forms) ----------

    [Fact]
    public void EventFingerprint_MatchesFixedVectors()
    {
        // sha256("") — a match with zero events.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            BatchFingerprint.EventFingerprint([]));
        // one event line, trailing '\n' included in the canonical form. The
        // event "type" component is the .NET enum name (as interpolated by the
        // runner), not the snake_case JSON spelling.
        Assert.Equal(
            "b3c9fd05d4a19221717963ab3cb34b3578116235a848ff4f56b5365258bb6f5d",
            BatchFingerprint.EventFingerprint(["1|1|Drop|us|掉下擂台"]));
    }

    [Fact]
    public void ResultFingerprint_MatchesFixedVectors()
    {
        // Stable fields only: seed/ticks/scores/penalties/doneReason + event
        // lines. Invariant "R" numbers, UTF-8, lowercase hex output.
        Assert.Equal(
            "5d135f2d31030c14f7bc770225b620eb934858a881d94b96ab0021b0e1c3e3ec",
            BatchFingerprint.ResultFingerprint(
                42, 60,
                new Scores { Us = 0, Them = 0 }, new Scores { Us = 0, Them = 0 },
                "比赛时间结束", []));
        Assert.Equal(
            "ae977d3f42a4023ffffaa277bd534be1439903e7034a6b2dc9a7ee8806cca95f",
            BatchFingerprint.ResultFingerprint(
                42, 60,
                new Scores { Us = 3, Them = 0 }, new Scores { Us = 1, Them = 0 },
                "比赛时间结束",
                ["1|1|Drop|us|掉下擂台", "2|2|BlockScore|us|us 推块得分 +1"]));
    }

    [Fact]
    public void Fingerprints_IgnoreSchedulingMetadata()
    {
        // The canonical form must not depend on caller-supplied volatile data:
        // only the listed stable fields change the hash.
        var baseFingerprint = BatchFingerprint.ResultFingerprint(
            1, 2, new Scores { Us = 0, Them = 0 }, new Scores(), "done", ["a"]);
        Assert.Equal(baseFingerprint, BatchFingerprint.ResultFingerprint(
            1, 2, new Scores { Us = 0, Them = 0 }, new Scores(), "done", ["a"]));
        Assert.NotEqual(baseFingerprint, BatchFingerprint.ResultFingerprint(
            1, 2, new Scores { Us = 0, Them = 0 }, new Scores(), "done", ["a", "b"]));
        Assert.NotEqual(baseFingerprint, BatchFingerprint.ResultFingerprint(
            2, 2, new Scores { Us = 0, Them = 0 }, new Scores(), "done", ["a"]));
    }
}
