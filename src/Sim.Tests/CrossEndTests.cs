using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Cross-end acceptance: the Godot shell's replay reconstruction and parity
/// verifier (pure C#, linked from godot/src) must reproduce a CLI-recorded
/// replay bit-for-bit. These tests run without the Godot editor/display.
/// </summary>
public sealed class CrossEndTests
{
    private const string FixturePath = "src/Sim.Tests/fixtures/godot-parity-seed42.json";

    /// <summary>
    /// 基线由 Sim.Cli 生成: `dotnet run --project src/Sim.Cli -- replay-record --seed 42
    /// --out replays/godot-parity-seed42.json` 后复制为测试 fixture。
    /// </summary>
    private static ReplayFile LoadParityBaseline()
    {
        var path = FindRepoFile(FixturePath);
        return ProtocolJson.Deserialize<ReplayFile>(File.ReadAllText(path));
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

    [Fact]
    public void Parity_VerifyCliRecordedBaseline_PassesBitForBit()
    {
        var file = LoadParityBaseline();
        var report = ParityCheck.Verify(file);

        Assert.True(report.Pass, report.Error ?? report.FirstDivergence);
        Assert.Equal(file.Ticks, report.Ticks);
        Assert.Equal(file.FinalScores.Us, report.Scores.Us);
        Assert.Equal(file.FinalScores.Them, report.Scores.Them);
        Assert.Equal(file.DoneReason, report.DoneReason);
        Assert.Equal(file.EventFingerprints.Count, report.EventCount);
        Assert.True(file.Ticks == 2400, "120 s baseline must run exactly 2400 ticks");
    }

    [Fact]
    public void Parity_TamperedFingerprint_ReportsDivergence()
    {
        var file = LoadParityBaseline();
        file = file with
        {
            EventFingerprints = [.. file.EventFingerprints.Take(0), "999|999|Forgery|score|fake", .. file.EventFingerprints.Skip(1)],
        };

        var report = ParityCheck.Verify(file);

        Assert.False(report.Pass);
        Assert.NotNull(report.FirstDivergence);
    }

    [Fact]
    public void MatchSession_LoadCliReplay_CachesEveryTickAndNavigates()
    {
        var file = LoadParityBaseline();
        var session = new MatchSession(new Scenario { Seed = 1 });

        session.LoadReplay(file);

        Assert.Equal(SessionMode.Replay, session.Mode);
        Assert.Equal(file.Ticks, session.ReplayCache.Count);
        Assert.Equal(0, session.ReplayIndex);

        // 单步前进/后退并在边界处钳制。
        Assert.True(session.ReplayStep(+1));
        Assert.Equal(1, session.ReplayIndex);
        Assert.True(session.ReplayStep(-1));
        Assert.Equal(0, session.ReplayIndex);
        Assert.False(session.ReplayStep(-1), "already at first frame");
        Assert.False(session.ReplayAtEnd);

        // 跳转到末帧。
        session.ReplaySeekTick(file.Ticks);
        Assert.True(session.ReplayAtEnd);
        Assert.Equal(file.Ticks, session.ReplayTickForIndex(session.ReplayIndex));
        var frame = session.ReplayFrame(1.0);
        Assert.Equal(file.FinalScores.Us, frame.Hud.ScoreUs);
        Assert.Equal(file.FinalScores.Them, frame.Hud.ScoreThem);
        Assert.True(frame.Hud.Done);
    }
}