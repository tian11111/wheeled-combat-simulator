using System.Text.Json;
using Sim.Core;
using Sim.Protocol;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// Regression suite for the deterministic match kernel (implement.md item 4):
/// referee scoring rules (block push-off, drops, score clock, inactivity,
/// restart penalties), mount/recover flow, determinism across engines and the
/// replay reproduction contract.
/// </summary>
public class MatchEngineTests(ITestOutputHelper output)
{
    // ---------- helpers ----------

    /// <summary>Official 2026 layout with frozen block coordinates for reproducibility.</summary>
    private static Scenario FixedScenario(long seed = 42) => new()
    {
        Seed = seed,
        Blocks = OfficialLayout.Blocks,
    };

    private static List<Snapshot> Run(MatchEngine engine, int maxTicks = 10_000)
    {
        engine.Arm();
        var snapshots = new List<Snapshot>();
        while (!engine.Done && snapshots.Count < maxTicks)
        {
            snapshots.Add(engine.Tick());
        }
        return snapshots;
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

    private static string EventFingerprint(CoreEvent e)
        => $"{e.Seq}|{e.Tick}|{e.T:R}|{e.Kind}|{(e.Neutral ? "-" : e.Robot.IsUs ? "us" : "them")}|{e.Cls}|{e.Msg}";

    private void AssertSameMatch(MatchEngine a, MatchEngine b)
    {
        var eventsA = a.Events.Events.Select(EventFingerprint).ToList();
        var eventsB = b.Events.Events.Select(EventFingerprint).ToList();
        if (eventsA.Count != eventsB.Count)
        {
            output.WriteLine($"event count mismatch: {eventsA.Count} vs {eventsB.Count}");
            DumpTail(a, b);
        }
        Assert.Equal(eventsA, eventsB);
        Assert.Equal(ProtocolJson.Serialize(a.CommitSnapshot()), ProtocolJson.Serialize(b.CommitSnapshot()));
        Assert.Equal(a.Scores.Us, b.Scores.Us);
        Assert.Equal(a.Scores.Them, b.Scores.Them);
    }

    private void DumpTail(MatchEngine a, MatchEngine b)
    {
        foreach (var (label, engine) in new[] { ("A", a), ("B", b) })
        {
            output.WriteLine($"--- engine {label} last events ---");
            foreach (var e in engine.Events.Events.TakeLast(8))
            {
                output.WriteLine($"  [{e.Seq}] t={e.T:0.0} {e.Kind}: {e.Msg}");
            }
            output.WriteLine($"  us=({engine.Us.X:R},{engine.Us.Y:R}) them=({engine.Them.X:R},{engine.Them.Y:R})");
        }
    }

    private static CoreEvent? SingleEvent(MatchEngine engine, EventKind kind)
        => engine.Events.Events.FirstOrDefault(e => e.Kind == kind);

    // ---------- full match ----------

    [Fact]
    public void FullMatch_TimeoutEnds_DoneWithReasonAndValidSnapshots()
    {
        var engine = new MatchEngine(FixedScenario());
        var snapshots = Run(engine);

        Assert.True(engine.Done);
        Assert.True(snapshots.Count <= 2400, $"match must end by timeout, ran {snapshots.Count} ticks");
        var last = snapshots[^1];
        Assert.Equal(MatchPhase.Done, last.Phase);
        Assert.Equal("比赛时间结束", last.DoneReason);
        Assert.Equal(0, last.Timer);

        foreach (var snapshot in snapshots.TakeLast(50))
        {
            Assert.Empty(snapshot.Validate());
        }
        Assert.All(engine.Events.Events, e => Assert.Empty(e.ToProtocolEvent().Validate()));
    }

    [Fact]
    public void DefaultMatchDuration_RunsExactly120Seconds()
    {
        var engine = new MatchEngine(FixedScenario(seed: 42));
        var snapshots = Run(engine);
        Assert.Equal(2400, snapshots.Count); // 120 s / 0.05 s
        Assert.Equal("比赛时间结束", snapshots[^1].DoneReason);
    }

    [Fact]
    public void CustomMatchDuration_HonorsScenarioField()
    {
        var scenario = FixedScenario(seed: 5) with
        {
            Field = FixedScenario(seed: 5).Field with { MatchDuration = 3 },
        };
        var engine = new MatchEngine(scenario);
        Assert.Equal(3, engine.MatchTimer, precision: 9);

        var snapshots = Run(engine);
        Assert.Equal(60, snapshots.Count); // 3 s / 0.05 s
        Assert.True(engine.Done);
        Assert.Equal("比赛时间结束", snapshots[^1].DoneReason);
    }

    // ---------- determinism ----------

    [Fact]
    public void Determinism_SameSeed_ProducesIdenticalStreams()
    {
        var a = Run(new MatchEngine(FixedScenario(seed: 42)));
        var b = Run(new MatchEngine(FixedScenario(seed: 42)));

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i += 100)
        {
            Assert.Equal(ProtocolJson.Serialize(a[i]), ProtocolJson.Serialize(b[i]));
        }
        Assert.Equal(ProtocolJson.Serialize(a[^1]), ProtocolJson.Serialize(b[^1]));

        var engineA = new MatchEngine(FixedScenario(seed: 42));
        var engineB = new MatchEngine(FixedScenario(seed: 42));
        Run(engineA);
        Run(engineB);
        AssertSameMatch(engineA, engineB);
    }

