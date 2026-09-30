using Sim.Cli;
using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;
using Sim.VisionReplay;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// Live-stream batch gate over the vendored mbri-vision-mini fixture:
/// <see cref="CsvStreamSource"/> reuses the import normalization, so the live path
/// (CsvStreamSource + LiveVisionBridge) and the offline path (`vision import` →
/// frames.jsonl → VisionReplayAdapter) must produce bit-identical consumption
/// records and match fingerprints, and a live session's delivered frames must
/// round-trip through a vision-replay-v1 sidecar with the same SimT 0 anchor.
/// </summary>
public class VisionStreamEquivalenceTests(ITestOutputHelper output) : IDisposable
{
    private const string MiniFixtureDir = "src/Sim.Tests/fixtures/mbri-vision-mini";
    private const string HuntCsvName = "hunt_drive_20260817_095205.csv";
    private const string RecheckCsvName = "good_recheck_20260816_223647_excerpt.csv";
    private const string ScenarioPath = "scenarios/wushu-ring-2026.json";
    private const double MaxAgeMs = 500;
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

    private static Scenario FixtureScenario()
        => ProtocolJson.Deserialize<Scenario>(File.ReadAllText(FindRepoFile(ScenarioPath)));

    private string NewWorkDir()
    {
        var work = Path.Combine(Path.GetTempPath(), $"visionstream-{Guid.NewGuid():N}");
        _tempDirs.Add(work);
        Directory.CreateDirectory(work);
        return work;
    }

    /// <summary>
    /// Fixture copy + real `vision import` run: the offline evidence package of the
    /// very same CSV bytes the live path reads.
    /// </summary>
    private (string WorkDir, string EvidenceDir, VisionImportReport Report, List<VisionFrameRecord> Frames) ImportedFixture()
    {
        var work = NewWorkDir();
        foreach (var file in Directory.EnumerateFiles(FindRepo(MiniFixtureDir)))
        {
            File.Copy(file, Path.Combine(work, Path.GetFileName(file)));
        }
        var evidenceDir = Path.Combine(work, "evidence");
        var importOut = Path.Combine(work, "import-report.json");
        var exit = Program.Main(
        [
            "vision", "import",
            "--manifest", Path.Combine(work, "selection.manifest.json"),
            "--evidence-out", evidenceDir,
            "--out", importOut,
            "--force",
        ]);
        Assert.Equal(0, exit);
        var report = ProtocolJson.Deserialize<VisionImportReport>(File.ReadAllText(importOut));
        var frames = VisionReplayIO.ParseFrames(
            File.ReadAllText(Path.Combine(evidenceDir, VisionReplayIO.FramesFileName)));
        return (work, evidenceDir, report, frames);
    }

    private sealed record MatchRun(List<string> Fingerprints, long Ticks, Scores Scores, string? DoneReason);

    private static MatchRun RunMatch(Scenario scenario, IVisionAdapter adapter)
    {
        using var engine = MatchEngineHost.Create(scenario, adapter);
        engine.Arm();
        var fingerprints = new List<string>();
        while (!engine.Done)
        {
            var snapshot = engine.Tick();
            if (snapshot.Events is { Count: > 0 })
            {
                fingerprints.AddRange(snapshot.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}"));
            }
        }
        return new MatchRun(fingerprints, engine.TickIndex, engine.Scores, engine.Us.Fsm.DoneReason);
    }

    /// <summary>Every ledger field of one classify call, as a comparable line.</summary>
    private static string Dump(VisionReplayConsumeRecord record)
        => $"{record.Role}|{record.SimT:R}|{record.FrameSequence?.ToString() ?? "-"}"
            + $"|{record.AgeMs?.ToString("R") ?? "-"}|{record.Reason ?? "-"}|{record.Label}|{record.Confidence:R}";

