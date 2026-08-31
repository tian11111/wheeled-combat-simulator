using System.Globalization;
using System.Text;
using Sim.Core;
using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// `batch` — headless parallel batch simulation for AI agents (no Godot, no
/// desktop shell). Runs one independent match per input seed through the
/// shared <see cref="MatchRunner"/> with a bounded worker pool, then emits
/// exactly one `sim-batch-result-v1` JSON object per input seed on stdout (or
/// an atomically replaced `--out` file), in input order.
///
/// Contract (see docs/CLI.md):
/// - stdout carries ONLY the JSONL stream; diagnostics and errors go to stderr.
/// - Preflight (option values, scenario load + Validate, output writability)
///   happens before any worker/controller starts; failures exit 2 with no
///   JSONL and no partial output file.
/// - Runtime failures still emit the full N rows (completed + failed) and exit 1.
/// - Exit codes: 0 all completed; 1 at least one failed row or output failure;
///   2 preflight/usage failure.
/// </summary>
internal static class BatchCommand
{
    /// <summary>Upper bound for --parallelism (worker pool size).</summary>
    public const int MaxParallelism = 32;

    /// <summary>Upper bound for the number of input seeds.</summary>
    public const int MaxSeeds = 4096;

    /// <summary>Default worker count: bounded by CPU count and 8.</summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount, 1, 8);

    public static int Run(string[] args)
    {
        var (settings, parseError, help) = Parse(args);
        if (help)
        {
            PrintHelp();
            return 0;
        }
        if (settings is null)
        {
            Console.Error.WriteLine($"batch: {parseError}");
            return 2;
        }

        string? preflightError = null;
        string canonicalScenario = "";
        string? outPath = null;
        try
        {
            canonicalScenario = BuildCanonicalScenario(settings);
            outPath = PreflightOutputPath(settings.Out);
        }
        catch (BatchUsageException ex)
        {
            preflightError = ex.Message;
        }
        if (preflightError is not null)
        {
            Console.Error.WriteLine($"batch: {preflightError}");
            return 2;
        }

        Console.Error.WriteLine($"batch: {settings.Seeds.Count} seed(s), parallelism={settings.Parallelism}");

        var executor = new BatchExecutor(settings.Parallelism);
        var seeds = settings.Seeds;
        var rows = executor.Execute(seeds, index => RunJob(index, seeds[index], canonicalScenario, settings));

        // Validate before output: rows are produced by this command, so an
        // invalid row is an internal invariant violation, never partial output.
        foreach (var row in rows)
        {
            var errors = string.Join(" ", row.Validate());
            if (errors.Length > 0)
            {
                Console.Error.WriteLine($"batch: internal error, refusing to emit invalid row: {errors}");
                return 1;
            }
        }

        return WriteOutput(rows, outPath);
    }

    // ---------- job execution ----------

    private static BatchMatchResult RunJob(int index, long seed, string canonicalScenario, BatchSettings settings)
    {
        try
        {
            // Each job deserializes its own scenario copy from the preflight
            // canonical payload (nested dictionaries/lists are never shared
            // across workers) and overrides the seed. Duration is already
            // baked into the canonical payload.
            var scenario = ProtocolJson.Deserialize<Scenario>(canonicalScenario) with { Seed = seed };
            var result = MatchRunner.Run(scenario, new MatchRunner.Options
            {
                ControllerUs = settings.ControllerUs,
                ControllerThem = settings.ControllerThem,
                TimeoutMs = settings.TimeoutMs,
                Events = false, // stdout is JSONL-only; full event text stays on match/replay-record
            });
            return ProjectCompleted(index, scenario.Id, result);
        }
        catch (MatchRunner.ControllerStartException ex)
        {
            return FailedRow(index, seed, "controller_start_failed", ex.Message);
        }
        catch (Exception ex)
        {
            return FailedRow(index, seed, "match_error", ex.Message);
        }
    }

    private static BatchMatchResult ProjectCompleted(int index, string scenarioId, MatchRunner.MatchRunResult result)
        => new()
        {
            InputIndex = index,
            Seed = result.Seed,
            Status = BatchMatchResult.StatusCompleted,
            ScenarioId = scenarioId,
            Ticks = result.Ticks,
            Scores = result.Scores,
            Penalties = result.Penalties,
            DoneReason = result.DoneReason,
            Faults = new BatchFaults { Us = result.UsFaults, Them = result.ThemFaults },
            EventCount = result.EventFingerprints.Count,
            EventFingerprint = BatchFingerprint.EventFingerprint(result.EventFingerprints),
            ResultFingerprint = BatchFingerprint.ResultFingerprint(
                result.Seed, result.Ticks, result.Scores, result.Penalties,
                result.DoneReason ?? "", result.EventFingerprints),
        };

