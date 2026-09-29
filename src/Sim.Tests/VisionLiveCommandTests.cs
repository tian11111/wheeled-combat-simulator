using Sim.Cli;
using Sim.Core;
using Sim.Protocol;
using Sim.VisionReplay;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// `vision live` command tests: argument/usage validation returns 2, validation and
/// IO failures return 1 with zero output, and one end-to-end run over the vendored
/// fixture proves the report structure (source/link/equivalence/sidecar/live/
/// baseline/diff), the sidecar package on disk, the structural equivalence with
/// <see cref="VisionReplayAdapter"/>, and that the written package stays consumable
/// by the existing `vision evaluate`.
/// </summary>
public class VisionLiveCommandTests(ITestOutputHelper output) : IDisposable
{
    private const string MiniFixtureDir = "src/Sim.Tests/fixtures/mbri-vision-mini";
    // 245 帧会话(实测 82 次 classify/23 次窗内服务/59 次 stale/2 次真实检测): 等价门若失配,
    // 这个规模足以暴露; hunt_drive 会话只有 2 次调用, 作为非空洞下界没有意义。
    private const string RecheckCsvName = "good_recheck_20260816_223647_excerpt.csv";
    private const string HuntCsvName = "hunt_drive_20260817_095205.csv";
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

    private string NewWorkDir()
    {
        var work = Path.Combine(Path.GetTempPath(), $"visionlive-{Guid.NewGuid():N}");
        _tempDirs.Add(work);
        Directory.CreateDirectory(work);
        return work;
    }

    private static string FixtureCsv(string name)
        => FindRepoFile(Path.Combine(MiniFixtureDir, name));

    private static int RunLive(params string[] args) => Program.Main([.. args]);

    private static VisionLiveBridgeReport ReadReport(string path)
        => ProtocolJson.Deserialize<VisionLiveBridgeReport>(File.ReadAllText(path));

    // ---------- usage / validation (exit 2 / 1, zero output) ----------

    [Fact]
    public void MissingRequiredArguments_IsUsageError()
    {
        var outPath = Path.Combine(NewWorkDir(), "r.json");
        Assert.Equal(2, RunLive("vision", "live", "--source", FixtureCsv(HuntCsvName), "--scenario", FindRepoFile(ScenarioPath)));
        Assert.Equal(2, RunLive("vision", "live", "--source", FixtureCsv(HuntCsvName), "--out", outPath));
        Assert.Equal(2, RunLive("vision", "live", "--scenario", FindRepoFile(ScenarioPath), "--out", outPath));
        Assert.False(File.Exists(outPath), "用法错误不得产出报告");
        Assert.False(Directory.Exists(Path.ChangeExtension(outPath, null)! + "-sidecar"), "用法错误不得产出 sidecar");
    }

