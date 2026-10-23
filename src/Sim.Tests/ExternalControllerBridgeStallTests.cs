using System.Diagnostics;
using Sim.Controller;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 策略进程停止读 stdin 的回归（审计实证的整场挂死）：观测写必须有
/// <c>--timeout-ms</c> 超时，超时按零动作 + fault 处理且**不杀进程**；
/// 上一帧的写仍阻塞时，新的 Decide 直接按写超时记 fault，不排队第二个写
/// （防每 tick 一个阻塞写任务堆叠）。正常应答路径不得被这条路径波及。
/// 与 CLI/控制器进程 fixture 同属串行集合：本类拉起真实子进程并做真实超时等待。
/// </summary>
[Collection("cli-console")]
public class ExternalControllerBridgeStallTests
{
    private const double TimeoutMs = 300;

    private static string FixtureExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EchoController.exe");
        Assert.True(File.Exists(exe), $"EchoController fixture missing: {exe}");
        return exe;
    }

    private static ExternalControllerBridge Start(string mode, double timeoutMs)
        => ExternalControllerBridge.Start($"\"{FixtureExe()}\" {mode}", timeoutMs);

    /// <summary>
    /// 单行观测远大于任何管道缓冲：子进程不读时 WriteLine 必然阻塞。
    /// 真实比赛 obs 约 7 KB，Windows 匿名管道缓冲约 4 KB —— 正常控制器
    /// 边读边消化不会阻塞；这里放大到 256 KB 让"不读"必然触发写阻塞。
    /// </summary>
    private static Observation BigObservation(long requestId = 1)
        => Samples.Observation() with
        {
            RequestId = requestId,
            Robot = Samples.Observation().Robot with { Action = new string('x', 256 * 1024) },
        };

    [Fact]
    public void StalledController_WriteTimesOut_ReturnsZeroWithFault_AndKeepsTheProcess()
    {
        using var bridge = Start("stall", TimeoutMs);
        var clock = Stopwatch.StartNew();

        var action = bridge.Decide(BigObservation());

        clock.Stop();
        Assert.Equal(RobotAction.Zero.V, action.V);
        Assert.Equal(RobotAction.Zero.W, action.W);
        Assert.Equal(1, bridge.Faults);
        Assert.Equal("controller stdin write timeout", bridge.LastFault);
        // 写超时与应答超时同一量级：不允许多等一个数量级。
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(TimeoutMs * 4),
            $"stalled write must return near TimeoutMs (took {clock.Elapsed.TotalMilliseconds:0} ms)");
        // 慢/暂塞的进程保留：写阻塞只是 tick 级失败，下一帧仍可能恢复。
        Assert.True(bridge.IsRunning, "write timeout must not kill the controller process");
    }

    [Fact]
    public void StalledController_ConsecutiveTicksDoNotStackWrites_AndStayWithinOneTimeoutEach()
    {
        using var bridge = Start("stall", TimeoutMs);
        var durations = new List<TimeSpan>();

        for (var tick = 0; tick < 5; tick++)
        {
            var clock = Stopwatch.StartNew();
            var action = bridge.Decide(BigObservation(requestId: tick + 1));
            clock.Stop();
            durations.Add(clock.Elapsed);

            Assert.Equal(RobotAction.Zero.V, action.V);
            Assert.Equal(RobotAction.Zero.W, action.W);
            // 每 tick 恰好一次 fault：不重复计、不吞掉。
            Assert.Equal(tick + 1, bridge.Faults);
            Assert.Equal("controller stdin write timeout", bridge.LastFault);
            // 任何一帧都不得超过一个超时量级（否则就是又挂住了）。
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(TimeoutMs * 4),
                $"tick {tick} exceeded the timeout budget (took {clock.Elapsed.TotalMilliseconds:0} ms)");
        }

        // 单在途写裁决：首帧等满一个超时后，后续帧因"上一写未完成"立即失败，
        // 总耗时不会随 tick 数线性堆叠到 N×TimeoutMs 以上（排队的实现会）。
        var total = durations.Aggregate(TimeSpan.Zero, (sum, next) => sum + next);
        Assert.True(total < TimeSpan.FromMilliseconds(TimeoutMs * 4),
            $"consecutive stalled ticks must not queue writes (total {total.TotalMilliseconds:0} ms)");
        Assert.True(bridge.IsRunning, "stalled controller must survive every tick");
    }

    [Fact]
    public void StalledController_IsReapedOnDispose_NoOrphanProcess()
    {
        var before = EchoControllerIds();
        var bridge = Start("stall", TimeoutMs);
        try
        {
            bridge.Decide(BigObservation());
            Assert.True(bridge.IsRunning);
        }
        finally
        {
            bridge.Dispose();
        }

        // Dispose 杀整棵进程树：等它真正退出，不留孤儿（Kill 是异步的）。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !EchoControllerIds().IsSubsetOf(before))
        {
            Thread.Sleep(50);
        }
        Assert.True(EchoControllerIds().IsSubsetOf(before),
            $"stalled controller survived Dispose: before=[{string.Join(",", before)}]"
            + $" after=[{string.Join(",", EchoControllerIds())}]");
    }

    [Fact]
    public void HealthyController_WithLargeObservation_StaysFaultFree()
    {
        // 计时写路径不得对正常应答的控制器误报：大 obs 仍走通、faults=0。
        using var bridge = Start("echo", timeoutMs: 5000);

        var action = bridge.Decide(BigObservation(requestId: 9));

        Assert.Equal(0.05, action.V, 12);
        Assert.Equal(0.1, action.W, 12);
        Assert.Equal("9", action.RequestId);
        Assert.Equal(0, bridge.Faults);
    }

    [Fact]
    public void SlowButRecoverableController_IsNotKilled_AndResumesOnALaterTick()
    {
        // 慢而非死: 首帧写超时后进程必须保留, 恢复读取后下一帧能拿到正常动作
        // (与既有"应答超时不杀进程"语义对齐)。
        using var bridge = ExternalControllerBridge.Start(
            $"\"{FixtureExe()}\" stallonce 700", TimeoutMs);
        var clock = Stopwatch.StartNew();
        var stalled = bridge.Decide(BigObservation(requestId: 1));
        clock.Stop();

        Assert.Equal(RobotAction.Zero.V, stalled.V);
        Assert.Equal(1, bridge.Faults);
        Assert.Equal("controller stdin write timeout", bridge.LastFault);
        Assert.True(bridge.IsRunning, "a slow controller must not be killed");
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(TimeoutMs * 4),
            $"stalled write must return near TimeoutMs (took {clock.Elapsed.TotalMilliseconds:0} ms)");

        // 进程在 700 ms 后恢复读取: 等它退出暂停期, 下一帧应恢复正常应答。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        RobotAction? recovered = null;
        while (DateTime.UtcNow < deadline)
        {
            recovered = bridge.Decide(BigObservation(requestId: 2));
            if (recovered.V != 0 || recovered.RequestId is not null)
            {
                break;
            }
            Thread.Sleep(100);
        }

        Assert.NotNull(recovered);
        Assert.Equal(0.05, recovered!.V, 12);
        Assert.Equal(0.1, recovered.W, 12);
        Assert.Equal("2", recovered.RequestId);
    }

    private static HashSet<int> EchoControllerIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EchoController"))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }
        return ids;
    }
}