    private static BatchMatchResult FailedRow(int index, long seed, string kind, string message)
        => new()
        {
            InputIndex = index,
            Seed = seed,
            Status = BatchMatchResult.StatusFailed,
            Faults = new BatchFaults(),
            Failure = new BatchFailure { Kind = kind, Message = message },
        };

    // ---------- preflight ----------

    private sealed class BatchUsageException(string message) : Exception(message);

    /// <summary>
    /// Loads and validates the scenario exactly once and serializes it to a
    /// canonical JSON payload; every worker later deserializes its own copy
    /// from this payload so nested scenario state is never shared.
    /// </summary>
    private static string BuildCanonicalScenario(BatchSettings settings)
    {
        Scenario scenario;
        if (settings.ScenarioPath is null)
        {
            scenario = Program.DefaultScenario();
        }
        else
        {
            string json;
            try
            {
                json = File.ReadAllText(settings.ScenarioPath);
            }
            catch (Exception ex)
            {
                throw new BatchUsageException($"cannot read scenario '{settings.ScenarioPath}': {ex.Message}");
            }
            try
            {
                scenario = ProtocolJson.Deserialize<Scenario>(json);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException)
            {
                throw new BatchUsageException($"invalid scenario '{settings.ScenarioPath}': {ex.Message}");
            }
            Console.Error.WriteLine($"scenario: {settings.ScenarioPath} (id={scenario.Id})");
        }

        if (settings.Duration is { } duration)
        {
            scenario = scenario with { Field = scenario.Field with { MatchDuration = duration } };
        }
        var errors = scenario.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new BatchUsageException($"invalid scenario '{settings.ScenarioPath ?? "(default)"}': {string.Join(" ", errors)}");
        }
        return ProtocolJson.Serialize(scenario);
    }

    /// <summary>Resolves and probes the --out path before any worker starts.</summary>
    private static string? PreflightOutputPath(string? outPath)
    {
        if (outPath is null)
        {
            return null;
        }
        var full = Path.GetFullPath(outPath);
        if (Directory.Exists(full))
        {
            throw new BatchUsageException($"output path '{outPath}' is an existing directory");
        }
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
        {
            throw new BatchUsageException($"output path '{outPath}' has no parent directory");
        }
        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex)
        {
            throw new BatchUsageException($"cannot create output directory '{parent}': {ex.Message}");
        }
        var probe = Path.Combine(parent, $".batch-write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
        }
        catch (Exception ex)
        {
            throw new BatchUsageException($"output path '{outPath}' is not writable: {ex.Message}");
        }
        finally
        {
            try { File.Delete(probe); } catch { /* best effort */ }
        }
        return full;
    }

    // ---------- output ----------

    private static int WriteOutput(IReadOnlyList<BatchMatchResult> rows, string? outPath)
    {
        var payload = new StringBuilder();
        foreach (var row in rows)
        {
            payload.Append(ProtocolJson.Serialize(row)).Append('\n');
        }

        var exitCode = rows.Any(r => r.Status == BatchMatchResult.StatusFailed) ? 1 : 0;
        if (outPath is null)
        {
            Console.Out.Write(payload.ToString());
            Console.Out.Flush();
            return exitCode;
        }

        var temp = Path.Combine(
            Path.GetDirectoryName(outPath)!,
            $".{Path.GetFileName(outPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(temp, payload.ToString());
            File.Move(temp, outPath, overwrite: true);
            return exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"batch: failed to write output '{outPath}': {ex.Message}");
            try { File.Delete(temp); } catch { /* best effort cleanup */ }
            return 1;
        }
    }

    // ---------- parsing ----------

    internal sealed record BatchSettings
    {
        public IReadOnlyList<long> Seeds { get; init; } = [];

        public string? ScenarioPath { get; init; }

        public double? Duration { get; init; }

        public string? ControllerUs { get; init; }

        public string? ControllerThem { get; init; }

        public double TimeoutMs { get; init; } = 100;

        public int Parallelism { get; init; }

        public string? Out { get; init; }
    }

    private static readonly string[] KnownOptions =
    [
        "--seed", "--seeds", "--scenario", "--duration", "--controller-us",
        "--controller-them", "--timeout-ms", "--parallelism", "--out", "--help", "-h",
    ];

    private static (BatchSettings? Settings, string? Error, bool Help) Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--events")
            {
                return (null, "--events is not supported: batch stdout is JSONL only (use match --events for event text)", false);
            }
            if (!KnownOptions.Contains(arg))
            {
                return (null, $"unknown option '{arg}' (accepted: {string.Join(" ", KnownOptions)})", false);
            }
            if (arg is "--help" or "-h")
            {
                return (null, null, true);
            }
            if (values.ContainsKey(arg))
            {
                return (null, $"duplicate option '{arg}'", false);
            }
            if (i + 1 >= args.Length)
            {
                return (null, $"option '{arg}' requires a value", false);
            }
            values[arg] = args[++i];
        }

        List<long> seeds;
        var hasSeed = values.TryGetValue("--seed", out var seedRaw);
        var hasSeeds = values.TryGetValue("--seeds", out var seedsRaw);
        if (hasSeed && hasSeeds)
        {
            return (null, "use --seed or --seeds, not both", false);
        }
        if (hasSeed || hasSeeds)
        {
            var error = ParseSeeds((hasSeed ? seedRaw : seedsRaw)!, out seeds);
            if (error is not null)
            {
                return (null, error, false);
            }
        }
        else
        {
            seeds = [42];
        }

        double? duration = null;
        if (values.TryGetValue("--duration", out var durationRaw))
        {
            var error = ParsePositiveFinite(durationRaw, "--duration", out var parsed);
            if (error is not null)
            {
                return (null, error, false);
            }
            duration = parsed;
        }

        double timeoutMs = 100;
        if (values.TryGetValue("--timeout-ms", out var timeoutRaw))
        {
            var error = ParsePositiveFinite(timeoutRaw, "--timeout-ms", out var parsed);
            if (error is not null)
            {
                return (null, error, false);
            }
            timeoutMs = parsed;
        }

        var parallelism = DefaultParallelism;
        if (values.TryGetValue("--parallelism", out var parallelismRaw))
        {
            if (!int.TryParse(parallelismRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1 || parsed > MaxParallelism)
            {
                return (null, $"invalid --parallelism '{parallelismRaw}' (expected an integer in 1..{MaxParallelism})", false);
            }
            parallelism = parsed;
        }

        return (new BatchSettings
        {
            Seeds = seeds,
            ScenarioPath = values.GetValueOrDefault("--scenario"),
            Duration = duration,
            ControllerUs = values.GetValueOrDefault("--controller-us"),
            ControllerThem = values.GetValueOrDefault("--controller-them"),
            TimeoutMs = timeoutMs,
            Parallelism = parallelism,
            Out = values.GetValueOrDefault("--out"),
        }, null, false);
    }

    private static string? ParseSeeds(string raw, out List<long> seeds)
    {
        seeds = [];
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) || seed < 0)
            {
                return $"invalid seed '{part}' (expected a non-negative integer)";
            }
            seeds.Add(seed);
        }
        if (seeds.Count == 0)
        {
            return "--seeds must contain at least one seed";
        }
        if (seeds.Count > MaxSeeds)
        {
            return $"too many seeds ({seeds.Count}); the limit is {MaxSeeds}";
        }
        return null;
    }

    private static string? ParsePositiveFinite(string raw, string option, out double value)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || !double.IsFinite(value) || value <= 0)
        {
            return $"invalid {option} '{raw}' (expected a finite positive number)";
        }
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($$"""
            Sim.Cli batch — AI agent 无头并行批量仿真 (JSONL, 不启动 Godot)

            用法:
              dotnet run --project src/Sim.Cli -- batch [--seed 42 | --seeds 1,2,3,4]
                         [--scenario <path>] [--duration 120]
                         [--controller-us <cmd>] [--controller-them <cmd>]
                         [--timeout-ms 100] [--parallelism 4] [--out <path>]

            选项:
              --seed N | --seeds a,b,c   输入种子列表 (1..{{MaxSeeds}} 个, 可重复, 按 inputIndex 区分)
              --scenario <path>          场景文件 (缺省为官方 2026 布局)
              --duration <s>             覆盖比赛时长 (正数)
              --controller-us/--controller-them <cmd>
                                         外部策略进程命令 (JSONL stdio); 每场独立启动/回收,
                                         缺省角色使用内置 FSM
              --timeout-ms <ms>          单帧响应截止 (正数, 默认 100)
              --parallelism <k>          同时运行的场次数 (整数 1..{{MaxParallelism}},
                                         默认 min(CPU 核数, 8))
              --out <path>               结果文件 (同目录临时文件 + 原子替换); 缺省写 stdout

            输出:
              每个输入种子一行 sim-batch-result-v1 JSON, 按输入顺序排列;
              stdout 只有 JSONL, 诊断与错误走 stderr. 不支持 --events.

            退出码:
              0 全部场次完成; 1 至少一场失败或输出失败; 2 参数/场景预检失败 (无 JSONL).
            """);
    }
}
