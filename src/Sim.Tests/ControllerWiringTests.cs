using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 桌面控制器来源装配（纯逻辑，无 Godot/无引擎/无进程）: 我方 external = SCORE_BLOCK
/// 展演, 只在 mujoco 场景启用; legacy 场景(physics.backend 未写)明确拒绝并回退内置
/// FSM; 应用时预检结论按"确定性失败回退 / 超时保留告警"分流; HUD 来源名的命令解析。
/// </summary>
public class ControllerWiringTests
{
    private static Scenario LegacyScenario() => Samples.Scenario();

    private static Scenario MujocoScenario() => Samples.Scenario() with
    {
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
    };

    private static ControllerProfile External(string command = "py -3.12 -X utf8 tools/rl-bridge/rl_desktop_runner.py --checkpoint x.zip")
        => new() { Mode = ControllerModes.External, Command = command, TimeoutMs = 5000 };

    private static ControllerProfile BuiltIn() => new();

    private static ControllerPreflightResult Failed(string kind, string message = "boom")
        => new(false, message) { FailureKind = kind };

    [Fact]
    public void BuiltInDefaults_AreNotAnExhibition()
    {
        var assignment = ControllerWiring.Resolve(MujocoScenario(), BuiltIn(), BuiltIn());

        Assert.Equal(ControllerModes.BuiltIn, assignment.Us.Mode);
        Assert.Equal(ControllerModes.BuiltIn, assignment.Them.Mode);
        Assert.False(assignment.Exhibition);
        Assert.Null(assignment.Notice);
    }

    [Fact]
    public void LegacyScenario_RejectsExternalUs_AndSaysWhy()
    {
        var assignment = ControllerWiring.Resolve(LegacyScenario(), External(), BuiltIn());

        // physics.backend 未写 = legacy 引擎: 外部进程被拒绝, 本场回退内置 FSM。
        Assert.Equal(ControllerModes.BuiltIn, assignment.Us.Mode);
        Assert.False(assignment.Exhibition);
        Assert.NotNull(assignment.Notice);
        Assert.True(assignment.Notice!.IsRejection);
        Assert.Equal(ControllerNoticeKinds.Rejected, assignment.Notice.Kind);
        Assert.Contains("mujoco", assignment.Notice.Message);
        Assert.Contains("physics.backend", assignment.Notice.Message);
        Assert.Equal(ControllerWiring.RequiresMujocoScenario, assignment.Notice.Message);
    }

    [Fact]
    public void LegacyScenario_DoesNotGateThemController()
    {
        // design 决策⑤: 对手语义 unchanged（外部 them 仍照旧从 Running 起决定）。
        var them = External("python opponent.py");
        var assignment = ControllerWiring.Resolve(LegacyScenario(), BuiltIn(), them);

        Assert.Equal(ControllerModes.BuiltIn, assignment.Us.Mode);
        Assert.Equal(ControllerModes.External, assignment.Them.Mode);
        Assert.Equal("python opponent.py", assignment.Them.Command);
        Assert.False(assignment.Exhibition);
        Assert.Null(assignment.Notice);
    }

    [Fact]
    public void MujocoScenario_KeepsExternalUs_AndEnablesExhibition()
    {
        var assignment = ControllerWiring.Resolve(MujocoScenario(), External(), BuiltIn());

        Assert.Equal(ControllerModes.External, assignment.Us.Mode);
        Assert.True(assignment.Exhibition);
        Assert.Null(assignment.Notice);
    }

    [Fact]
    public void DeterministicPreflightFailure_FallsBackToBuiltIn()
    {
        foreach (var kind in new[] { ControllerPreflightKinds.Launch, ControllerPreflightKinds.Protocol })
        {
            var probe = Failed(kind, $"{kind} failed: 坏命令");
            var assignment = ControllerWiring.Resolve(MujocoScenario(), External(), BuiltIn(), probe);

            Assert.Equal(ControllerModes.BuiltIn, assignment.Us.Mode);
            Assert.False(assignment.Exhibition);
            Assert.NotNull(assignment.Notice);
            Assert.True(assignment.Notice!.IsRejection);
            Assert.Contains("坏命令", assignment.Notice.Message);
            Assert.Contains("回退内置 FSM", assignment.Notice.Message);
        }
    }

