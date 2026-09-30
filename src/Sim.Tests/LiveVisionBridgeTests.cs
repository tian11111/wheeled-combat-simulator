using System.Text.Json;
using Sim.Core;
using Sim.Protocol;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// LiveVisionBridge contract tests (R1): SimT 0 = the source session's first
/// frame, the closed staleness window, explicit unknown reasons, RNG-free and
/// world-truth-free consumption, and the JSONL contract whose field names mirror
/// the real-car MBri CSV columns. The full equivalence gate (CSV stream + whole
/// match, bit-identical ledger) belongs to the next batch; the engine-level checks
/// here already pin the shared selector for a synthetic stream.
/// </summary>
public class LiveVisionBridgeTests(ITestOutputHelper output)
{
    private const string EvidenceId = "vr-livebridge00000";
    private const string EvidenceSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // ---------- fixtures ----------

    private static VisionReplayFrameDetection Det(string label, double confidence = 0.9, double offsetX = 0.25)
        => new() { Label = label, Confidence = confidence, OffsetX = offsetX };

    private static VisionReplayFrame Frame(long sequence, double timestampMs, string status,
        VisionReplayFrameDetection[]? detections = null, int? selected = null, string? error = null)
        => new()
        {
            Sequence = sequence,
            TimestampMs = timestampMs,
            Status = status,
            Error = error,
            SelectedTargetIndex = selected,
            Detections = detections ?? [],
        };

    private static VisionContext Context(string role, double simT, Func<double>? rng = null)
        => new()
        {
            T = simT,
            Role = role,
            Robot = new RobotRuntime { Role = role, Name = role },
            // World truth deliberately CONTRADICTS the stream: the bridge must
            // never use it to fabricate answers.
            Target = new VisionTargetInfo { Kind = "debuff", Name = "减益块", D = 1, Rel = "正前" },
            Opponent = new RobotRuntime { Role = RoleNames.Them, Name = "对手" },
            Random = rng,
        };

    /// <summary>Stream: target(buff)@0, no_target@200, error@400, no_data_or_stale@600, target-no-selection@800.</summary>
    private static List<VisionReplayFrame> MatrixFrames() =>
    [
        Frame(1, 0, "target", [Det("buff")], selected: 0),
        Frame(2, 200, "no_target"),
        Frame(3, 400, "error", error: "inference failed"),
        Frame(4, 600, "no_data_or_stale"),
        Frame(5, 800, "target", [Det("debuff")], selected: null),
    ];

    /// <summary>Always-fresh stream (100 ms cadence for ~300 s) so every classify serves a real detection.</summary>
    private static List<VisionReplayFrame> FreshFrames(long seedBase = 100)
        => Enumerable.Range(0, 3000).Select(i => Frame(
            seedBase + i, i * 100.0, "target",
            [Det(i % 5 == 4 ? "debuff" : "buff", 0.8)], selected: 0)).ToList();

    /// <summary>
    /// Test double: releases each frame once the pumped SimT reaches its arrival
    /// time. <see cref="FromFrames"/> applies the CSV-source scaling (arrival =
    /// (timestampMs − session first frame)/1000 s); explicit schedules drive the
    /// pathological cases (delayed first frame, out-of-order release, re-received
    /// frame).
    /// </summary>
    private sealed class StreamSource(IReadOnlyList<(double Arrival, VisionReplayFrame Frame)> schedule)
        : IVisionStreamSource
    {
        private readonly List<VisionReplayFrame> _released = [];
        private int _cursor;

        public static StreamSource FromFrames(IReadOnlyList<VisionReplayFrame> frames)
        {
            var first = frames.Count > 0 ? frames[0].TimestampMs : 0;
            return new StreamSource(frames.Select(f => ((f.TimestampMs - first) / 1000.0, f)).ToList());
        }

        /// <summary>Pump calls seen by the source, in order (lazy-pump proof).</summary>
        public List<double> Pumps { get; } = [];

        public int LastReleased { get; private set; }

        public IReadOnlyList<VisionReplayFrame> Released => _released;

        public int PumpUntil(double simTimeSeconds)
        {
            Pumps.Add(simTimeSeconds);
            var added = 0;
            while (_cursor < schedule.Count && schedule[_cursor].Arrival <= simTimeSeconds)
            {
                _released.Add(schedule[_cursor].Frame);
                _cursor++;
                added++;
            }
            LastReleased = added;
            return added;
        }
    }

    private static (string Label, string Source) ResultOf(LiveVisionBridge bridge, double simT)
    {
        var detection = bridge.Classify(Context(RoleNames.Us, simT));
        return (detection.Label, detection.Source);
    }