    /// <summary>Working-set frame fields (label/confidence/offset_x per detection), as a comparable line.</summary>
    private static string Dump(VisionReplayFrame frame)
        => $"{frame.Sequence}|{frame.TimestampMs:R}|{frame.Status}|{frame.Error ?? "-"}"
            + $"|{frame.SelectedTargetIndex?.ToString() ?? "-"}"
            + "|" + string.Join(",", frame.Detections.Select(d => $"{d.Label}:{d.Confidence:R}:{d.OffsetX?.ToString("R") ?? "-"}"));

    /// <summary>Every field of one evidence frame + its detections, as a comparable line.</summary>
    private static string Dump(VisionFrameRecord frame)
        => $"{frame.Session}|{frame.Sequence}|{frame.TimestampMs:R}|{frame.ReceivedAgeMs:R}|{frame.Status}"
            + $"|{frame.Error ?? "-"}|{frame.Fps?.ToString("R") ?? "-"}|{frame.InferenceMs?.ToString("R") ?? "-"}"
            + $"|{frame.FrameWidth}x{frame.FrameHeight}|{frame.DuplicateReceives}|{frame.SelectedTargetIndex?.ToString() ?? "-"}"
            + "|" + string.Join(",", frame.Detections.Select(d =>
                $"{d.ClassId}:{d.RawType}:{d.Label}:{d.Confidence:R}:{d.OffsetX:R}:{d.OffsetY:R}:{string.Join("/", d.Bbox)}"));