    [Fact]
    public void Determinism_DifferentSeeds_Diverge()
    {
        // Unfixed blocks force seeded referee placement to differ, so the
        // sensor-noise/scene streams must diverge between seeds.
        Scenario open(long seed) => new() { Seed = seed };
        var a = new MatchEngine(open(7));
        var b = new MatchEngine(open(42));
        Run(a, maxTicks: 1200);
        Run(b, maxTicks: 1200);

        var messagesA = a.Events.Events.Select(e => e.Msg).ToList();
        var messagesB = b.Events.Events.Select(e => e.Msg).ToList();
        Assert.NotEqual(messagesA, messagesB);
        Assert.True(Math.Abs(a.Us.X - b.Us.X) > 1e-9 || Math.Abs(a.Us.Y - b.Us.Y) > 1e-9,
            "different seeds must lead to different trajectories");
    }

    // ---------- mount ----------

    [Fact]
    public void Arm_MountsPlatform_AndEntersSearch()
    {
        var engine = new MatchEngine(FixedScenario());
        engine.Arm();

        var mounted = false;
        for (var i = 0; i < 600 && !engine.Done; i++)
        {
            engine.Tick();
            if (!mounted && engine.Us.WasOn)
            {
                mounted = true;
                var mountEvent = engine.Events.Events
                    .FirstOrDefault(e => e.Kind == EventKind.Mount && !e.Neutral && e.Robot.IsUs);
                Assert.NotNull(mountEvent);
                output.WriteLine($"mount at tick {i}: {mountEvent!.Msg}");
            }
        }

        Assert.True(mounted, "armed robot must mount the platform within 600 ticks");
        Assert.NotEqual(FsmState.MountRing, engine.Us.Fsm.State);
    }

    // ---------- block scoring ----------

    [Fact]
    public void BuffPushedOff_GainsThreePoints()
    {
        var scenario = new Scenario
        {
            Seed = 7,
            Blocks = [new() { Kind = BlockKind.Buff, X = 2.95, Y = 1.9 }],
        };
        var engine = new MatchEngine(scenario);
        engine.Us.X = 2.3;
        engine.Us.Y = 1.9;
        engine.Us.Th = 0;

        var drive = new RobotAction { V = 1.5 };
        for (var i = 0; i < 120 && engine.Scores.Us == 0; i++)
        {
            engine.Tick(drive);
        }

        var scored = SingleEvent(engine, EventKind.BlockScore);
        Assert.NotNull(scored);
        output.WriteLine($"buff score event: {scored!.Msg}");
        Assert.Contains("+3", scored.Msg);
        Assert.Equal(3, engine.Scores.Us);
        Assert.True(engine.Blocks[0].Out, "pushed-off buff must be out for the rest of the match");
    }

