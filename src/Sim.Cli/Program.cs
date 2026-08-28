using Sim.Core;
using Sim.Protocol;

namespace Sim.Cli;

public static class Program
{
    private const int MaxTicks = 10_000;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "match" => RunMatch(ParseOptions(args)),
                "replay-record" => RunReplayRecord(ParseOptions(args)),
                "replay-check" => RunReplayCheck(args),
                "calibrate" => CalibrateCommand.Run(args),
                "sensor-calibration" => SensorCalibrationCommand.Run(args),
                "--help" or "-h" => Help(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // ---------- option plumbing ----------

    private sealed class Options
    {
        public List<long> Seeds { get; init; } = [42];
        public string? ScenarioPath { get; init; }
        public double? Duration { get; init; }
        public string? ControllerUs { get; init; }
        public string? ControllerThem { get; init; }
        public double TimeoutMs { get; init; } = 100;
        public bool Events { get; init; }
        public string? Out { get; init; }

        public Scenario BuildScenario(long seed)
        {
            var scenario = ScenarioPath is null ? DefaultScenario() : LoadScenario(ScenarioPath);
            scenario = scenario with
            {
                Seed = seed,
                Field = Duration is null
                    ? scenario.Field
                    : scenario.Field with { MatchDuration = Duration.Value },
            };
            return scenario;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        string? Get(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var seeds = new List<long>();
        var seedsRaw = Get("--seeds") ?? Get("--seed");
        if (seedsRaw is not null)
        {
            foreach (var part in seedsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                seeds.Add(long.Parse(part));
            }
        }
        return new Options
        {
            Seeds = seeds.Count > 0 ? seeds : [42],
            ScenarioPath = Get("--scenario"),
            Duration = double.TryParse(Get("--duration"), out var duration) ? duration : null,
            ControllerUs = Get("--controller-us"),
            ControllerThem = Get("--controller-them"),
            TimeoutMs = double.TryParse(Get("--timeout-ms"), out var timeout) ? timeout : 100,
            Events = args.Contains("--events"),
            Out = Get("--out"),
        };
    }

    private static Scenario LoadScenario(string path)
    {
        var scenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(path));
        var errors = scenario.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid scenario '{path}': {string.Join(" ", errors)}");
        }
        Console.Error.WriteLine($"scenario: {path} (id={scenario.Id}, seed={scenario.Seed})");
        return scenario;
    }

    /// <summary>Default layout: official 2026 field with frozen block coordinates.</summary>
    private static Scenario DefaultScenario() => new()
    {
        Seed = 42,
        Blocks = OfficialLayout.Blocks,
    };

    // ---------- match ----------

    private sealed record MatchResult(
        long Seed, long Ticks, Scores Scores, Scores Penalties,
        string? DoneReason, long UsFaults, long ThemFaults,
        List<string> EventFingerprints, ReplayHeader Header);

    private static MatchResult RunOne(Options options, long seed)
    {
        var engine = new MatchEngine(options.BuildScenario(seed));
        using PythonBridge? usBridge = options.ControllerUs is null
            ? null
            : PythonBridge.Start(options.ControllerUs, options.TimeoutMs);
        using PythonBridge? themBridge = options.ControllerThem is null
            ? null
            : PythonBridge.Start(options.ControllerThem, options.TimeoutMs);

        engine.Arm();
        var fingerprints = new List<string>();
        var snapshots = new List<Snapshot>();
        while (!engine.Done && snapshots.Count < MaxTicks)
        {
            RobotAction? usAction = null;
            RobotAction? themAction = null;
            if (usBridge is not null && !engine.Done)
            {
                usAction = usBridge.Decide(engine.BuildObservation(engine.Us));
            }
            if (themBridge is not null && !engine.Done)
            {
                themAction = themBridge.Decide(engine.BuildObservation(engine.Them));
            }
            var snapshot = engine.Tick(usAction, themAction);
            snapshots.Add(snapshot);
            if (snapshot.Events is { Count: > 0 })
            {
                foreach (var evt in snapshot.Events)
                {
                    fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                    if (options.Events)
                    {
                        Console.WriteLine($"[{evt.Seq,4}] t={evt.T,7:0.00} {evt.Type,-16} {evt.Msg}");
                    }
                }
            }
        }

        return new MatchResult(
            seed,
            snapshots.Count,
            engine.Scores,
            engine.RestartPenalties,
            engine.Done ? snapshots[^1].DoneReason : "(未结束)",
            usBridge?.Faults ?? 0,
            themBridge?.Faults ?? 0,
            fingerprints,
            engine.BuildReplayHeader());
    }

    private static void PrintResult(MatchResult result)
    {
        Console.WriteLine(
            $"seed={result.Seed} ticks={result.Ticks} score 我方 {result.Scores.Us:0.#} : {result.Scores.Them:0.#} 对手"
            + $" done={result.DoneReason} faults(us/them)={result.UsFaults}/{result.ThemFaults}"
            + $" penalties={result.Penalties.Us:0.#}/{result.Penalties.Them:0.#}");
    }

    private static int RunMatch(Options options)
    {
        var results = new List<MatchResult>();
        foreach (var seed in options.Seeds)
        {
            var result = RunOne(options, seed);
            PrintResult(result);
            results.Add(result);
        }
        if (results.Count > 1)
        {
            var usWins = results.Count(r => r.Scores.Us > r.Scores.Them);
            var draws = results.Count(r => r.Scores.Us == r.Scores.Them);
            Console.WriteLine($"summary: matches={results.Count} 我方胜={usWins} 平={draws} 对手胜={results.Count - usWins - draws}");
        }
        return 0;
    }

    // ---------- replay-record / replay-check ----------

    private static int RunReplayRecord(Options options)
    {
        if (options.Out is null)
        {
            Console.Error.WriteLine("replay-record requires --out <path>");
            return 2;
        }
        if (options.Seeds.Count != 1)
        {
            Console.Error.WriteLine("replay-record requires exactly one --seed");
            return 2;
        }

        var scenario = options.BuildScenario(options.Seeds[0]);
        var result = RunOne(options, options.Seeds[0]);
        var file = new ReplayFile
        {
            Scenario = scenario,
            Header = result.Header,
            Ticks = result.Ticks,
            FinalScores = result.Scores,
            DoneReason = result.DoneReason,
            EventFingerprints = result.EventFingerprints,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
        File.WriteAllText(options.Out, ProtocolJson.Serialize(file));
        PrintResult(result);
        Console.WriteLine($"replay written: {options.Out} ({file.EventFingerprints.Count} events, {file.Header.TickCount} recorded ticks)");
        return 0;
    }

    private static int RunReplayCheck(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith('-') && a != "replay-check")
            ?? throw new ArgumentException("replay-check requires a replay file path");
        var file = ProtocolJson.Deserialize<ReplayFile>(File.ReadAllText(path));
        var errors = file.Header.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid replay header: {string.Join(" ", errors)}");
        }

        var engine = new MatchEngine(file.Scenario);
        var actionsByTick = file.Header.Ticks.ToDictionary(t => t.Tick, t => t.Actions);
        var commandsByTick = file.Header.Ticks
            .Where(t => t.Commands is { Count: > 0 })
            .ToDictionary(t => t.Tick, t => t.Commands!);

        var fingerprints = new List<string>();
        engine.Arm();
        var lastTick = Math.Max(file.Ticks, file.Header.Ticks.Count > 0 ? file.Header.Ticks[^1].Tick : 0);
        for (var tick = 1; tick <= lastTick && !engine.Done; tick++)
        {
            if (commandsByTick.TryGetValue(tick, out var commands))
            {
                ApplyCommands(engine, commands);
            }
            actionsByTick.TryGetValue(tick, out var actions);
            var snapshot = engine.Tick(
                actions?.GetValueOrDefault(RoleNames.Us),
                actions?.GetValueOrDefault(RoleNames.Them));
            if (snapshot.Events is { Count: > 0 })
            {
                foreach (var evt in snapshot.Events)
                {
                    fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                }
            }
        }

        var scoreOk = engine.Scores.Us == file.FinalScores.Us && engine.Scores.Them == file.FinalScores.Them;
        var eventsOk = fingerprints.SequenceEqual(file.EventFingerprints);
        Console.WriteLine($"replay-check {path}: scores {engine.Scores.Us:0.#}:{engine.Scores.Them:0.#}"
            + $" (expected {file.FinalScores.Us:0.#}:{file.FinalScores.Them:0.#})"
            + $" events {fingerprints.Count}/{file.EventFingerprints.Count}");
        if (scoreOk && eventsOk)
        {
            Console.WriteLine("PASS: replay reproduces the recorded match bit-for-bit.");
            return 0;
        }
        var firstDiff = fingerprints.Zip(file.EventFingerprints).FirstOrDefault(p => p.First != p.Second);
        if (firstDiff.First is not null || firstDiff.Second is not null)
        {
            Console.Error.WriteLine($"first event divergence:\n  replay: {firstDiff.First ?? "(none)"}\n  record: {firstDiff.Second ?? "(none)"}");
        }
        Console.Error.WriteLine("FAIL: replay does not reproduce the recorded match.");
        return 1;
    }

