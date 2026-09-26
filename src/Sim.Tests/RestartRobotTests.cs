using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Real restart contract (task 08-28-godot-camera-gray-restart):
/// <see cref="MatchEngine.RestartRobot"/> returns the target to its scenario
/// start pose via <see cref="FieldTransform"/>, cleans motion/sensor/FSM
/// transients, awards the opponent exactly +3 once (2026 裁判同意重启), preserves the match clock
/// / other robot / blocks, and records the additive
/// <c>restart_robot:&lt;role&gt;</c> command. The legacy penalty-only
/// <c>restart:&lt;role&gt;:&lt;kind&gt;</c> replay commands must stay
/// byte-compatible. Also seeds/verifies the checked-in restart replay fixture.
/// </summary>
public sealed class RestartRobotTests
{
    private const string FixturePath = "src/Sim.Tests/fixtures/godot-parity-seed42.json";
    private const string FixtureName = "restart-replay-seed42.json";

    private static string FixtureFilePath()
    {
        // 仓库定位走一个必然存在的既有 fixture; 重启基线与其同目录。
        var known = FindRepoFile(FixturePath);
        return Path.Combine(Path.GetDirectoryName(known)!, FixtureName);
    }

    // ---------- helpers ----------

    private static Scenario FixedScenario(long seed = 42, Pose2? pose = null) => new()
    {
        Seed = seed,
        Field = new FieldParams { Pose = pose },
        Blocks = OfficialLayout.Blocks,
    };

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

    /// <summary>Arms and steps one tick with a zero action so the phase is RUNNING.</summary>
    private static MatchEngine RunningEngine(Scenario scenario)
    {
        var engine = new MatchEngine(scenario);
        engine.Tick(RobotAction.Zero); // 外部动作路径: PREP → RUNNING
        return engine;
    }

    private static List<string> CommandLog(MatchEngine engine)
        => engine.BuildReplayHeader().Ticks
            .SelectMany(t => t.Commands ?? [])
            .ToList();