    [Fact]
    public void DebuffPushedOff_OpponentGainsSix()
    {
        var scenario = new Scenario
        {
            Seed = 7,
            Blocks = [new() { Kind = BlockKind.Debuff, X = 2.95, Y = 1.9 }],
        };
        var engine = new MatchEngine(scenario);
        engine.Us.X = 2.3;
        engine.Us.Y = 1.9;
        engine.Us.Th = 0;

        var drive = new RobotAction { V = 1.5 };
        for (var i = 0; i < 120 && engine.Scores.Them == 0; i++)
        {
            engine.Tick(drive);
        }

        var scored = SingleEvent(engine, EventKind.BlockScore);
        Assert.NotNull(scored);
        output.WriteLine($"debuff score event: {scored!.Msg}");
        Assert.Contains("对方 +6", scored.Msg);
        Assert.Equal(6, engine.Scores.Them);
        Assert.Equal(0, engine.Scores.Us);
    }

    // ---------- robot drops ----------

    [Fact]
    public void SimultaneousDrop_AwardsNoPoints()
    {
        var engine = new MatchEngine(FixedScenario(seed: 11));
        engine.Us.X = 2.75;
        engine.Us.Y = 1.9;
        engine.Us.Th = 0;
        engine.Them.X = 1.05;
        engine.Them.Y = 1.9;
        engine.Them.Th = Math.PI;

        var outward = new Dictionary<RobotRuntime, RobotAction> { [engine.Us] = new() { V = 1.5 }, [engine.Them] = new() { V = 1.5 } };
        for (var i = 0; i < 200; i++)
        {
            engine.Tick(outward.GetValueOrDefault(engine.Us), outward.GetValueOrDefault(engine.Them));
        }

        var simultaneous = SingleEvent(engine, EventKind.SimultaneousDrop);
        Assert.NotNull(simultaneous);
        output.WriteLine($"simultaneous drop: {simultaneous!.Msg}");
        Assert.Equal(0, engine.Scores.Us);
        Assert.Equal(0, engine.Scores.Them);
    }

    // ---------- inactivity ----------

    [Fact]
    public void Inactivity_ManualStationaryOnStage_OpponentGainsOneOnce()
    {
        var engine = new MatchEngine(FixedScenario(seed: 13));
        engine.Us.X = 1.9;
        engine.Us.Y = 1.9;
        engine.Us.Th = 0;

        for (var i = 0; i < 220; i++)
        {
            engine.Tick(RobotAction.Zero);
        }

        var inactivityEvents = engine.Events.Events.Where(e => e.Kind == EventKind.Inactivity).ToList();
        Assert.Single(inactivityEvents);
        Assert.False(inactivityEvents[0].Neutral);
        Assert.True(inactivityEvents[0].Robot.IsUs, "inactivity penalty must be charged to the stationary us robot");
        output.WriteLine($"inactivity event: {inactivityEvents[0].Msg}");
        // 消极判罚给对方 +1；同时我方独占擂台读秒在 10s 处也给我方 +1（遗留规则并存）。
        Assert.Equal(1, engine.Scores.Them);
        Assert.Equal(1, engine.Scores.Us);
    }

    // ---------- restart penalty ----------

    [Fact]
    public void RestartPenalty_Debug3_Restart4_RecordedInReplay()
    {
        var engine = new MatchEngine(FixedScenario());

        Assert.Equal(3, engine.RestartPenalty(RoleNames.Us));
        Assert.Equal(4, engine.RestartPenalty(RoleNames.Them, "restart"));
        Assert.Equal(3, engine.RestartPenalties.Us);
        Assert.Equal(4, engine.RestartPenalties.Them);
        Assert.Equal(4, engine.Scores.Us);   // them's restart penalty → us +4
        Assert.Equal(3, engine.Scores.Them); // us's debug penalty → them +3

        // 外部动作把引擎推进 RUNNING，挂起的判罚命令才会写入回放（PREP 早退路径不记录）。
        engine.Tick(RobotAction.Zero);
        var header = engine.BuildReplayHeader();
        var commands = header.Ticks.SelectMany(t => t.Commands ?? []).ToList();
        Assert.Equal(new[] { "restart:us:debug", "restart:them:restart" }, commands);

        var penalties = engine.Events.Events.Where(e => e.Kind == EventKind.RestartPenalty).ToList();
        Assert.Equal(2, penalties.Count);
    }

    // ---------- observations & fallbacks ----------

