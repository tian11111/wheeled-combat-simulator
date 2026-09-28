using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 回放播放时钟: 播放速度必须由固定 tickSeconds 决定, 而不是渲染帧数
/// (旧实现每渲染帧前进一 tick, 60Hz≈3× / 144Hz≈7.2× 播放)。
/// </summary>
public class ReplayPlaybackTests
{
    private static MatchSession SessionWithReplay(int ticks = 40)
    {
        var scenario = new Scenario { Seed = 7, Blocks = OfficialLayout.Blocks };
        var engine = new MatchEngine(scenario);
        engine.Arm();
        var events = new List<string>();
        Snapshot last = null!;
        for (var i = 0; i < ticks && !engine.Done; i++)
        {
            last = engine.Tick();
            events.AddRange(last.Events?.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}") ?? []);
        }

        var file = new ReplayFile
        {
            Scenario = scenario,
            Header = engine.BuildReplayHeader(),
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = last.DoneReason,
            EventFingerprints = events,
        };
        var session = new MatchSession(scenario);
        session.LoadReplay(file);
        return session;
    }

    [Fact]
    public void ReplayPlayback_AdvancesByFixedTickClockNotFrames()
    {
        var session = SessionWithReplay();
        Assert.True(session.ReplayCache.Count > 20, "fixture replay should have enough ticks");
        Assert.Equal(0, session.ReplayIndex);
        session.ReplayPlaying = true;

        // 0.5 s @ tickSeconds=0.05 ⇒ 恰好 10 tick, 与"调用次数(=渲染帧数)"无关。
        Assert.True(session.AdvanceReplayPlayback(0.5));
        Assert.Equal(10, session.ReplayIndex);

        // 零碎时间只累加不推进; 累计跨过一个 tick 才推进。
        Assert.False(session.AdvanceReplayPlayback(0.02));
        Assert.Equal(10, session.ReplayIndex);
        Assert.True(session.AdvanceReplayPlayback(0.04));  // 0.02 + 0.04 = 0.06 ⇒ +1 tick
        Assert.Equal(11, session.ReplayIndex);

        // 暂停时推进无效且累加清零。
        session.ReplayPlaying = false;
        Assert.False(session.AdvanceReplayPlayback(1.0));
        session.ReplayPlaying = true;
        Assert.False(session.AdvanceReplayPlayback(0.01));
        Assert.Equal(11, session.ReplayIndex);

        // 大 delta 追赶到末尾后自动停播、累加清零。
        session.AdvanceReplayPlayback(1000.0);
        Assert.True(session.ReplayAtEnd);
        Assert.False(session.ReplayPlaying);
        Assert.False(session.AdvanceReplayPlayback(0.05));  // 停播后不再推进
    }

    [Fact]
    public void ReplayClock_ResetsOnManualSeek()
    {
        var session = SessionWithReplay();
        session.ReplayPlaying = true;
        Assert.False(session.AdvanceReplayPlayback(0.04));  // 累加 0.04 (< 0.05)
        session.ReplaySeekTick(5);
        session.ResetReplayClock();
        Assert.False(session.AdvanceReplayPlayback(0.02));  // 残留已清: 0.02 仍不足一 tick
        Assert.Equal(4, session.ReplayIndex);               // ReplaySeekTick(5) 是 1-based → index 4
    }
}