    [Fact]
    public void InvalidMaxAge_IsRejected_NotSilentlyDefaulted()
    {
        var work = NewWorkDir();
        var outPath = Path.Combine(work, "r.json");
        Assert.Equal(1, RunLive("vision", "live",
            "--source", FixtureCsv(HuntCsvName), "--scenario", FindRepoFile(ScenarioPath),
            "--out", outPath, "--max-age-ms", "abc"));
        Assert.False(File.Exists(outPath));
        Assert.Equal(1, RunLive("vision", "live",
            "--source", FixtureCsv(HuntCsvName), "--scenario", FindRepoFile(ScenarioPath),
            "--out", outPath, "--max-age-ms", "0"));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void ExistingOutput_RequiresForce()
    {
        var work = NewWorkDir();
        var outPath = Path.Combine(work, "r.json");
        File.WriteAllText(outPath, "{}");
        Assert.Equal(1, RunLive("vision", "live",
            "--source", FixtureCsv(HuntCsvName), "--scenario", FindRepoFile(ScenarioPath), "--out", outPath));
        Assert.Equal("{}", File.ReadAllText(outPath)); // 未被改写
    }

    [Fact]
    public void MissingOrNonDialectSource_IsValidationError()
    {
        var work = NewWorkDir();
        var outPath = Path.Combine(work, "r.json");
        Assert.Equal(1, RunLive("vision", "live",
            "--source", Path.Combine(work, "missing.csv"), "--scenario", FindRepoFile(ScenarioPath), "--out", outPath));
        var bad = Path.Combine(work, "bad.csv");
        File.WriteAllText(bad, "t,sequence\n1,2\n");
        Assert.Equal(1, RunLive("vision", "live",
            "--source", bad, "--scenario", FindRepoFile(ScenarioPath), "--out", outPath));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void InvalidScenario_IsValidationError()
    {
        var work = NewWorkDir();
        var scenarioPath = Path.Combine(work, "bad-scenario.json");
        File.WriteAllText(scenarioPath, "{\"id\":\"\"}");
        var outPath = Path.Combine(work, "r.json");
        Assert.Equal(1, RunLive("vision", "live",
            "--source", FixtureCsv(HuntCsvName), "--scenario", scenarioPath, "--out", outPath));
        Assert.False(File.Exists(outPath));
    }

    // ---------- end to end ----------

    [Fact]
    public void Live_RunsWholeMatch_WritesReportAndSidecar_WithStructuralEquivalence()
    {
        var work = NewWorkDir();
        var outPath = Path.Combine(work, "vision-live.json");
        var exit = RunLive("vision", "live",
            "--source", FixtureCsv(RecheckCsvName),
            "--scenario", FindRepoFile(ScenarioPath),
            "--max-age-ms", "500", "--out", outPath, "--force");
        Assert.Equal(0, exit);

        var report = ReadReport(outPath);
        Assert.Empty(report.Validate());
        Assert.Equal("vision-live-bridge-report-v1", report.Schema);
        Assert.Equal(VisionReplaySchemas.VisionLiveReportFormat, report.Schema);
        Assert.False(report.GroundTruth);
        Assert.Equal("evidence_only", report.Grade);
        Assert.Equal(500.0, report.MaxAgeMs);
        Assert.Equal("wushu-ring-2026", report.ScenarioId);
        Assert.Equal(64, report.ContentSha256!.Length);

        // source: 显式类别映射、如实入账(与清单声明一致)。
        var source = report.Source!;
        Assert.Equal("csv", source.Kind);
        Assert.Equal(RecheckCsvName, source.Session);
        Assert.Equal(Path.GetFullPath(FixtureCsv(RecheckCsvName)), source.Path);
        Assert.Equal("mbri-hunt-detections", source.Dialect);
        Assert.Equal(64, source.Sha256.Length);
        Assert.Equal("buff", source.ClassMapping["good"]);
        Assert.Equal("debuff", source.ClassMapping["bad"]);
        Assert.Equal(640, source.FrameWidth);
        Assert.Equal(480, source.FrameHeight);
        Assert.True(source.Frames > 200, $"fixture 帧数异常: {source.Frames}");
        Assert.True(source.DurationSeconds > 40, $"fixture 时长异常: {source.DurationSeconds}");

        // link: 非空洞(不是"两侧都 stale"的假绿), 且 stale 率与实测相符。
        var link = report.Link!;
        Assert.True(link.ClassifyCalls >= 10, $"整场 classify 过少({link.ClassifyCalls})");
        Assert.True(link.ServedFrames >= 10, $"窗内被服务帧过少({link.ServedFrames})");
        Assert.True(link.DetectionCalls > 0, "整场没有任何真实检测, 等价门可能空洞");
        Assert.True(link.StaleCalls > 0, "fixture 流远短于比赛时长, 后段必有 stale");
        Assert.Equal(link.ClassifyCalls, link.DetectionCalls + link.UnknownCalls);
        Assert.Equal(link.ClassifyCalls,
            link.ServedCalls + link.StaleCalls + link.UnknownReasons.GetValueOrDefault("no_frame"));
        Assert.Equal(link.StaleCalls, link.UnknownReasons.GetValueOrDefault("stale"));
        Assert.Equal(link.StaleRate, link.StaleCalls / (double)link.ClassifyCalls, 9);
        Assert.Equal(link.DeliveredFrames, link.ServedFrames + link.UnservedFrames);
        Assert.Contains(link.FsmDetections, kv => kv.Key is "buff" or "debuff");
        Assert.True(link.FrameAgeMs!.Count > 0);
        Assert.True(link.FrameAgeMs.P95 >= link.FrameAgeMs.P50);
        Assert.True(link.ReportedFps!.Count > 0);
        Assert.True(link.FirstServeSimT < link.LastServeSimT);

        // equivalence: 同帧数据经 sidecar 重读后, 消费序列/事件指纹/比分逐位一致。
        var equivalence = report.Equivalence!;
        Assert.Equal(VisionReplayAdapter.ModeName, equivalence.Reference);
        Assert.Null(equivalence.FirstDivergence);
        Assert.True(equivalence.ConsumptionSequenceMatches, equivalence.Conclusion);
        Assert.True(equivalence.EventFingerprintMatches, equivalence.Conclusion);
        Assert.True(equivalence.ScoresMatch, equivalence.Conclusion);
        Assert.Equal(equivalence.LiveCalls, equivalence.ReplayCalls);
        Assert.Equal(equivalence.LiveLedgerSha256, equivalence.ReplayLedgerSha256);
        Assert.Equal(64, equivalence.LiveLedgerSha256.Length);

        // sidecar: 文件在盘上, 锚点 = 源会话首帧, 哈希锁定与报告一致。
        var sidecar = report.Sidecar!;
        Assert.Equal(Path.GetFullPath(Path.Combine(work, "vision-live-sidecar")), sidecar.Directory);
        var framesPath = Path.Combine(sidecar.Directory, VisionReplayIO.FramesFileName);
        var importPath = Path.Combine(sidecar.Directory, VisionReplayIO.ImportReportFileName);
        Assert.True(File.Exists(framesPath), $"缺少 {framesPath}");
        Assert.True(File.Exists(importPath), $"缺少 {importPath}");
        var framesBytes = File.ReadAllBytes(framesPath);
        Assert.Equal(VisionReplayIO.Sha256Hex(framesBytes), sidecar.EvidenceSha256);
        Assert.Equal(VisionReplayIO.EvidenceId(sidecar.EvidenceSha256), sidecar.EvidenceId);
        var packageFrames = VisionReplayIO.ParseFrames(System.Text.Encoding.UTF8.GetString(framesBytes));
        Assert.Equal(sidecar.Frames, packageFrames.Count);
        Assert.Equal(link.DeliveredFrames, sidecar.Frames);
        Assert.Equal(link.ServedFrames, sidecar.ServedFrames);
        Assert.Equal(link.UnservedFrames, sidecar.UnservedFrames);
        // 决策③: 包内首帧 = 源会话首帧(SimT 0 基准锚点), 交付帧是会话帧的前缀。
        var originalSource = CsvStreamSource.Load(FixtureCsv(RecheckCsvName));
        Assert.Equal(originalSource.SessionFirstTimestampMs, sidecar.AnchorTimestampMs);
        Assert.Equal(originalSource.EvidenceFrames[0].TimestampMs, packageFrames[0].TimestampMs);
        Assert.Equal(
            originalSource.EvidenceFrames.Take(packageFrames.Count).Select(f => f.Sequence).ToArray(),
            packageFrames.Select(f => f.Sequence).ToArray());

        // live / baseline / diff: live 场次用桥, 基线用默认 classifyRate 桩, diff 明写非位对位。
        Assert.Equal("liveBridge", report.Live!.VisionMode);
        Assert.Equal("default(classifyRate)", report.Baseline!.VisionMode);
        Assert.Equal(report.Seed, report.Live.Seed);
        Assert.Equal(report.Seed, report.Baseline.Seed);
        Assert.True(report.Live.EventCount > 0);
        Assert.NotEmpty(report.Live.EventKinds);
        Assert.Equal("比赛时间结束", report.Live.DoneReason);
        var diff = report.Diff!;
        Assert.Contains("不做位对位", diff.Note, StringComparison.Ordinal);
        Assert.Equal(report.Baseline.FinalScores.Us - report.Live.FinalScores.Us, diff.ScoreUs, 9);
        Assert.Equal(report.Baseline.Ticks - report.Live.Ticks, diff.Ticks);
        Assert.Equal(report.Baseline.EventCount - report.Live.EventCount, diff.EventCount);
        Assert.Equal(diff.EventCount,
            diff.EventKinds.Values.Sum());
        Assert.Contains(report.Limitations, l => l.Contains("不证明识别准确率"));
        Assert.Contains(report.Limitations, l => l.Contains("摘要级"));

        // 既有 CLI 必须能直接消费该包(决策③: sidecar 与 vision evaluate 兼容)。
        var evalOut = Path.Combine(work, "sidecar-eval.json");
        var evalExit = Program.Main(
        [
            "vision", "evaluate",
            "--evidence", sidecar.Directory,
            "--scenario", FindRepoFile(ScenarioPath),
            "--out", evalOut, "--force",
        ]);
        Assert.Equal(0, evalExit);
        var evalReport = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));
        Assert.Equal(sidecar.EvidenceId, evalReport.EvidenceId);
        Assert.Equal(equivalence.LiveCalls, evalReport.Policy!.ClassifyCalls);

        output.WriteLine($"live: classify={link.ClassifyCalls} 服务={link.ServedCalls} stale={link.StaleCalls}"
            + $" 检测={link.DetectionCalls} 交付={link.DeliveredFrames} 包={packageFrames.Count}"
            + $" live 比分={report.Live.FinalScores.Us}:{report.Live.FinalScores.Them}"
            + $" 基线={report.Baseline.FinalScores.Us}:{report.Baseline.FinalScores.Them}");
    }