    /// <summary>All ledger fields except the mode/source name, as one comparable line.</summary>
    private static string Dump(VisionReplayConsumeRecord record)
        => $"{record.Role}|{record.SimT:R}|{record.FrameSequence?.ToString() ?? "-"}"
            + $"|{record.AgeMs?.ToString("R") ?? "-"}|{record.Reason ?? "-"}|{record.Label}|{record.Confidence:R}";

    // ---------- time base / staleness ----------

    [Fact]
    public void Classify_EmptyStream_IsNoFrameAndReleasesNothing()
    {
        var source = StreamSource.FromFrames([]);
        var bridge = new LiveVisionBridge(source, 500);

        var detection = bridge.Classify(Context(RoleNames.Us, 3.0));
        Assert.Equal("unknown", detection.Label);
        Assert.Equal("no_frame", detection.Source);
        Assert.Equal(0, detection.Confidence);
        Assert.Empty(bridge.ReleasedFrames);

        var record = Assert.Single(bridge.Consumes);
        Assert.Equal(RoleNames.Us, record.Role);
        Assert.Equal(3.0, record.SimT);
        Assert.Equal("no_frame", record.Reason);
        Assert.Null(record.FrameSequence);
        Assert.Null(record.AgeMs);
    }

    [Fact]
    public void Classify_BeforeTheFirstFrameArrives_IsNoFrameThenAnchorsSimTZero()
    {
        // 源尚未交付任何帧（外部进程源启动初期）：no_frame，不猜、不伪造。
        var source = new StreamSource([(0.5, Frame(7, 1_786_931_530_037, "target", [Det("buff")], selected: 0))]);
        var bridge = new LiveVisionBridge(source, maxAgeMs: 1000);

        Assert.Equal("no_frame", bridge.Classify(Context(RoleNames.Us, 0.2)).Source);
        Assert.Empty(bridge.ReleasedFrames);

        // 首个交付帧即会话锚点（它的 epoch 就是 SimT 0）：桥的时钟 tMs = 锚点 + T*1000，
        // 于是它在 SimT 0.5 首次被服务时年龄 = 500ms。源必须与帧时间戳同一条时间轴释放
        // （CSV 源：到达 =（ts − 首帧）/1000；1x 真推理源：墙钟），否则年龄带恒定偏移。
        var detection = bridge.Classify(Context(RoleNames.Us, 0.5));
        Assert.Equal("buff", detection.Label);
        Assert.Single(bridge.ReleasedFrames);
        Assert.Equal(7, bridge.Consumes[1].FrameSequence);
        Assert.Equal(500.0, bridge.Consumes[1].AgeMs);
    }

    [Fact]
    public void Classify_SessionFirstFrameIsSimTZero()
    {
        // 到达时刻 = (vision_timestamp_ms − 首帧)/1000（CSV 源的 SimT 缩放语义）。
        var source = StreamSource.FromFrames(
        [
            Frame(1, 1_786_931_530_037, "target", [Det("buff")], selected: 0),
            Frame(2, 1_786_931_530_237, "target", [Det("debuff")], selected: 0),
        ]);
        var bridge = new LiveVisionBridge(source, 500);

        // SimT 0.1：只有首帧到达（它的 epoch 就是 SimT 0），age 100ms。
        Assert.Equal("buff", bridge.Classify(Context(RoleNames.Us, 0.1)).Label);
        Assert.Equal(100.0, bridge.Consumes[0].AgeMs);
        Assert.Equal(1, bridge.Consumes[0].FrameSequence);

        // SimT 0.25：第二帧（到达 0.2）成为窗内最新帧，age 50ms。
        Assert.Equal("debuff", bridge.Classify(Context(RoleNames.Us, 0.25)).Label);
        Assert.Equal(50.0, bridge.Consumes[1].AgeMs);
        Assert.Equal(2, bridge.Consumes[1].FrameSequence);
    }

    [Fact]
    public void Classify_StalenessWindowIsClosedAtMaxAge()
    {
        var source = StreamSource.FromFrames([Frame(1, 0, "target", [Det("buff")], selected: 0)]);
        var bridge = new LiveVisionBridge(source, maxAgeMs: 500);

        // age == maxAgeMs 仍在窗内（相机缓存的最后有效帧）；只有严格更旧才 stale。
        Assert.Equal("buff", bridge.Classify(Context(RoleNames.Us, 0.5)).Label);
        Assert.Equal(500.0, bridge.Consumes[0].AgeMs);

        var stale = bridge.Classify(Context(RoleNames.Us, 0.5001));
        Assert.Equal("unknown", stale.Label);
        Assert.Equal("stale", stale.Source);
        // stale 记录仍如实带帧号与年龄（审计用），只是标签退回 unknown。
        Assert.Equal(1, bridge.Consumes[1].FrameSequence);
        Assert.Equal("stale", bridge.Consumes[1].Reason);
        Assert.True(bridge.Consumes[1].AgeMs > 500.0);
    }

