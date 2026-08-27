using Sim.Calibration;
using Sim.Cli;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// sensor-calibration-v1 import tests over the vendored MBri mini subset:
/// determinism (content fingerprint, absolute-path independence), honest
/// status reporting for stored-vs-recomputed drift, and the rule that every
/// invalid input path produces either no report (hard validation errors) or
/// a report that marks the affected family non-candidate.
/// </summary>
public class SensorCalibrationImportTests
{
    private const string MiniFixtureDir = "src/Sim.Tests/fixtures/mbri-mini";
    private readonly List<string> _tempDirs = [];

    private sealed record Run(string DataDir, string ManifestPath, string OutPath, int Exit);

    private Run RunImport(Action<string>? mutate = null, string? extraFile = null, string? configPath = null)
    {
        var dir = CopyMini(mutate, extraFile);
        var outPath = Path.Combine(dir, "out", "report.json");
        var args = new List<string>
        {
            "sensor-calibration", "import",
            "--data-dir", dir,
            "--manifest", Path.Combine(dir, "selection.manifest.json"),
            "--out", outPath,
            "--force",
        };
        if (configPath is not null)
        {
            args.Add("--config");
            args.Add(configPath);
        }
        var exit = Program.Main(args.ToArray());
        return new Run(dir, Path.Combine(dir, "selection.manifest.json"), outPath, exit);
    }