    [Fact]
    public void Observation_RequestIdsMonotonic_SensorsAndObjectsPresent()
    {
        var engine = new MatchEngine(FixedScenario());

        var first = engine.BuildObservation(engine.Us);
        var second = engine.BuildObservation(engine.Us);
        var third = engine.BuildObservation(engine.Them);
        Assert.Equal(1, first.RequestId);
        Assert.Equal(2, second.RequestId);
        Assert.Equal(3, third.RequestId);
        Assert.Equal(RoleNames.Them, third.Role);

        Assert.Equal("WAIT_START", first.Robot.State);
        Assert.False(first.Robot.OnPlatform);
        Assert.Equal(120, first.Timer, precision: 5);
        Assert.NotNull(first.Opponent);

        Assert.NotNull(first.RawSensors);
        foreach (var channel in new[] { "gF", "gB", "gL", "gR", "uL", "uR", "sFL", "sFR", "dLF", "dRF", "dLB", "dRB", "f", "r" })
        {
            Assert.True(first.RawSensors.ContainsKey(channel), $"legacy channel '{channel}' missing from rawSensors");
        }
        Assert.NotNull(first.Objects);
        Assert.Equal(2, first.Objects.Buffs.Count);
        Assert.NotNull(first.Objects.Debuff);
    }

    [Fact]
    public void NonFiniteAction_FallsBackToZero_NotRecordedInReplay()
    {
        var engine = new MatchEngine(FixedScenario());
        engine.Arm();
        var startX = engine.Us.X;

        for (var i = 0; i < 30; i++)
        {
            engine.Tick(new RobotAction { V = double.NaN, W = double.PositiveInfinity });
        }

        Assert.Equal(startX, engine.Us.X, precision: 9);
        Assert.Equal(0, engine.Us.V, precision: 9);
        Assert.Empty(engine.BuildReplayHeader().Ticks);

        var obs = engine.BuildObservation(engine.Us);
        var second = engine.BuildObservation(engine.Us);
        Assert.Equal(1, obs.RequestId);
        Assert.Equal(2, second.RequestId);
    }

    // ---------- replay reproduction ----------

    [Fact]
    public void ReplayRecordedActions_ReproduceIdenticalOutcome()
    {
        // Engine A: scripted manual match; record accepted actions per tick.
        const int scriptTicks = 400;
        var recorded = new Dictionary<long, RobotAction>();
        var engineA = new MatchEngine(FixedScenario(seed: 21));
        for (var t = 0; t < scriptTicks && !engineA.Done; t++)
        {
            // A varied but deterministic script: forward with sinusoidal turn.
            var action = new RobotAction { V = 1.2 + 0.3 * Math.Sin(t / 17.0), W = 1.5 * Math.Sin(t / 9.0) };
            engineA.Tick(action, null);
            recorded[t + 1] = action.ClampTo(engineA.Us.Vehicle);
        }

        // Engine B: replay strictly from the recorded stream.
        var engineB = new MatchEngine(FixedScenario(seed: 21));
        for (var t = 1; t <= scriptTicks && !engineB.Done; t++)
        {
            recorded.TryGetValue(t, out var action);
            engineB.Tick(action, null);
        }

        AssertSameMatch(engineA, engineB);
    }

    // ---------- scenario fixture ----------

    [Fact]
    public void OfficialScenarioFixture_LoadsValidatesAndRunsDeterministically()
    {
        var path = FindRepoFile(Path.Combine("scenarios", "wushu-ring-2026.json"));
        var scenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(path));
        Assert.Empty(scenario.Validate());
        Assert.Equal(42, scenario.Seed);
        Assert.Equal(3, scenario.Blocks.Count);

        var a = new MatchEngine(scenario);
        var b = new MatchEngine(scenario);
        for (var i = 0; i < 400; i++)
        {
            a.Tick();
            b.Tick();
            if (i % 100 == 0)
            {
                Assert.Equal(ProtocolJson.Serialize(a.CommitSnapshot()), ProtocolJson.Serialize(b.CommitSnapshot()));
            }
        }
        Assert.Equal(
            a.Events.Events.Select(EventFingerprint),
            b.Events.Events.Select(EventFingerprint));
    }
}
