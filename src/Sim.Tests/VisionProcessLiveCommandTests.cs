using Sim.Cli;
using Sim.Core;
using Sim.Protocol;
using Sim.VisionReplay;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// `vision live --process` protocol smoke: the repo's VisionStreamStub child process
/// streams JSONL frames → ExternalProcessStreamSource → LiveVisionBridge over a whole
/// match → the delivered frames are written as a vision-replay-v1 sidecar and the
/// sidecar, re-read by <see cref="VisionReplayAdapter"/>, must consume the very same
/// ledger (the structural equivalence of the shared selector, now over a real
/// subprocess pipe instead of a CSV file).
///
/// The live run uses a short scenario built in-test (implement plan batch 5: the
/// `--realtime 1x` wall cost equals the match duration, so tests must not use the
/// 120 s official scenario and must not add a CLI option for it). The stub paces its
/// frames by wall clock with a small lead so every frame is delivered before the
/// classify call that its timestamp belongs to — that is the documented condition
/// under which a process session is reproducible.
/// </summary>
public class VisionProcessLiveCommandTests(ITestOutputHelper output) : IDisposable
{
    private const string ScenarioPath = "scenarios/wushu-ring-2026.json";
    private const double MatchSeconds = 6.0;
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

    private static string StubExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "VisionStreamStub.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                "VisionStreamStub.exe not found next to the test assembly; build Sim.Tests first");
        }
        return exe;
    }

    private string NewWorkDir()
    {
        var work = Path.Combine(Path.GetTempPath(), $"visionprocess-{Guid.NewGuid():N}");
        _tempDirs.Add(work);
        Directory.CreateDirectory(work);
        return work;
    }

    /// <summary>官方场景的短时长副本(测试内构造, 不新增场景文件)。</summary>
    private string ShortScenario(string work)
    {
        var scenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(FindRepoFile(ScenarioPath)));
        var shortScenario = scenario with { Field = scenario.Field with { MatchDuration = MatchSeconds } };
        var path = Path.Combine(work, "short-scenario.json");
        File.WriteAllText(path, ProtocolJson.Serialize(shortScenario));
        return path;
    }

    private static int RunLive(params string[] args) => Program.Main([.. args]);

    // ---------- usage / validation (exit 2 / 1, zero output) ----------

    [Fact]
    public void Live_RequiresExactlyOneSource()
    {
        var work = NewWorkDir();
        var scenario = ShortScenario(work);
        var outPath = Path.Combine(work, "r.json");
        Assert.Equal(2, RunLive("vision", "live", "--scenario", scenario, "--out", outPath));
        Assert.Equal(2, RunLive("vision", "live",
            "--source", "whatever.csv", "--process", StubExe(), "--scenario", scenario, "--out", outPath));
        Assert.Equal(2, RunLive("vision", "live", "--process", StubExe(), "--out", outPath));
        Assert.False(File.Exists(outPath), "用法错误不得产出报告");
    }

    [Fact]
    public void Realtime_OnlyAcceptsExplicit1x()
    {
        var work = NewWorkDir();
        var scenario = ShortScenario(work);
        var outPath = Path.Combine(work, "r.json");
        Assert.Equal(1, RunLive("vision", "live",
            "--process", StubExe(), "--realtime", "2x", "--scenario", scenario, "--out", outPath));
        Assert.False(File.Exists(outPath));
        // 缺值: --realtime 后面没有参数, 显式报错而不是静默按快跑跑。
        Assert.Equal(1, RunLive("vision", "live",
            "--process", StubExe(), "--scenario", scenario, "--out", outPath, "--realtime"));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void UnstartableProcess_IsValidationError_ZeroOutput()
    {
        var work = NewWorkDir();
        var scenario = ShortScenario(work);
        var outPath = Path.Combine(work, "r.json");
        var stderr = new StringWriter();
        var original = Console.Error;
        int exit;
        try
        {
            Console.SetError(stderr);
            exit = RunLive("vision", "live",
                "--process", "definitely-not-a-real-program-xyz --foo",
                "--scenario", scenario, "--out", outPath);
        }
        finally
        {
            Console.SetError(original);
        }
        Assert.Equal(1, exit);
        Assert.Contains("启动视觉流进程失败", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(outPath));
        Assert.False(Directory.Exists(Path.ChangeExtension(outPath, null)! + "-sidecar"));
    }

    [Fact]
    public void ProcessWithoutFrames_IsAnExplicitFailure_NotAnEmptyEvidencePackage()
    {
        // 子进程零帧(比如权重/相机缺失): 没有 SimT 0 基准就写不出证据包, 必须显式失败,
        // 绝不产出一个空的"证据"。
        var work = NewWorkDir();
        var scenario = ShortScenario(work);
        var outPath = Path.Combine(work, "r.json");
        Assert.Equal(1, RunLive("vision", "live",
            "--process", $"\"{StubExe()}\" --count 0",
            "--scenario", scenario, "--out", outPath));
        Assert.False(File.Exists(outPath));
        Assert.False(Directory.Exists(Path.ChangeExtension(outPath, null)! + "-sidecar"));
    }

    // ---------- end-to-end protocol smoke ----------

    [Fact]
    public void ProcessSource_StubSubprocess_FullChain_SidecarReplayEquivalence()
    {
        var work = NewWorkDir();
        var scenario = ShortScenario(work);
        var outPath = Path.Combine(work, "vision-live-process.json");
        // 100 帧 @50ms 时间戳/墙钟节奏, 提前 400ms 发出(补偿进程启动与管道延迟, 并给
        // CI 负载留出调度余量): 比赛时长 6s 内流在 ~4.6s 结束 —— 之后 classify 走
        // stale, 也是要覆盖的路径。
        var command = $"\"{StubExe()}\" --count 100 --step-ms 50 --interval-ms 50 --lead-ms 400";
        var exit = RunLive("vision", "live",
            "--process", command, "--realtime", "1x",
            "--scenario", scenario, "--out", outPath, "--force");
        Assert.Equal(0, exit);

        var report = ProtocolJson.Deserialize<VisionLiveBridgeReport>(File.ReadAllText(outPath));
        Assert.Empty(report.Validate());
        Assert.Equal(VisionReplaySchemas.VisionLiveReportFormat, report.Schema);
        Assert.False(report.GroundTruth);
        Assert.Equal("evidence_only", report.Grade);
        Assert.Equal("wushu-ring-2026", report.ScenarioId);

        // source: 进程源如实入账(命令行/无源文件哈希/帧尺寸/类别映射); 进程分区带状态与故障。
        var source = report.Source!;
        Assert.Equal("process", source.Kind);
        Assert.Equal(command, source.Path);
        Assert.Equal(ExternalProcessStreamSource.DefaultSession, source.Session);
        Assert.Equal(ExternalProcessStreamSource.StreamDialect, source.Dialect);
        Assert.Equal("", source.Sha256);
        Assert.Equal(0, source.Bytes);
        Assert.Equal(640, source.FrameWidth);
        Assert.Equal(480, source.FrameHeight);
        Assert.Equal("buff", source.ClassMapping["good"]);
        Assert.Equal("debuff", source.ClassMapping["bad"]);
        Assert.True(source.Frames > 0);
        var process = report.Process!;
        Assert.Equal(command, process.Command);
        Assert.Equal(1.0, process.Realtime);
        Assert.Equal(0, process.Faults);
        Assert.Equal(0, process.RejectedLines);
        Assert.Null(process.LastFault);
        Assert.Equal("exited", process.State);
        Assert.Equal(0, process.ExitCode);

        // link: 非空洞(至少一次 classify 落在真实检测上), 且计数自洽。
        var link = report.Link!;
        Assert.True(link.ClassifyCalls >= 1, $"整场 classify 过少({link.ClassifyCalls})");
        Assert.True(link.DetectionCalls >= 1, "整场没有任何真实检测, 等价门可能空洞");
        Assert.Equal(link.ClassifyCalls, link.DetectionCalls + link.UnknownCalls);
        Assert.Equal(link.DeliveredFrames, link.ServedFrames + link.UnservedFrames);
        Assert.Contains("buff", link.FsmDetections.Keys);
        Assert.True(link.FrameAgeMs!.Count > 0);

        // equivalence: sidecar 重读后消费台账/事件指纹/比分逐位一致(共享选帧器的结构性保证)。
        var equivalence = report.Equivalence!;
        Assert.True(equivalence.ConsumptionSequenceMatches, equivalence.Conclusion);
        Assert.True(equivalence.EventFingerprintMatches, equivalence.Conclusion);
        Assert.True(equivalence.ScoresMatch, equivalence.Conclusion);
        Assert.Equal(equivalence.LiveCalls, equivalence.ReplayCalls);
        Assert.Equal(equivalence.LiveLedgerSha256, equivalence.ReplayLedgerSha256);
        Assert.Null(equivalence.FirstDivergence);

        // sidecar: 文件在盘上, 帧流全部来自进程会话, 交付帧数一致。
        var sidecar = report.Sidecar!;
        var framesPath = Path.Combine(sidecar.Directory, VisionReplayIO.FramesFileName);
        Assert.True(File.Exists(framesPath), $"缺少 {framesPath}");
        Assert.True(File.Exists(Path.Combine(sidecar.Directory, VisionReplayIO.ImportReportFileName)));
        var frames = VisionReplayIO.ParseFrames(File.ReadAllText(framesPath));
        Assert.Equal(link.DeliveredFrames, frames.Count);
        Assert.Equal(sidecar.Frames, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.Equal(ExternalProcessStreamSource.DefaultSession, frame.Session);
            Assert.Equal(640, frame.FrameWidth);
            Assert.Equal(480, frame.FrameHeight);
            Assert.Equal("target", frame.Status);
            var detection = Assert.Single(frame.Detections);
            Assert.Equal("buff", detection.Label);
            Assert.Equal("good", detection.RawType);
        });
        // 单调非降 + 锚点 = 首帧(SimT 0 基准): 与 CSV 源同一时基契约。
        Assert.Equal(frames[0].TimestampMs, sidecar.AnchorTimestampMs);
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.True(frames[i].TimestampMs >= frames[i - 1].TimestampMs, $"第 {i} 帧时间戳倒退");
        }

        // 既有 CLI 必须能直接消费该包(进程源 sidecar 与 CSV 源同一形态)。
        var evalOut = Path.Combine(work, "sidecar-eval.json");
        Assert.Equal(0, RunLive("vision", "evaluate",
            "--evidence", sidecar.Directory, "--scenario", scenario, "--out", evalOut, "--force"));
        var evalReport = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));
        Assert.Equal(sidecar.EvidenceId, evalReport.EvidenceId);
        Assert.Equal(equivalence.LiveCalls, evalReport.Policy!.ClassifyCalls);

        output.WriteLine($"进程源场次: 交付={link.DeliveredFrames} classify={link.ClassifyCalls}"
            + $" 检测={link.DetectionCalls} 等价={equivalence.Conclusion} 包={frames.Count} 帧");
    }
}
