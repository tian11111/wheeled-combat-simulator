using Sim.Calibration;
using Sim.Cli;
using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// End-to-end calibration pipeline tests: AC2 legacy-number equivalence,
/// fit/holdout separation, synthetic-never-promotes, real-source promotion
/// (against a TEMP fidelity copy), deterministic fingerprints, and the
/// applied-scenario replay round trip. The shipped fidelity.json must never
/// be touched by tests.
/// </summary>
public class CalibrationPipelineTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private static string FixturePath => FindRepoFile(Path.Combine("src", "Sim.Tests", "fixtures", "telemetry-synthetic-v1.json"));

    public void Dispose()
    {
        foreach (var path in _tempFiles.Where(File.Exists))
        {
            File.Delete(path);
        }
    }

    private string TempFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"simcal-{Guid.NewGuid():N}-{name}");
        _tempFiles.Add(path);
        return path;
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

    private static TelemetryFile LoadFixture()
        => ProtocolJson.Deserialize<TelemetryFile>(File.ReadAllText(FixturePath));

    private static CalibrationReport Calibrate(TelemetryFile file, bool real = false)
    {
        if (real)
        {
            file = file with { Capture = file.Capture with { Source = "real" } };
        }
        return ReportWriter.Fingerprint(
            Calibrator.Calibrate(file, "fixture-sha-for-tests"), generatedAt: "2026-08-27T00:00:00Z");
    }

    // ---------- AC2: legacy synthetic fixture numbers ----------

    [Fact]
    public void SyntheticFixture_RecoversLegacyNumbers()
    {
        var report = Calibrate(LoadFixture());
        Assert.True(report.Fits["latFrictionK"].Calibrated);
        Assert.Equal(8, report.Fits["latFrictionK"].Value!.Value, 2);
        Assert.Equal(3, report.Fits["angDamping"].Value!.Value, 2);
        Assert.Equal(0.45, report.Fits["BLOCK_MU_K"].Value!.Value, 2);
        Assert.Equal(0.33, report.Fits["COLLISION_RESTITUTION"].Value!.Value, 3);
        var stall = report.Fits["STALL_SPEED"].Value!.Value;
        Assert.InRange(stall, 0.025, 0.069999);
    }

    [Fact]
    public void SyntheticFixture_ShowFitAndHoldoutColumnsSeparately()
    {
        var report = Calibrate(LoadFixture());
        foreach (var fit in report.Fits.Values)
        {
            Assert.True(fit.Calibrated);
            Assert.True(fit.FitSamples >= 1, "fit samples");
            Assert.True(fit.HoldoutSamples >= 1, "holdout samples reported per kind");
            Assert.NotNull(fit.HoldoutRmse ?? fit.HoldoutAccuracy);
        }
        Assert.NotNull(report.Mount);
        Assert.Equal(12, report.Mount!.FitTrials);
        Assert.Equal(12, report.Mount.HoldoutTrials);
    }

    // ---------- R3/R6: no promotion from synthetic ----------

    [Fact]
    public void SyntheticSource_ProducesNoPromotion_NoPatch()
    {
        var report = Calibrate(LoadFixture(), real: false);
        Assert.False(report.Eligibility.Friction);
        Assert.False(report.Eligibility.Collision);
        Assert.False(report.Eligibility.Stall);
        Assert.False(report.Eligibility.Mount);
        Assert.Empty(report.RecommendedPatch.Vehicles);
        Assert.Empty(report.RecommendedPatch.Parameters);
        foreach (var fit in report.Fits.Values)
        {
            Assert.False(fit.Eligible);
            Assert.Contains("real", fit.Reason ?? "");
        }
    }

    [Fact]
    public void RealSource_Eligible_PatchReady()
    {
        var report = Calibrate(LoadFixture(), real: true);
        Assert.True(report.Eligibility.Friction);
        Assert.True(report.Eligibility.Collision);
        Assert.True(report.Eligibility.Stall);
        Assert.True(report.Eligibility.Mount);
        Assert.Equal(8, report.RecommendedPatch.Vehicles["us"].LatFrictionK!.Value, 2);
        Assert.Equal(0.45, report.RecommendedPatch.Parameters["BLOCK_MU_K"], 2);
        Assert.Equal(0.33, report.RecommendedPatch.Parameters["COLLISION_RESTITUTION"], 3);
        Assert.True(report.RecommendedPatch.Parameters.ContainsKey("STALL_SPEED"));
    }

    [Fact]
    public void FidelityPromotion_OnlyOnRealSource_TempCopyOnly()
    {
        var repoFidelity = FindRepoFile("fidelity.json");
        var repoBytes = File.ReadAllBytes(repoFidelity);

        // Temp copy + synthetic report: nothing may change.
        var tempFidelity = TempFile("fidelity.json");
        File.WriteAllBytes(tempFidelity, repoBytes);
        var synth = ReportWriter.Fingerprint(
            Calibrator.Calibrate(LoadFixture(), "sha"), "2026-08-27T00:00:00Z");
        var synthUpdated = CalibrateCommand.UpdateFidelity(tempFidelity, synth, "report.json", force: false);
        Assert.Empty(synthUpdated);
        Assert.Equal(repoBytes, File.ReadAllBytes(tempFidelity));

        // Temp copy + real report: all four subsystems promote with evidence.
        var real = Calibrate(LoadFixture(), real: true);
        var updated = CalibrateCommand.UpdateFidelity(tempFidelity, real, "report.json", force: false);
        Assert.Equal(["friction", "collision", "stall", "mount"], updated);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(tempFidelity))!;
        Assert.Equal("calibrated", doc["subsystems"]!["friction"]!["status"]!.GetValue<string>());
        Assert.Contains("sim-cli calibrate", doc["subsystems"]!["mount"]!["evidence"]!.GetValue<string>());
        Assert.NotNull(doc["lastCalibration"]);

        // The shipped repo file remains untouched.
        Assert.Equal(repoBytes, File.ReadAllBytes(repoFidelity));
    }

    // ---------- AC3: determinism ----------

    [Fact]
    public void SameInput_ProducesIdenticalContentFingerprint()
    {
        var a = Calibrate(LoadFixture());
        var b = Calibrate(LoadFixture());
        Assert.Equal(a.ContentSha256, b.ContentSha256);
        Assert.Equal(ProtocolJson.Serialize(a), ProtocolJson.Serialize(b));

        // Volatile generatedAt excluded from the fingerprint: same content hash.
        var c = ReportWriter.Fingerprint(
            Calibrator.Calibrate(LoadFixture(), "fixture-sha-for-tests"),
            generatedAt: "2020-01-01T00:00:00Z");
        Assert.Equal(a.ContentSha256, c.ContentSha256);
        Assert.NotEqual(a.GeneratedAt, c.GeneratedAt);
    }

    // ---------- Step 7/10: applied scenario drives the engine identically ----------

    [Fact]
    public void AppliedScenario_Patched_LoadsRunsAndReplaysBitForBit()
    {
        var report = Calibrate(LoadFixture(), real: true);
        var official = ProtocolJson.Deserialize<Scenario>(
            File.ReadAllText(FindRepoFile(Path.Combine("scenarios", "wushu-ring-2026.json"))));

        var patched = ReportWriter.ApplyPatch(official, report);
        Assert.Empty(patched.Validate());
        Assert.Equal(8, patched.Vehicles[RoleNames.Us].LatFrictionK, 2);
        Assert.Equal(0.33, patched.Parameters!["COLLISION_RESTITUTION"], 3);
        // Layout/seed/ruleset preserved verbatim.
        Assert.Equal(official.Seed, patched.Seed);
        Assert.Equal(official.Id, patched.Id);
        Assert.Equal(official.Field.Pose!.X, patched.Field.Pose!.X);
        // Official scenario object untouched.
        Assert.Equal(8, official.Vehicles[RoleNames.Us].LatFrictionK);

        var path = TempFile("calibrated.json");
        ReportWriter.EmitScenario(path, patched);
        var loaded = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(path));
        Assert.Equal(ProtocolJson.Serialize(patched), ProtocolJson.Serialize(loaded));

        // Engine round trip: record the patched scenario and verify its replay.
        var engine = new MatchEngine(loaded);
        engine.Arm();
        var prints = new List<string>();
        while (!engine.Done)
        {
            var snap = engine.Tick();
            prints.AddRange(snap.Events?.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}") ?? []);
        }
        var file = new ReplayFile
        {
            Scenario = loaded,
            Header = engine.BuildReplayHeader(),
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = engine.CommitSnapshot().DoneReason,
            EventFingerprints = prints,
        };
        Assert.True(ParityCheck.Verify(file).Pass);
    }

    // ---------- AC1: CLI invalid-input paths ----------

    [Fact]
    public void Cli_Calibrate_MissingInput_Exits2_NoOutput()
    {
        Assert.Equal(2, Program.Main(["calibrate"]));
    }

    [Fact]
    public void Cli_Calibrate_InvalidTelemetry_Exits1_NoReportWritten()
    {
        var bad = TempFile("bad-telemetry.json");
        File.WriteAllText(bad, """
            {"protocolVersion":"v1","schema":"telemetry-v1","schemaVersion":1,
             "units":{"length":"cm","time":"s","angle":"rad"},
             "vehicle":{"id":"x"},"capture":{"source":"real","date":"2026-08-27"},
             "trials":[{"id":"a","kind":"stall","frames":[{"t":0.1,"speed":0.02,"stalled":true},{"t":0.0,"speed":0.03,"stalled":false}]}]}
            """);
        var reportOut = TempFile("never-written.json");
        Assert.Equal(1, Program.Main(["calibrate", "--input", bad, "--out", reportOut]));
        Assert.False(File.Exists(reportOut));
    }

    [Fact]
    public void Cli_Calibrate_OverwriteGuard_RequiresForce()
    {
        var telemetry = TempFile("telemetry-real.json");
        var real = LoadFixture() with { Capture = LoadFixture().Capture with { Source = "real" } };
        File.WriteAllText(telemetry, ProtocolJson.Serialize(real));
        var outPath = TempFile("report.json");
        File.WriteAllText(outPath, "{}");
        Assert.Equal(1, Program.Main(["calibrate", "--input", telemetry, "--out", outPath]));
        Assert.Equal("{}", File.ReadAllText(outPath));
        Assert.Equal(0, Program.Main(["calibrate", "--input", telemetry, "--out", outPath, "--force"]));
    }

    // ---------- unit coverage for the fitters' degenerate paths ----------

    [Fact]
    public void Fitters_DegeneratePaths_Insufficient()
    {
        Assert.False(Fitters.FitExponentialDecay(
            [new ExponentialPair(0.05, -0.4)], "lateral_coast").Calibrated);
        Assert.False(Fitters.FitExponentialDecay(
            [new ExponentialPair(0, 1), new ExponentialPair(0, 2), new ExponentialPair(0, 3), new ExponentialPair(0, 4)], "x").Calibrated);
        Assert.False(Fitters.FitBlockFriction([new BlockPair(1, 0.9, 0.05)]).Calibrated);
        // Only pre-impact samples: never enough usable after the after<=0 filter.
        Assert.False(Fitters.FitRestitution(
            [new CollisionSample(1, 0.5), new CollisionSample(1, 0.5), new CollisionSample(1, 0.5)]).Calibrated);
        // Stall without both labels.
        Assert.False(Fitters.FitStallThreshold(
            [.. Enumerable.Range(0, 6).Select(i => new StallSample(i * 0.01, true))]).Calibrated);
    }

    [Fact]
    public void MountEvaluator_PredictsKernelGateBoundaries()
    {
        Assert.False(MountEvaluator.PredictAccepted(0.3, 0, 0.3, 0.26));   // vn must EXCEED vmin
        Assert.True(MountEvaluator.PredictAccepted(0.31, 0, 0.3, 0.26));
        Assert.False(MountEvaluator.PredictAccepted(0.5, 0.2, 0.3, 0.26)); // vt too large (≈21.8°)
        Assert.True(MountEvaluator.PredictAccepted(0.5, 0.1, 0.3, 0.26));  // ≈11.3° within 15°
        Assert.False(MountEvaluator.PredictAccepted(-0.5, 0, 0.3, 0.26));  // moving away
    }

    [Fact]
    public void MountEvaluator_CoverageRules_ReportInsufficiency()
    {
        // 14 holdout trials with both outcomes but only one bucket → coverage reason.
        var trials = Enumerable.Range(0, 20)
            .Select(i => (new MountSample(0.5, 0.02, i % 2 == 0), i < 6 ? "fit" : "holdout"))
            .ToList();
        var evaluation = MountEvaluator.Evaluate(trials, 0.3, 0.26);
        Assert.NotNull(evaluation.Reason);
        Assert.Contains("覆盖不足", evaluation.Reason);
        Assert.True(evaluation.Buckets.Count >= 1);
    }
}
