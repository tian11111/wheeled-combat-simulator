using Sim.Cli;
using Sim.Protocol;
using Sim.VisionReplay;

namespace Sim.Tests;

/// <summary>
/// End-to-end vision evaluate tests over the vendored mbri-vision-mini
/// fixture: import → evaluate produces a vision-replay-report-v1 with link
/// quality + policy consumption layers, same evidence replays give identical
/// fingerprints, tampered evidence is rejected, and fidelity.json stays
/// byte-identical (no promotion in Phase A).
/// </summary>
public class VisionReplayEvaluateTests : IDisposable
{
    private const string MiniFixtureDir = "src/Sim.Tests/fixtures/mbri-vision-mini";
    private const string ScenarioPath = "scenarios/wushu-ring-2026.json";
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort temp cleanup
            }
        }
    }

    private static string FindRepo(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private (string WorkDir, string EvidenceDir, string ImportOut, string EvalOut) NewWorkspace()
    {
        var work = Path.Combine(Path.GetTempPath(), $"visioneval-{Guid.NewGuid():N}");
        _tempDirs.Add(work);
        Directory.CreateDirectory(work);
        foreach (var file in Directory.EnumerateFiles(FindRepo(MiniFixtureDir)))
        {
            File.Copy(file, Path.Combine(work, Path.GetFileName(file)));
        }
        var evidenceDir = Path.Combine(work, "evidence");
        var importOut = Path.Combine(work, "import-report.json");
        var evalOut = Path.Combine(work, "eval-report.json");
        var exit = Program.Main(
        [
            "vision", "import",
            "--manifest", Path.Combine(work, "selection.manifest.json"),
            "--evidence-out", evidenceDir,
            "--out", importOut,
            "--force",
        ]);
        Assert.Equal(0, exit);
        return (work, evidenceDir, importOut, evalOut);
    }

    private static int RunEvaluate(string evidenceDir, string scenarioPath, string evalOut, params string[] extra)
    {
        var args = new List<string>
        {
            "vision", "evaluate",
            "--evidence", evidenceDir,
            "--scenario", scenarioPath,
            "--out", evalOut,
            "--force",
        };
        args.AddRange(extra);
        return Program.Main([.. args]);
    }

    // ---------- end to end ----------

    [Fact]
    public void ImportThenEvaluate_ProducesValidThreeLayerReport()
    {
        var (work, evidenceDir, _, evalOut) = NewWorkspace();
        Assert.Equal(0, RunEvaluate(evidenceDir, FindRepoFile(ScenarioPath), evalOut));
        var report = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));
        Assert.Empty(report.Validate());

        Assert.Equal("vision-replay-report-v1", report.Schema);
        Assert.False(report.GroundTruth);
        Assert.Equal("evidence_only", report.Grade);
        Assert.Equal("vision=random_stub (evidence_only)", report.Conclusion);

        // Layer 1: link quality over the whole package.
        Assert.True(report.Link!.Frames > 100);
        Assert.Equal(2, report.Link!.Sessions);
        Assert.True(report.Link!.ValidRate > 0);
        Assert.Equal(1.0, report.Link!.ValidRate + report.Link!.NoTargetRate
            + report.Link!.ErrorRate + report.Link!.NoDataOrStaleRate, 6);
        Assert.NotEmpty(report.Link!.SequenceGapHistogram);
        Assert.True(report.Link!.Fps!.Count > 0);
        Assert.True(report.Link!.FirstValidDetectionMs is >= 0);

        // Layer 3: policy consumption over the replayed session.
        var policy = report.Policy!;
        Assert.Equal("wushu-ring-2026", policy.ScenarioId);
        Assert.True(policy.ClassifyCalls > 0);
        Assert.Equal(policy.ClassifyCalls,
            policy.ConsumedCalls + policy.UnknownReasons.GetValueOrDefault("no_frame"));
        Assert.NotEmpty(policy.FsmDetections);
        Assert.NotEmpty(policy.Frames);
        Assert.Equal(64, policy.PolicyFingerprint.Length);
        Assert.NotEmpty(policy.StateTransitions);

        // Layer 2: honestly not run; Phase B checklist present.
        Assert.Equal("not_run(no_ground_truth)", report.Detection.Status);
        Assert.Equal("not_run(no_ground_truth)", report.Holdout.Status);
        Assert.NotEmpty(report.PhaseB);
        Assert.Contains(report.Limitations, l => l.Contains("不证明识别准确率"));
    }

    [Fact]
    public void SameEvidenceReplay_IsBitIdentical()
    {
        var (work, evidenceDir, _, evalOut) = NewWorkspace();
        var scenario = FindRepoFile(ScenarioPath);
        Assert.Equal(0, RunEvaluate(evidenceDir, scenario, evalOut));
        var first = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));

        var evalOut2 = Path.Combine(work, "eval-report-2.json");
        Assert.Equal(0, RunEvaluate(evidenceDir, scenario, evalOut2));
        var second = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut2));

        Assert.Equal(first.Policy!.PolicyFingerprint, second.Policy!.PolicyFingerprint);
        Assert.Equal(first.Policy!.EventCount, second.Policy!.EventCount);
        Assert.Equal(first.Policy!.FinalScores.Us, second.Policy!.FinalScores.Us);
        Assert.Equal(first.Policy!.StateTransitions, second.Policy!.StateTransitions);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
    }

    [Fact]
    public void ModifiedScenario_EvaluatesAgainstSameEvidence()
    {
        // One small modified scenario (shorter match) must also replay cleanly.
        var (work, evidenceDir, _, _) = NewWorkspace();
        var baseScenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(FindRepoFile(ScenarioPath)));
        var modified = baseScenario with
        {
            Id = baseScenario.Id,
            Seed = 7,
            Field = baseScenario.Field with { MatchDuration = 20 },
        };
        var modifiedPath = Path.Combine(work, "modified-scenario.json");
        File.WriteAllText(modifiedPath, ProtocolJson.Serialize(modified));
        var evalOut = Path.Combine(work, "eval-modified.json");
        Assert.Equal(0, RunEvaluate(evidenceDir, modifiedPath, evalOut));
        var report = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));
        Assert.Empty(report.Validate());
        Assert.Equal(7, report.Policy!.Seed);
        Assert.True(report.Policy!.Ticks > 0 && report.Policy!.Ticks <= 401,
            $"unexpected tick count {report.Policy!.Ticks}");
    }

    [Fact]
    public void TamperedEvidence_IsRejected()
    {
        var (_, evidenceDir, _, evalOut) = NewWorkspace();
        var framesPath = Path.Combine(evidenceDir, VisionReplayIO.FramesFileName);
        var text = File.ReadAllText(framesPath);
        File.WriteAllText(framesPath, text.Replace("\"sequence\":12", "\"sequence\":1200", StringComparison.Ordinal));
        var exit = RunEvaluate(evidenceDir, FindRepoFile(ScenarioPath), evalOut);
        Assert.Equal(1, exit);
        Assert.False(File.Exists(evalOut));
    }

    [Fact]
    public void InvalidMaxAge_IsRejected_NotSilentlyDefaulted()
    {
        var (_, evidenceDir, _, evalOut) = NewWorkspace();
        var scenario = FindRepoFile(ScenarioPath);
        var exit = RunEvaluate(evidenceDir, scenario, evalOut, "--max-age-ms", "abc");
        Assert.Equal(1, exit);
        Assert.False(File.Exists(evalOut));
    }

    [Fact]
    public void ExplicitMaxAge_IsHonored_InvariantDecimal()
    {
        var (work, evidenceDir, _, _) = NewWorkspace();
        var evalOut = Path.Combine(work, "eval-age.json");
        Assert.Equal(0, RunEvaluate(evidenceDir, FindRepoFile(ScenarioPath), evalOut, "--max-age-ms", "250.5"));
        var report = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));
        Assert.Equal(250.5, report.Policy!.MaxAgeMs, 6);
    }

    [Fact]
    public void UnknownSession_IsRejected()
    {
        var (_, evidenceDir, _, evalOut) = NewWorkspace();
        var exit = RunEvaluate(evidenceDir, FindRepoFile(ScenarioPath), evalOut, "--session", "nope.csv");
        Assert.Equal(1, exit);
        Assert.False(File.Exists(evalOut));
    }

    [Fact]
    public void SessionSelection_ReplaysOnlyThatSession()
    {
        var (work, evidenceDir, importOut, evalOut) = NewWorkspace();
        var importReport = ProtocolJson.Deserialize<VisionImportReport>(File.ReadAllText(importOut));
        var session = importReport.Files[0].Path;
        Assert.Equal(0, RunEvaluate(evidenceDir, FindRepoFile(ScenarioPath), evalOut, "--session", session));
        var report = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));
        Assert.Equal(session, report.Policy!.Session);
        Assert.All(report.Policy!.Frames, f => Assert.Equal(session, f.Session));
        Assert.True(report.Link!.Sessions == 2); // link quality still covers the whole package
    }

    [Fact]
    public void FidelityJson_StaysByteIdentical()
    {
        var fidelity = FindRepoFile("fidelity.json");
        var before = File.ReadAllBytes(fidelity);
        var (work, evidenceDir, _, evalOut) = NewWorkspace();
        Assert.Equal(0, RunEvaluate(evidenceDir, FindRepoFile(ScenarioPath), evalOut));
        Assert.Equal(before, File.ReadAllBytes(fidelity));
    }

    // ---------- link metric units ----------

    private static VisionFrameRecord Frame(long seq, double tsMs, string status, int? selected = null,
        double? fps = null, double? inferenceMs = null, double confidence = 0.9, string session = "s.csv")
        => new()
        {
            Session = session,
            Sequence = seq,
            TimestampMs = tsMs,
            ReceivedAgeMs = 10,
            Status = status,
            Fps = fps,
            InferenceMs = inferenceMs,
            FrameWidth = 640,
            FrameHeight = 480,
            SelectedTargetIndex = selected,
            Detections = selected is null
                ? []
                : [new VisionFrameDetection { ClassId = 0, RawType = "good", Label = "buff", Confidence = confidence }],
        };

    [Fact]
    public void LinkMetrics_ComputeRatesGapsAndJitter()
    {
        var frames = new List<VisionFrameRecord>
        {
            Frame(1, 0, "target", selected: 0, fps: 5, inferenceMs: 100),
            Frame(3, 200, "target", selected: 0, fps: 6, inferenceMs: 200),  // gap 2
            Frame(4, 400, "no_target"),
            Frame(10, 600, "error"),                                          // gap 6
            Frame(11, 800, "target", selected: 0),                            // flip: next selection is bad
            Frame(12, 1000, "target", selected: 0),
        };
        // Make the last frame a debuff selection to create one flip.
        frames[^1] = frames[^1] with
        {
            Detections = [new VisionFrameDetection { ClassId = 1, RawType = "bad", Label = "debuff", Confidence = 0.5 }],
        };
        var quality = VisionLinkMetrics.Compute(frames);
        Assert.Equal(6, quality.Frames);
        Assert.Equal(1, quality.Sessions);
        Assert.Equal(4.0 / 6, quality.ValidRate, 6);
        Assert.Equal(1.0 / 6, quality.ErrorRate, 6);
        Assert.Equal(1, quality.SequenceGapHistogram.GetValueOrDefault(2));
        Assert.Equal(1, quality.SequenceGapHistogram.GetValueOrDefault(6));
        Assert.Equal(3, quality.SequenceGapHistogram.GetValueOrDefault(1));
        Assert.Equal(1, quality.SelectionFlips);
        Assert.Equal(0, quality.FirstValidDetectionMs);
        Assert.Equal(5, quality.Fps!.Min);
        Assert.Equal(6, quality.Fps!.Max);
        Assert.Equal(2, quality.TargetRetention!.Runs);
    }

    [Fact]
    public void LinkMetrics_SessionsAreEvaluatedIndependently()
    {
        var frames = new List<VisionFrameRecord>
        {
            Frame(5, 0, "target", session: "a.csv"),
            Frame(1, 100, "no_target", session: "b.csv"),
        };
        var quality = VisionLinkMetrics.Compute(frames);
        Assert.Equal(2, quality.Sessions);
        Assert.Empty(quality.SequenceGapHistogram);
    }

    [Fact]
    public void Percentile_MatchesPythonLinearInterpolation()
    {
        var sorted = new List<double> { 10, 20, 30 };
        Assert.Equal(10, VisionLinkMetrics.Percentile(sorted, 0.0));
        Assert.Equal(30, VisionLinkMetrics.Percentile(sorted, 1.0));
        Assert.Equal(20, VisionLinkMetrics.Percentile(sorted, 0.5));
        Assert.Equal(29, VisionLinkMetrics.Percentile(sorted, 0.95), 6);
        Assert.Equal(2.0, VisionLinkMetrics.Median([1, 3, 2]));
        Assert.Equal(2.5, VisionLinkMetrics.Median([1, 2, 3, 4]));
        Assert.True(double.IsNaN(VisionLinkMetrics.Percentile([], 0.5)));
    }
}
