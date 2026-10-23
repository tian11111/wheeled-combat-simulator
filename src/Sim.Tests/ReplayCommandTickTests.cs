using Sim.Core;
using Sim.GodotShell;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// R1 replay command-tick semantics (task 10-22-reliability-audit-fixes).
///
/// A command recorded on tick N lands before the <c>StepSimExt</c> of tick N, and the
/// events it emits carry tick N (the row it is recorded on). Two consequences are pinned:
///
/// 1. The FSM's own auto-restart (<c>Fsm.cs</c> 翻覆超时) is <b>not</b> recorded as a
///    replay command. It is re-derived by replaying the same action stream; recording it
///    as <c>restart_robot:&lt;role&gt;</c> made replay apply it a second time (and one tick
///    earlier), forking every MuJoCo match that flipped — the audit's 6/10 FAIL set.
/// 2. Externally issued commands (referee R/T, legacy <c>restart:&lt;role&gt;:&lt;kind&gt;</c>)
///    stay recorded and replay bit-for-bit, with command tick == event tick.
/// </summary>
public sealed class ReplayCommandTickTests
{
    private const string ParityFixturePath = "src/Sim.Tests/fixtures/godot-parity-seed42.json";

    private static Scenario MujocoV1(long seed) => new()
    {
        Seed = seed,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        Blocks = OfficialLayout.Blocks,
    };

    private static Scenario Legacy(long seed) => new()
    {
        Seed = seed,
        Blocks = OfficialLayout.Blocks,
    };

    /// <summary>Records a whole match, optionally issuing a command between ticks.</summary>
    private static ReplayFile Record(Scenario scenario, Action<MatchEngine, long>? beforeTick = null,
        int maxTicks = 2401)
    {
        using var engine = MatchEngineHost.Create(scenario);
        engine.Arm();
        var fingerprints = new List<string>();
        for (long tick = 1; tick <= maxTicks && !engine.Done; tick++)
        {
            beforeTick?.Invoke(engine, tick);
            var snapshot = engine.Tick();
            if (snapshot.Events is { Count: > 0 })
            {
                fingerprints.AddRange(snapshot.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}"));
            }
        }
        return new ReplayFile
        {
            Scenario = scenario,
            Header = engine.BuildReplayHeader() with { CreatedAt = DateTimeOffset.UnixEpoch },
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = engine.Us.Fsm.DoneReason.Length > 0 ? engine.Us.Fsm.DoneReason : engine.Them.Fsm.DoneReason,
            EventFingerprints = fingerprints,
        };
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

    private static Dictionary<long, List<string>> CommandRows(ReplayFile file)
        => file.Header.Ticks
            .Where(t => t.Commands is { Count: > 0 })
            .ToDictionary(t => t.Tick, t => t.Commands!);

    // ---------- 1. MuJoCo auto-restart: no command row, replay reproduces ----------

    /// <summary>
    /// Audit FAIL seed 1 (auto restarts at ticks 336/395). The fix must keep the recorded
    /// trajectory identical while dropping the self-generated restart commands, so the
    /// replay no longer applies them a second time.
    /// </summary>
    [Fact]
    public void MujocoAutoRestart_IsNotRecordedAsCommand_AndReplaysBitForBit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Record(MujocoV1(seed: 1));

        // 回归覆盖前提: 该种子确实发生过自动重启, 否则本测试没有钉住缺陷。
        Assert.Contains(file.EventFingerprints, f => f.Contains("|Restart|"));
        Assert.Empty(CommandRows(file));

        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
        Assert.Equal(file.Ticks, report.Ticks);
        Assert.Equal(file.EventFingerprints.Count, report.EventCount);
    }

    // ---------- 2. legacy RestartPenalty: command tick == event tick ----------

    /// <summary>
    /// <c>restart:&lt;role&gt;:&lt;kind&gt;</c> is issued outside <c>Tick()</c>; it must be
    /// recorded on the row whose StepSimExt applies it, and its event must carry that row.
    /// </summary>
    [Fact]
    public void RestartPenalty_CommandTickEqualsEventTick_AndReplaysBitForBit()
    {
        var file = Record(Legacy(seed: 21), (engine, tick) =>
        {
            if (tick == 400)
            {
                engine.RestartPenalty(RoleNames.Us, "restart");
            }
            if (tick == 900)
            {
                engine.RestartPenalty(RoleNames.Them, "debug");
            }
        });

        var commands = CommandRows(file);
        Assert.Equal(new[] { 400L, 900L }, commands.Keys.OrderBy(t => t));
        Assert.Equal(new[] { "restart:us:restart" }, commands[400]);
        Assert.Equal(new[] { "restart:them:debug" }, commands[900]);

        var penaltyEvents = file.EventFingerprints
            .Select(f => f.Split('|'))
            .Where(p => p[2] == "RestartPenalty")
            .Select(p => long.Parse(p[1]))
            .ToList();
        Assert.Equal(new[] { 400L, 900L }, penaltyEvents);

        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
    }

    // ---------- 3. external real restart: command tick == event tick ----------

    [Fact]
    public void ExternalRestartRobot_CommandTickEqualsEventTick_AndReplaysBitForBit()
    {
        const long restartTick = 120;
        var file = Record(Legacy(seed: 42), (engine, tick) =>
        {
            if (tick == restartTick)
            {
                Assert.True(engine.RestartRobot(RoleNames.Us));
            }
        });

        var commands = CommandRows(file);
        Assert.Equal(new[] { restartTick }, commands.Keys);
        Assert.Equal(new[] { "restart_robot:us" }, commands[restartTick]);

        var restartEvent = file.EventFingerprints
            .Select(f => f.Split('|'))
            .Single(p => p[2] == "Restart");
        Assert.Equal(restartTick, long.Parse(restartEvent[1]));

        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
    }

    // ---------- 4. no-command anchor: the seed-42 baseline is untouched ----------

    /// <summary>
    /// The command-path fix must not perturb matches without commands: the checked-in
    /// seed-42 parity baseline still verifies bit-for-bit and contains no command rows.
    /// </summary>
    [Fact]
    public void NoCommandBaseline_StillReplaysBitForBit()
    {
        var file = ProtocolJson.Deserialize<ReplayFile>(
            File.ReadAllText(FindRepoFile(ParityFixturePath)));

        Assert.Empty(CommandRows(file));
        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
        Assert.Equal(2400, file.Ticks);
        Assert.Equal(211, file.EventFingerprints.Count);
    }
}
