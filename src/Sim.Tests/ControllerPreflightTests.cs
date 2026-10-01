using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

public class ControllerPreflightTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "EchoController.exe");

    private static ControllerProfile Profile(string mode, double timeoutMs = 500)
        => new() { Mode = ControllerModes.External, Command = mode, TimeoutMs = timeoutMs };

    private static string FixtureCommand(string mode) => $"\"{FixturePath}\" {mode}";

    [Fact]
    public void BuiltInOrEmpty_ProfilePassesWithoutProbe()
    {
        Assert.True(ControllerPreflight.Run(new ControllerProfile()).Ok);
        var externalNoCommand = ControllerPreflight.Run(
            new ControllerProfile { Mode = ControllerModes.External, Command = "" });
        Assert.True(externalNoCommand.Ok);
    }

    [Fact]
    public void HealthyController_HandshakeSucceeds_AndReportsTheAction()
    {
        var result = ControllerPreflight.Run(Profile(FixtureCommand("echo")));
        Assert.True(result.Ok, $"echo probe failed: {result.Message}");
        Assert.Contains("握手成功", result.Message);
        Assert.Contains("0.05", result.Message);
    }

    [Fact]
    public void MissingExecutable_FailsWithLaunchError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"definitely-missing-{Guid.NewGuid():N}.exe");
        var result = ControllerPreflight.Run(Profile($"\"{missing}\" echo"));
        Assert.False(result.Ok);
        Assert.DoesNotContain("握手成功", result.Message);
    }

    [Fact]
    public void ImmediatelyExitingController_FailsTheProbe()
    {
        var result = ControllerPreflight.Run(Profile(FixtureCommand("die")));
        Assert.False(result.Ok);
    }

    [Fact]
    public void ImmediatelyExitingController_FailsFastWithLaunchReason_NotTimeout()
    {
        var started = DateTime.UtcNow;
        var result = ControllerPreflight.Run(Profile(FixtureCommand("die"), timeoutMs: 5000));
        var elapsed = DateTime.UtcNow - started;

        Assert.False(result.Ok);
        Assert.Equal(ControllerPreflightKinds.Launch, result.FailureKind);
        // 命令当场退出(脚本不存在)不是"首帧加载慢": 文案不得再借用桥的应答超时。
        Assert.DoesNotContain("timeout", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"dead command must fail fast, not burn TimeoutMs (took {elapsed.TotalMilliseconds:0} ms)");
    }

    [Fact]
    public void BadLineController_FailsTheProbe()
    {
        var result = ControllerPreflight.Run(Profile(FixtureCommand("bad")));
        Assert.False(result.Ok);
    }

    [Fact]
    public void WrongRequestIdController_FailsTheProbe()
    {
        var result = ControllerPreflight.Run(Profile(FixtureCommand("wrongid")));
        Assert.False(result.Ok);
    }

    [Fact]
    public void SilentController_TimesOutAndFails()
    {
        var result = ControllerPreflight.Run(Profile(FixtureCommand("hang"), timeoutMs: 300));
        Assert.False(result.Ok);
        Assert.Contains("timeout", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_ReapsItsControllerProcess()
    {
        var before = ProcessCount();
        var result = ControllerPreflight.Run(Profile(FixtureCommand("echo")));
        Assert.True(result.Ok, $"echo probe failed: {result.Message}");
        // Give the reaper a moment; the count must return to the pre-probe level
        // even if other tests are running their own EchoController instances.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && ProcessCount() > before)
        {
            Thread.Sleep(50);
        }
        Assert.True(ProcessCount() <= before,
            $"probe leaked controller processes: before={before} after={ProcessCount()}");
    }

    [Fact]
    public void FaultClassification_SeparatesLaunchProtocolAndTimeout()
    {
        Assert.Equal(ControllerPreflightKinds.Timeout,
            ControllerPreflight.ClassifyFault("controller response timeout for requestId=1"));
        Assert.Equal(ControllerPreflightKinds.Launch,
            ControllerPreflight.ClassifyFault("controller process is not running"));
        Assert.Equal(ControllerPreflightKinds.Launch,
            ControllerPreflight.ClassifyFault(
                "controller process exited without a response (command failed to start or crashed)"));
        Assert.Equal(ControllerPreflightKinds.Launch,
            ControllerPreflight.ClassifyFault("controller stdin failed: pipe closed"));
        Assert.Equal(ControllerPreflightKinds.Launch,
            ControllerPreflight.ClassifyFault("controller stdout failed: boom"));
        Assert.Equal(ControllerPreflightKinds.Protocol,
            ControllerPreflight.ClassifyFault("invalid action: not json"));
        Assert.Equal(ControllerPreflightKinds.Protocol, ControllerPreflight.ClassifyFault(null));
    }

    /// <summary>
    /// 装配决策依赖的失败类别必须与真实进程行为一致:
    /// 启动不了/当场退出 = launch(确定性回退), 坏应答 = protocol(确定性回退),
    /// 不应答 = timeout(保留外部控制器, 只是告警)。
    /// </summary>
    [Fact]
    public void FailureKinds_MatchTheRealFixtureBehaviors()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"definitely-missing-{Guid.NewGuid():N}.exe");
        Assert.Equal(ControllerPreflightKinds.Launch,
            ControllerPreflight.Run(Profile($"\"{missing}\" echo")).FailureKind);
        Assert.Equal(ControllerPreflightKinds.Launch,
            ControllerPreflight.Run(Profile(FixtureCommand("die"))).FailureKind);
        Assert.Equal(ControllerPreflightKinds.Protocol,
            ControllerPreflight.Run(Profile(FixtureCommand("bad"))).FailureKind);
        Assert.Equal(ControllerPreflightKinds.Protocol,
            ControllerPreflight.Run(Profile(FixtureCommand("wrongid"))).FailureKind);
        Assert.Equal(ControllerPreflightKinds.Timeout,
            ControllerPreflight.Run(Profile(FixtureCommand("hang"), timeoutMs: 300)).FailureKind);
        Assert.Equal("", ControllerPreflight.Run(Profile(FixtureCommand("echo"))).FailureKind);
    }

    private static int ProcessCount()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("EchoController").Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
