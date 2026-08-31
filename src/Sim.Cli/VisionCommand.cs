using System.Security.Cryptography;
using Sim.Core;
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
    private const long MaxFileBytes = 128L * 1024 * 1024;
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
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxFileBytes)
            {
                throw new VisionEvidenceException($"文件超过 128MB 上限: {name} ({bytes.Length} B)");
            }
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

        // Pre-flight: evidence package integrity BEFORE any output.
        var framesPath = Path.Combine(evidenceDir, VisionReplayIO.FramesFileName);
        var importReportPath = Path.Combine(evidenceDir, VisionReplayIO.ImportReportFileName);
        if (!File.Exists(framesPath) || !File.Exists(importReportPath))
        {
            throw new VisionEvidenceException(
                $"证据目录缺少 {VisionReplayIO.FramesFileName} / {VisionReplayIO.ImportReportFileName}: {evidenceDir}");
        }
        var framesBytes = File.ReadAllBytes(framesPath);
        var evidenceSha256 = VisionReplayIO.Sha256Hex(framesBytes);
        var importReport = ProtocolJson.Deserialize<VisionImportReport>(
            File.ReadAllText(importReportPath));
        var importErrors = importReport.Validate().ToList();
        if (importErrors.Count > 0)
        {
            throw new VisionEvidenceException($"导入报告无效: {string.Join(" ", importErrors)}");
        }
        if (importReport.EvidenceSha256 is null)
        {
            throw new VisionEvidenceException("导入报告缺少 evidenceSha256, 无法哈希锁定证据包");
        }
        if (importReport.EvidenceSha256 != evidenceSha256)
        {
            throw new VisionEvidenceException(
                $"证据包哈希不一致: 报告 {importReport.EvidenceSha256} 实际 {evidenceSha256}");
        }
        var frames = VisionReplayIO.ParseFrames(System.Text.Encoding.UTF8.GetString(framesBytes));

        var scenarioBytes = File.ReadAllBytes(scenarioPath);
        var scenario = ProtocolJson.Deserialize<Scenario>(System.Text.Encoding.UTF8.GetString(scenarioBytes));
        var scenarioErrors = scenario.Validate().ToList();
        if (scenarioErrors.Count > 0)
        {
            throw new VisionEvidenceException($"场景无效 '{scenarioPath}': {string.Join(" ", scenarioErrors)}");
        }

        var sessions = frames.Select(f => f.Session).Distinct(StringComparer.Ordinal).ToList();
        var session = sessionArg ?? importReport.Files.FirstOrDefault()?.Path ?? sessions[0];
        if (!sessions.Contains(session, StringComparer.Ordinal))
        {
            throw new VisionEvidenceException(
                $"会话 '{session}' 不在证据包内 (可用: {string.Join(", ", sessions)})");
        }
        var replayFrames = frames.Where(f => f.Session == session)
            .Select(f => new VisionReplayFrame
            {
                Sequence = f.Sequence,
                TimestampMs = f.TimestampMs,
                Status = f.Status,
                Error = f.Error,
                SelectedTargetIndex = f.SelectedTargetIndex,
                Detections = f.Detections.Select(d => new VisionReplayFrameDetection
                {
                    Label = d.Label,
                    Confidence = d.Confidence,
                    OffsetX = d.OffsetX,
                }).ToList(),
            })
            .ToList();

        // Policy consumption replay: injected engine, full match, deterministic.
        var adapter = new VisionReplayAdapter(
            replayFrames,
            importReport.EvidenceId ?? VisionReplayIO.EvidenceId(evidenceSha256),
            evidenceSha256,
            maxAgeMs);
        var engine = new MatchEngine(scenario, adapter);
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
            EvidenceId = importReport.EvidenceId ?? VisionReplayIO.EvidenceId(evidenceSha256),
            EvidenceSha256 = evidenceSha256,
            ImportReportSha256 = VisionReplayIO.Sha256Hex(File.ReadAllText(importReportPath)),
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
}