    [Fact]
    public void Classify_ServesNewestInWindowAndMapsFaultsToExplicitReasons()
    {
        var bridge = new LiveVisionBridge(StreamSource.FromFrames(MatrixFrames()), 500);

        var served = bridge.Classify(Context(RoleNames.Us, 0.05));
        Assert.Equal("buff", served.Label);
        Assert.Equal(0.9, served.Confidence);
        Assert.Equal("liveBridge", served.Source);
        Assert.Equal(0.25, served.OffsetX);

        Assert.Equal(("unknown", "no_target"), ResultOf(bridge, 0.25));
        Assert.Equal(("unknown", "error"), ResultOf(bridge, 0.45));
        Assert.Equal(("unknown", "stale"), ResultOf(bridge, 0.65)); // no_data_or_stale → stale
        Assert.Equal(("unknown", "no_selection"), ResultOf(bridge, 0.85));
        // 流结束后不得循环或伪造：窗口外一律 stale。
        Assert.Equal(("unknown", "stale"), ResultOf(bridge, 30.0));
    }

    [Fact]
    public void Classify_OutOfRangeSelection_IsNoSelection()
    {
        var frames = new List<VisionReplayFrame> { Frame(1, 0, "target", [Det("buff")], selected: 3) };
        var bridge = new LiveVisionBridge(StreamSource.FromFrames(frames), 500);

        Assert.Equal(("unknown", "no_selection"), ResultOf(bridge, 0.1));
        Assert.Equal(1, bridge.Consumes[0].FrameSequence);
        Assert.Equal(100.0, bridge.Consumes[0].AgeMs);
    }

    [Fact]
    public void Classify_RepeatedCalls_ServeTheSameFrameLikeACameraCache()
    {
        var bridge = new LiveVisionBridge(StreamSource.FromFrames(MatrixFrames()), 500);
        var a = bridge.Classify(Context(RoleNames.Us, 0.05));
        var b = bridge.Classify(Context(RoleNames.Us, 0.15));
        Assert.Equal(a.Label, b.Label);
        // Both calls consumed frame 1 (the camera cache keeps serving it).
        Assert.Equal(2, bridge.Consumes.Count(c => c.FrameSequence == 1));
        Assert.Equal(2, bridge.Consumes.Count);
    }

    [Fact]
    public void Classify_TracksRolesIndependently()
    {
        var bridge = new LiveVisionBridge(StreamSource.FromFrames(MatrixFrames()), 500);
        _ = bridge.Classify(Context(RoleNames.Us, 0.05));
        _ = bridge.Classify(Context(RoleNames.Them, 0.85));
        Assert.Equal(1, bridge.LastByRole[RoleNames.Us].FrameSequence);
        Assert.Equal(5, bridge.LastByRole[RoleNames.Them].FrameSequence);
        Assert.Equal(2, bridge.Consumes.Count);
    }

    [Fact]
    public void Classify_IgnoresWorldTruth_AnswerComesFromTheStreamOnly()
    {
        // Context.Target 说 debuff，流里的帧说 buff：桥必须交付流里的标签。
        var bridge = new LiveVisionBridge(StreamSource.FromFrames(MatrixFrames()), 500);
        Assert.Equal("buff", bridge.Classify(Context(RoleNames.Us, 0.05)).Label);
    }

    [Fact]
    public void Classify_NeverConsumesTheRandomStream()
    {
        var bridge = new LiveVisionBridge(StreamSource.FromFrames(FreshFrames()), 500);
        var draws = 0;
        for (var i = 0; i < 20; i++)
        {
            _ = bridge.Classify(Context(RoleNames.Us, 0.05 * i, rng: () =>
            {
                draws++;
                return 0.5;
            }));
        }
        Assert.Equal(0, draws);
    }

    // ---------- pump / working set ----------

