using System.Security.Cryptography;
using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;
using Sim.VisionReplay;

namespace Sim.Cli;

/// <summary>
/// `vision` — offline real-vision evidence line (vision-replay-v1). Phase A:
/// evidence_only. Import normalizes selected MBri hunt-dialect CSVs into a
/// hash-locked frame package; evaluate computes pure link-quality metrics and
/// replays the evidence through an injected <see cref="VisionReplayAdapter"/>
/// to prove the vision→FSM data flow. Exit codes: 0 report written, 1
/// validation/IO failure (no output), 2 usage. `fidelity.json` is NEVER
/// touched: replaying the model's own CSV output does not validate accuracy.
/// </summary>
public static class VisionCommand
{
    // 与 live CSV 源共用同一上限(Sim.VisionReplay), 避免同类输入两条路径保护不一致。
    private const long MaxFileBytes = VisionReplayIO.MaxSourceFileBytes;
    private const string ToolVersion = "vision-replay-1.0.0";
    private const double DefaultMaxAgeMs = 500;

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 2;
        }
        try
        {
            return args[1] switch
            {
                "import" => Import(args),
                "evaluate" => Evaluate(args),
                "live" => Live(args),
                _ => UnknownSubcommand(args[1]),
            };
        }
        catch (VisionEvidenceException ex)
        {
            Console.Error.WriteLine($"vision: {ex.Message}");
            return 1;
        }
        catch (MbriCsvException ex)
        {
            Console.Error.WriteLine($"vision CSV: {ex.Message}");
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"vision IO: {ex.Message}");
            return 1;
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"vision JSON: {ex.Message}");
            return 1;
        }
    }

    private static string? Get(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"unknown vision subcommand '{subcommand}'");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            用法: vision import --manifest <json> --evidence-out <dir> --out <report.json>
                          [--data-dir <path>] [--force]
                  vision evaluate --evidence <dir> --scenario <json> --out <report.json>
                          [--max-age-ms 500] [--session <file>] [--json] [--force]
                  vision live --source <csv> --scenario <json> --out <report.json>
                          [--max-age-ms 500] [--json] [--force]
                  vision live --process "<命令行>" --scenario <json> --out <report.json>
                          [--realtime 1x] [--max-age-ms 500] [--json] [--force]
            """);
    }

    // ---------- import ----------

    private static int Import(string[] args)
    {
        var manifestPath = Get(args, "--manifest");
        var evidenceOut = Get(args, "--evidence-out");
        var outPath = Get(args, "--out");
        var dataDir = Get(args, "--data-dir");
        var force = args.Contains("--force");
        if (manifestPath is null || evidenceOut is null || outPath is null)
        {
            Console.Error.WriteLine("缺少 --manifest / --evidence-out / --out");
            return 2;
        }
        if (File.Exists(outPath) && !force)
        {
            Console.Error.WriteLine($"vision: 输出已存在 (覆盖需 --force): {outPath}");
            return 1;
        }

        // Pre-flight: manifest + every selected file fully verified BEFORE any output.
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = ProtocolJson.Deserialize<VisionReplayManifest>(System.Text.Encoding.UTF8.GetString(manifestBytes));
        var manifestErrors = manifest.Validate().ToList();
        if (manifestErrors.Count > 0)
        {
            throw new VisionEvidenceException(string.Join(" ", manifestErrors));
        }
        var root = dataDir is null
            ? Path.GetDirectoryName(Path.GetFullPath(manifestPath))!
            : Path.GetFullPath(dataDir);
        var selectedNames = manifest.SelectedFiles();
        var loaded = new List<(string Name, byte[] Bytes)>();
        foreach (var name in selectedNames)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new VisionEvidenceException($"选择文件不存在: {name} (于 {root})");
            }
            // 上限在读取之前判定: 超大输入不得先进内存(与 CsvStreamSource 同一顺序与上限)。
            var size = new FileInfo(path).Length;
            if (size > MaxFileBytes)
            {
                throw new VisionEvidenceException($"文件超过 128MB 上限: {name} ({size} B)");
            }
            var bytes = File.ReadAllBytes(path);
            var expected = manifest.Files.First(f => f.Path == name);
            var sha256 = VisionReplayIO.Sha256Hex(bytes);
            if (sha256 != expected.Sha256)
            {
                throw new VisionEvidenceException(
                    $"选择文件哈希与清单不一致: {name} 清单 {expected.Sha256} 实际 {sha256}");
            }
            if (bytes.Length != expected.Bytes)
            {
                throw new VisionEvidenceException(
                    $"选择文件字节数与清单不一致: {name} 清单 {expected.Bytes} 实际 {bytes.Length}");
            }
            loaded.Add((name, bytes));
        }
        // Audit accounting (R2): every top-level CSV under the data root that
        // the manifest did NOT select is listed as ignored — with or without
        // an explicit --data-dir (default root is the manifest's directory).
        var ignored = Directory.EnumerateFiles(root, "*.csv", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !selectedNames.Contains(n, StringComparer.Ordinal))
            .Cast<string>()
            .ToList();

        var result = VisionEvidenceBuilder.Build(
            manifest, loaded, ignored, ToolVersion);
        var report = VisionReplayIO.Fingerprint(
            result.Report with { ManifestSha256 = VisionReplayIO.Sha256Hex(manifestBytes) },
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        // Everything validated — now write (atomic): frames.jsonl + archived
        // import report inside the evidence package, audit report at --out.
        var framesBytes = VisionReplayIO.SerializeFrames(result.Frames);
        VisionReplayIO.WriteAtomically(Path.Combine(evidenceOut, VisionReplayIO.FramesFileName), framesBytes);
        var reportJson = ProtocolJson.Serialize(report);
        VisionReplayIO.WriteAtomically(
            Path.Combine(evidenceOut, VisionReplayIO.ImportReportFileName), reportJson);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        VisionReplayIO.WriteAtomically(outPath, reportJson);

        Console.WriteLine($"视觉证据导入: {outPath}");
        Console.WriteLine($"  evidenceId={report.EvidenceId} evidenceSha256={report.EvidenceSha256?[..16]}…");
        Console.WriteLine($"  contentSha256={report.ContentSha256?[..16]}… 类别映射 good→{report.ClassMapping.GetValueOrDefault("good")} bad→{report.ClassMapping.GetValueOrDefault("bad")}");
        foreach (var file in report.Files)
        {
            Console.WriteLine($"  {file.Path}: 方言={file.Dialect} 行={file.Rows}(预热 {file.WarmupRows}) 帧={file.Frames} 检测={file.Detections} 重收={file.DuplicateReceives}");
        }
        foreach (var rejection in report.RejectedFiles)
        {
            Console.WriteLine($"  拒绝 {rejection.Path}: {rejection.Reason}");
        }
        Console.WriteLine($"  使用 {report.Files.Count} / 忽略 {report.IgnoredFiles.Count} / 拒绝 {report.RejectedFiles.Count} 文件");
        Console.WriteLine($"  证据包: {Path.GetFullPath(evidenceOut)} (frames.jsonl; 真值=无, 分级={report.Grade})");
        return 0;
    }

    // ---------- evaluate ----------

    private static int Evaluate(string[] args)
    {
        var evidenceDir = Get(args, "--evidence");
        var scenarioPath = Get(args, "--scenario");
        var outPath = Get(args, "--out");
        var sessionArg = Get(args, "--session");
        var maxAgeRaw = Get(args, "--max-age-ms");
        double maxAgeMs;
        if (maxAgeRaw is null)
        {
            maxAgeMs = DefaultMaxAgeMs;
        }
        else if (!double.TryParse(maxAgeRaw, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out maxAgeMs))
        {
            // 显式拒绝不可解析的取值: 静默回退默认窗口会让 stale 统计失真且无从察觉。
            throw new VisionEvidenceException($"--max-age-ms 不是有效数值: '{maxAgeRaw}'");
        }
        var json = args.Contains("--json");
        var force = args.Contains("--force");
        if (evidenceDir is null || scenarioPath is null || outPath is null)
        {
            Console.Error.WriteLine("缺少 --evidence / --scenario / --out");
            return 2;
        }
        if (File.Exists(outPath) && !force)
        {
            Console.Error.WriteLine($"vision: 输出已存在 (覆盖需 --force): {outPath}");
            return 1;
        }
        if (!Directory.Exists(evidenceDir))
        {
            throw new VisionEvidenceException($"证据目录不存在: {evidenceDir}");
        }
        if (!double.IsFinite(maxAgeMs) || maxAgeMs <= 0)
        {
            throw new VisionEvidenceException($"--max-age-ms 必须为正的有限数值, 得到 {Get(args, "--max-age-ms") ?? "(缺省)"}");
        }

        // Pre-flight: evidence package integrity BEFORE any output. 哈希锁定读取与帧映射
        // 抽到 VisionEvidencePackage(桌面 visionReplay 源共用同一实现, 复用而非复制)。
        var package = VisionEvidencePackage.Load(evidenceDir);
        var frames = package.Frames;
        var evidenceSha256 = package.EvidenceSha256;
        var importReport = package.ImportReport;

        var scenarioBytes = File.ReadAllBytes(scenarioPath);
        var scenario = ProtocolJson.Deserialize<Scenario>(System.Text.Encoding.UTF8.GetString(scenarioBytes));
        var scenarioErrors = scenario.Validate().ToList();
        if (scenarioErrors.Count > 0)
        {
            throw new VisionEvidenceException($"场景无效 '{scenarioPath}': {string.Join(" ", scenarioErrors)}");
        }

        var sessions = frames.Select(f => f.Session).Distinct(StringComparer.Ordinal).ToList();
        var session = package.SelectSession(sessionArg);

        // Policy consumption replay: injected engine, full match, deterministic.
        // 规范证据帧 → 适配器工作集: 映射只有一份(Sim.VisionReplay), 与 live CSV 流、
        // sidecar 重读共用, 避免三处各写一遍 label/confidence/offset_x 口径; 每场
        // 新建适配器(台账与 SimT 0 基准不跨场复用)。
        var adapter = package.CreateAdapter(session, maxAgeMs);
        using var engine = Sim.Hosting.MatchEngineHost.Create(scenario, adapter);
        var fingerprints = new List<string>();
        var transitions = new List<string>();
        var previousStates = new Dictionary<string, string>();
        var ticks = 0L;
        Scores finalScores;
        string? doneReason;
        engine.Arm();
        while (!engine.Done && ticks < 10_000)
        {
            var snapshot = engine.Tick();
            ticks = snapshot.Tick + 1;
            foreach (var (role, state) in snapshot.Robots)
            {
                if (state.State is { } stateName
                    && previousStates.TryGetValue(role, out var previous) && previous != stateName)
                {
                    transitions.Add($"{role}:{previous}→{stateName}");
                }
                if (state.State is { } name)
                {
                    previousStates[role] = name;
                }
            }
            if (snapshot.Events is { Count: > 0 })
            {
                foreach (var evt in snapshot.Events)
                {
                    fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                }
            }
        }
        finalScores = engine.Scores;
        doneReason = engine.Done ? engine.BuildSnapshot().DoneReason : "(未结束)";

        var consumption = BuildConsumption(frames.Where(f => f.Session == session).ToList(), adapter.Consumes);
        var policy = new VisionPolicyConsumption
        {
            ScenarioId = scenario.Id,
            Seed = scenario.Seed,
            Ticks = ticks,
            FinalScores = finalScores,
            DoneReason = doneReason,
            VisionMode = VisionReplayAdapter.ModeName,
            MaxAgeMs = maxAgeMs,
            Session = session,
            ClassifyCalls = adapter.Consumes.Count,
            // A call "consumed" a frame whenever one was served in-window; the
            // outcome may still be unknown (no_target/no_selection/error/stale).
            ConsumedCalls = adapter.Consumes.Count(c => c.FrameSequence is not null),
            UnknownCalls = adapter.Consumes.Count(c => c.Reason is not null),
            UnknownReasons = adapter.Consumes
                .Where(c => c.Reason is not null)
                .GroupBy(c => c.Reason!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            FsmDetections = adapter.Consumes
                .GroupBy(c => c.Label, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Frames = consumption,
            StateTransitions = transitions,
            EventCount = fingerprints.Count,
            PolicyFingerprint = VisionReplayIO.Sha256Hex(string.Join("\n", fingerprints)),
        };

        var link = VisionLinkMetrics.Compute(frames);
        var report = new VisionReplayReport
        {
            ToolVersion = ToolVersion,
            EvidenceId = package.EvidenceId,
            EvidenceSha256 = evidenceSha256,
            ImportReportSha256 = package.ImportReportSha256,
            Label = importReport.Label,
            Source = importReport.Source,
            Model = importReport.Model,
            ClassMapping = importReport.ClassMapping,
            Link = link,
            Policy = policy,
            Detection = new VisionDetectionQuality
            {
                Status = "not_run(no_ground_truth)",
                Note = "Phase A 证据无逐帧人工真值; 混淆矩阵/P/R/F1/IoU 字段为 Phase B 预留",
            },
            Holdout = new VisionHoldout
            {
                Status = "not_run(no_ground_truth)",
                Note = "无真值即无 development/holdout 划分; Phase B 必须按完整采集 session 划分",
            },
            Grade = VisionReplaySchemas.EvidenceOnly,
            Conclusion = "vision=random_stub (evidence_only)",
            GroundTruth = false,
            PhaseB =
            [
                "补采: 新采集相机帧/视频, 覆盖 good/bad/无目标/遮挡/远距场景, 保留原始帧",
                "补标: 逐帧人工标注类别+边界框; 真值与模型输出分字段保存, 禁止文件名推断",
                "划分: 按完整采集 session 划分 development/holdout, 相邻帧不得跨侧",
                "门槛: 预固定混淆矩阵/P/R/F1 与 IoU 门限后, 另立任务实现 holdout 门禁与 fidelity 晋升",
            ],
            Limitations =
            [
                "回放的是模型自身 CSV 输出, 只证明链路与策略消费, 不证明识别准确率",
                "opponent 检测在 MBri YOLO 类别中不存在(IR 接近探测), Phase A 证据不含 opponent",
                "策略回放指纹用于同证据逐位复现, 不冒充真实物理比赛成绩",
                "时间映射固定 SimT 0 = 会话首帧: 证据时长短于比赛时长时, 其后的 classify 调用按过期(stale)计入 unknown, 不静默造帧",
            ],
        };
        report = VisionReplayIO.Fingerprint(
            report, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        VisionReplayIO.WriteAtomically(outPath, ProtocolJson.Serialize(report));

        Console.WriteLine($"视觉回放评估: {outPath}");
        Console.WriteLine($"  evidenceId={report.EvidenceId} evidenceSha256={report.EvidenceSha256[..16]}… maxAgeMs={maxAgeMs} 会话={sessions.Count} 帧={frames.Count}");
        Console.WriteLine($"  链路: 有效 {link.ValidRate:P1} / 无目标 {link.NoTargetRate:P1} / 错误 {link.ErrorRate:P1} / 无数据 {link.NoDataOrStaleRate:P1};"
            + $" FPS p50={link.Fps?.P50:0.##} p95={link.Fps?.P95:0.##}; 推理 p95={link.InferenceMs?.P95:0.#}ms;"
            + $" 首次有效检测={link.FirstValidDetectionMs:0.#}ms; 选中抖动={link.SelectionFlips}");
        Console.WriteLine($"  策略回放: 场景={policy.ScenarioId} seed={policy.Seed} ticks={policy.Ticks} 比分 {policy.FinalScores.Us:0.#}:{policy.FinalScores.Them:0.#};"
            + $" classify调用={policy.ClassifyCalls}(消费 {policy.ConsumedCalls}/unknown {policy.UnknownCalls});"
            + $" 状态转移={policy.StateTransitions.Count}; 指纹={policy.PolicyFingerprint[..16]}…");
        Console.WriteLine("  结论: vision=random_stub (evidence_only) — fidelity.json 不晋升; Phase B 补采/补标清单见报告");
        if (json)
        {
            Console.WriteLine(ProtocolJson.Serialize(report));
        }
        return 0;
    }

    /// <summary>Per-frame consumption ledger joined with the classify-call stream.</summary>
    private static List<VisionFrameConsumption> BuildConsumption(
        IReadOnlyList<VisionFrameRecord> sessionFrames,
        IReadOnlyList<VisionReplayConsumeRecord> consumes)
    {
        var bySequence = consumes
            .Where(c => c.FrameSequence is { })
            .GroupBy(c => c.FrameSequence!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var ledger = new List<VisionFrameConsumption>();
        foreach (var frame in sessionFrames)
        {
            VisionReplayConsumeRecord? first = null;
            if (bySequence.TryGetValue(frame.Sequence, out var calls))
            {
                first = calls.OrderBy(c => c.SimT).First();
            }
            ledger.Add(new VisionFrameConsumption
            {
                Session = frame.Session,
                Sequence = frame.Sequence,
                Status = frame.Status,
                Consumed = bySequence.TryGetValue(frame.Sequence, out var list) ? list.Count : 0,
                FirstConsumedBy = first?.Role,
                FirstConsumedSimT = first?.SimT,
                FirstResult = first is null
                    ? null
                    : first.Reason ?? first.Label,
            });
        }
        return ledger;
    }

    // ---------- live ----------

    /// <summary>
    /// `vision live`: 事件源为一场"活源"视觉场次 —— 二选一：<c>--source</c> 的
    /// CsvStreamSource 按 SimT 缩放释放真车 CSV 的检测流，或 <c>--process</c> 的
    /// ExternalProcessStreamSource 逐行读外部进程 stdout 的 JSONL 帧（墙钟到达）。
    /// LiveVisionBridge 在 FSM 的 classify 阶段取"窗内最新帧"（与 VisionReplayAdapter
    /// 共用同一选帧器）。跑完整场后：①收集链路质量（帧龄/stale 率/消费统计）②把本场
    /// 交付帧流写成 vision-replay-v1 sidecar 证据包 ③用 VisionReplayAdapter 重读该包
    /// 并重放同一场景，逐条比对消费台账（等价确认）④跑同 seed 的默认 classifyRate 桩
    /// 场次做**摘要级**基线对比。桥不抽共享随机流、不读世界真值，故 live 与 visionReplay
    /// 两条路对同一帧集逐位一致 —— 进程源额外要求 <c>--realtime 1x</c>（帧按墙钟到达，
    /// 引擎快跑会让窗内无新帧 ⇒ 全 stale，那是假阴性不是实时结果）。
    /// </summary>
    private static int Live(string[] args)
    {
        var sourcePath = Get(args, "--source");
        var processCommand = Get(args, "--process");
        var scenarioPath = Get(args, "--scenario");
        var outPath = Get(args, "--out");
        var maxAgeRaw = Get(args, "--max-age-ms");
        var realtimeRaw = Get(args, "--realtime");
        double maxAgeMs;
        if (maxAgeRaw is null)
        {
            maxAgeMs = LiveVisionBridge.DefaultMaxAgeMs;
        }
        else if (!double.TryParse(maxAgeRaw, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out maxAgeMs))
        {
            // 与 evaluate 同一纪律: 不可解析的取值显式拒绝, 不静默回退默认窗口。
            throw new VisionEvidenceException($"--max-age-ms 不是有效数值: '{maxAgeRaw}'");
        }
        // 等号形式显式支持: --realtime=1x 与 --realtime 1x 同义; 两种形式同给显式拒绝,
        // 绝不静默忽略任何一种写法(否则等号形式会落回快跑的假阴性路径)。
        var realtimeEquals = Array.Find(args, a => a.StartsWith("--realtime=", StringComparison.Ordinal));
        if (realtimeEquals is not null)
        {
            if (realtimeRaw is not null)
            {
                throw new VisionEvidenceException(
                    "--realtime 不能同时以 '--realtime <v>' 与 '--realtime=<v>' 给出");
            }
            realtimeRaw = realtimeEquals["--realtime=".Length..];
        }
        var realtime = args.Contains("--realtime") || realtimeEquals is not null;
        if (realtime && realtimeRaw != "1x")
        {
            // 外部流按墙钟到达: 只有 1x 有意义(其它倍率会系统性失真), 拒绝猜测。
            throw new VisionEvidenceException($"--realtime 只支持 1x, 得到 '{realtimeRaw ?? "(缺值)"}'");
        }
        var json = args.Contains("--json");
        var force = args.Contains("--force");
        if (sourcePath is null && processCommand is null)
        {
            Console.Error.WriteLine("缺少 --source <csv> 或 --process \"<命令行>\" (二选一)");
            return 2;
        }
        if (sourcePath is not null && args.Contains("--process"))
        {
            Console.Error.WriteLine("--source 与 --process 只能二选一");
            return 2;
        }
        if (processCommand is not null && string.IsNullOrWhiteSpace(processCommand))
        {
            // 空命令行会走 ExternalProcessStreamSource.Start 的 ArgumentException 兜底成 exit 1;
            // 用法错误按惯例归 2。
            Console.Error.WriteLine("--process 命令行不能为空");
            return 2;
        }
        if (scenarioPath is null || outPath is null)
        {
            Console.Error.WriteLine("缺少 --scenario / --out");
            return 2;
        }
        if (File.Exists(outPath) && !force)
        {
            Console.Error.WriteLine($"vision: 输出已存在 (覆盖需 --force): {outPath}");
            return 1;
        }
        if (!double.IsFinite(maxAgeMs) || maxAgeMs <= 0)
        {
            throw new VisionEvidenceException($"--max-age-ms 必须为正的有限数值, 得到 {maxAgeRaw ?? "(缺省)"}");
        }
        if (processCommand is not null && !realtime)
        {
            // 不阻拦(短场景/快照检查仍有意义), 但必须高声说明否则报告会"全 stale"却没人知道原因。
            Console.Error.WriteLine("vision live: 警告 — 外部进程源未加 --realtime 1x: 引擎将快跑, "
                + "帧多数在 classify 之后才到达, 报告会如实呈现高 stale 率/低服务率。");
        }

        // 全量预检(先于任何写出): CSV 源整体解析校验(存在/上限/方言/行/帧) + 场景 Validate。
        var csvSource = sourcePath is null ? null : CsvStreamSource.Load(sourcePath);
        var scenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(scenarioPath));
        var scenarioErrors = scenario.Validate().ToList();
        if (scenarioErrors.Count > 0)
        {
            throw new VisionEvidenceException($"场景无效 '{scenarioPath}': {string.Join(" ", scenarioErrors)}");
        }

        ExternalProcessStreamSource? processSource = null;
        try
        {
            IVisionStreamSource source;
            if (csvSource is not null)
            {
                source = csvSource;
            }
            else
            {
                try
                {
                    processSource = ExternalProcessStreamSource.Start(processCommand!);
                }
                catch (Exception error) when (error is InvalidOperationException or IOException)
                {
                    throw new VisionEvidenceException(error.Message);
                }
                source = processSource;
            }

            // 1) live 场次: 注入桥, 跑整场(进程源在 --realtime 1x 下按墙钟步进)。
            var bridge = new LiveVisionBridge(source, maxAgeMs);
            var liveRun = RunMatch(scenario, bridge, realtime ? 1.0 : null);

            // 2) 交付帧流 → 规范证据帧: CSV 源复用解析产物; 进程源按契约映射(缺几何/尺寸/
            // 帧龄审计字段的行显式拒绝 —— 缺字段就写不出可复现的包, 绝不用 0 顶替)。
            var session = csvSource?.Session ?? processSource!.Session;
            var delivered = csvSource?.ReleasedEvidenceFrames
                ?? processSource!.DeliveredFrames
                    .Select(f => VisionReplayFrames.ToEvidenceFrame(f, session))
                    .ToList();
            if (delivered.Count == 0)
            {
                throw new VisionEvidenceException(
                    "live 场次未交付任何帧: 无法建立 SimT 0 基准/写 sidecar"
                    + (processSource is null ? "" : "(进程源请检查子进程是否逐帧 flush 且按时间戳升序输出)"));
            }
            var anchor = csvSource?.SessionFirstTimestampMs ?? delivered[0].TimestampMs;
            var classMapping = csvSource?.Manifest.ClassMapping
                ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["good"] = "buff", ["bad"] = "debuff" };
            var processSection = processSource is null ? null : new VisionLiveProcess
            {
                Command = processSource.Command,
                Session = processSource.Session,
                State = processSource.State switch
                {
                    VisionProcessState.Running => "running",
                    VisionProcessState.Exited => "exited",
                    _ => "faulted",
                },
                ExitCode = processSource.ExitCode,
                Faults = processSource.Faults,
                RejectedLines = processSource.RejectedLines,
                LastFault = processSource.LastFault,
                ConflictingDuplicates = bridge.ConflictingDuplicates,
                Realtime = realtime ? 1.0 : 0.0,
            };
            if (processSource is { Faults: > 0 })
            {
                Console.Error.WriteLine($"vision live: 警告 — 外部流故障 {processSource.Faults} 次"
                    + $"(坏行 {processSource.RejectedLines}): {processSource.LastFault}");
            }
            if (bridge.ConflictingDuplicates > 0)
            {
                // 同 (timestampMs,sequence) 但内容不同的重复帧: 桥按契约保留首次交付继续跑
                // (流路径不炸引擎), 但这是 import 路径会硬拒绝的协议违例, 必须高声披露。
                Console.Error.WriteLine($"vision live: 警告 — {bridge.ConflictingDuplicates} 个重复"
                    + " (timestampMs,sequence) 帧的内容与首次交付不一致(已保留首次交付,"
                    + " 详见报告 process.conflictingDuplicates)");
            }

            // 3) sidecar: 本场交付帧流 → vision-replay-v1 证据包(含会话首帧作为 SimT 0 锚点)。
            var outFull = Path.GetFullPath(outPath);
            var outStem = Path.GetFileNameWithoutExtension(outFull);
            var sidecarDir = Path.Combine(Path.GetDirectoryName(outFull)!, $"{outStem}-sidecar");
            var stat = csvSource?.Stat ?? ProcessStat(session, delivered);
            var sidecar = VisionSidecar.Write(sidecarDir, new VisionSidecarRequest
            {
                Frames = delivered,
                AnchorTimestampMs = anchor,
                Stat = stat,
                ClassMapping = classMapping,
                Label = session,
                ToolVersion = ToolVersion,
                Source = csvSource is not null ? "mbri-csv" : ExternalProcessStreamSource.SourceTag,
                Limitations = csvSource is not null
                    ? []
                    :
                    [
                        "外部进程源没有源文件: files[0] 的 sha256/bytes 为空(0); 可复现身份由包内 frames.jsonl 的 evidenceSha256 承担",
                        "进程源帧按墙钟到达: 本包是**已交付**帧流, 与 CSV 源的 SimT 缩放语义不同(到达时刻由子进程节奏决定)",
                    ],
            });

            // 4) 等价确认: 重读包 → VisionReplayAdapter(既有参照) → 同一场景重放。
            var framesPath = Path.Combine(sidecarDir, VisionReplayIO.FramesFileName);
            var packageFrames = VisionReplayIO.ParseFrames(File.ReadAllText(framesPath));
            var replayAdapter = new VisionReplayAdapter(
                VisionReplayFrames.ToReplayFrames(packageFrames),
                sidecar.EvidenceId ?? VisionReplayIO.EvidenceId(sidecar.EvidenceSha256!),
                sidecar.EvidenceSha256!,
                maxAgeMs);
            var replayRun = RunMatch(scenario, replayAdapter);
            var equivalence = BuildEquivalence(bridge.Consumes, replayAdapter.Consumes, liveRun, replayRun);

            // 5) 基线: 同 seed、同场景、默认 classifyRate 桩(不注入 adapter), 唯一变量是视觉源。
            var baselineRun = RunMatch(scenario, visionAdapter: null);

            // 6) 链路质量 + 报告。
            var link = BuildLiveLink(bridge.Consumes, delivered, maxAgeMs);
            var diff = BuildDiff(liveRun, baselineRun);
            var sidecarSection = new VisionLiveSidecar
            {
                Directory = Path.GetFullPath(sidecarDir),
                EvidenceId = sidecar.EvidenceId ?? "",
                EvidenceSha256 = sidecar.EvidenceSha256 ?? "",
                Frames = packageFrames.Count,
                ServedFrames = link.ServedFrames,
                UnservedFrames = link.UnservedFrames,
                AnchorTimestampMs = anchor,
            };
            var report = new VisionLiveBridgeReport
            {
                ToolVersion = ToolVersion,
                ScenarioId = scenario.Id,
                Seed = scenario.Seed,
                MaxAgeMs = maxAgeMs,
                Source = csvSource is not null
                    ? new VisionLiveSource
                    {
                        Kind = "csv",
                        Path = csvSource.SourcePath,
                        Session = csvSource.Session,
                        Sha256 = csvSource.Stat.Sha256,
                        Bytes = csvSource.Stat.Bytes,
                        Dialect = csvSource.Stat.Dialect,
                        Frames = csvSource.EvidenceFrames.Count,
                        Detections = csvSource.Stat.Detections,
                        WarmupRows = csvSource.Stat.WarmupRows,
                        DuplicateReceives = csvSource.Stat.DuplicateReceives,
                        DurationSeconds = csvSource.DurationSeconds,
                        FrameWidth = csvSource.Manifest.FrameWidth,
                        FrameHeight = csvSource.Manifest.FrameHeight,
                        ClassMapping = classMapping,
                        TimeBase = csvSource.Manifest.TimeBase,
                    }
                    : new VisionLiveSource
                    {
                        Kind = "process",
                        Path = processSource!.Command,
                        Session = session,
                        // 进程源没有源文件(哈希/字节数不适用), 如实留空而不是编一个哈希。
                        Sha256 = "",
                        Bytes = 0,
                        Dialect = ExternalProcessStreamSource.StreamDialect,
                        Frames = delivered.Count,
                        Detections = stat.Detections,
                        WarmupRows = 0,
                        DuplicateReceives = 0,
                        DurationSeconds = (delivered[^1].TimestampMs - delivered[0].TimestampMs) / 1000.0,
                        FrameWidth = delivered[0].FrameWidth,
                        FrameHeight = delivered[0].FrameHeight,
                        ClassMapping = classMapping,
                        TimeBase = "外部进程按墙钟到达: 到达时刻 = stdout 读到该行的时刻; 需 --realtime 1x 对齐引擎步进",
                    },
                Process = processSection,
                Link = link,
                Equivalence = equivalence,
                Sidecar = sidecarSection,
                Live = liveRun,
                Baseline = baselineRun,
                Diff = diff,
                Grade = VisionReplaySchemas.EvidenceOnly,
                GroundTruth = false,
                Conclusion = equivalence.ConsumptionSequenceMatches
                    ? "vision=liveBridge (evidence_only): 与 VisionReplayAdapter 逐条等价成立"
                    : "vision=liveBridge (evidence_only): 等价确认不成立(见 equivalence)",
                Limitations =
                [
                    csvSource is null
                        ? "live 桥回放的是外部进程(YOLO 桥)自身的检测输出, 只证明链路与策略消费, 不证明识别准确率"
                        : "live 桥回放的是模型自身 CSV 输出, 只证明链路与策略消费, 不证明识别准确率",
                    "时间映射固定 SimT 0 = 源会话首帧: 源时长短于比赛时长时, 其后的 classify 调用按过期(stale)计入 unknown, 不静默造帧",
                    "基线对比为摘要级: 两场 RNG 消费天然不同(仅默认 classifyRate 桩抽 VisionContext.Random), 不做位对位",
                    "live 场次不可作为普通动作流回放复现(帧到达依赖外部时序); 用本报告写出的 sidecar 证据包做确定性复现",
                    "opponent 检测在 MBri YOLO 类别中不存在(IR 接近探测), 本证据不含 opponent",
                    .. processSection is null
                        ? Array.Empty<string>()
                        :
                        [
                            "外部进程源按墙钟到达: 未加 --realtime 1x 时引擎快跑, 帧多数在 classify 之后到达, 报告会如实呈现高 stale 率/低服务率",
                            "进程源的子进程必须逐帧 flush(python -u / flush=True): 整块缓冲会让帧成簇到达, 帧龄与 stale 率随之失真",
                        ],
                ],
            };
            report = VisionReplayIO.Fingerprint(report, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
            VisionReplayIO.WriteAtomically(outFull, ProtocolJson.Serialize(report));

            Console.WriteLine($"视觉活源场次: {outPath}");
            Console.WriteLine($"  源={session} kind={report.Source!.Kind} 帧={report.Source.Frames} 时长={report.Source.DurationSeconds:0.###}s maxAgeMs={maxAgeMs}"
                + $" 交付={link.DeliveredFrames} 被服务帧={link.ServedFrames} 未服务={link.UnservedFrames}");
            if (processSection is not null)
            {
                Console.WriteLine($"  进程: state={processSection.State} exitCode={processSection.ExitCode?.ToString() ?? "-"}"
                    + $" 故障={processSection.Faults}(坏行 {processSection.RejectedLines}) realtime={processSection.Realtime:0.#}"
                    + $" 命令=\"{processSection.Command}\"");
            }
            Console.WriteLine($"  链路: classify={link.ClassifyCalls} 窗内服务={link.ServedCalls} stale={link.StaleCalls} 真实检测={link.DetectionCalls}"
                + $" stale率={link.StaleRate:P1}; 帧龄 p50={link.FrameAgeMs?.P50:0.#}ms p95={link.FrameAgeMs?.P95:0.#}ms max={link.FrameAgeMs?.Max:0.#}ms"
                + $"; 首次服务={link.FirstServeSimT:0.#}s 末次={link.LastServeSimT:0.#}s");
            Console.WriteLine($"  等价: {equivalence.Conclusion} (live {equivalence.LiveCalls} / replay {equivalence.ReplayCalls} 条;"
                + $" 台账 {equivalence.LiveLedgerSha256[..16]}… vs {equivalence.ReplayLedgerSha256[..16]}…)");
            Console.WriteLine($"  场次: live 比分 {liveRun.FinalScores.Us:0.#}:{liveRun.FinalScores.Them:0.#} ticks={liveRun.Ticks} done={liveRun.DoneReason};"
                + $" 基线(classifyRate 桩) 比分 {baselineRun.FinalScores.Us:0.#}:{baselineRun.FinalScores.Them:0.#} ticks={baselineRun.Ticks} done={baselineRun.DoneReason}");
            Console.WriteLine($"  摘要 diff(基线−live): 比分 {diff.ScoreUs:+0.#;-0.#;0}:{diff.ScoreThem:+0.#;-0.#;0} ticks {diff.Ticks:+#;-#;0} 事件 {diff.EventCount:+#;-#;0} (非位对位)");
            Console.WriteLine($"  sidecar: {sidecarSection.Directory} evidenceId={sidecarSection.EvidenceId} sha={sidecarSection.EvidenceSha256[..16]}…"
                + " (用 vision evaluate --evidence 可复跑)");
            if (json)
            {
                Console.WriteLine(ProtocolJson.Serialize(report));
            }
            if (!equivalence.ConsumptionSequenceMatches)
            {
                // 等价确认不成立是本批次最严重的契约违约: 报告已写出(诊断依据齐全), 退出码仍按
                // 惯例(0 报告已写), 但必须高声告警, 绝不静默放过。
                Console.Error.WriteLine($"vision live: 警告 — 等价确认不成立: {equivalence.FirstDivergence}");
            }
            return 0;
        }
        finally
        {
            // 真推理子进程不会自己退出: 场次结束(含异常路径)必须回收整个进程树。
            processSource?.Dispose();
        }
    }

    /// <summary>进程源的 import 风格统计(无源文件: 哈希/字节数留空, 其余按交付帧如实统计)。</summary>
    private static VisionImportFileStat ProcessStat(string session, IReadOnlyList<VisionFrameRecord> frames)
    {
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var detections = 0;
        foreach (var frame in frames)
        {
            statusCounts[frame.Status] = statusCounts.GetValueOrDefault(frame.Status) + 1;
            detections += frame.Detections.Count;
        }
        return new VisionImportFileStat
        {
            Path = session,
            Sha256 = "",
            Bytes = 0,
            Dialect = ExternalProcessStreamSource.StreamDialect,
            Rows = frames.Count,
            WarmupRows = 0,
            ReceiveGroups = frames.Count,
            Frames = frames.Count,
            Detections = detections,
            DuplicateReceives = 0,
            FirstTimestampMs = frames[0].TimestampMs,
            LastTimestampMs = frames[^1].TimestampMs,
            StatusCounts = statusCounts,
        };
    }

    /// <summary>Runs one full match (no controller injection) and summarizes it for the report.</summary>
    private static VisionLiveRun RunMatch(Scenario scenario, IVisionAdapter? visionAdapter, double? realtime = null)
    {
        using var engine = MatchEngineHost.Create(scenario, visionAdapter);
        engine.Arm();
        var fingerprints = new List<string>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var ticks = 0L;
        // --realtime 1x: 外部流的帧按墙钟到达, 引擎步进必须与墙钟对齐(绝对目标时刻,
        // sleep 粒度不累积漂移), 否则窗口内没有新帧 ⇒ 全 stale 的假阴性报告。
        var clock = realtime is null ? null : System.Diagnostics.Stopwatch.StartNew();
        // 与 evaluate 同一上界: 默认 120s/0.05s=2400 tick, 超长场景也必须有界退出。
        while (!engine.Done && ticks < 10_000)
        {
            var snapshot = engine.Tick();
            ticks = snapshot.Tick + 1;
            if (snapshot.Events is { Count: > 0 })
            {
                foreach (var evt in snapshot.Events)
                {
                    fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                    var kind = ProtocolJson.Serialize(evt.Type).Trim('"');
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                }
            }
            if (clock is not null)
            {
                ThrottleToWallClock(clock, EngineSeconds(scenario, engine.TickIndex) / realtime!.Value);
            }
        }
        return new VisionLiveRun
        {
            VisionMode = visionAdapter?.Id ?? "default(classifyRate)",
            Seed = scenario.Seed,
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = engine.Done ? engine.BuildSnapshot().DoneReason : "(未结束)",
            EventKinds = kinds,
            EventCount = fingerprints.Count,
            EventFingerprint = VisionReplayIO.Sha256Hex(string.Join("\n", fingerprints)),
        };
    }

    /// <summary>已执行 tick 数对应的仿真秒数(与引擎同一 tick 步长)。</summary>
    private static double EngineSeconds(Scenario scenario, long tickIndex)
        => tickIndex * scenario.Field.TickSeconds;

    /// <summary>
    /// `--realtime 1x`: 墙钟对齐到目标秒数(绝对目标, 不累积 sleep 粒度漂移);
    /// 尾段 2ms 让出时间片, 避免 Windows 默认 ~15.6ms 的 sleep 粒度把 1x 拉慢。
    /// </summary>
    private static void ThrottleToWallClock(System.Diagnostics.Stopwatch clock, double targetSeconds)
    {
        var target = TimeSpan.FromSeconds(targetSeconds);
        while (true)
        {
            var remaining = target - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            if (remaining > TimeSpan.FromMilliseconds(2))
            {
                Thread.Sleep(remaining - TimeSpan.FromMilliseconds(2));
            }
            else
            {
                Thread.Sleep(0);
            }
        }
    }

    /// <summary>Runs one full match (no controller injection) and summarizes it for the report.</summary>
    private static VisionLiveRun RunMatch(Scenario scenario, IVisionAdapter? visionAdapter)
    {
        using var engine = MatchEngineHost.Create(scenario, visionAdapter);
        engine.Arm();
        var fingerprints = new List<string>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var ticks = 0L;
        // 与 evaluate 同一上界: 默认 120s/0.05s=2400 tick, 超长场景也必须有界退出。
        while (!engine.Done && ticks < 10_000)
        {
            var snapshot = engine.Tick();
            ticks = snapshot.Tick + 1;
            if (snapshot.Events is { Count: > 0 })
            {
                foreach (var evt in snapshot.Events)
                {
                    fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                    var kind = ProtocolJson.Serialize(evt.Type).Trim('"');
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                }
            }
        }
        return new VisionLiveRun
        {
            VisionMode = visionAdapter?.Id ?? "default(classifyRate)",
            Seed = scenario.Seed,
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = engine.Done ? engine.BuildSnapshot().DoneReason : "(未结束)",
            EventKinds = kinds,
            EventCount = fingerprints.Count,
            EventFingerprint = VisionReplayIO.Sha256Hex(string.Join("\n", fingerprints)),
        };
    }

    /// <summary>
    /// Structural equivalence check: the live ledger must equal the sidecar-replayed
    /// ledger record for record (every field), and the two matches must share the
    /// event fingerprint and scores. A divergence names the first record that split.
    /// </summary>
    private static VisionLiveEquivalence BuildEquivalence(
        IReadOnlyList<VisionReplayConsumeRecord> live,
        IReadOnlyList<VisionReplayConsumeRecord> replay,
        VisionLiveRun liveRun,
        VisionLiveRun replayRun)
    {
        var liveDump = live.Select(DumpConsume).ToList();
        var replayDump = replay.Select(DumpConsume).ToList();
        string? divergence = null;
        for (var i = 0; i < Math.Min(liveDump.Count, replayDump.Count); i++)
        {
            if (liveDump[i] != replayDump[i])
            {
                divergence = $"#{i} live={liveDump[i]} replay={replayDump[i]}";
                break;
            }
        }
        if (divergence is null && liveDump.Count != replayDump.Count)
        {
            // 前 extra 条已逐条相同, 分叉点就是第一条多出的记录, 下标如实指名而不是硬编码 #0。
            var extra = Math.Min(liveDump.Count, replayDump.Count);
            divergence = liveDump.Count > replayDump.Count
                ? $"#{extra} live 多出 {liveDump.Count - extra} 条 (live={liveDump[extra]})"
                : $"#{extra} replay 多出 {replayDump.Count - extra} 条 (replay={replayDump[extra]})";
        }
        var consumptionMatches = divergence is null;
        var eventsMatch = liveRun.EventFingerprint == replayRun.EventFingerprint;
        var scoresMatch = liveRun.FinalScores.Us == replayRun.FinalScores.Us
            && liveRun.FinalScores.Them == replayRun.FinalScores.Them;
        return new VisionLiveEquivalence
        {
            Reference = VisionReplayAdapter.ModeName,
            ConsumptionSequenceMatches = consumptionMatches,
            EventFingerprintMatches = eventsMatch,
            ScoresMatch = scoresMatch,
            LiveCalls = live.Count,
            ReplayCalls = replay.Count,
            LiveLedgerSha256 = VisionReplayIO.Sha256Hex(string.Join("\n", liveDump)),
            ReplayLedgerSha256 = VisionReplayIO.Sha256Hex(string.Join("\n", replayDump)),
            FirstDivergence = divergence,
            Conclusion = consumptionMatches && eventsMatch && scoresMatch
                ? "同帧数据经 sidecar 重读后消费序列、事件指纹与比分逐位一致"
                : $"不一致(消费={consumptionMatches}/事件={eventsMatch}/比分={scoresMatch})",
        };
    }

    /// <summary>Link quality of the live session: frame ages, stale rate and FSM consumption census.</summary>
    private static VisionLiveLink BuildLiveLink(
        IReadOnlyList<VisionReplayConsumeRecord> consumes,
        IReadOnlyList<VisionFrameRecord> delivered,
        double maxAgeMs)
    {
        ArgumentNullException.ThrowIfNull(consumes);
        var unknown = consumes
            .Where(c => c.Reason is not null)
            .GroupBy(c => c.Reason!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var staleCalls = unknown.GetValueOrDefault("stale");
        var served = consumes
            .Where(c => c.FrameSequence is not null && c.Reason != "stale")
            .ToList();
        var detections = consumes.Where(c => c.Reason is null && c.FrameSequence is not null).ToList();
        var servedSequences = served.Select(c => c.FrameSequence!.Value).ToHashSet();
        return new VisionLiveLink
        {
            ClassifyCalls = consumes.Count,
            ServedCalls = served.Count,
            UnknownCalls = consumes.Count(c => c.Reason is not null),
            StaleCalls = staleCalls,
            DetectionCalls = consumes.Count(c => c.Reason is null),
            StaleRate = consumes.Count == 0 ? 0 : staleCalls / (double)consumes.Count,
            UnknownReasons = unknown,
            FsmDetections = consumes
                .GroupBy(c => c.Label, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            FrameAgeMs = Distribution(consumes.Where(c => c.AgeMs is { }).Select(c => c.AgeMs!.Value)),
            DetectionAgeMs = Distribution(detections.Where(c => c.AgeMs is { }).Select(c => c.AgeMs!.Value)),
            ReportedFps = Distribution(delivered.Where(f => f.Fps is { }).Select(f => f.Fps!.Value)),
            ReportedInferenceMs = Distribution(delivered.Where(f => f.InferenceMs is { }).Select(f => f.InferenceMs!.Value)),
            ServedFrames = servedSequences.Count,
            DeliveredFrames = delivered.Count,
            UnservedFrames = delivered.Count - servedSequences.Count,
            FirstServeSimT = served.Count == 0 ? null : served.Min(c => c.SimT),
            LastServeSimT = served.Count == 0 ? null : served.Max(c => c.SimT),
        };
    }

    /// <summary>Summary-level baseline diff (baseline − live) with the explicit non-bit-comparison note.</summary>
    private static VisionLiveBaselineDiff BuildDiff(VisionLiveRun live, VisionLiveRun baseline)
    {
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kind in live.EventKinds.Keys.Concat(baseline.EventKinds.Keys).Distinct(StringComparer.Ordinal))
        {
            kinds[kind] = baseline.EventKinds.GetValueOrDefault(kind) - live.EventKinds.GetValueOrDefault(kind);
        }
        return new VisionLiveBaselineDiff
        {
            ScoreUs = baseline.FinalScores.Us - live.FinalScores.Us,
            ScoreThem = baseline.FinalScores.Them - live.FinalScores.Them,
            Ticks = baseline.Ticks - live.Ticks,
            EventCount = baseline.EventCount - live.EventCount,
            EventKinds = kinds,
            Note = "摘要级对比(基线−live): 两场 RNG 消费天然不同(仅默认 classifyRate 桩抽 VisionContext.Random), 不做位对位; "
                + "唯一受控变量是视觉源(live 桥 vs 默认 classifyRate 桩)",
        };
    }

    /// <summary>Min/p50/p95/max summary using the vision line's percentile semantics (empty → count 0).</summary>
    private static VisionDistribution Distribution(IEnumerable<double> source)
    {
        var values = source.ToList();
        if (values.Count == 0)
        {
            return new VisionDistribution { Count = 0 };
        }
        values.Sort();
        return new VisionDistribution
        {
            Count = values.Count,
            Min = values[0],
            P50 = VisionLinkMetrics.Percentile(values, 0.5),
            P95 = VisionLinkMetrics.Percentile(values, 0.95),
            Max = values[^1],
        };
    }

    /// <summary>All ledger fields of one classify call, as one comparable line (equivalence record identity).</summary>
    private static string DumpConsume(VisionReplayConsumeRecord record)
        => $"{record.Role}|{record.SimT:R}|{record.FrameSequence?.ToString() ?? "-"}"
            + $"|{record.AgeMs?.ToString("R") ?? "-"}|{record.Reason ?? "-"}|{record.Label}|{record.Confidence:R}";
}
