using System.Text.Json;
using Sim.Core;
using Sim.Protocol;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// VisionReplayAdapter contract tests (R3): deterministic frame selection by
/// time window, explicit unknown/fault results (stale|error|no_target|
/// no_selection), never reading world truth, never consuming the shared
/// Mulberry32 stream, and bit-identical same-evidence replays.
/// </summary>
public class VisionReplayAdapterTests(ITestOutputHelper output)
{
    private const string EvidenceId = "vr-test000000000000";
    private const string EvidenceSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static VisionReplayFrameDetection Det(string label, double confidence = 0.9, double offsetX = 0.25)
        => new() { Label = label, Confidence = confidence, OffsetX = offsetX };

    private static VisionReplayFrame Frame(long sequence, double timestampMs, string status,
        VisionReplayFrameDetection[]? detections = null, int? selected = null, string? error = null)
        => new()
        {
            Sequence = sequence,
            TimestampMs = timestampMs,
            Status = status,
            Error = error,
            SelectedTargetIndex = selected,
            Detections = detections ?? [],
        };

    private static VisionContext Context(string role, double simT, Func<double>? rng = null)
        => new()
        {
            T = simT,
            Role = role,
            Robot = new RobotRuntime { Role = role, Name = role },
            // World truth deliberately CONTRADICTS the evidence: the adapter
            // must never use it to fabricate answers.
            Target = new VisionTargetInfo { Kind = "debuff", Name = "减益块", D = 1, Rel = "正前" },
            Opponent = new RobotRuntime { Role = RoleNames.Them, Name = "对手" },
            Random = rng,
        };

    /// <summary>Evidence: target(buff) @0, no_target @200, error @400, no_data_or_stale @600, target-no-selection @800.</summary>
    private static VisionReplayAdapter MatrixAdapter(double maxAgeMs = 500)
        => new(
        [
            Frame(1, 0, "target", [Det("buff")], selected: 0),
            Frame(2, 200, "no_target"),
            Frame(3, 400, "error", error: "inference failed"),
            Frame(4, 600, "no_data_or_stale"),
            Frame(5, 800, "target", [Det("debuff")], selected: null),
        ], EvidenceId, EvidenceSha, maxAgeMs);

    // ---------- selection / fault matrix ----------

    [Fact]
    public void Classify_ServesNewestFrameInWindow()
    {
        var adapter = MatrixAdapter();
        var detection = adapter.Classify(Context(RoleNames.Us, 0.05));
        Assert.Equal("buff", detection.Label);
        Assert.Equal(0.9, detection.Confidence);
        Assert.Equal("visionReplay", detection.Source);
        Assert.Equal(0.25, detection.OffsetX);
    }

    [Fact]
    public void Classify_StatusFaults_MapToExplicitUnknownReasons()
    {
        var adapter = MatrixAdapter();
        Assert.Equal(("unknown", "no_target"), ResultOf(adapter, 0.25));
        Assert.Equal(("unknown", "error"), ResultOf(adapter, 0.45));
        Assert.Equal(("unknown", "stale"), ResultOf(adapter, 0.65)); // no_data_or_stale → stale
        Assert.Equal(("unknown", "no_selection"), ResultOf(adapter, 0.85));
    }

    [Fact]
    public void Classify_FrameOlderThanMaxAge_IsStale()
    {
        var adapter = MatrixAdapter(maxAgeMs: 100);
        // Newest frame at 0.9s is frame 5 (0.8s): age 150ms > 100ms window.
        Assert.Equal(("unknown", "stale"), ResultOf(adapter, 0.95));
    }

    [Fact]
    public void Classify_SessionTail_ReturnsStaleNotFabricatedFrames()
    {
        var adapter = MatrixAdapter();
        // Long after the evidence ends the adapter must not loop or fabricate.
        Assert.Equal(("unknown", "stale"), ResultOf(adapter, 30.0));
    }

    [Fact]
    public void Classify_RepeatedCalls_ServeSameFrameLikeACameraCache()
    {
        var adapter = MatrixAdapter();
        var a = adapter.Classify(Context(RoleNames.Us, 0.05));
        var b = adapter.Classify(Context(RoleNames.Us, 0.15));
        Assert.Equal(a.Label, b.Label);
        // Both calls consumed frame 1 (the camera cache keeps serving it).
        Assert.Equal(2, adapter.Consumes.Count(c => c.FrameSequence == 1));
        Assert.Equal(2, adapter.Consumes.Count);
    }

    [Fact]
    public void Classify_TracksRolesIndependently()
    {
        var adapter = MatrixAdapter();
        _ = adapter.Classify(Context(RoleNames.Us, 0.05));
        _ = adapter.Classify(Context(RoleNames.Them, 0.85));
        Assert.Equal(1, adapter.LastByRole[RoleNames.Us].FrameSequence);
        Assert.Equal(5, adapter.LastByRole[RoleNames.Them].FrameSequence);
    }