    [Fact]
    public void Classify_LazilyPumpsTheSourceUpToSimT()
    {
        var source = StreamSource.FromFrames(MatrixFrames());
        var bridge = new LiveVisionBridge(source, 500);

        _ = bridge.Classify(Context(RoleNames.Us, 0.25));
        Assert.Equal(new[] { 0.25 }, source.Pumps); // 只有 classify 驱动源（lazy pump）
        Assert.Equal(2, source.LastReleased);       // 到达时刻 ≤ 0.25 的帧：0 与 0.2
        Assert.Equal(2, bridge.ReleasedFrames.Count);

        _ = bridge.Classify(Context(RoleNames.Us, 0.45));
        Assert.Equal(new[] { 0.25, 0.45 }, source.Pumps);
        Assert.Equal(3, bridge.ReleasedFrames.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, bridge.ReleasedFrames.Select(f => f.Sequence).ToArray());
    }

    [Fact]
    public void ReleasedFrames_AreHeldSortedByTimestampThenSequence()
    {
        // 乱序交付（同一批里先到 ts=400 再到 ts=0）：工作集仍按 (TimestampMs, Sequence)
        // 升序，与 VisionReplayAdapter 对证据包的排序口径一致。
        var source = new StreamSource(
        [
            (0.0, Frame(2, 400, "target", [Det("debuff")], selected: 0)),
            (0.0, Frame(1, 0, "target", [Det("buff")], selected: 0)),
        ]);
        var bridge = new LiveVisionBridge(source, 500);

        Assert.Equal("buff", bridge.Classify(Context(RoleNames.Us, 0.2)).Label);
        Assert.Equal(200.0, bridge.Consumes[0].AgeMs);
        // 乱序交付不改写工作集顺序：仍按 (TimestampMs, Sequence) 升序。
        Assert.Equal(new long[] { 1, 2 }, bridge.ReleasedFrames.Select(f => f.Sequence).ToArray());
        Assert.Equal("debuff", bridge.Classify(Context(RoleNames.Us, 0.45)).Label);
        Assert.Equal(50.0, bridge.Consumes[1].AgeMs);

        // 同一时间戳两帧：取 (时间戳, 帧号) 最大的那帧（回放适配器 ThenBy(sequence) 口径）。
        var tie = new StreamSource(
        [
            (0.0, Frame(3, 0, "target", [Det("debuff")], selected: 0)),
            (0.0, Frame(1, 0, "target", [Det("buff")], selected: 0)),
            (0.0, Frame(2, 0, "target", [Det("buff")], selected: 0)),
        ]);
        var tieBridge = new LiveVisionBridge(tie, 500);
        Assert.Equal("debuff", tieBridge.Classify(Context(RoleNames.Us, 0.0)).Label);
        Assert.Equal(3, tieBridge.Consumes[0].FrameSequence);
        Assert.Equal(new long[] { 1, 2, 3 }, tieBridge.ReleasedFrames.Select(f => f.Sequence).ToArray());
    }

    [Fact]
    public void ReleasedFrames_RepeatedReceivesKeepTheFirstDelivery()
    {
        // 同一 (时间戳, 帧号) 的重复接收保留首次交付（与导入 collapse 规则一致），
        // 不改写 FSM 已经吃过的帧内容。
        var source = new StreamSource(
        [
            (0.0, Frame(1, 1000, "target", [Det("buff")], selected: 0)),
            (0.0, Frame(1, 1000, "target", [Det("debuff")], selected: 0)),
        ]);
        var bridge = new LiveVisionBridge(source, 500);

        Assert.Equal("buff", bridge.Classify(Context(RoleNames.Us, 0.0)).Label);
        Assert.Equal(1, bridge.DuplicateFrames);
        var frame = Assert.Single(bridge.ReleasedFrames);
        Assert.Equal("buff", frame.Detections[0].Label);
    }

    // ---------- equivalence with the reference adapter ----------

    [Fact]
    public void Classify_MatchesTheReplayAdapterRecordForRecord()
    {
        // 共享选帧器（决策②）的适配器级证明：同一批帧、同一 maxAge、同一调用序，
        // 两条路径的消费台账必须逐字段一致（整场引擎级等价门见下一批的 CSV 源）。
        var frames = MatrixFrames();
        var replay = new VisionReplayAdapter(frames, EvidenceId, EvidenceSha, 500);
        var live = new LiveVisionBridge(StreamSource.FromFrames(frames), 500);

        foreach (var simT in new[] { 0.0, 0.05, 0.25, 0.45, 0.65, 0.85, 5.0 })
        {
            foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
            {
                var expected = replay.Classify(Context(role, simT));
                var actual = live.Classify(Context(role, simT));
                Assert.Equal(expected.Label, actual.Label);
                Assert.Equal(expected.Confidence, actual.Confidence);
                Assert.Equal(expected.OffsetX, actual.OffsetX);
            }
        }

        Assert.Equal(replay.Consumes.Count, live.Consumes.Count);
        for (var i = 0; i < replay.Consumes.Count; i++)
        {
            var expected = Dump(replay.Consumes[i]);
            var actual = Dump(live.Consumes[i]);
            output.WriteLine($"#{i} replay={expected} live={actual}");
            Assert.True(expected == actual, $"第 {i} 条消费记录分叉：{expected} != {actual}");
        }
    }