    private static void ApplyCommands(MatchEngine engine, List<string> commands)
    {
        foreach (var command in commands)
        {
            var parts = command.Split(':', 3);
            if (parts.Length == 3 && parts[0] == "restart")
            {
                engine.RestartPenalty(parts[1], parts[2]);
            }
            else if (parts.Length == 2 && parts[0] == "restart_robot")
            {
                if (RoleNames.IsKnownRole(parts[1]))
                {
                    engine.RestartRobot(parts[1]);
                }
                else
                {
                    Console.Error.WriteLine($"warning: unknown recorded command '{command}' ignored");
                }
            }
            else
            {
                Console.Error.WriteLine($"warning: unknown recorded command '{command}' ignored");
            }
        }
    }

    // ---------- help ----------

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Sim.Cli — 武术擂台无头评测/回放工具

            用法:
              dotnet run --project src/Sim.Cli -- match [--seed 42|--seeds 1,2,3] [--scenario <path>]
                         [--duration 120] [--controller-us <cmd>] [--controller-them <cmd>]
                         [--timeout-ms 100] [--events]
              dotnet run --project src/Sim.Cli -- replay-record --seed 42 --out replays/seed-42.json
                         [--scenario <path>] [--controller-us <cmd>] [--events]
              dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
              dotnet run --project src/Sim.Cli -- calibrate --input telemetry.json [--out calibration/report.json]
                         [--vehicle-id ID] [--base-scenario scenarios/wushu-ring-2026.json]
                         [--emit-scenario scenarios/calibrated.json] [--fidelity fidelity.json]
                         [--update-fidelity] [--force]
              dotnet run --project src/Sim.Cli -- sensor-calibration import --data-dir <MBri/data>
                         --manifest selection.json --out calibration/sensor-report.json
                         [--config config.py] [--force]

            说明:
              --controller-* 启动外部策略进程（JSONL stdio 协议, decide(obs) -> {"v":..,"w":..});
              缺省时对应角色使用内置 FSM。超时/坏行按零动作回退并计入 faults。
            """);
    }
}