    [Fact]
    public void PreflightTimeout_KeepsExternalButWarns()
    {
        // RL 桥先加载 checkpoint 再服务: 首帧可能慢于 TimeoutMs, 超时不等于坏命令。
        var probe = Failed(ControllerPreflightKinds.Timeout, "controller response timeout for requestId=1");
        var assignment = ControllerWiring.Resolve(MujocoScenario(), External(), BuiltIn(), probe);

        Assert.Equal(ControllerModes.External, assignment.Us.Mode);
        Assert.True(assignment.Exhibition);
        Assert.NotNull(assignment.Notice);
        Assert.False(assignment.Notice!.IsRejection);
        Assert.Equal(ControllerNoticeKinds.Warning, assignment.Notice.Kind);
        Assert.Contains("timeout", assignment.Notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5000", assignment.Notice.Message);
    }

    [Fact]
    public void PassedPreflight_KeepsExternalWithoutNotice()
    {
        var assignment = ControllerWiring.Resolve(MujocoScenario(), External(), BuiltIn(),
            new ControllerPreflightResult(true, "握手成功 v=0 w=0"));

        Assert.Equal(ControllerModes.External, assignment.Us.Mode);
        Assert.True(assignment.Exhibition);
        Assert.Null(assignment.Notice);
    }

    [Fact]
    public void PreflightIsIgnoredForLegacyScenarios_BecauseTheCommandIsNeverRun()
    {
        // legacy 拒绝优先于预检结论: 不启动进程也不看预检。
        var assignment = ControllerWiring.Resolve(LegacyScenario(), External(), BuiltIn(),
            new ControllerPreflightResult(true, "握手成功"));

        Assert.Equal(ControllerModes.BuiltIn, assignment.Us.Mode);
        Assert.Equal(ControllerWiring.RequiresMujocoScenario, assignment.Notice!.Message);
    }

    [Fact]
    public void ExternalWithoutCommand_IsTreatedAsBuiltIn()
    {
        var assignment = ControllerWiring.Resolve(MujocoScenario(),
            new ControllerProfile { Mode = ControllerModes.External, Command = "   " }, BuiltIn());

        Assert.Equal(ControllerModes.BuiltIn, assignment.Us.Mode);
        Assert.False(assignment.Exhibition);
        Assert.Null(assignment.Notice);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("python", "python")]
    [InlineData("py -3.12 -X utf8 tools/rl-bridge/rl_desktop_runner.py --checkpoint x.zip", "rl_desktop_runner.py")]
    [InlineData("\"C:/Program Files/Bot/bot.exe\" run --fast", "bot.exe")]
    [InlineData("C:\\tools\\bridge\\adapter.py --model m.zip", "adapter.py")]
    public void CommandName_ParsesTheProcessOrScriptIdentity(string command, string expected)
        => Assert.Equal(expected, ControllerWiring.CommandName(command));

    [Fact]
    public void DescribeSource_NamesTheProcessOrTheBuiltInFsm()
    {
        Assert.Equal("内置 FSM", ControllerWiring.DescribeSource(BuiltIn()));
        Assert.Equal("内置 FSM", ControllerWiring.DescribeSource(null));
        Assert.Equal("外部进程 · rl_desktop_runner.py",
            ControllerWiring.DescribeSource(External("py -3.12 -X utf8 tools/rl-bridge/rl_desktop_runner.py --checkpoint x.zip")));
        Assert.Equal("外部进程 · bot.exe",
            ControllerWiring.DescribeSource(External("\"C:/Program Files/Bot/bot.exe\" run")));
    }
}