    // ---------- engine integration ----------

    private static Scenario FixedScenario(long seed = 42) => new()
    {
        Seed = seed,
        Blocks = OfficialLayout.Blocks,
    };

    private static List<string> RunToCompletion(MatchEngine engine)
    {
        engine.Arm();
        var fingerprints = new List<string>();
        while (!engine.Done)
        {
            var snapshot = engine.Tick();
            if (snapshot.Events is not { Count: > 0 })
            {
                continue;
            }
            fingerprints.AddRange(snapshot.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}"));
        }
        return fingerprints;
    }

    [Fact]
    public void InjectedEngine_LiveStreamMatchesTheReplayAdapterBitForBit()
    {
        // 同一批帧、同一场景：live 桥与回放适配器的消费台账与事件指纹必须逐位相同。
        var frames = FreshFrames();
        var replay = new VisionReplayAdapter(frames, EvidenceId, EvidenceSha, 500);
        var fingerprints1 = RunToCompletion(new MatchEngine(FixedScenario(), replay));

        var live = new LiveVisionBridge(StreamSource.FromFrames(frames), 500);
        var fingerprints2 = RunToCompletion(new MatchEngine(FixedScenario(), live));

        Assert.True(replay.Consumes.Count > 10, "expected the FSM to classify repeatedly");
        Assert.Equal(replay.Consumes.Count, live.Consumes.Count);
        for (var i = 0; i < replay.Consumes.Count; i++)
        {
            var expected = Dump(replay.Consumes[i]);
            var actual = Dump(live.Consumes[i]);
            Assert.True(expected == actual, $"第 {i} 条消费记录分叉：{expected} != {actual}");
        }
        Assert.Equal(fingerprints1, fingerprints2);
    }

    private sealed class ScriptedAdapter(IReadOnlyList<string> labels, bool draws = false) : IVisionAdapter
    {
        private int _index;

        /// <summary>Labels actually handed out (a starved run reports fewer than requested).</summary>
        public int Consumed => _index;

        /// <summary>True when the run needed more labels than the bridge produced (a divergence).</summary>
        public bool Starved { get; private set; }

        /// <summary>Classify calls seen, in order (role + SimT) — the call-sequence probe.</summary>
        public List<(string Role, double SimT)> CallLog { get; } = [];

        public string Id => "scripted";

        public VisionDetection Classify(VisionContext context)
        {
            CallLog.Add((context.Role, context.T));
            var label = "unknown";
            if (_index < labels.Count)
            {
                label = labels[_index++];
            }
            else
            {
                Starved = true;
            }
            if (draws)
            {
                var random = context.Random ?? throw new InvalidOperationException("no rng");
                _ = random();
                _ = random();
                _ = random();
            }
            return new VisionDetection
            {
                Label = label,
                Confidence = 0.8,
                Source = "scripted",
                OffsetX = 0.25,
            };
        }
    }