    [Fact]
    public void Live_IsDeterministicAcrossRuns_SameEvidenceSameNumbers()
    {
        var work = NewWorkDir();
        var scenario = FindRepoFile(ScenarioPath);
        var csv = FixtureCsv(RecheckCsvName);
        var firstOut = Path.Combine(work, "same.json");
        Assert.Equal(0, RunLive("vision", "live", "--source", csv, "--scenario", scenario, "--out", firstOut, "--force"));
        var first = ReadReport(firstOut);
        // 同一 --out 复跑(覆盖): 报告内容指纹必须逐位一致 —— generatedAt 不入哈希,
        // sidecar 目录/证据包哈希/两次场次的账本与指纹全部由输入决定。
        Assert.Equal(0, RunLive("vision", "live", "--source", csv, "--scenario", scenario, "--out", firstOut, "--force"));
        var second = ReadReport(firstOut);

        Assert.Equal(first.Equivalence!.LiveLedgerSha256, second.Equivalence!.LiveLedgerSha256);
        Assert.Equal(first.Equivalence.ReplayLedgerSha256, second.Equivalence.ReplayLedgerSha256);
        Assert.Equal(first.Live!.EventFingerprint, second.Live!.EventFingerprint);
        Assert.Equal(first.Baseline!.EventFingerprint, second.Baseline!.EventFingerprint);
        Assert.Equal(first.Live.FinalScores.Us, second.Live.FinalScores.Us);
        Assert.Equal(first.Diff!.EventKinds, second.Diff!.EventKinds);
        Assert.Equal(first.Sidecar!.EvidenceSha256, second.Sidecar!.EvidenceSha256);
        Assert.Equal(first.Sidecar.Directory, second.Sidecar.Directory);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
    }

