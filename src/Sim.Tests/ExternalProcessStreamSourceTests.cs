using System.Diagnostics;
using Sim.Core;
using Sim.Protocol;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// ExternalProcessStreamSource contract tests (R7): a real child process streams
/// JSONL frames, <see cref="ExternalProcessStreamSource.PumpUntil"/> is non-blocking
/// (it only drains complete lines already buffered), malformed lines become stream
/// faults instead of frames, the process lifecycle is explicit (exited/faulted) and
/// afterwards the bridge keeps serving via the plain camera-cache rule — stale with
/// a reason, never an exception.
///
/// The child is the repo's own VisionStreamStub fixture (a nested executable
/// project), so these tests run in CI without Python, camera or model weights.
/// </summary>
public class ExternalProcessStreamSourceTests(ITestOutputHelper output)
{
    private static string StubExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "VisionStreamStub.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(
                "VisionStreamStub.exe not found next to the test assembly; build Sim.Tests first");
        }
        return exe;
    }

    private static string StubCommand(params string[] args)
        => $"\"{StubExe()}\" {string.Join(' ', args)}";

    /// <summary>有界等待: 进程源按墙钟到达, 测试必须等真实帧而不是假设它们立刻可用。</summary>
    private static void WaitForFrames(ExternalProcessStreamSource source, int expected, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (source.DeliveredFrames.Count < expected && Environment.TickCount64 < deadline)
        {
            source.PumpUntil(0);
            Thread.Sleep(5);
        }
        Assert.True(
            source.DeliveredFrames.Count >= expected,
            $"等待 {expected} 帧超时: 只交付了 {source.DeliveredFrames.Count} 帧 (state={source.State}, fault={source.LastFault})");
    }

    private static void WaitForExit(ExternalProcessStreamSource source, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (source.State == VisionProcessState.Running && Environment.TickCount64 < deadline)
        {
            source.PumpUntil(0);
            Thread.Sleep(5);
        }
        Assert.NotEqual(VisionProcessState.Running, source.State);
    }

    private static VisionContext Context(string role, double simT)
        => new()
        {
            T = simT,
            Role = role,
            Robot = new RobotRuntime { Role = role, Name = role },
            Opponent = new RobotRuntime { Role = RoleNames.Them, Name = "对手" },
        };

    // ---------- lifecycle / plumbing ----------

    [Fact]
    public void PumpUntil_IsNonBlocking_AndDeliversTheBufferedCompleteLines()
    {
        // 桩在 1.2s 内一帧不吐: 期间 PumpUntil 必须立刻返回 0(Windows 管道上
        // 阻塞式 ReadFile 会让"Peek 非阻塞"是错的, 本类用后台读取线程规避)。
        using var source = ExternalProcessStreamSource.Start(
            StubCommand("--count", "3", "--initial-delay-ms", "1200", "--step-ms", "100"));
        var clock = Stopwatch.StartNew();
        var added = source.PumpUntil(3.0);
        clock.Stop();
        Assert.Equal(0, added);
        Assert.True(clock.ElapsedMilliseconds < 400,
            $"PumpUntil 阻塞了 {clock.ElapsedMilliseconds}ms: 非阻塞契约被破坏");
        Assert.Equal(VisionProcessState.Running, source.State);
        Assert.Empty(source.DeliveredFrames);

        WaitForFrames(source, 3);
        Assert.Equal(3, source.DeliveredFrames.Count);
        Assert.Equal(3, source.Released.Count);

        // 字段逐一核对: 桩帧的线格式与契约一致, 且 Released 是同一帧的回放形态。
        var first = source.DeliveredFrames[0];
        Assert.Equal(1, first.Sequence);
        Assert.Equal(1_786_931_530_037.0, first.TimestampMs);
        Assert.Equal("target", first.Status);
        Assert.Equal(1, first.DetectionCount);
        Assert.Equal(0, first.SelectedTargetIndex);
        Assert.Equal(640, first.FrameWidth);
        Assert.Equal(480, first.FrameHeight);
        Assert.Equal(7.5, first.ReceivedAgeMs);
        var detection = Assert.Single(first.Detections);
        Assert.Equal("buff", detection.Label);
        Assert.Equal(0, detection.ClassId);
        Assert.Equal("good", detection.TargetType);
        Assert.Equal(0.0, detection.OffsetX);
        Assert.Equal(320.0, detection.CenterX);
        Assert.Equal(100.0, source.DeliveredFrames[1].TimestampMs - source.DeliveredFrames[0].TimestampMs); // 步长 100ms
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(source.DeliveredFrames[i].Sequence, source.Released[i].Sequence);
            Assert.Equal(source.DeliveredFrames[i].TimestampMs, source.Released[i].TimestampMs);
            Assert.Equal(source.DeliveredFrames[i].Status, source.Released[i].Status);
        }

        WaitForExit(source);
        Assert.Equal(VisionProcessState.Exited, source.State);
        Assert.Equal(0, source.ExitCode);
        Assert.Equal(0, source.Faults);
        Assert.Equal(0, source.RejectedLines);
        output.WriteLine($"桩进程: 帧={source.DeliveredFrames.Count} state={source.State} exit={source.ExitCode}");
    }

    [Fact]
    public void ProcessExit_LeavesTheStreamStale_WithRecordedReason_NotAnException()
    {
        using var source = ExternalProcessStreamSource.Start(
            StubCommand("--count", "2", "--step-ms", "200", "--interval-ms", "20"));
        WaitForFrames(source, 2);
        WaitForExit(source);
        Assert.Equal(VisionProcessState.Exited, source.State);

        // 流已结束: 桥仍按相机缓存规则服务 —— 窗内的旧帧照发, 出窗记 stale(带 reason),
        // 绝不因为"源已死"抛异常。
        var bridge = new LiveVisionBridge(source, maxAgeMs: 500);
        Assert.Equal("buff", bridge.Classify(Context(RoleNames.Us, 0.1)).Label);
        Assert.Equal(100.0, bridge.Consumes[0].AgeMs); // 首帧锚点 = SimT 0, 0.1s 时的帧龄 = 100ms

        var stale = bridge.Classify(Context(RoleNames.Us, 1.0));
        Assert.Equal("unknown", stale.Label);
        Assert.Equal("stale", stale.Source);
        Assert.Equal("stale", bridge.Consumes[1].Reason);
        Assert.Equal(2, bridge.Consumes[1].FrameSequence);
        Assert.Equal(800.0, bridge.Consumes[1].AgeMs);
        Assert.Equal(0, source.PumpUntil(10.0)); // 进程结束后不再交付任何帧
    }

    [Fact]
    public void ProcessExit_WithNoFrames_IsNoFrameForTheBridge()
    {
        using var source = ExternalProcessStreamSource.Start(StubCommand("--count", "0"));
        WaitForExit(source);
        Assert.Empty(source.DeliveredFrames);

        var bridge = new LiveVisionBridge(source, maxAgeMs: 500);
        var detection = bridge.Classify(Context(RoleNames.Us, 3.0));
        Assert.Equal("unknown", detection.Label);
        Assert.Equal("no_frame", detection.Source);
        Assert.Equal("no_frame", bridge.Consumes[0].Reason);
    }

    [Fact]
    public void RunawayOutput_TripsTheQueueCap_FaultsAndKeepsBufferedLinesDeliverable()
    {
        // 上限纪律: 子进程输出远快于引擎消费时, 读取线程在上限处停止并把流定性为故障
        // (管道写满后子进程自然被背压阻塞), 队列内存有界; 已入队的完整行仍可交付,
        // 绝不静默丢帧。单读者线程 + 每次读一行前查上限 ⇒ 恰好入队 maxQueuedLines 行,
        // 泵空时如数交付(无竞态)。
        using var source = ExternalProcessStreamSource.Start(
            StubCommand("--count", "60", "--step-ms", "1", "--interval-ms", "1"),
            maxQueuedLines: 10);
        var deadline = Environment.TickCount64 + 15000;
        while (source.State == VisionProcessState.Running && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }
        Assert.NotEqual(VisionProcessState.Running, source.State);
        Assert.Equal(VisionProcessState.Faulted, source.State);
        Assert.Contains("上限", source.LastFault, StringComparison.Ordinal);
        Assert.Equal(1, source.Faults);

        // 已缓冲行仍可交付: 恰好上限那么多, 一行不丢。
        Assert.Equal(10, source.PumpUntil(0));
        Assert.Equal(0, source.PumpUntil(0)); // 读取已停, 不会再来
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            source.DeliveredFrames.Select(f => f.Sequence).ToArray());
    }

    [Fact]
    public void MalformedLines_AreStreamFaults_NotFrames_AndNonZeroExitIsFaulted()
    {
        using var source = ExternalProcessStreamSource.Start(
            StubCommand("--count", "3", "--garbage-at", "1", "--exit-code", "3"));
        WaitForFrames(source, 3);
        WaitForExit(source);

        Assert.Equal(3, source.DeliveredFrames.Count); // 坏行绝不成帧
        Assert.Equal(1, source.RejectedLines);
        // 坏行 1 次 + 非零退出 1 次。
        Assert.Equal(2, source.Faults);
        Assert.Equal(VisionProcessState.Faulted, source.State);
        Assert.Equal(3, source.ExitCode);
        Assert.Contains("3", source.LastFault, StringComparison.Ordinal);
        Assert.NotNull(source.LastRejectedLine);
        Assert.StartsWith("{ 这不是 JSON", source.LastRejectedLine, StringComparison.Ordinal);

        // 坏行之后的帧仍按序交付(坏行不占帧号), 桥照常服务它们。
        Assert.Equal(new long[] { 1, 2, 3 }, source.DeliveredFrames.Select(f => f.Sequence).ToArray());
        var bridge = new LiveVisionBridge(source, maxAgeMs: 500);
        Assert.Equal("buff", bridge.Classify(Context(RoleNames.Us, 0.25)).Label);
        Assert.Equal(2, bridge.Consumes[0].FrameSequence);
    }

    [Fact]
    public void NullShapedPayloads_AreFrameFaults_AndTheStreamContinues()
    {
        // 评审实测缺陷的回归钉: 显式 "detections": null 曾让 Validate 抛
        // NullReferenceException 逃出 PumpUntil 的 JsonException catch → 整场崩溃、零产出。
        // 现在: 一条坏行 = 一条帧级故障, 后续合法帧照常交付。
        var work = Path.Combine(Path.GetTempPath(), $"visionstub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var validLine = new VisionStreamFrame
            {
                Sequence = 9,
                TimestampMs = 1_786_931_530_037,
                Status = "target",
                ReceivedAgeMs = 5,
                DetectionCount = 1,
                SelectedTargetIndex = 0,
                FrameWidth = 640,
                FrameHeight = 480,
                Detections =
                [
                    new VisionStreamDetection
                    {
                        ClassId = 0, TargetType = "good", Label = "buff", Confidence = 0.9,
                        BboxX1 = 10, BboxY1 = 10, BboxX2 = 20, BboxY2 = 20,
                        CenterX = 15, CenterY = 15, OffsetX = 0, OffsetY = 0,
                    },
                ],
            }.ToJsonLine();
            var payloadPath = Path.Combine(work, "hostile.jsonl");
            File.WriteAllLines(payloadPath,
            [
                "null", // 整行 JSON null
                """{"sequence":1,"vision_timestamp_ms":1786931530037,"vision_status":"no_target","detection_count":0,"detections":null}""",
                """{"sequence":2,"vision_timestamp_ms":1786931530037,"vision_status":"target","detection_count":1,"selected_target":0,"detections":[null]}""",
                """{"sequence":3,"vision_timestamp_ms":1786931530037,"vision_status":"bogus","detection_count":0,"detections":[]}""",
                validLine,
            ]);

            using var source = ExternalProcessStreamSource.Start(
                StubCommand("--raw-file", $"\"{payloadPath}\""));
            WaitForFrames(source, 1); // 坏行若在此抛出, 测试即以异常失败(回归钉本身)
            WaitForExit(source);

            var frame = Assert.Single(source.DeliveredFrames); // 只有末尾合法帧成帧
            Assert.Equal(9, frame.Sequence);
            Assert.Equal("buff", frame.Detections[0].Label);
            Assert.Equal(4, source.RejectedLines);
            Assert.Equal(4, source.Faults);
            Assert.Equal(VisionProcessState.Exited, source.State);
            Assert.Equal(0, source.ExitCode);
            Assert.NotNull(source.LastFault);
            Assert.NotNull(source.LastRejectedLine);
            output.WriteLine($"坏行 {source.RejectedLines} 条全部记为帧级故障, 合法帧仍交付: {source.LastFault}");
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // best-effort temp cleanup
            }
        }
    }

    [Fact]
    public void Dispose_ReapsALingeringProcessAndStopsDelivery()
    {
        var source = ExternalProcessStreamSource.Start(StubCommand("--count", "2", "--linger"));
        WaitForFrames(source, 2);

        var clock = Stopwatch.StartNew();
        source.Dispose();
        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 5000, $"Dispose 未及时回收子进程: {clock.ElapsedMilliseconds}ms");

        // 句柄已回收: 再拉取不再交付、也不抛异常(状态可能停在 killed 的 Faulted, 如实记录)。
        Assert.Equal(0, source.PumpUntil(1.0));
        Assert.Equal(2, source.DeliveredFrames.Count);
        Assert.NotEqual(VisionProcessState.Running, source.State);
        source.Dispose(); // 幂等
    }

    [Fact]
    public void Start_UnstartableCommand_IsAnExplicitFailure()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ExternalProcessStreamSource.Start("definitely-not-a-real-program-xyz --foo"));
        Assert.Contains("启动视觉流进程失败", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ExternalProcessStreamSource.Start("   "));
    }
}