    [Fact]
    public void InjectedEngine_BridgeDoesNotShiftTheRngStream()
    {
        // 桥不抽流：与"照抄桥的标签但不抽流"的适配器对照，事件指纹与 classify 调用
        // 序列必须逐位一致；再与"同样标签但每次 classify 多抽 3 个随机数"的适配器
        // 对照，指纹必须不同——证明零抽流约束是承重的，不是空洞断言（照
        // VisionReplayAdapterTests 的反证写法）。
        var live = new LiveVisionBridge(StreamSource.FromFrames(FreshFrames()), 500);
        var fingerprints1 = RunToCompletion(new MatchEngine(FixedScenario(), live));
        Assert.True(live.Consumes.Count > 10, "expected the FSM to classify repeatedly");

        var labels = live.Consumes.Select(c => c.Label).ToList();
        var scripted = new ScriptedAdapter(labels);
        var fingerprints2 = RunToCompletion(new MatchEngine(FixedScenario(), scripted));

        Assert.False(scripted.Starved, "scripted replay needed more labels than the bridge produced");
        Assert.Equal(live.Consumes.Count, scripted.Consumed);
        var probe = Math.Min(live.Consumes.Count, scripted.CallLog.Count);
        for (var i = 0; i < probe; i++)
        {
            Assert.True(
                live.Consumes[i].Role == scripted.CallLog[i].Role && live.Consumes[i].SimT == scripted.CallLog[i].SimT,
                $"classify 调用序列在第 {i} 条分叉: live={live.Consumes[i].Role}@{live.Consumes[i].SimT:R}"
                + $" scripted={scripted.CallLog[i].Role}@{scripted.CallLog[i].SimT:R}"
                + $" (live {live.Consumes.Count} 条 / scripted {scripted.CallLog.Count} 条)");
        }
        Assert.Equal(fingerprints1, fingerprints2);

        // 反证：同样标签、每次 classify 多抽 3 个随机数 ⇒ 共享流位移 ⇒ 指纹必然分叉。
        var drawing = new ScriptedAdapter(labels, draws: true);
        var fingerprints3 = RunToCompletion(new MatchEngine(FixedScenario(), drawing));
        Assert.NotEqual(fingerprints1, fingerprints3);
    }

    [Fact]
    public void InjectedEngine_DefaultPathStaysUntouched()
    {
        // 本批全 additive：默认路径（不注入）的回放头不出现 liveBridge/证据字段。
        var engine = new MatchEngine(FixedScenario());
        engine.Arm();
        engine.Tick();
        var header = engine.BuildReplayHeader();
        Assert.Equal("default", header.VisionMode);
        Assert.Null(header.VisionEvidenceId);
        var json = ProtocolJson.Serialize(header);
        Assert.DoesNotContain("liveBridge", json, StringComparison.Ordinal);
        Assert.Empty(header.Validate());
    }

    [Fact]
    public void Constructor_DefaultsToTheFiveHundredMillisecondWindow()
    {
        var bridge = new LiveVisionBridge(StreamSource.FromFrames(MatrixFrames()));
        Assert.Equal("liveBridge", bridge.Id);
        Assert.Equal(500.0, bridge.MaxAgeMs);
        Assert.Equal(500.0, LiveVisionBridge.DefaultMaxAgeMs);
    }

    [Theory]
    [InlineData("null_source")]
    [InlineData("zero_max_age")]
    [InlineData("negative_max_age")]
    [InlineData("nan_max_age")]
    [InlineData("infinite_max_age")]
    public void Constructor_RejectsInvalidInputs(string mode)
    {
        var source = StreamSource.FromFrames(MatrixFrames());
        switch (mode)
        {
            case "null_source":
                Assert.Throws<ArgumentNullException>(() => new LiveVisionBridge(null!, 500));
                break;
            case "zero_max_age":
                Assert.Throws<ArgumentException>(() => new LiveVisionBridge(source, 0));
                break;
            case "negative_max_age":
                Assert.Throws<ArgumentException>(() => new LiveVisionBridge(source, -1));
                break;
            case "nan_max_age":
                Assert.Throws<ArgumentException>(() => new LiveVisionBridge(source, double.NaN));
                break;
            case "infinite_max_age":
                Assert.Throws<ArgumentException>(() => new LiveVisionBridge(source, double.PositiveInfinity));
                break;
        }
    }

    // ---------- JSONL contract ----------

    /// <summary>One real-car CSV row group (hunt_drive dialect) as the stream contract sees it.</summary>
    private static VisionStreamFrame StreamFrame() => new()
    {
        T = 1.715403,
        ReceivedAgeMs = 3.036785999938729,
        Sequence = 1,
        TimestampMs = 1_786_931_530_037,
        Status = "target",
        SelectedTargetIndex = 0,
        DetectionCount = 1,
        FrameWidth = 640,
        FrameHeight = 480,
        Fps = 3.377868,
        InferenceMs = 186.525678,
        ArrivalSimT = 0,
        Detections =
        [
            new VisionStreamDetection
            {
                ClassId = 0,
                TargetType = "good",
                Label = "buff",
                Confidence = 0.881051,
                BboxX1 = 100,
                BboxY1 = 120,
                BboxX2 = 200,
                BboxY2 = 260,
                CenterX = 150,
                CenterY = 190,
                OffsetX = 0.25,
                OffsetY = -0.1,
            },
        ],
    };