    [Fact]
    public void ShortSession_ReportsHighStaleRateAndStillSucceeds()
    {
        // hunt_drive 流仅 18.2 s 而比赛 120 s: 窗口内服务极少, 之后如实记 stale, 不伪造帧。
        var work = NewWorkDir();
        var outPath = Path.Combine(work, "hunt.json");
        Assert.Equal(0, RunLive("vision", "live",
            "--source", FixtureCsv(HuntCsvName), "--scenario", FindRepoFile(ScenarioPath),
            "--out", outPath, "--force"));
        var report = ReadReport(outPath);
        Assert.Empty(report.Validate());
        Assert.True(report.Link!.ClassifyCalls > 0);
        Assert.True(report.Link.ServedCalls > 0, "前段必有窗内服务帧");
        Assert.Equal(report.Link.ClassifyCalls,
            report.Link.ServedCalls + report.Link.StaleCalls + report.Link.UnknownReasons.GetValueOrDefault("no_frame"));
        Assert.Equal(0, report.Link.UnservedFrames + report.Link.ServedFrames - report.Link.DeliveredFrames);
        Assert.True(report.Equivalence!.ConsumptionSequenceMatches, report.Equivalence.Conclusion);
        Assert.True(report.Live!.Ticks > 0);
    }

    [Fact]
    public void JsonFlag_PrintsFullReportOnStdout()
    {
        var work = NewWorkDir();
        var outPath = Path.Combine(work, "with-json.json");
        var stdout = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(stdout);
            Assert.Equal(0, RunLive("vision", "live",
                "--source", FixtureCsv(HuntCsvName), "--scenario", FindRepoFile(ScenarioPath),
                "--out", outPath, "--json", "--force"));
        }
        finally
        {
            Console.SetOut(original);
        }
        var text = stdout.ToString();
        var lastLine = text.TrimEnd().Split('\n')[^1].Trim();
        var report = ProtocolJson.Deserialize<VisionLiveBridgeReport>(lastLine);
        Assert.Empty(report.Validate());
        Assert.Equal(ReadReport(outPath).ContentSha256, report.ContentSha256);
    }
}