    /// <summary>
    /// Deterministically records a full match with a mid-match real restart of
    /// each role (R on us at tick 300, T on them at tick 600). CreatedAt is
    /// pinned so the serialized fixture is byte-stable across runs.
    /// </summary>
    public static ReplayFile BuildRestartReplay(long seed = 42)
    {
        var scenario = FixedScenario(seed);
        var engine = new MatchEngine(scenario);
        engine.Arm();
        var fingerprints = new List<string>();
        for (long tick = 1; tick <= 2400 && !engine.Done; tick++)
        {
            if (tick == 300)
            {
                Assert.True(engine.RestartRobot(RoleNames.Us));
            }
            if (tick == 600)
            {
                Assert.True(engine.RestartRobot(RoleNames.Them));
            }
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

    // ---------- phase gating ----------

    [Fact]
    public void RestartRobot_InPrep_RejectsWithoutSideEffects()
    {
        var engine = new MatchEngine(FixedScenario());

        Assert.False(engine.RestartRobot(RoleNames.Us));

        Assert.Equal(0, engine.Scores.Us);
        Assert.Equal(0, engine.Scores.Them);
        Assert.Equal(0, engine.RestartPenalties.Us);
        Assert.Empty(engine.Events.Events);
        Assert.Empty(engine.BuildReplayHeader().Ticks);
        Assert.NotEqual(FsmState.MountRing, engine.Us.Fsm.State);
    }

    [Fact]
    public void RestartRobot_AfterFinished_RejectsWithoutSideEffects()
    {
        var engine = new MatchEngine(FixedScenario());
        var snapshots = engine.RunToEnd();
        var scoreUs = engine.Scores.Us;
        var scoreThem = engine.Scores.Them;
        var eventCount = engine.Events.Events.Count;

        Assert.True(engine.Done);
        Assert.False(engine.RestartRobot(RoleNames.Us));
        Assert.False(engine.RestartRobot(RoleNames.Them));

        Assert.Equal(scoreUs, engine.Scores.Us);
        Assert.Equal(scoreThem, engine.Scores.Them);
        Assert.Equal(eventCount, engine.Events.Events.Count);
        Assert.Equal(snapshots.Count, engine.TickIndex);
    }

    [Fact]
    public void RestartRobot_InPaused_IsAcceptedAndKeepsPaused()
    {
        var engine = RunningEngine(FixedScenario());
        engine.Pause("测试暂停");

        Assert.True(engine.RestartRobot(RoleNames.Them));
        Assert.Equal(MatchControlPhase.Paused, engine.Phase);
        Assert.True(engine.Paused);
        Assert.Equal(FsmState.MountRing, engine.Them.Fsm.State);
    }

    [Fact]
    public void RestartRobot_UnknownRole_Throws()
    {
        var engine = RunningEngine(FixedScenario());
        Assert.Throws<ArgumentException>(() => engine.RestartRobot("self"));
        Assert.Throws<ArgumentNullException>(() => engine.RestartRobot(null!));
    }

    // ---------- state contract ----------

    [Fact]
    public void RestartRobot_ResetsPoseThroughFieldTransform()
    {
        var pose = new Pose2 { X = 5, Y = -3, Th = Math.PI / 6 };
        var scenario = FixedScenario(seed: 11, pose: pose);
        var engine = RunningEngine(scenario);

        // 把目标机器人挪离出发点, 然后真实重启。
        engine.Us.X = 100.5;
        engine.Us.Y = -200.25;
        engine.Us.Th = 2.5;
        engine.Us.Vx = 1.5;
        engine.Us.Vy = -0.5;

        Assert.True(engine.RestartRobot(RoleNames.Us));

        var t = new FieldTransform(pose.X, pose.Y, pose.Th);
        var start = scenario.Field.Starts[RoleNames.Us];
        var (ex, ey) = t.LocalToWorldPoint(start.X, start.Y);
        Assert.Equal(ex, engine.Us.X);
        Assert.Equal(ey, engine.Us.Y);
        Assert.Equal(t.LocalToWorldHeading(start.Th), engine.Us.Th);
        Assert.Equal(0, engine.Us.Vx);
        Assert.Equal(0, engine.Us.Vy);
        Assert.Equal(0, engine.Us.V);
        Assert.Equal(0, engine.Us.W);
        Assert.Equal(0, engine.Us.ZG); // 出发点在走道, 平台外高度 0
        Assert.False(engine.Field.OnPlatform(engine.Us.X, engine.Us.Y));
    }

    [Fact]
    public void RestartRobot_CleansTransients_AndReentersMountRing()
    {
        var engine = RunningEngine(FixedScenario());
        var r = engine.Us;
        r.IsStalled = true;
        r.StallT = 3;
        r.WedgedFront = true;
        r.FrontLoad = 0.2;
        r.DropPending = true;
        r.Omega = 1;
        r.SpinOmega = 2;
        r.Pitch = 0.3;
        r.Roll = -0.2;
        r.CmdQueue.Enqueue((0.3, 0.1));
        r.CmdV = 0.3;
        r.CmdW = 0.1;
        r.Fsm.Manual = true;
        r.Fsm.State = FsmState.Attack;
        r.Fsm.DoneReason = "恢复次数超限 → 停车";
        r.Fsm.Rec.Count = 5;

        Assert.True(engine.RestartRobot(RoleNames.Us));

        Assert.False(r.IsStalled);
        Assert.Equal(0, r.StallT);
        Assert.False(r.WedgedFront);
        Assert.Equal(1, r.FrontLoad);
        Assert.False(r.DropPending);
        Assert.Equal(0, r.Omega);
        Assert.Equal(0, r.SpinOmega);
        Assert.Equal(0, r.Pitch);
        Assert.Equal(0, r.Roll);
        Assert.Empty(r.CmdQueue);
        Assert.Equal(0, r.CmdV);
        Assert.Equal(0, r.CmdW);
        // FSM: 全新子状态 + 武装 + MOUNT_RING (可复活已结束的机器人)。
        Assert.Equal(FsmState.MountRing, r.Fsm.State);
        Assert.True(r.Fsm.Armed);
        Assert.False(r.Fsm.Manual);
        Assert.Equal("", r.Fsm.DoneReason);
        Assert.Equal(0, r.Fsm.Rec.Count);
        Assert.Equal("scan", r.Fsm.Scan.Phase);
        Assert.Null(r.Fsm.ScoreTarget);
        Assert.Equal("posture", r.Fsm.Mount.Phase);
        // 传感器通道已重采样 (新位姿), 不再是旧滞后状态。
        Assert.NotEmpty(r.Sens);
        Assert.NotEmpty(r.RawSens);
    }

    [Fact]
    public void RestartRobot_AwardsOpponentExactlyThree_AndRecordsCommandOnce()
    {
        var engine = RunningEngine(FixedScenario());

        Assert.True(engine.RestartRobot(RoleNames.Us));
        Assert.Equal(3, engine.Scores.Them);  // 2026 规则: 裁判同意的重启 对手 +3, 且仅此一次
        Assert.Equal(0, engine.Scores.Us);
        Assert.Equal(3, engine.RestartPenalties.Us); // 被重启方计一次判罚
        Assert.Equal(0, engine.RestartPenalties.Them);
        // 记分牌明细: 重启来源 +3, 且与总分一致。
        Assert.Equal(3, engine.BuildSnapshot().ScoreBreakdown!["them"]["restart"]);

        var restarts = engine.Events.Events.Where(e => e.Kind == EventKind.Restart).ToList();
        Assert.Single(restarts);
        Assert.Equal(RoleNames.Us, restarts[0].Robot.Role);
        Assert.Equal("score", restarts[0].Cls);

        // 命令随下一个被提交的 tick 写入回放: 附加命令, 恰好一次。
        engine.Tick();
        Assert.Equal(new[] { "restart_robot:us" }, CommandLog(engine));
    }

    [Fact]
    public void RestartRobot_PreservesMatchTimer_OtherRobot_AndBlocks()
    {
        var engine = RunningEngine(FixedScenario());
        for (var i = 0; i < 100; i++)
        {
            engine.Tick();
        }
        var timerBefore = engine.MatchTimer;
        var themX = engine.Them.X;
        var themState = engine.Them.Fsm.State;
        var themTimer = engine.Them.Fsm.Timer;
        var blocks = engine.Blocks.Select(b => (b.X, b.Y, b.Out)).ToList();

        Assert.True(engine.RestartRobot(RoleNames.Us));

        Assert.Equal(timerBefore, engine.MatchTimer, precision: 9);
        Assert.Equal(themX, engine.Them.X);
        Assert.Equal(themState, engine.Them.Fsm.State);
        Assert.Equal(themTimer, engine.Them.Fsm.Timer, precision: 9);
        Assert.Equal(blocks, engine.Blocks.Select(b => (b.X, b.Y, b.Out)).ToList());
        // 目标机器人计时器接到当前比赛时钟 (不延长)。
        Assert.Equal(timerBefore, engine.Us.Fsm.Timer, precision: 9);
        // SimT (比赛已进行时间) 不回退。
        Assert.True(engine.Us.Fsm.SimT > 0);
    }

    [Fact]
    public void RestartRobot_FinishedTargetRevivableWhileMatchActive()
    {
        var engine = RunningEngine(FixedScenario());
        engine.Them.Fsm.State = FsmState.Finished; // 对手提前结束 (如恢复次数超限)

        Assert.True(engine.RestartRobot(RoleNames.Them));
        Assert.Equal(FsmState.MountRing, engine.Them.Fsm.State);
        Assert.True(engine.Them.Fsm.Armed);
        Assert.Equal("", engine.Them.Fsm.DoneReason);
    }

    [Fact]
    public void RestartRobot_ResumedTargetMountsAgain()
    {
        var engine = new MatchEngine(FixedScenario());
        engine.Arm();
        for (var i = 0; i < 120; i++)
        {
            engine.Tick(); // 双方已登台进入 SEARCH
        }
        Assert.True(engine.RestartRobot(RoleNames.Us));

        var restartSeq = engine.Events.Events.Single(e => e.Kind == EventKind.Restart).Seq;
        var mounted = false;
        for (var i = 0; i < 600 && !mounted; i++)
        {
            engine.Tick();
            mounted = engine.Events.Events.Any(e => e.Seq > restartSeq
                && e.Kind == EventKind.Mount && !e.Neutral && e.Robot.IsUs);
        }
        Assert.True(mounted, "restarted robot must mount the platform again");
    }

    // ---------- determinism ----------

    [Fact]
    public void RestartRobot_SameSchedule_ProducesBitIdenticalMatches()
    {
        static MatchEngine Play()
        {
            var engine = new MatchEngine(new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks });
            engine.Arm();
            for (long tick = 1; tick <= 700 && !engine.Done; tick++)
            {
                if (tick == 300)
                {
                    Assert.True(engine.RestartRobot(RoleNames.Us));
                }
                if (tick == 500)
                {
                    Assert.True(engine.RestartRobot(RoleNames.Them));
                }
                engine.Tick();
            }
            return engine;
        }

        var a = Play();
        var b = Play();
        var eventsA = a.Events.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.T:R}|{e.Kind}|{e.Cls}|{e.Msg}").ToList();
        var eventsB = b.Events.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.T:R}|{e.Kind}|{e.Cls}|{e.Msg}").ToList();
        Assert.Equal(eventsA, eventsB);
        Assert.Equal(ProtocolJson.Serialize(a.CommitSnapshot()), ProtocolJson.Serialize(b.CommitSnapshot()));
        Assert.Equal(a.Scores.Us, b.Scores.Us);
        Assert.Equal(a.Scores.Them, b.Scores.Them);
    }

    // ---------- replay fixture (checked in, regenerated byte-identically) ----------

    /// <summary>
    /// The checked-in restart fixture must equal the deterministic in-memory
    /// regeneration (same seed, restarts at ticks 300/600, pinned CreatedAt) —
    /// replay baselines are never hand-edited — and must parity-verify.
    /// </summary>
    [Fact]
    public void RestartReplayFixture_MatchesRegeneration_AndParityVerifies()
    {
        var path = EnsureFixture();

        var stored = File.ReadAllText(path);
        Assert.Equal(ProtocolJson.Serialize(BuildRestartReplay()), stored); // 基线漂移即失败 (禁止手改)

        var file = ProtocolJson.Deserialize<ReplayFile>(stored);
        Assert.Contains(file.Header.Ticks.SelectMany(t => t.Commands ?? []), c => c == "restart_robot:us");
        Assert.Contains(file.Header.Ticks.SelectMany(t => t.Commands ?? []), c => c == "restart_robot:them");

        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
        Assert.Equal(file.Ticks, report.Ticks);
        Assert.Equal(file.FinalScores.Us, report.Scores.Us);
        Assert.Equal(file.FinalScores.Them, report.Scores.Them);
        Assert.Equal(file.EventFingerprints.Count, report.EventCount);
    }

    /// <summary>Returns the fixture path, generating the deterministic baseline on first use.</summary>
    private static string EnsureFixture()
    {
        var path = FixtureFilePath();
        if (!File.Exists(path))
        {
            File.WriteAllText(path, ProtocolJson.Serialize(BuildRestartReplay()));
        }
        return path;
    }

    [Fact]
    public void RestartReplayFixture_ReplaysThroughMatchSession()
    {
        var file = ProtocolJson.Deserialize<ReplayFile>(File.ReadAllText(EnsureFixture()));
        var session = new MatchSession(new Scenario { Seed = 7, Blocks = OfficialLayout.Blocks });

        session.LoadReplay(file);
        Assert.Equal(SessionMode.Replay, session.Mode);
        Assert.Equal(file.EventFingerprints.Count, CountReplayEvents(session));

        session.ReplaySeekTick(session.ReplayCache.Count);
        var finalFrame = session.ReplayFrame(1);
        Assert.Equal(file.FinalScores.Us, finalFrame.Hud.ScoreUs);
        Assert.Equal(file.FinalScores.Them, finalFrame.Hud.ScoreThem);

        // F5: 重置回实况必须落在回放自带的场景上 (会话重置场景代表当前实况)。
        session.ResetToLive();
        Assert.Equal(SessionMode.Live, session.Mode);
        Assert.Equal(ProtocolJson.Serialize(file.Scenario), ProtocolJson.Serialize(session.Engine.Scenario));
    }

    private static int CountReplayEvents(MatchSession session)
        => Enumerable.Range(0, session.ReplayCache.Count)
            .Sum(i => session.ReplayCache[i].Events?.Count ?? 0);

    // ---------- legacy command compatibility ----------

    [Fact]
    public void LegacyRestartCommand_StaysPenaltyOnly_NoPoseReset()
    {
        var engine = RunningEngine(FixedScenario());
        engine.Arm();
        for (var i = 0; i < 60; i++)
        {
            engine.Tick();
        }
        var xBefore = engine.Us.X;
        var stateBefore = engine.Us.Fsm.State;
        var timerBefore = engine.MatchTimer;

        Assert.Equal(4, engine.RestartPenalty(RoleNames.Us, "restart"));
        Assert.Equal(xBefore, engine.Us.X);               // 旧命令只判罚, 不复位位姿
        Assert.Equal(stateBefore, engine.Us.Fsm.State);   // FSM 状态不变
        Assert.Equal(timerBefore, engine.MatchTimer, precision: 9); // 比赛时钟不动
        Assert.Equal(4, engine.Scores.Them);
        Assert.Equal(4, engine.RestartPenalties.Us);

        engine.Tick();
        Assert.Contains("restart:us:restart", CommandLog(engine));
        Assert.DoesNotContain(CommandLog(engine), c => c.StartsWith("restart_robot:", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyCommandReplay_StillVerifies_BitForBit()
    {
        // 用旧命令流录制一份回放并逐位验证: 新解码器绝不重解释旧字节。
        var scenario = new Scenario { Seed = 21, Blocks = OfficialLayout.Blocks };
        var engine = new MatchEngine(scenario);
        engine.Arm();
        var fingerprints = new List<string>();
        for (long tick = 1; tick <= 2400 && !engine.Done; tick++)
        {
            if (tick == 400)
            {
                engine.RestartPenalty(RoleNames.Us, "restart");
            }
            if (tick == 900)
            {
                engine.RestartPenalty(RoleNames.Them, "debug");
            }
            var snapshot = engine.Tick();
            if (snapshot.Events is { Count: > 0 })
            {
                fingerprints.AddRange(snapshot.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}"));
            }
        }
        var file = new ReplayFile
        {
            Scenario = scenario,
            Header = engine.BuildReplayHeader() with { CreatedAt = DateTimeOffset.UnixEpoch },
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = engine.Us.Fsm.DoneReason.Length > 0 ? engine.Us.Fsm.DoneReason : engine.Them.Fsm.DoneReason,
            EventFingerprints = fingerprints,
        };
        var commands = file.Header.Ticks.SelectMany(t => t.Commands ?? []).ToList();
        Assert.Contains("restart:us:restart", commands);
        Assert.Contains("restart:them:debug", commands);

        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
    }

    [Fact]
    public void OldOfficialFixture_StillParityVerifies()
    {
        var file = ProtocolJson.Deserialize<ReplayFile>(
            File.ReadAllText(FindRepoFile("src/Sim.Tests/fixtures/godot-parity-seed42.json")));
        var report = ParityCheck.Verify(file);
        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
    }
}