    private static void AssertConsumesEqual(
        IReadOnlyList<VisionReplayConsumeRecord> expected, IReadOnlyList<VisionReplayConsumeRecord> actual, ITestOutputHelper output)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var left = Dump(expected[i]);
            var right = Dump(actual[i]);
            output.WriteLine($"#{i} replay={left} live={right}");
            Assert.True(left == right, $"第 {i} 条消费记录分叉: replay={left} live={right}");
        }
    }

    // ---------- source ----------

    [Fact]
    public void CsvStreamSource_StartsAtSimTZeroAndScalesArrivals()
    {
        var work = NewWorkDir();
        var csvPath = Path.Combine(work, HuntCsvName);
        File.Copy(FindRepoFile(Path.Combine(MiniFixtureDir, HuntCsvName)), csvPath);
        var source = CsvStreamSource.Load(csvPath);

        Assert.Equal(HuntCsvName, source.Session);
        Assert.Equal(Path.GetFullPath(csvPath), source.SourcePath);
        Assert.True(source.EvidenceFrames.Count > 100, $"fixture 帧数异常: {source.EvidenceFrames.Count}");
        // 预热行(无 sequence 的 no_data_or_stale 行)与 import 一致地被丢弃。
        Assert.Equal(1, source.EvidenceFrames[0].Sequence);
        Assert.Equal(0.0, source.Frames[0].ArrivalSimT);
        Assert.Equal(source.EvidenceFrames.Count, source.Frames.Count);
        Assert.Equal(source.EvidenceFrames.Count, source.Stat.Frames);
        // 显式类别映射与帧尺寸: 单文件流没有清单可对照, 但口径必须写明。
        Assert.Equal("buff", source.Manifest.ClassMapping["good"]);
        Assert.Equal("debuff", source.Manifest.ClassMapping["bad"]);
        Assert.Equal(640, source.Manifest.FrameWidth);
        Assert.Equal(480, source.Manifest.FrameHeight);

        // 到达时刻 = (vision_timestamp_ms − 首帧)/1000 s, 边界闭合。
        Assert.Equal(1, source.PumpUntil(0.0));
        Assert.Single(source.Released);
        var secondArrival = (source.EvidenceFrames[1].TimestampMs - source.EvidenceFrames[0].TimestampMs) / 1000.0;
        Assert.True(secondArrival > 0, "fixture 前两帧应有不同时间戳");
        Assert.Equal(0, source.PumpUntil(secondArrival - 0.001));
        Assert.Equal(1, source.PumpUntil(secondArrival));
        Assert.Equal(2, source.Released.Count);
        Assert.Equal(source.EvidenceFrames[1].Sequence, source.Released[1].Sequence);

        // 流末尾之后不再放出任何帧; 全部交付时顺序与证据帧一致。
        Assert.Equal(source.EvidenceFrames.Count - 2, source.PumpUntil(source.DurationSeconds + 1));
        Assert.Equal(source.EvidenceFrames.Count, source.ReleasedEvidenceFrames.Count);
        Assert.Equal(0, source.PumpUntil(source.DurationSeconds + 10));
        Assert.Equal(
            source.EvidenceFrames.Select(f => f.Sequence).ToArray(),
            source.ReleasedEvidenceFrames.Select(f => f.Sequence).ToArray());
        output.WriteLine($"fixture: {source.Session} 帧={source.EvidenceFrames.Count} 时长={source.DurationSeconds:0.###}s");
    }

    [Fact]
    public void CsvStreamSource_RejectsNonDialectAndMissingFile()
    {
        var work = NewWorkDir();
        var bad = Path.Combine(work, "bad.csv");
        File.WriteAllText(bad, "t,sequence\n1,2\n");
        var error = Assert.Throws<VisionEvidenceException>(() => CsvStreamSource.Load(bad));
        Assert.Contains("表头", error.Message, StringComparison.Ordinal);
        Assert.Contains("vision_status", error.Message, StringComparison.Ordinal);
        Assert.Throws<VisionEvidenceException>(() => CsvStreamSource.Load(Path.Combine(work, "missing.csv")));

        // 只有预热行(无 sequence)的文件没有 SimT 0 基准, 必须显式拒绝。
        var headers = MbriVisionDialect.HuntDetectionColumns;
        var warmupOnly = Path.Combine(work, "warmup.csv");
        File.WriteAllText(warmupOnly, string.Join(",", headers) + "\n" + string.Join(",", headers.Select(h => h == "vision_status" ? "no_data_or_stale" : "")));
        var noFrame = Assert.Throws<VisionEvidenceException>(() => CsvStreamSource.Load(warmupOnly));
        Assert.Contains("不含任何帧行", noFrame.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvStreamSource_RejectsOversizedFileLikeTheImportPath()
    {
        // 与 vision import 同一 128MB 上限: 必须在读入内存之前拒绝(此处用稀疏长度
        // 声明的超限文件, 校验发生在 File.ReadAllBytes 之前, 不会真读 129MB)。
        var work = NewWorkDir();
        var huge = Path.Combine(work, HuntCsvName);
        using (var stream = File.Create(huge))
        {
            stream.SetLength(VisionReplayIO.MaxSourceFileBytes + 1);
        }
        var error = Assert.Throws<VisionEvidenceException>(() => CsvStreamSource.Load(huge));
        Assert.Contains(VisionReplayIO.MaxSourceFileBytesLabel, error.Message, StringComparison.Ordinal);
        Assert.Contains(HuntCsvName, error.Message, StringComparison.Ordinal);
        File.Delete(huge);
    }

    [Fact]
    public void CsvStreamSource_StreamsTheSameFramesAsTheImportPath()
    {
        var (work, _, report, frames) = ImportedFixture();
        var imported = frames.Where(f => f.Session == HuntCsvName).ToList();
        var source = CsvStreamSource.Load(Path.Combine(work, HuntCsvName));

        // 同一解析器: live 源与 import 产物逐字段一致(否则等价门无从谈起)。
        Assert.Equal(imported.Count, source.EvidenceFrames.Count);
        for (var i = 0; i < imported.Count; i++)
        {
            Assert.True(Dump(imported[i]) == Dump(source.EvidenceFrames[i]),
                $"第 {i} 帧与 import 产物不一致: import={Dump(imported[i])} live={Dump(source.EvidenceFrames[i])}");
        }
        // 解析统计口径一致(方言/行数/预热/重收/首末时间戳); import 的分文件统计不填
        // sha/bytes(清单已持有), live 源如实补齐 —— 必须等于夹具清单声明的哈希。
        var importStat = report.Files.Single(f => f.Path == HuntCsvName);
        var manifest = ProtocolJson.Deserialize<VisionReplayManifest>(
            File.ReadAllText(Path.Combine(work, "selection.manifest.json")));
        var declared = manifest.Files.Single(f => f.Path == HuntCsvName);
        Assert.Equal(declared.Sha256, source.Stat.Sha256);
        Assert.Equal(declared.Bytes, source.Stat.Bytes);
        Assert.Equal(importStat.Dialect, source.Stat.Dialect);
        Assert.Equal(importStat.Rows, source.Stat.Rows);
        Assert.Equal(importStat.WarmupRows, source.Stat.WarmupRows);
        Assert.Equal(importStat.DuplicateReceives, source.Stat.DuplicateReceives);
        Assert.Equal(importStat.FirstTimestampMs, source.Stat.FirstTimestampMs);
        Assert.Equal(importStat.LastTimestampMs, source.Stat.LastTimestampMs);
        // 线契约视图 (VisionStreamFrame) 投影回工作集必须无损。
        for (var i = 0; i < imported.Count; i++)
        {
            Assert.Equal(Dump(VisionReplayFrames.ToReplayFrame(imported[i])), Dump(source.Frames[i].ToReplayFrame()));
        }
        // 线契约自检: CSV 归一化产出的每一帧都必须满足 VisionStreamFrame 契约 ——
        // CSV 方言与 JSONL 流用同一套拒绝口径, 合法数据不得被新校验误伤。
        Assert.All(source.Frames, frame => Assert.Empty(frame.Validate()));
        output.WriteLine($"import 会话 {HuntCsvName}: 帧={imported.Count} 检测={imported.Sum(f => f.Detections.Count)}"
            + $" 重收={imported.Sum(f => f.DuplicateReceives)} 时长={source.DurationSeconds:0.###}s");
    }

    // ---------- equivalence gate ----------

    /// <summary>
    /// 等价门(本批核心验收): 同一 CSV 字节、同一 maxAge、同一场景/seed, live 路径
    /// (CsvStreamSource + LiveVisionBridge) 与离线路径(vision import 证据包 →
    /// VisionReplayAdapter) 的消费台账必须逐条逐字段一致, 事件指纹/ticks/比分同样逐位一致。
    /// 实测参照(既有 CLI): `vision evaluate --session &lt;csv&gt; --scenario wushu-ring-2026`
    /// — hunt_drive 会话 classify 2 次(消费 2/unknown 2, ticks 2401, 比分 0:0);
    /// good_recheck 会话 classify 82 次(消费 82/unknown 80, ticks 2401, 比分 41:11)。
    /// 会话越短被服务次数越少, 故"非空洞"下界按实测会话分别给出, 不做无据的通用阈值。
    /// </summary>
    [Theory]
    [InlineData(HuntCsvName, 2, false)]
    [InlineData(RecheckCsvName, 10, true)]
    public void LiveBridge_ConsumesIdenticallyToTheReplayAdapter(string csvName, int minServed, bool expectsRealLabel)
    {
        var (work, _, report, frames) = ImportedFixture();
        var imported = frames.Where(f => f.Session == csvName).ToList();
        var scenario = FixtureScenario();

        // 路 B(离线): import 证据包(哈希锁定) → VisionReplayAdapter
        var replay = new VisionReplayAdapter(
            VisionReplayFrames.ToReplayFrames(imported), report.EvidenceId!, report.EvidenceSha256!, MaxAgeMs);
        var replayRun = RunMatch(scenario, replay);

        // 路 A(live): 同一 CSV 字节 → CsvStreamSource → LiveVisionBridge
        var source = CsvStreamSource.Load(Path.Combine(work, csvName));
        var bridge = new LiveVisionBridge(source, MaxAgeMs);
        var liveRun = RunMatch(scenario, bridge);

        AssertConsumesEqual(replay.Consumes, bridge.Consumes, output);

        // 非空洞: 全部记录都必须消费到真实帧(不是"两侧都 no_frame/stale"的假绿)。
        var served = bridge.Consumes.Count(c => c.FrameSequence is not null);
        Assert.True(bridge.Consumes.Count > 0, "整场没有任何 classify 调用, 等价门无从成立");
        Assert.Equal(bridge.Consumes.Count, served);
        Assert.True(served >= minServed, $"{csvName}: 被服务帧过少({served} < {minServed}), 等价门可能空洞");
        if (expectsRealLabel)
        {
            Assert.Contains(bridge.Consumes, c => c.Label is "buff" or "debuff");
        }

        // 源交付集必须是会话帧的前缀(只放出已到达的帧, 不裁剪也不补帧), 且锚点一致。
        Assert.True(source.ReleasedEvidenceFrames.Count <= source.EvidenceFrames.Count);
        Assert.Equal(
            source.EvidenceFrames.Take(source.ReleasedEvidenceFrames.Count).Select(f => f.Sequence).ToArray(),
            source.ReleasedEvidenceFrames.Select(f => f.Sequence).ToArray());
        Assert.Equal(source.EvidenceFrames[0].TimestampMs, source.ReleasedEvidenceFrames[0].TimestampMs);
        Assert.Equal(source.ReleasedEvidenceFrames.Count, bridge.ReleasedFrames.Count);

        var labels = bridge.Consumes
            .GroupBy(c => c.Label, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");
        output.WriteLine($"live {csvName}: classify={bridge.Consumes.Count} 消费={served}"
            + $" 交付帧={source.ReleasedEvidenceFrames.Count}/{source.EvidenceFrames.Count}"
            + $" 标签[{string.Join(",", labels)}] ticks={liveRun.Ticks}"
            + $" 比分={liveRun.Scores.Us:0.#}:{liveRun.Scores.Them:0.#} done={liveRun.DoneReason}");

        // 行为面: 事件指纹/ticks/比分/结束原因逐位一致。
        Assert.Equal(replayRun.Fingerprints, liveRun.Fingerprints);
        Assert.Equal(replayRun.Ticks, liveRun.Ticks);
        Assert.Equal(replayRun.Scores.Us, liveRun.Scores.Us);
        Assert.Equal(replayRun.Scores.Them, liveRun.Scores.Them);
        Assert.Equal(replayRun.DoneReason, liveRun.DoneReason);
    }

    // ---------- sidecar ----------

    [Fact]
    public void LiveSession_SidecarReplaysIdenticallyAndStaysConsumable()
    {
        // 用 245 帧会话(实测 82 次 classify/82 次消费, 含真实 buff/debuff 标签): sidecar
        // 复现若失配, 这个规模足以暴露; hunt_drive 会话的等价性由上面的 Theory 覆盖。
        var (work, _, _, _) = ImportedFixture();
        var scenario = FixtureScenario();
        var source = CsvStreamSource.Load(Path.Combine(work, RecheckCsvName));
        var bridge = new LiveVisionBridge(source, MaxAgeMs);
        var liveRun = RunMatch(scenario, bridge);
        var served = bridge.Consumes.Count(c => c.FrameSequence is not null);
        Assert.True(served >= 10, $"被服务帧过少({served}), sidecar 复现门可能空洞");
        Assert.Contains(bridge.Consumes, c => c.Label is "buff" or "debuff");

        // live 场次实际交付的帧流 → vision-replay-v1 证据包(含会话首帧作为 SimT 0 基准)。
        var sidecarDir = Path.Combine(work, "live-sidecar");
        var report = VisionSidecar.Write(sidecarDir, new VisionSidecarRequest
        {
            Frames = source.ReleasedEvidenceFrames,
            AnchorTimestampMs = source.SessionFirstTimestampMs,
            Stat = source.Stat,
            ClassMapping = source.Manifest.ClassMapping,
            Label = "mbri-vision-mini-live",
            ToolVersion = "test",
            GeneratedAt = "2026-01-01T00:00:00Z",
        });
        Assert.Empty(report.Validate());
        Assert.False(report.GroundTruth);
        Assert.Equal("evidence_only", report.Grade);
        Assert.Equal(VisionReplaySchemas.VisionReplayFormat, report.Schema);
        Assert.Equal("2026-01-01T00:00:00Z", report.GeneratedAt);
        Assert.Equal(source.Session, report.Files[0].Path);
        Assert.Equal("buff", report.ClassMapping["good"]);
        Assert.NotNull(report.EvidenceId);
        var framesPath = Path.Combine(sidecarDir, VisionReplayIO.FramesFileName);
        var reportPath = Path.Combine(sidecarDir, VisionReplayIO.ImportReportFileName);
        Assert.True(File.Exists(framesPath), $"缺少 {framesPath}");
        Assert.True(File.Exists(reportPath), $"缺少 {reportPath}");

        // 锚点与哈希锁定: 包内首帧 == 会话首帧(SimT 0 语义), sha 与写出的字节一致;
        // 包内容 = 本场次源实际交付的帧(会话帧的前缀), 既不是全量也不是被裁剪的"只录被服务帧"。
        var framesBytes = File.ReadAllBytes(framesPath);
        Assert.Equal(VisionReplayIO.Sha256Hex(framesBytes), report.EvidenceSha256);
        var packageFrames = VisionReplayIO.ParseFrames(System.Text.Encoding.UTF8.GetString(framesBytes));
        Assert.Equal(source.EvidenceFrames[0].TimestampMs, packageFrames[0].TimestampMs);
        Assert.Equal(source.ReleasedEvidenceFrames.Count, packageFrames.Count);
        Assert.Equal(
            source.EvidenceFrames.Take(packageFrames.Count).Select(f => f.Sequence).ToArray(),
            packageFrames.Select(f => f.Sequence).ToArray());
        output.WriteLine($"sidecar {source.Session}: 包帧={packageFrames.Count}/{source.EvidenceFrames.Count}"
            + $" 消费={served} evidenceId={report.EvidenceId}");

        // 既有 VisionReplayAdapter 复现: 台账与指纹必须与现场逐位一致。
        var replay = new VisionReplayAdapter(
            VisionReplayFrames.ToReplayFrames(packageFrames), report.EvidenceId!, report.EvidenceSha256!, MaxAgeMs);
        var replayRun = RunMatch(scenario, replay);
        AssertConsumesEqual(bridge.Consumes, replay.Consumes, output);
        Assert.Equal(liveRun.Fingerprints, replayRun.Fingerprints);
        Assert.Equal(liveRun.Ticks, replayRun.Ticks);
        Assert.Equal(liveRun.Scores.Us, replayRun.Scores.Us);
        Assert.Equal(liveRun.DoneReason, replayRun.DoneReason);

        // 既有 CLI 必须能直接消费该包(决策③: sidecar 与 vision evaluate 兼容)。
        var evalOut = Path.Combine(work, "sidecar-eval.json");
        var exit = Program.Main(
        [
            "vision", "evaluate",
            "--evidence", sidecarDir,
            "--scenario", FindRepoFile(ScenarioPath),
            "--out", evalOut,
            "--force",
        ]);
        Assert.Equal(0, exit);
        var evalReport = ProtocolJson.Deserialize<VisionReplayReport>(File.ReadAllText(evalOut));
        Assert.Equal(report.EvidenceId, evalReport.EvidenceId);
        Assert.Equal(bridge.Consumes.Count, evalReport.Policy!.ClassifyCalls);
    }

    [Fact]
    public void Sidecar_RefusesUnanchoredMixedOrUnmappedFrames()
    {
        var (_, _, _, frames) = ImportedFixture();
        var imported = frames.Where(f => f.Session == HuntCsvName).Take(5).ToList();
        var directory = Path.Combine(NewWorkDir(), "sidecar");
        var anchorMs = imported[0].TimestampMs;
        var stat = new VisionImportFileStat { Path = HuntCsvName, Frames = imported.Count };
        static Dictionary<string, string> Mapping() => new() { ["good"] = "buff", ["bad"] = "debuff" };
        VisionSidecarRequest Request(
            IReadOnlyList<VisionFrameRecord> list,
            double? anchor = null,
            IReadOnlyDictionary<string, string>? mapping = null,
            VisionImportFileStat? fileStat = null)
            => new()
            {
                Frames = list,
                AnchorTimestampMs = anchor ?? anchorMs,
                Stat = fileStat ?? stat,
                ClassMapping = mapping ?? Mapping(),
                Label = "x",
                ToolVersion = "test",
            };

        // 空帧流: 没有 SimT 0 基准, 拒绝。
        var empty = Assert.Throws<VisionEvidenceException>(() => VisionSidecar.Write(directory, Request([])));
        Assert.Contains("空", empty.Message, StringComparison.Ordinal);

        // 首帧是锚点但后续逆序: 单调非降被破坏(重放二分选帧依赖升序), 拒绝。
        var unordered = Assert.Throws<VisionEvidenceException>(
            () => VisionSidecar.Write(directory, Request([imported[0], imported[2], imported[1]])));
        Assert.Contains("单调非降", unordered.Message, StringComparison.Ordinal);

        // 交付前缀丢了会话首帧(锚点右移): 单靠单调非降发现不了, 必须由显式锚点拦下。
        var shifted = Assert.Throws<VisionEvidenceException>(
            () => VisionSidecar.Write(directory, Request(imported.Skip(1).ToList())));
        Assert.Contains("不等于源会话锚点", shifted.Message, StringComparison.Ordinal);

        // (timestampMs, sequence) 重复: VisionReplayAdapter 构造会拒绝, 写出前拦下。
        var duplicated = Assert.Throws<VisionEvidenceException>(
            () => VisionSidecar.Write(directory, Request([imported[0], imported[0]])));
        Assert.Contains("重复", duplicated.Message, StringComparison.Ordinal);

        // 帧本身不合法(未知 status): 读取侧 VisionReplayIO.ParseFrames 会拒收, 写出前拦下。
        var invalid = Assert.Throws<VisionEvidenceException>(
            () => VisionSidecar.Write(directory, Request([imported[0] with { Status = "bogus" }])));
        Assert.Contains("第 0 帧无效", invalid.Message, StringComparison.Ordinal);
        Assert.Contains("bogus", invalid.Message, StringComparison.Ordinal);

        // 会话混装: 拒绝。
        Assert.Throws<VisionEvidenceException>(
            () => VisionSidecar.Write(directory, Request([imported[0], imported[1] with { Session = "other.csv" }])));

        // 统计 path 与帧 session 不一致(vision evaluate 默认会话选择依赖它): 拒绝。
        var wrongStat = Assert.Throws<VisionEvidenceException>(() => VisionSidecar.Write(directory,
            Request(imported, fileStat: stat with { Path = "other.csv" })));
        Assert.Contains("必须等于帧 session", wrongStat.Message, StringComparison.Ordinal);

        // 锚点非有限值: 拒绝(不得写出无语义的基准)。
        Assert.Throws<VisionEvidenceException>(
            () => VisionSidecar.Write(directory, Request(imported, anchor: double.NaN)));

        // 无显式类别映射: 禁止沉默默认, 拒绝。
        Assert.Throws<VisionEvidenceException>(() => VisionSidecar.Write(directory,
            Request(imported, mapping: new Dictionary<string, string>())));

        // 全部拒绝 ⇒ 零产出。
        Assert.False(Directory.Exists(directory), $"校验失败却写出了目录: {directory}");
    }
}
