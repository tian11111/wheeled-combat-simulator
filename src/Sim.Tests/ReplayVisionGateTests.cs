using Sim.Core;
using Sim.GodotShell;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 决策⑥ recording/reproduction gate: a live-bridge session must be refused as an
/// ordinary action-stream replay (its frame arrival depends on external timing)
/// with a message that points at the sidecar evidence package; the default and
/// visionReplay paths must stay untouched (three-state coverage).
/// </summary>
public class ReplayVisionGateTests
{
    private static Scenario FixedScenario(long seed = 42) => new()
    {
        Seed = seed,
        Blocks = OfficialLayout.Blocks,
        // 门禁只关心 header 与复现语义: 用短场次(6 s)而不是整场 120 s, 避免拖慢全量回归。
        Field = new FieldParams { MatchDuration = 6 },
    };

    private static ReplayFile RecordedMatch(IVisionAdapter? adapter = null)
    {
        using var engine = MatchEngineHost.Create(FixedScenario(), adapter);
        engine.Arm();
        var events = new List<string>();
        Snapshot last = null!;
        while (!engine.Done)
        {
            last = engine.Tick();
            events.AddRange(last.Events?.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}") ?? []);
        }
        return new ReplayFile
        {
            Scenario = FixedScenario(),
            Header = engine.BuildReplayHeader(),
            Ticks = engine.TickIndex,
            FinalScores = engine.Scores,
            DoneReason = last.DoneReason,
            EventFingerprints = events,
        };
    }

    /// <summary>Always-fresh single-frame stream: enough frames for a whole match.</summary>
    private static LiveVisionBridge FreshBridge()
    {
        var frames = Enumerable.Range(0, 3000)
            .Select(i => new VisionReplayFrame
            {
                Sequence = 100 + i,
                TimestampMs = i * 100.0,
                Status = "target",
                SelectedTargetIndex = 0,
                Detections = [new VisionReplayFrameDetection { Label = "buff", Confidence = 0.8 }],
            })
            .ToList();
        return new LiveVisionBridge(new ListSource(frames), LiveVisionBridge.DefaultMaxAgeMs);
    }

    private sealed class ListSource(IReadOnlyList<VisionReplayFrame> frames) : IVisionStreamSource
    {
        private readonly List<VisionReplayFrame> _released = [];
        private readonly double _firstMs = frames[0].TimestampMs;
        private int _cursor;

        public IReadOnlyList<VisionReplayFrame> Released => _released;

        public int PumpUntil(double simTimeSeconds)
        {
            var added = 0;
            while (_cursor < frames.Count && frames[_cursor].TimestampMs <= _firstMs + simTimeSeconds * 1000.0)
            {
                _released.Add(frames[_cursor++]);
                added++;
            }
            return added;
        }
    }

    [Fact]
    public void LiveBridgeSession_WritesItsOwnVisionMode()
    {
        var file = RecordedMatch(FreshBridge());
        Assert.Equal("liveBridge", file.Header.VisionMode);
        Assert.Equal(LiveVisionBridge.ModeName, file.Header.VisionMode);
        // live 场次没有动作流之外的证据身份: 证据 id/sha 只属于 visionReplay 包。
        Assert.Null(file.Header.VisionEvidenceId);
        Assert.Null(file.Header.VisionEvidenceSha256);
        Assert.Empty(file.Header.Validate());
    }

    [Fact]
    public void InjectedEngine_LiveBridgePerceptionReportsItsMode()
    {
        using var engine = MatchEngineHost.Create(FixedScenario(), FreshBridge());
        engine.Arm();
        var snapshot = engine.Tick();
        Assert.Equal("liveBridge", snapshot.Perception!.Vision!.Mode);
        Assert.Null(snapshot.Perception.Vision.ClassifyRate);
    }

    [Theory]
    [InlineData("liveBridge", false)]
    [InlineData("visionReplay", true)]
    [InlineData("default", true)]
    public void EnsureRecordable_RefusesOnlyLiveBridge(string mode, bool recordable)
    {
        var header = new ReplayHeader { VisionMode = mode };
        if (recordable)
        {
            MatchEngineHost.EnsureRecordable(header);
            return;
        }
        var refusal = Assert.Throws<InvalidOperationException>(() => MatchEngineHost.EnsureRecordable(header));
        Assert.Contains("liveBridge", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("sidecar", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("vision evaluate --evidence", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForReplay_RefusesLiveBridgeSessionWithSidecarGuidance()
    {
        var file = RecordedMatch(FreshBridge());
        var refusal = Assert.Throws<InvalidOperationException>(() => MatchEngineHost.CreateForReplay(file));
        Assert.Contains("liveBridge", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("sidecar", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParityCheck_FailsLiveBridgeSessionWithSidecarGuidance()
    {
        // godot 载入回放/parity 共用 CreateForReplay; ParityCheck 契约是"永不抛",
        // 因此这里必须以失败报告 + 指路信息拒绝。
        var report = ParityCheck.Verify(RecordedMatch(FreshBridge()));
        Assert.False(report.Pass);
        Assert.NotNull(report.Error);
        Assert.Contains("liveBridge", report.Error!, StringComparison.Ordinal);
        Assert.Contains("sidecar", report.Error!, StringComparison.Ordinal);
        Assert.Contains("vision evaluate --evidence", report.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForReplay_StillAcceptsDefaultAndReplaySessions()
    {
        // 默认路径: 录制 → 复现 → parity 逐位 PASS(门禁不得误伤)。
        var plain = RecordedMatch();
        Assert.Equal("default", plain.Header.VisionMode);
        MatchEngineHost.EnsureRecordable(plain.Header);
        using (var engine = MatchEngineHost.CreateForReplay(plain))
        {
            Assert.NotNull(engine);
        }
        Assert.True(ParityCheck.Verify(plain).Pass, "default 录制路径被门禁误伤");

        // visionReplay 路径: 注入回放适配器录制的场次同样不被拒绝(其复现靠证据包,
        // 但门禁只管 liveBridge)。
        var replayFile = RecordedMatch(FreshReplayAdapter(out _));
        Assert.Equal("visionReplay", replayFile.Header.VisionMode);
        MatchEngineHost.EnsureRecordable(replayFile.Header);
        using (var engine = MatchEngineHost.CreateForReplay(replayFile))
        {
            Assert.NotNull(engine);
        }
    }

    private static VisionReplayAdapter FreshReplayAdapter(out string evidenceId)
    {
        var frames = Enumerable.Range(0, 3000)
            .Select(i => new VisionReplayFrame
            {
                Sequence = 100 + i,
                TimestampMs = i * 100.0,
                Status = "target",
                SelectedTargetIndex = 0,
                Detections = [new VisionReplayFrameDetection { Label = "buff", Confidence = 0.8 }],
            })
            .ToList();
        evidenceId = "vr-gatetest000000";
        return new VisionReplayAdapter(
            frames, evidenceId, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 500);
    }
}
