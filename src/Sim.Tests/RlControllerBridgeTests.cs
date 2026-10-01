using Sim.Controller;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 外部控制器桥 × RL 观测契约（无引擎依赖, EchoController fixture）:
/// 11 维 <c>rlObservation</c> 的加性线格式、UTF-8 行与 requestId 对齐、以及
/// 超时/错配/死进程统一零动作 + fault 计数。RL 适配器自身的"残帧零动作"义务
/// 在 Python 侧（bridge_adapter_selftest.py, 批 2）。
/// 与 CLI/控制器进程 fixture 同属串行集合: 本类拉起 EchoController 子进程并做真实
/// 超时等待, 不得与墙钟敏感的测试(如 TrainingResetPerformanceTests)并行抢占 CPU。
/// </summary>
[Collection("cli-console")]
public class RlControllerBridgeTests
{
    private static readonly double[] RlObservationValues =
        [0.25, -0.5, 0.75, 1.0, -1.0, 0.0, 1.0, 0.0, 0.5, -0.25, 0.125];

    private static Observation ObservationWithRl(double[]? rlObservation, long requestId = 7)
        => Samples.Observation() with
        {
            RequestId = requestId,
            RlObservation = rlObservation,
        };

    [Fact]
    public void RlObservation_IsSerializedAdditivelyAndOmittedWhenNull()
    {
        var withRl = ObservationWithRl(RlObservationValues);
        var json = ProtocolJson.Serialize(withRl);

        using (var document = System.Text.Json.JsonDocument.Parse(json))
        {
            var serialized = document.RootElement.GetProperty("rlObservation")
                .EnumerateArray().Select(e => e.GetDouble()).ToArray();
            Assert.Equal(RlObservationValues, serialized);
        }

        // null 字段不出现 (WhenWritingNull) ⇒ 普通 match/rl-env/旧消费者字节不变。
        var legacyJson = ProtocolJson.Serialize(ObservationWithRl(null));
        Assert.DoesNotContain("rlObservation", legacyJson);
        Assert.Equal(ProtocolJson.Serialize(Samples.Observation() with { RequestId = 7 }), legacyJson);
        Assert.Null(ProtocolJson.Deserialize<Observation>(legacyJson).RlObservation);

        // 有值也照常通过协议校验; 非有限值被显式拒绝。
        ProtocolValidator.EnsureValid(withRl);
        var nan = (double[])RlObservationValues.Clone();
        nan[10] = double.NaN;
        Assert.Throws<ProtocolValidationException>(() => ProtocolValidator.EnsureValid(ObservationWithRl(nan)));
    }

    [Fact]
    public void Bridge_SendsRlObservationOverUtf8StdinAndAppliesTheEchoedAction()
    {
        using var bridge = Start("echo", timeoutMs: 500);

        var action = bridge.Decide(ObservationWithRl(RlObservationValues, requestId: 7));

        Assert.Equal(0.05, action.V, 12);
        Assert.Equal(0.1, action.W, 12);
        Assert.Equal("7", action.RequestId);
        Assert.Equal(0, bridge.Faults);
        Assert.True(bridge.IsRunning);
    }

    [Fact]
    public void Bridge_WritesUtf8ObservationLines_IndependentOfConsoleCodePage()
    {
        using var bridge = Start("utf8probe", timeoutMs: 500);

        var observation = ObservationWithRl(RlObservationValues);
        Assert.Equal("旋转扫描", observation.Robot.Action);
        var action = bridge.Decide(observation);

        // utf8probe 用独立 UTF-8 reader 解 obs 行: 中文 action 4 个字符, 首码点 U+65CB。
        Assert.Equal(4.0, action.V, 12);
        Assert.Equal(0x65CB, action.W, 12);
        Assert.Equal("7", action.RequestId);
        Assert.Equal(0, bridge.Faults);
    }

    [Fact]
    public void Bridge_MissingRlObservationIsForwardedVerbatim_NotRejected()
    {
        // 预检/残帧(无 robot.objects 的 obs)在 C# 侧必须照常送达; 是否零动作由适配器决定。
        using var bridge = Start("echo", timeoutMs: 500);
        var preflight = new Observation { RequestId = 1 };

        var action = bridge.Decide(preflight);

        Assert.Equal(0.05, action.V, 12);
        Assert.Equal(0, bridge.Faults);
    }

    [Fact]
    public void Bridge_Timeout_ReturnsZeroActionAndCountsOneFaultPerFrame()
    {
        using var bridge = Start("hang", timeoutMs: 50);

        var first = bridge.Decide(ObservationWithRl(RlObservationValues));
        Assert.Equal(RobotAction.Zero.V, first.V);
        Assert.Equal(RobotAction.Zero.W, first.W);
        Assert.Equal(1, bridge.Faults);
        Assert.Contains("timeout", bridge.LastFault);

        var second = bridge.Decide(ObservationWithRl(RlObservationValues, requestId: 8));
        Assert.Equal(RobotAction.Zero.V, second.V);
        Assert.Equal(2, bridge.Faults);
    }

    [Fact]
    public void Bridge_RequestIdMismatch_IsDroppedNeverApplied()
    {
        using var bridge = Start("wrongid", timeoutMs: 100);

        var action = bridge.Decide(ObservationWithRl(RlObservationValues, requestId: 11));

        Assert.Equal(RobotAction.Zero.V, action.V);
        Assert.Equal(RobotAction.Zero.W, action.W);
        Assert.Equal(1, bridge.Faults);
        Assert.Contains("timeout", bridge.LastFault);
    }

    [Fact]
    public void Bridge_DeadProcess_ReturnsZeroActionWithFault()
    {
        using var bridge = Start("die", timeoutMs: 100);

        var action = bridge.Decide(ObservationWithRl(RlObservationValues));

        Assert.Equal(RobotAction.Zero.V, action.V);
        Assert.Equal(RobotAction.Zero.W, action.W);
        Assert.True(bridge.Faults >= 1);
        Assert.NotEqual("", bridge.LastFault);
    }

    [Fact]
    public void Bridge_ProcessExitsWithoutResponse_FaultsFastInsteadOfWaitingForTimeout()
    {
        using var bridge = Start("die", timeoutMs: 5000);

        var started = DateTime.UtcNow;
        var action = bridge.Decide(ObservationWithRl(RlObservationValues));
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(RobotAction.Zero.V, action.V);
        Assert.Equal(1, bridge.Faults);
        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"dead controller must not burn the full 5000 ms timeout (took {elapsed.TotalMilliseconds:0} ms)");
    }

    private static ExternalControllerBridge Start(string mode, double timeoutMs)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EchoController.exe");
        Assert.True(File.Exists(exe), $"EchoController fixture missing: {exe}");
        return ExternalControllerBridge.Start($"\"{exe}\" {mode}", timeoutMs);
    }
}
