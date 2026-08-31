using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Sim.Cli;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Console-capturing CLI tests must never interleave with other tests that
/// swap Console.Out — this collection runs exclusively.
/// </summary>
[CollectionDefinition("cli-console", DisableParallelization = true)]
public sealed class CliConsoleCollection;

/// <summary>
/// End-to-end regression for the headless `batch` command (no controllers:
/// built-in FSM paths, preflight validation, JSONL output, executor seam).
/// </summary>
[Collection("cli-console")]
public class BatchCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sim-batch-tests").FullName;
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;

    private string Out(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup on Windows
        }
    }

    private (int Code, string StdOut, string StdErr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = Program.Main(args);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }
    }

    private static List<BatchMatchResult> ParseRows(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.StartsWith("{", line)); // JSONL only, no human text
        return lines.Select(line => ProtocolJson.Deserialize<BatchMatchResult>(line)).ToList();
    }

    // ---------- happy paths ----------

    [Fact]
    public void SingleSeed_Completes_WithStableRowAndJsonOnlyStdout()
    {
        var (code, stdout, stderr) = Run("batch", "--seed", "7", "--duration", "3");
        Assert.Equal(0, code);
        var rows = ParseRows(stdout);
        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(BatchMatchResult.StatusCompleted, row.Status);
        Assert.Equal(0, row.InputIndex);
        Assert.Equal(7, row.Seed);
        Assert.Equal(60, row.Ticks);
        Assert.Equal("wushu-ring-2026", row.ScenarioId);
        Assert.Equal("比赛时间结束", row.DoneReason);
        Assert.True(row.EventCount > 0);
        Assert.Equal(0, row.Faults!.Us);
        Assert.Equal(0, row.Faults!.Them);
        Assert.Multiple(
            () => Assert.Equal(64, row.EventFingerprint!.Length),
            () => Assert.Equal(64, row.ResultFingerprint!.Length),
            () => Assert.All(row.EventFingerprint!, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c))),
            () => Assert.All(row.ResultFingerprint!, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c))));
        Assert.Contains("batch: 1 seed(s), parallelism=", stderr);
        Assert.DoesNotContain("seed=7 ticks=", stdout); // legacy human summary stays on match
    }

    [Fact]
    public void MultiSeed_EmitsOneRowPerSeed_InInputOrder()
    {
        var (code, stdout, _) = Run("batch", "--seeds", "3,1,2", "--duration", "0.5");
        Assert.Equal(0, code);
        var rows = ParseRows(stdout);
        Assert.Equal(3, rows.Count);
        Assert.Equal([3, 1, 2], rows.Select(r => r.Seed));
        Assert.Equal([0, 1, 2], rows.Select(r => r.InputIndex));
    }

    [Fact]
    public void DuplicateSeeds_Kept_AndDistinguishedByInputIndex()
    {
        var (code, stdout, _) = Run("batch", "--seeds", "5,5", "--duration", "0.5");
        Assert.Equal(0, code);
        var rows = ParseRows(stdout);
        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Seed);
        Assert.Equal(5, rows[1].Seed);
        Assert.Equal(0, rows[0].InputIndex);
        Assert.Equal(1, rows[1].InputIndex);
        // same input ⇒ identical stable core fields, incl. both fingerprints
        Assert.Equal(rows[0].EventFingerprint, rows[1].EventFingerprint);
        Assert.Equal(rows[0].ResultFingerprint, rows[1].ResultFingerprint);
        Assert.Equal(rows[0].Ticks, rows[1].Ticks);
        Assert.Equal(rows[0].Scores, rows[1].Scores);
    }

    [Fact]
    public void Parallelism1_Projection_MatchesLegacyMatchOutput()
    {
        var legacy = Run("match", "--seed", "7", "--duration", "3");
        Assert.Equal(0, legacy.Code);
        var match = Regex.Match(legacy.StdOut.Replace("\r", ""), """
            ^seed=(?<seed>\d+) ticks=(?<ticks>\d+) score 我方 (?<us>[-0-9.]+) : (?<them>[-0-9.]+) 对手 done=(?<done>.+?) faults\(us/them\)=(?<uf>\d+)/(?<tf>\d+) penalties=(?<up>[-0-9.]+)/(?<tp>[-0-9.]+)$
            """, RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, "legacy match line format changed:\n" + legacy.StdOut);

        var batch = Run("batch", "--seed", "7", "--duration", "3", "--parallelism", "1");
        Assert.Equal(0, batch.Code);
        var row = ParseRows(batch.StdOut).Single();
        Assert.Equal(long.Parse(match.Groups["seed"].Value), row.Seed);
        Assert.Equal(long.Parse(match.Groups["ticks"].Value), row.Ticks);
        Assert.Equal(double.Parse(match.Groups["us"].Value, CultureInfo.InvariantCulture), row.Scores!.Us);
        Assert.Equal(double.Parse(match.Groups["them"].Value, CultureInfo.InvariantCulture), row.Scores!.Them);
        Assert.Equal(match.Groups["done"].Value, row.DoneReason);
        Assert.Equal(long.Parse(match.Groups["uf"].Value), row.Faults!.Us);
        Assert.Equal(long.Parse(match.Groups["tf"].Value), row.Faults!.Them);
        Assert.Equal(double.Parse(match.Groups["up"].Value, CultureInfo.InvariantCulture), row.Penalties!.Us);
        Assert.Equal(double.Parse(match.Groups["tp"].Value, CultureInfo.InvariantCulture), row.Penalties!.Them);
    }

    [Fact]
    public void Parallelism_DoesNotChangeStableResults()
    {
        var slow = ParseRows(Run("batch", "--seeds", "1,2,3", "--duration", "0.5", "--parallelism", "1").StdOut);
        var fast = ParseRows(Run("batch", "--seeds", "1,2,3", "--duration", "0.5", "--parallelism", "4").StdOut);
        Assert.Equal(3, slow.Count);
        Assert.Equal(3, fast.Count);
        var stable = new Func<BatchMatchResult, object[]>(r => new object[]
        {
            r.Seed, r.Ticks, r.Scores!.Us, r.Scores!.Them,
            r.Penalties!.Us, r.Penalties!.Them, r.DoneReason,
            r.Faults!.Us, r.Faults!.Them, r.EventCount,
            r.EventFingerprint, r.ResultFingerprint,
        });
        Assert.Equal(slow.Select(stable), fast.Select(stable));
    }

    // ---------- --out ----------

    [Fact]
    public void OutFile_ReceivesJsonl_AtomicReplace_CleanTempFiles()
    {
        var outPath = Out("batch.jsonl");
        var first = Run("batch", "--seeds", "7,8", "--duration", "0.5", "--out", outPath);
        Assert.Equal(0, first.Code);
        Assert.Equal("", first.StdOut); // with --out, stdout carries no JSONL
        var text = File.ReadAllText(outPath);
        Assert.EndsWith("\n", text);
        Assert.Equal(2, ParseRows(text).Count);

        // second run replaces (never appends) and leaves no temp files behind
        var second = Run("batch", "--seeds", "9", "--duration", "0.5", "--out", outPath);
        Assert.Equal(0, second.Code);
        var replaced = File.ReadAllLines(outPath);
        Assert.Single(replaced);
        Assert.Contains("\"seed\":9", replaced[0]);
        Assert.Empty(Directory.GetFiles(_dir, ".batch.jsonl.tmp-*"));
    }

    // ---------- preflight / usage failures ----------

    public static IEnumerable<object[]> InvalidInputs()
    {
        var tooManySeeds = string.Join(",", Enumerable.Repeat("1", 4097));
        yield return new object[] { new[] { "batch", "--seeds", "abc" } };
        yield return new object[] { new[] { "batch", "--seeds", "-1" } };
        yield return new object[] { new[] { "batch", "--seeds", tooManySeeds } };
        yield return new object[] { new[] { "batch", "--seed", "1", "--seeds", "2" } };
        yield return new object[] { new[] { "batch", "--duration", "0" } };
        yield return new object[] { new[] { "batch", "--duration", "-3" } };
        yield return new object[] { new[] { "batch", "--duration", "abc" } };
        yield return new object[] { new[] { "batch", "--timeout-ms", "0" } };
        yield return new object[] { new[] { "batch", "--timeout-ms", "abc" } };
        yield return new object[] { new[] { "batch", "--parallelism", "0" } };
        yield return new object[] { new[] { "batch", "--parallelism", "33" } };
        yield return new object[] { new[] { "batch", "--parallelism", "abc" } };
        yield return new object[] { new[] { "batch", "--bogus", "1" } };
        yield return new object[] { new[] { "batch", "--events" } };
        yield return new object[] { new[] { "batch", "--out" } };
        yield return new object[] { new[] { "batch", "--scenario", "does-not-exist.json" } };
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void PreflightFailures_Exit2_WithZeroStdout(string[] args)
    {
        var (code, stdout, stderr) = Run(args);
        Assert.Equal(2, code);
        Assert.Equal("", stdout); // no JSONL at all
        Assert.NotEqual("", stderr); // concise reason on stderr
    }

    [Fact]
    public void BadScenarioContent_FailsPreflight()
    {
        var broken = Out("broken.json");
        File.WriteAllText(broken, "{ this is not json");
        Assert.Equal(2, Run("batch", "--seed", "1", "--scenario", broken).Code);

        var invalid = Out("invalid-seed.json");
        File.WriteAllText(invalid, """{"id":"x","seed":-5}""");
        var (code, stdout, _) = Run("batch", "--seed", "1", "--scenario", invalid);
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void UnwritableOut_FailsPreflight_NoPartialFile()
    {
        var blocker = Out("blocker"); // an existing FILE used as a parent "directory"
        File.WriteAllText(blocker, "x");
        var (code, stdout, _) = Run("batch", "--seed", "1", "--duration", "0.5", "--out", Path.Combine(blocker, "child.jsonl"));
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Equal("x", File.ReadAllText(blocker)); // target untouched
        // no probe/temp artifacts left anywhere
        Assert.Empty(Directory.GetFiles(_dir, ".batch-write-probe-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void OutDirExistingDirectory_FailsPreflight()
    {
        var target = Out("adirectory");
        Directory.CreateDirectory(target);
        var (code, stdout, _) = Run("batch", "--seed", "1", "--duration", "0.5", "--out", target);
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.True(Directory.Exists(target));
    }

    // ---------- executor seam (bounded parallelism) ----------

    private static BatchMatchResult StubRow(int index) => new()
    {
        InputIndex = index,
        Seed = index,
        Status = BatchMatchResult.StatusCompleted,
        ScenarioId = "wushu-ring-2026",
        Ticks = 1,
        Scores = new Scores(),
        Penalties = new Scores(),
        DoneReason = "done",
        Faults = new BatchFaults(),
        EventCount = 0,
        EventFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        ResultFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    };

    [Fact]
    public void BatchExecutor_BarrierProvesTrueOverlap_AndEachSlotWrittenOnce()
    {
        using var barrier = new Barrier(2);
        var passed = new bool[2];
        var writes = new int[2];
        var rows = new BatchExecutor(2).Execute([1, 2], index =>
        {
            Interlocked.Increment(ref writes[index]);
            passed[index] = barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            return StubRow(index);
        });

        Assert.All(passed, p => Assert.True(p, "workers did not overlap: parallelism was not actually concurrent"));
        Assert.Equal([1, 1], writes); // exactly one write per input slot
        Assert.Equal(2, rows.Length);
        Assert.Equal(0, rows[0].InputIndex);
        Assert.Equal(1, rows[1].InputIndex);
    }

    [Fact]
    public void BatchExecutor_ContainedWorkerException_BecomesSchedulerFailureRow()
    {
        var rows = new BatchExecutor(2).Execute([1, 2], index =>
        {
            if (index == 1)
            {
                throw new InvalidOperationException("boom");
            }
            return StubRow(index);
        });

        Assert.Equal(2, rows.Length); // no silently dropped input index
        Assert.Equal(BatchMatchResult.StatusCompleted, rows[0].Status);
        Assert.Equal(BatchMatchResult.StatusFailed, rows[1].Status);
        Assert.Equal("batch_scheduler", rows[1].Failure!.Kind);
        Assert.Contains("boom", rows[1].Failure!.Message);
        Assert.Equal(2, rows[1].Seed);
        Assert.Equal(0, rows[1].Faults!.Us);
    }

    [Fact]
    public void BatchExecutor_SerialMode_StillWritesEverySlotInOrder()
    {
        var seeds = new long[] { 9, 8, 7 };
        var rows = new BatchExecutor(1).Execute(seeds, index => StubRow(index) with { Seed = seeds[index] });
        Assert.Equal([0, 1, 2], rows.Select(r => r.InputIndex));
        Assert.Equal([9, 8, 7], rows.Select(r => r.Seed));
    }
}

/// <summary>
/// Batch runs with real external controller processes via the minimal JSONL
/// fixture (echo / wrongid / bad / die / hang). Each job must start and reap
/// its own bridge; faults must land only on their own seed and role.
/// </summary>
[Collection("cli-console")]
public class BatchControllerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sim-batch-ctrl-tests").FullName;
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;

    public void Dispose()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup on Windows
        }
    }

    private static string EchoControllerExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EchoController.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                "EchoController.exe not found next to the test assembly; build Sim.Tests first");
        }
        return exe;
    }

    private (int Code, string StdOut, string StdErr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = Program.Main(args);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }
    }

    private static List<BatchMatchResult> ParseRows(string stdout)
        => stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ProtocolJson.Deserialize<BatchMatchResult>(line))
            .ToList();

    [Fact]
    public void EchoController_IndependentBridgePerJob_CompletesWithoutFaults()
    {
        var exe = EchoControllerExe();
        // Generous deadline: the first frame races controller process startup;
        // echo replies are immediate afterwards, so this costs no wall-clock.
        var (code, stdout, _) = Run(
            "batch", "--seeds", "1,2", "--duration", "0.5", "--parallelism", "2",
            "--timeout-ms", "500",
            "--controller-us", $"{exe} echo");
        Assert.Equal(0, code);
        var rows = ParseRows(stdout);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(BatchMatchResult.StatusCompleted, row.Status);
            // every requestId echoed ⇒ every action matched ⇒ zero faults
            Assert.Equal(0, row.Faults!.Us);
            Assert.Equal(0, row.Faults!.Them);
            Assert.True(row.EventCount > 0);
        });
    }

    [Fact]
    public void WrongRequestId_ActionsDropped_TimeoutFaultsPerTick()
    {
        var exe = EchoControllerExe();
        var (code, stdout, _) = Run(
            "batch", "--seed", "3", "--duration", "0.5", "--timeout-ms", "50",
            "--controller-us", $"{exe} wrongid");
        Assert.Equal(0, code);
        var row = ParseRows(stdout).Single();
        // id-mismatched actions must never be applied: one fault per tick and
        // the match still completes on the zero-action fallback.
        Assert.Equal(BatchMatchResult.StatusCompleted, row.Status);
        Assert.Equal(row.Ticks, row.Faults!.Us);
        Assert.Equal(0, row.Faults!.Them);
    }

    [Fact]
    public void BadLines_LandOnlyOnTheirOwnRole()
    {
        var exe = EchoControllerExe();
        var (code, stdout, _) = Run(
            "batch", "--seeds", "4,5", "--duration", "0.5", "--timeout-ms", "50",
            "--controller-us", $"{exe} bad");
        Assert.Equal(0, code);
        var rows = ParseRows(stdout);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(BatchMatchResult.StatusCompleted, row.Status);
            Assert.Equal(row.Ticks, row.Faults!.Us);  // bad line per tick ⇒ us fault
            Assert.Equal(0, row.Faults!.Them);        // no them controller ⇒ no them faults
            Assert.True(row.EventCount > 0);          // match produced events regardless
        });
    }

    [Fact]
    public void DeadController_FaultsIsolatedPerSeed_MatchStillCompletes()
    {
        var exe = EchoControllerExe();
        var (code, stdout, _) = Run(
            "batch", "--seeds", "6,7", "--duration", "0.5",
            "--controller-us", $"{exe} die");
        Assert.Equal(0, code);
        var rows = ParseRows(stdout);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(BatchMatchResult.StatusCompleted, row.Status);
            Assert.True(row.Faults!.Us > 0);
            Assert.Equal(0, row.Faults!.Them);
        });
    }

    [Fact]
    public void MissingController_IsRuntimeFailure_FullNRowsExit1()
    {
        var (code, stdout, _) = Run(
            "batch", "--seeds", "1,2", "--duration", "0.5",
            "--controller-us", "definitely-missing-controller-xyz");
        Assert.Equal(1, code);
        var rows = ParseRows(stdout); // full N lines still emitted, all valid JSON
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(BatchMatchResult.StatusFailed, row.Status);
            Assert.Equal("controller_start_failed", row.Failure!.Kind);
            Assert.NotEqual("", row.Failure!.Message);
            Assert.Null(row.Ticks);
            Assert.Null(row.Scores);
            Assert.Null(row.EventFingerprint);
        });
        Assert.Equal([0, 1], rows.Select(r => r.InputIndex));
    }

    [Fact]
    public void HangController_ProcessesReapedAfterBatch()
    {
        var exe = EchoControllerExe();
        var (code, stdout, _) = Run(
            "batch", "--seeds", "1,2", "--duration", "0.5", "--parallelism", "2",
            "--timeout-ms", "20", "--controller-us", $"{exe} hang");
        Assert.Equal(0, code);
        Assert.All(ParseRows(stdout), row => Assert.True(row.Faults!.Us > 0));

        // the controller ignores stdin and never exits on its own; only bridge
        // disposal can terminate it, so zero remaining processes proves reaping.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Process.GetProcessesByName("EchoController").Length == 0)
            {
                return;
            }
            Thread.Sleep(100);
        }
        Assert.Fail("EchoController processes survived the batch: a bridge was not disposed");
    }
}