    [Fact]
    public void Classify_IgnoresWorldTruth_AnswerComesFromEvidenceOnly()
    {
        var adapter = MatrixAdapter();
        // Context.Target says "debuff" but the recorded frame says buff: the
        // adapter must hand the FSM the evidence label, never the world truth.
        var detection = adapter.Classify(Context(RoleNames.Us, 0.05));
        Assert.Equal("buff", detection.Label);
    }

    [Fact]
    public void Classify_NeverConsumesTheRandomStream()
    {
        var adapter = MatrixAdapter();
        var draws = 0;
        for (var i = 0; i < 20; i++)
        {
            _ = adapter.Classify(Context(RoleNames.Us, 0.05 * i, rng: () =>
            {
                draws++;
                return 0.5;
            }));
        }
        Assert.Equal(0, draws);
    }

    [Fact]
    public void ExternalSnapshot_FollowsLegacyShape()
    {
        var adapter = MatrixAdapter();
        _ = adapter.Classify(Context(RoleNames.Us, 0.05));
        _ = adapter.Classify(Context(RoleNames.Them, 0.25)); // no_target → unknown
        var external = adapter.BuildExternalSnapshot();
        Assert.NotNull(external);
        var roles = external!.Value.GetProperty("roles");
        Assert.Equal(1, roles.GetProperty("us").GetProperty("frameId").GetInt64());
        Assert.Equal("buff", roles.GetProperty("us").GetProperty("detection").GetProperty("label").GetString());
        // The no_target consumption keeps the object shape and honestly
        // reports an unknown detection with its reason code.
        var them = roles.GetProperty("them");
        Assert.Equal(2, them.GetProperty("frameId").GetInt64());
        Assert.Equal("no_target", them.GetProperty("reason").GetString());
        Assert.Equal("unknown", them.GetProperty("detection").GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("no_frames")]
    [InlineData("bad_sha")]
    [InlineData("zero_max_age")]
    public void Constructor_RejectsInvalidInputs(string mode)
    {
        var frames = new List<VisionReplayFrame> { Frame(1, 0, "target", [Det("buff")], 0) };
        switch (mode)
        {
            case "no_frames":
                Assert.Throws<ArgumentException>(() => new VisionReplayAdapter([], EvidenceId, EvidenceSha, 500));
                break;
            case "bad_sha":
                Assert.Throws<ArgumentException>(() => new VisionReplayAdapter(frames, EvidenceId, "nothex", 500));
                break;
            case "zero_max_age":
                Assert.Throws<ArgumentException>(() => new VisionReplayAdapter(frames, EvidenceId, EvidenceSha, 0));
                break;
        }
    }

    private static (string Label, string Source) ResultOf(VisionReplayAdapter adapter, double simT)
    {
        var detection = adapter.Classify(Context(RoleNames.Us, simT));
        return (detection.Label, detection.Source);
    }

    // ---------- engine integration ----------

    private static Scenario FixedScenario(long seed = 42) => new()
    {
        Seed = seed,
        Blocks = OfficialLayout.Blocks,
    };

    private static List<string> RunToCompletion(MatchEngine engine)
    {
        engine.Arm();
        var fingerprints = new List<string>();
        while (!engine.Done)
        {
            var snapshot = engine.Tick();
            if (snapshot.Events is not { Count: > 0 })
            {
                continue;
            }
            fingerprints.AddRange(snapshot.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}"));
        }
        return fingerprints;
    }

    /// <summary>Always-fresh evidence so every classify serves a real detection.</summary>
    private static VisionReplayAdapter FreshBuffEvidence(long seedBase = 100)
        => new(
            Enumerable.Range(0, 3000).Select(i => Frame(
                seedBase + i, i * 100.0, "target",
                [Det(i % 5 == 4 ? "debuff" : "buff", 0.8)], selected: 0)).ToList(),
            EvidenceId, EvidenceSha, maxAgeMs: 500);

    private sealed class ScriptedAdapter(IReadOnlyList<string> labels) : IVisionAdapter
    {
        private int _index;

        public int Consumed => _index;

        public string Id => "scripted";

        public VisionDetection Classify(VisionContext context)
        {
            var label = labels[_index++];
            return new VisionDetection
            {
                Label = label,
                Confidence = 0.8,
                Source = "scripted",
                OffsetX = 0.25,
            };
        }
    }

    [Fact]
    public void InjectedEngine_SameEvidenceTwice_BitIdenticalMatch()
    {
        var a = RunToCompletion(new MatchEngine(FixedScenario(), FreshBuffEvidence()));
        var b = RunToCompletion(new MatchEngine(FixedScenario(), FreshBuffEvidence()));
        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Fact]
    public void InjectedEngine_ReplayAdapterDoesNotShiftTheRngStream()
    {
        // Run 1: the real adapter. Run 2: a scripted adapter replaying the
        // exact same labels WITHOUT drawing context.Random. If the replay
        // adapter consumed the shared Mulberry32 stream, run 1's downstream
        // draws (scan dir / evade side) would differ and the matches diverge.
        var replayAdapter = FreshBuffEvidence();
        var engine1 = new MatchEngine(FixedScenario(), replayAdapter);
        var fingerprints1 = RunToCompletion(engine1);
        Assert.True(replayAdapter.Consumes.Count > 10, "expected the FSM to classify repeatedly");

        var scripted = new ScriptedAdapter(replayAdapter.Consumes.Select(c => c.Label).ToList());
        var engine2 = new MatchEngine(FixedScenario(), scripted);
        var fingerprints2 = RunToCompletion(engine2);
        Assert.Equal(replayAdapter.Consumes.Count, scripted.Consumed);
        Assert.Equal(fingerprints1, fingerprints2);
    }

    [Fact]
    public void InjectedEngine_DrawingFromContextRandom_WouldShiftTheStream()
    {
        // Counter-proof: an adapter that DOES draw three values per classify
        // changes downstream FSM draws — the zero-draw discipline above is
        // load-bearing, not vacuous.
        var engine1 = new MatchEngine(FixedScenario(), new AlwaysDebuff(draws: false));
        var fingerprints1 = RunToCompletion(engine1);
        var engine2 = new MatchEngine(FixedScenario(), new AlwaysDebuff(draws: true));
        var fingerprints2 = RunToCompletion(engine2);
        Assert.NotEqual(fingerprints1, fingerprints2);
    }

    private sealed class AlwaysDebuff(bool draws) : IVisionAdapter
    {
        public string Id => "alwaysDebuff";

        public VisionDetection Classify(VisionContext context)
        {
            if (draws)
            {
                var random = context.Random ?? throw new InvalidOperationException("no rng");
                _ = random();
                _ = random();
                _ = random();
            }
            return new VisionDetection { Label = "debuff", Confidence = 0.9, Source = "alwaysDebuff", OffsetX = 0.1 };
        }
    }

    [Fact]
    public void DefaultAndNullAdapter_ProduceIdenticalMatches()
    {
        var a = RunToCompletion(new MatchEngine(FixedScenario()));
        var b = RunToCompletion(new MatchEngine(FixedScenario(), null));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ReplayHeader_InjectedAdapterWritesEvidenceFields()
    {
        var adapter = FreshBuffEvidence();
        var engine = new MatchEngine(FixedScenario(), adapter);
        engine.Arm();
        engine.Tick();
        var header = engine.BuildReplayHeader();
        Assert.Equal("visionReplay", header.VisionMode);
        Assert.Equal(EvidenceId, header.VisionEvidenceId);
        Assert.Equal(EvidenceSha, header.VisionEvidenceSha256);
        Assert.Empty(header.Validate());
    }

    [Fact]
    public void ReplayHeader_DefaultPathStaysByteCompatible()
    {
        var engine = new MatchEngine(FixedScenario());
        engine.Arm();
        engine.Tick();
        var header = engine.BuildReplayHeader();
        Assert.Equal("default", header.VisionMode);
        Assert.Null(header.VisionEvidenceId);
        Assert.Null(header.VisionEvidenceSha256);
        var json = ProtocolJson.Serialize(header);
        Assert.DoesNotContain("visionEvidence", json, StringComparison.Ordinal);
        Assert.DoesNotContain("visionReplay", json, StringComparison.Ordinal);
        Assert.Empty(header.Validate());
    }

    [Fact]
    public void ReplayHeader_Validate_RequiresIdAndShaTogether()
    {
        var header = new ReplayHeader { VisionEvidenceId = "vr-x" };
        Assert.Contains(header.Validate(), e => e.Contains("visionEvidenceId and visionEvidenceSha256"));
        var badSha = new ReplayHeader { VisionEvidenceId = "vr-x", VisionEvidenceSha256 = "zz" };
        Assert.Contains(badSha.Validate(), e => e.Contains("64 hex"));
    }

    [Fact]
    public void Observation_PrecisionMetadata_ReportsVisionReplayMode()
    {
        var adapter = FreshBuffEvidence();
        var engine = new MatchEngine(FixedScenario(), adapter);
        engine.Arm();
        var snapshot = engine.Tick();
        Assert.Equal("visionReplay", snapshot.Perception!.Vision!.Mode);
        Assert.Null(snapshot.Perception.Vision.ClassifyRate);
        Assert.NotNull(snapshot.Perception.Vision.External);
        var observation = engine.BuildObservation(engine.Us);
        Assert.Equal("visionReplay", observation.Perception!.Vision!.Mode);
        output.WriteLine($"external: {snapshot.Perception.Vision.External}");
    }
}