    private string CopyMini(Action<string>? mutate = null, string? extraFileName = null)
    {
        var src = FindRepoFile(MiniFixtureDir);
        var dir = Path.Combine(Path.GetTempPath(), $"mbrisensor-{Guid.NewGuid():N}");
        _tempDirs.Add(dir);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        }
        if (extraFileName is not null)
        {
            File.WriteAllText(Path.Combine(dir, extraFileName), "t,a\n1,2\n");
        }
        mutate?.Invoke(dir);
        return dir;
    }

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

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static string FindRepoFileDir(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static SensorCalibrationReport LoadReport(Run run)
    {
        Assert.Equal(0, run.Exit);
        Assert.True(File.Exists(run.OutPath));
        var report = ProtocolJson.Deserialize<SensorCalibrationReport>(File.ReadAllText(run.OutPath));
        Assert.Empty(report.Validate());
        return report;
    }

    // ---------- happy path over the vendored real data ----------

    [Fact]
    public void MiniImport_DeterministicContentHash()
    {
        var a = RunImport();
        var b = RunImport();
        var reportA = LoadReport(a);
        var reportB = LoadReport(b);
        Assert.NotNull(reportA.ContentSha256);
        Assert.Equal(reportA.ContentSha256, reportB.ContentSha256);
        // Absolute paths never leak into the report (normalized names only).
        var json = ProtocolJson.Serialize(reportA);
        Assert.DoesNotContain(a.DataDir, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), json, StringComparison.Ordinal);
    }

    [Fact]
    public void MiniImport_HonestStatusesAndDriftVisible()
    {
        var report = LoadReport(RunImport());
        var gray = report.Blocks.Single(b => b.Model == "gray");
        var front = report.Blocks.Single(b => b.Model == "frontAdc");
        var shovel = report.Blocks.Single(b => b.Model == "shovel");

        // gray: replay gates pass; stored band values cannot be recomputed
        // (no group labels) — candidate only with a config snapshot.
        Assert.Equal(SensorCalibrationStatus.EvidenceOnly, gray.Status);
        Assert.NotNull(gray.Replay);
        Assert.True(gray.Replay!.Passed);
        Assert.True(gray.Replay.Samples > 1000);
        Assert.False(gray.Replay.InvalidRows > gray.Replay.Samples / 10);

        // front band model reproduces the stored diff band on its own files.
        Assert.Equal(SensorCalibrationStatus.EvidenceOnly, front.Status);
        Assert.True(front.RuntimeCandidate);
        Assert.True(front.Replay!.DecisionCounts.GetValueOrDefault("left") > 0);
        Assert.True(front.Replay.DecisionCounts.GetValueOrDefault("right") > 0);
        Assert.True(front.Replay.DecisionCounts.GetValueOrDefault("forward") > 0);
        Assert.DoesNotContain("diff_low", front.Reason ?? "", StringComparison.Ordinal);

        // shovel: stored enter disagrees with recomputation over THIS batch
        // (the mixed-batch drift the audit predicted) — visible, never merged.
        Assert.Equal(SensorCalibrationStatus.Rejected, shovel.Status);
        Assert.False(shovel.RuntimeCandidate);
        Assert.Contains("hang_enter", shovel.Reason);
        var enterDelta = report.Comparison.Single(d => d.Model == "shovel" && d.Field == "hang_enter");
        Assert.Equal(668.5, enterDelta.Stored);
        Assert.False(enterDelta.Consistent);
        Assert.NotNull(enterDelta.Recomputed);
        Assert.False(report.BatchConsistent);
    }

    [Fact]
    public void MiniImport_TransparencyAndConfigSnapshot()
    {
        var report = LoadReport(RunImport(extraFile: "unselected_random.csv"));
        // 12 selected files recorded with hashes.
        Assert.Equal(12, report.Files.Count);
        Assert.All(report.Files, f => Assert.Equal(64, f.Sha256.Length));
        // Unselected files are listed as ignored — never silently treated as input.
        Assert.Contains("unselected_random.csv", report.IgnoredFiles);
        // gray limitation must state it cannot build a field gray grid.
        Assert.Contains(report.Limitations, l => l.Contains("GrayGridMap"));
        Assert.False(report.Gray!.CoordinateData);
    }

    [Fact]
    public void MiniImport_ConfigSnapshotMakesGrayDriftVisible()
    {
        // MBri config.py ships near_edge_enter 0.35 vs stored model 0.50.
        var configCopy = CopyMini();
        var configPath = Path.Combine(configCopy, "config.py");
        File.WriteAllText(configPath, """
            GRAY_NEAR_EDGE_ENTER = 0.35  # 灰度
            IR_DIRECTION_RATIO_THRESHOLD = 0.20
            SHOVEL_HANG_ENTER = 1134.1
            SHOVEL_HANG_CLEAR = 1317.5
            """);
        var report = LoadReport(RunImport(configPath: configPath));
        var gray = report.Blocks.Single(b => b.Model == "gray");
        Assert.False(gray.RuntimeCandidate);
        Assert.Contains("near_edge_enter", gray.Reason ?? "");
        Assert.Contains("frontAdc", string.Join(",", report.Comparison.Select(c => c.Model)));
    }

    // ---------- invalid inputs ----------

    [Fact]
    public void EvenFilterWindow_NoReportExit1()
    {
        var run = RunImport(mutate: dir =>
        {
            var path = Path.Combine(dir, "gray_model.csv");
            var lines = File.ReadAllLines(path);
            lines[1] = lines[1].Replace(",3,", ",4,");
            File.WriteAllLines(path, lines);
        });
        Assert.Equal(1, run.Exit);
        Assert.False(File.Exists(run.OutPath));
    }

    [Fact]
    public void ThresholdReversal_NoReportExit1()
    {
        var run = RunImport(mutate: dir =>
        {
            var path = Path.Combine(dir, "gray_model.csv");
            var text = File.ReadAllText(path);
            File.WriteAllText(path, text.Replace(",0.5,0.65,", ",0.9,0.65,"));
        });
        Assert.Equal(1, run.Exit);
        Assert.False(File.Exists(run.OutPath));
    }

    [Fact]
    public void MissingSelectedFile_NoReportExit1()
    {
        var run = RunImport(mutate: dir =>
            File.Delete(Path.Combine(dir, "shovel_stage_instage.csv")));
        Assert.Equal(1, run.Exit);
        Assert.False(File.Exists(run.OutPath));
    }

    [Fact]
    public void BadRawHeader_RejectedListedAndNoGrayCandidate()
    {
        var run = RunImport(mutate: dir =>
        {
            var path = Path.Combine(dir, "中轴.csv");
            var lines = File.ReadAllLines(path);
            lines[0] = "t,x,y,z,v";
            File.WriteAllLines(path, lines);
        });
        var report = LoadReport(run);
        Assert.Contains(report.RejectedFiles, r => r.Path == "中轴.csv" && r.Reason.Contains("表头"));
        var gray = report.Blocks.Single(b => b.Model == "gray");
        Assert.False(gray.RuntimeCandidate);
        Assert.Equal(11, report.Files.Count);
    }

    [Fact]
    public void BadManifestLabel_MissingExpect_NoReport()
    {
        var run = RunImport(mutate: dir =>
        {
            var path = Path.Combine(dir, "selection.manifest.json");
            var manifest = ProtocolJson.Deserialize<SensorImportManifest>(File.ReadAllText(path));
            var bad = manifest with { Label = "" };
            File.WriteAllText(path, ProtocolJson.Serialize(bad));
        });
        Assert.Equal(1, run.Exit);
        Assert.False(File.Exists(run.OutPath));
    }

    [Fact]
    public void UsageMissingArgs_Exit2()
    {
        Assert.Equal(2, Program.Main(["sensor-calibration"]));
        Assert.Equal(2, Program.Main(["sensor-calibration", "import"]));
    }

    [Fact]
    public void FidelityJsonByteIdentical_AfterImport()
    {
        var fidelity = FindRepoFileDir("fidelity.json");
        var before = File.ReadAllBytes(fidelity);
        RunImport();
        Assert.Equal(before, File.ReadAllBytes(fidelity));
    }

    // ---------- unit coverage for the pure parts ----------

    [Theory]
    [InlineData(new double[] { 1, 3, 2 }, 2.0)]
    [InlineData(new double[] { 1, 2, 3, 4 }, 2.5)]
    [InlineData(new double[] { 5 }, 5.0)]
    public void Median_MatchesPythonStatistics(double[] values, double expected)
        => Assert.Equal(expected, SensorReplay.Median(values.ToList()));

    [Fact]
    public void Percentile_LinearInterpolationEndpoints()
    {
        var sorted = new List<double> { 10, 20, 30 };
        Assert.Equal(10, SensorReplay.Percentile(sorted, 0.0));
        Assert.Equal(30, SensorReplay.Percentile(sorted, 1.0));
        Assert.Equal(20, SensorReplay.Percentile(sorted, 0.5));
        Assert.True(double.IsNaN(SensorReplay.Percentile(new List<double>(), 0.5)));
    }

    [Fact]
    public void ShovelReplay_TransitionsAndClearSemantics()
    {
        var model = new ShovelModel { FilterWindow = 3, HangEnter = 100, HangClear = 50 };
        var rows = new List<SensorReplay.ShovelRow>();
        for (var i = 0; i < 4; i++)
        {
            rows.Add(new SensorReplay.ShovelRow(i * 0.05, 20, 30, true)); // stage
        }
        for (var i = 4; i < 8; i++)
        {
            rows.Add(new SensorReplay.ShovelRow(i * 0.05, 400, 500, true)); // hang
        }
        var r = SensorReplay.ReplayShovel(model, rows);
        Assert.Equal(1, r.HangTransitions);
        Assert.Equal(3, r.HangAsserts); // i=5 first hang, i=6,7 steady
        Assert.Equal(0, r.ClearTransitions); // no return-to-stage rows
        Assert.Equal(8, r.TotalRows);
        Assert.Equal(6, r.ReadyRows);
    }

    [Fact]
    public void AdcReplay_BandDecisionsAndRatioDisagreement()
    {
        var model = new FrontAdcModel
        {
            FilterWindow = 3, SignalMin = 10, DiffLow = -50, DiffHigh = 50, RatioThreshold = 0.02,
        };
        // diff=+100 → band "right" (diff > high); ratio = 100/1100 ≈ 0.09 > 0.02
        // → ratio model "left": one disagreement expected.
        var rows = new List<SensorReplay.AdcRow>
        {
            new(0.0, 600, 500, true),
            new(0.05, 600, 500, true),
            new(0.1, 600, 500, true),
        };
        var r = SensorReplay.ReplayFrontAdc(model, rows, "right");
        Assert.Equal(1, r.ReadyRows);
        Assert.Equal(1, r.DirectionCounts["right"]);
        Assert.Equal(0, r.Mismatches);
        Assert.Equal(1, r.DirectionCounts.GetValueOrDefault("band_ratio_disagree"));
    }

    [Fact]
    public void AdcReplay_InvalidRowResetsBuffer()
    {
        var model = new FrontAdcModel { FilterWindow = 3, SignalMin = 10, DiffLow = -50, DiffHigh = 50 };
        var rows = new List<SensorReplay.AdcRow>
        {
            new(0.0, 600, 500, true), new(0.05, 600, 500, true),
            new(0.1, 600, 500, false), // invalid → reset per MBri fail-safe
            new(0.15, 600, 500, true), new(0.2, 600, 500, true), new(0.25, 600, 500, true),
        };
        var r = SensorReplay.ReplayFrontAdc(model, rows, "right");
        Assert.Equal(1, r.InvalidRows);
        Assert.Equal(1, r.ReadyRows); // only the last three rows fill the window
    }

    [Fact]
    public void CsvTable_QuotedFieldsAndRagged()
    {
        var table = CsvTable.Parse("t.csv", "a,b\n\"x,1\",2\n3,4\n");
        Assert.Equal(new[] { "a", "b" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("x,1", table.Rows[0][0]);
        Assert.Throws<CsvParseException>(() => CsvTable.Parse("t.csv", "a,b\n1,2,3\n"));
        Assert.Throws<CsvParseException>(() => CsvTable.Parse("t.csv", "wrong\n1\n", ["a", "b"]));
        Assert.Throws<CsvParseException>(() => CsvTable.Parse("t.csv", ""));
    }

    [Fact]
    public void ConfigSnapshot_ParsesScalarsIgnoresDictsAndComments()
    {
        var snapshot = ConfigSnapshot.Parse("""
            GRAY_FILTER_WINDOW = 3
            SHOVEL_HANG_ENTER = 1134.1  # 悬空阈值
            SHOVEL_HANG_CLEAR = 1317.5
            GRAY_EDGE_REFERENCE = {
                "front": 494.0,
            }
            SOME_TEXT = "not a number"
            IR_DIRECTION_RATIO_THRESHOLD = 0.2
            """);
        Assert.Null(snapshot.GrayNearEdgeEnter);
        Assert.Equal(1317.5, snapshot.ShovelHangClear);
        Assert.Equal(1134.1, snapshot.ShovelHangEnter);
        Assert.Equal(0.2, snapshot.FrontRatioThreshold);
    }
}