    [Fact]
    public void StreamFrame_SerializesWithRealCarColumnNames()
    {
        var json = StreamFrame().ToJsonLine();
        var root = JsonDocument.Parse(json).RootElement;

        foreach (var name in new[]
        {
            "t", "received_age_ms", "sequence", "vision_timestamp_ms", "vision_status",
            "selected_target", "detection_count", "frame_width", "frame_height", "fps",
            "inference_ms", "arrival_sim_t", "detections", "label", "confidence",
        })
        {
            Assert.True(root.TryGetProperty(name, out _), $"JSONL 契约缺少字段 {name}: {json}");
        }
        var detection = root.GetProperty("detections")[0];
        foreach (var name in new[]
        {
            "class_id", "target_type", "label", "confidence", "bbox_x1", "bbox_y1",
            "bbox_x2", "bbox_y2", "center_x", "center_y", "offset_x", "offset_y",
        })
        {
            Assert.True(detection.TryGetProperty(name, out _), $"JSONL 检测契约缺少字段 {name}: {json}");
        }

        // 真车列名是 snake_case：camelCase 别名不得出现（字段名由显式 JsonPropertyName 决定）。
        Assert.DoesNotContain("timestampMs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("receivedAgeMs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedTarget", json, StringComparison.Ordinal);
        // 帧级 label/confidence = 选中检测的值（CSV 的 selected 行）。
        Assert.Equal("buff", root.GetProperty("label").GetString());
        Assert.Equal(0.881051, root.GetProperty("confidence").GetDouble());
    }

    [Fact]
    public void StreamFrame_ToReplayFrame_StripsAuditFields()
    {
        var replayFrame = StreamFrame().ToReplayFrame();

        Assert.Equal(1, replayFrame.Sequence);
        Assert.Equal(1_786_931_530_037, replayFrame.TimestampMs);
        Assert.Equal("target", replayFrame.Status);
        Assert.Equal(0, replayFrame.SelectedTargetIndex);
        var detection = Assert.Single(replayFrame.Detections);
        Assert.Equal("buff", detection.Label);
        Assert.Equal(0.881051, detection.Confidence);
        Assert.Equal(0.25, detection.OffsetX);

        // 审计字段（t/received_age_ms/fps/inference_ms/帧尺寸/bbox/class_id/target_type/
        // arrival_sim_t）不进适配器工作集：VisionReplayFrame 里没有它们。
        var json = ProtocolJson.Serialize(replayFrame);
        Assert.DoesNotContain("bbox", json, StringComparison.Ordinal);
        Assert.DoesNotContain("classId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("arrival", json, StringComparison.Ordinal);
        Assert.DoesNotContain("inferenceMs", json, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamFrame_JsonLineRoundTrips()
    {
        var frame = StreamFrame();
        var line = frame.ToJsonLine();
        var parsed = VisionStreamFrame.ParseLine(line);

        Assert.Equal(line, parsed.ToJsonLine());
        Assert.Equal(frame.Sequence, parsed.Sequence);
        Assert.Equal(frame.TimestampMs, parsed.TimestampMs);
        Assert.Equal(frame.SelectedTargetIndex, parsed.SelectedTargetIndex);
        Assert.Equal(frame.ArrivalSimT, parsed.ArrivalSimT);
        Assert.Equal("buff", parsed.SelectedDetection!.Label);
        Assert.Equal(frame.Detections[0].BboxX2, parsed.Detections[0].BboxX2);
        Assert.Equal(frame.Detections[0].TargetType, parsed.Detections[0].TargetType);
    }

    [Fact]
    public void StreamFrame_ValidateAndParseLine_RejectContractViolations()
    {
        // 契约错误必须在边界上显式失败: 未知/缺失 status、detection_count 与行数不符、
        // 非 target 帧带检测、选中越界、非法 label/confidence/class_id 都不允许被"降级"
        // 成 unknown/error 检测喂给 FSM(与 CSV 方言的硬拒绝同一口径)。
        var valid = StreamFrame();
        Assert.Empty(valid.Validate());

        var violations = new (string Name, VisionStreamFrame Frame)[]
        {
            ("unknown_status", valid with { Status = "bogus" }),
            ("missing_status", valid with { Status = "" }),
            ("no_target_with_detections", valid with { Status = "no_target" }),
            ("target_without_detections", valid with { SelectedTargetIndex = null, DetectionCount = 0, Detections = [] }),
            ("count_mismatch", valid with { DetectionCount = 3 }),
            ("selection_out_of_range", valid with { SelectedTargetIndex = 3 }),
            ("bad_label", valid with { Detections = [valid.Detections[0] with { Label = "bonus" }] }),
            ("bad_confidence", valid with { Detections = [valid.Detections[0] with { Confidence = 1.5 }] }),
            ("bad_class_id", valid with { Detections = [valid.Detections[0] with { ClassId = 2 }] }),
            ("negative_sequence", valid with { Sequence = -1 }),
        };
        foreach (var (name, frame) in violations)
        {
            Assert.NotEmpty(frame.Validate());
            var error = Assert.Throws<JsonException>(() => VisionStreamFrame.ParseLine(frame.ToJsonLine()));
            Assert.Contains("不符合契约", error.Message, StringComparison.Ordinal);
            output.WriteLine($"{name}: {error.Message}");
        }

        // 非有限几何值: 契约层先拦下(序列化器本身另有拒绝, 不在本测试断言其异常类型)。
        var nonFinite = valid with { Detections = [valid.Detections[0] with { BboxX1 = double.NaN }] };
        Assert.NotEmpty(nonFinite.Validate());

        // 合法帧必须能往返: 新校验不得误伤契约内的帧。
        Assert.Equal(valid.ToJsonLine(), VisionStreamFrame.ParseLine(valid.ToJsonLine()).ToJsonLine());
    }

    [Fact]
    public void StreamFrame_ParseLine_RejectsNullShapedPayloads_AsContractErrors()
    {
        // 线上"空形状"攻击: JSON null 列表/JSON null 元素/整行 null —— 都必须以
        // JsonException(契约错误)报出, 绝不可是 NullReferenceException 穿出边界
        // (进程源把它当帧级故障; 桥/FSM 不得被炸掉)。
        var nullDetections = """{"sequence":1,"vision_timestamp_ms":1786931530037,"vision_status":"no_target","detection_count":0,"detections":null}""";
        var error = Assert.Throws<System.Text.Json.JsonException>(
            () => VisionStreamFrame.ParseLine(nullDetections));
        Assert.Contains("detections 不能为 null", error.Message, StringComparison.Ordinal);

        // JSON 字面量 null: ProtocolJson 的既有保证(反序列化为 null 即 JsonException)。
        Assert.Throws<System.Text.Json.JsonException>(() => VisionStreamFrame.ParseLine("null"));
        Assert.Throws<System.Text.Json.JsonException>(() => VisionStreamFrame.ParseLine("   "));

        // JSON null 元素: Validate 的既有分支已拦下(这里钉住回归)。
        var nullElement = """{"sequence":2,"vision_timestamp_ms":1786931530037,"vision_status":"target","detection_count":1,"selected_target":0,"detections":[null]}""";
        var elementError = Assert.Throws<System.Text.Json.JsonException>(
            () => VisionStreamFrame.ParseLine(nullElement));
        Assert.Contains("detections[0] 为空", elementError.Message, StringComparison.Ordinal);

        // 记录侧同一条: Detections 置 null 的帧不得被 Validate 当成"合法空流"。
        var frame = StreamFrame() with { Detections = null! };
        Assert.Contains(frame.Validate(), e => e.Contains("detections 不能为 null", StringComparison.Ordinal));
        // 其余检查仍照常执行(不是提前 return): 选中下标越界与空列表一起报出。
        var both = (StreamFrame() with { Detections = null!, SelectedTargetIndex = 5 }).Validate().ToList();
        Assert.Contains(both, e => e.Contains("detections 不能为 null", StringComparison.Ordinal));
        Assert.Contains(both, e => e.Contains("超出 detections 范围", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamFrame_SelectedDetectionFollowsTheCsvSelectedRow()
    {
        var frame = StreamFrame() with
        {
            DetectionCount = 2,
            SelectedTargetIndex = 1,
            Detections =
            [
                new VisionStreamDetection { Label = "buff", Confidence = 0.4, OffsetX = -0.5 },
                new VisionStreamDetection { Label = "debuff", Confidence = 0.7, OffsetX = 0.5 },
            ],
        };
        Assert.Equal("debuff", frame.Label);
        Assert.Equal(0.7, frame.Confidence);
        Assert.Equal(2, frame.ToReplayFrame().Detections.Count);
        Assert.Equal("debuff", frame.ToReplayFrame().Detections[1].Label);

        // 无选中（no_selection / no_target）：帧级 label/confidence 缺失，不伪造。
        var none = frame with { SelectedTargetIndex = null };
        Assert.Null(none.Label);
        Assert.Null(none.Confidence);
        Assert.Null(none.SelectedDetection);
        var root = JsonDocument.Parse(none.ToJsonLine()).RootElement;
        Assert.False(root.TryGetProperty("label", out _));
        Assert.False(root.TryGetProperty("confidence", out _));
    }
}
