using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 桌面后台 driver: 既有外部控制器语义 + SCORE_BLOCK 展演交接(预推进→交接门控)与
/// legacy 场景拒绝。会展演用例起 EchoController 子进程并按墙钟跑 MuJoCo 场景,
/// 与 CLI/控制器进程 fixture 同属串行集合, 不与墙钟敏感测试并行抢 CPU。
/// </summary>
[Collection("cli-console")]
public class DesktopLiveDriverTests
{
    [Fact]
    public async Task BuiltInDriver_CommandsAreSerializedOnWorkerAndPublishesSnapshots()
    {
        var scenario = Samples.Scenario() with
        {
            Field = Samples.Scenario().Field with { MatchDuration = 2.0 },
        };
        using var driver = new DesktopLiveDriver(scenario, new ControllerProfile(), new ControllerProfile());
        driver.Start();
        driver.RequestArm();

        await WaitUntil(() => driver.Status.Phase == Sim.Core.MatchControlPhase.Running);
        Assert.Equal(0, driver.Status.UsController.Faults);
        Assert.Equal(0, driver.Status.ThemController.Faults);

        driver.RequestPause();
        await WaitUntil(() => driver.Status.Paused);
        driver.RequestResume();
        await WaitUntil(() => !driver.Status.Paused && driver.Status.Phase == Sim.Core.MatchControlPhase.Running);
        await WaitUntil(() => driver.Status.Tick > 0);

        Assert.True(driver.TryTakeLatest(out var snapshot));
        Assert.True(snapshot.Tick > 0 && snapshot.Tick <= driver.Status.Tick);
    }

    [Fact]
    public async Task ExternalDriver_UsesSharedJsonlBridgeAndDisposesController()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "EchoController.exe");
        Assert.True(File.Exists(executable), $"EchoController fixture missing: {executable}");
        var scenario = Samples.Scenario() with
        {
            Field = Samples.Scenario().Field with { MatchDuration = 0.35 },
        };
        var profile = new ControllerProfile
        {
            Mode = ControllerModes.External,
            Command = $"\"{executable}\" echo",
            TimeoutMs = 500,
        };
        using var driver = new DesktopLiveDriver(scenario, profile, new ControllerProfile());
        driver.Start();
        driver.RequestArm();

        await WaitUntil(() => driver.Status.Done || driver.Status.UsController.Faults > 0,
            timeoutMs: 5000);

        Assert.True(driver.Status.Done);
        Assert.True(driver.Status.UsController.Configured);
        Assert.True(driver.Status.UsController.Running || driver.Status.UsController.Faults == 0);
        Assert.True(driver.TryTakeLatest(out _));
    }

    [Fact]
    public async Task ExhibitionDriver_PrerollsToScoreBlockThenHandsOffToTheExternalPolicy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // EchoController.exe fixture 仅 Windows 构建。
        }
        var executable = Path.Combine(AppContext.BaseDirectory, "EchoController.exe");
        Assert.True(File.Exists(executable), $"EchoController fixture missing: {executable}");
        var profile = new ControllerProfile
        {
            Mode = ControllerModes.External,
            // rlguard: 只在帧真的携带 11 维有限 rlObservation 时才应答, 否则退出
            // ⇒ 下面两处 faults==0 同时钉住 driver 每帧都注入了 rlObservation
            // (删掉 DesktopLiveDriver.BuildExhibitionObservation 的注入赋值即 red)。
            Command = $"\"{executable}\" rlguard",
            TimeoutMs = 500,
        };
        using var driver = new DesktopLiveDriver(MujocoScenario(), profile, new ControllerProfile(),
            exhibitionUs: true);
        driver.Start();
        driver.RequestArm();

        // 可见预推进：逐 tick 探测入场（真实时间节奏，上台阶段照常发布快照），
        // 到 SCORE_BLOCK 即交接；能到达交接本身就证明预推进期间我方没有收到任何
        // 外部动作（非 null 动作会把角色切 Manual，永远进不了 SCORE_BLOCK，同 CLI
        // 展演口径）。同场景入场 tick 与 CLI 静默快进逐位一致（10-02 域标定重录: 281 → 369）。
        await WaitUntil(() => driver.Status.Handoff is not null || driver.Status.HandoffReason is not null,
            timeoutMs: 60000, message: "driver did not hand off");
        Assert.Null(driver.Status.HandoffReason);
        Assert.NotNull(driver.Status.Handoff);
        Assert.Contains("tick=369", driver.Status.Handoff!);
        Assert.Contains("增益块", driver.Status.Handoff!);
        Assert.True(driver.Status.UsController.Running);
        Assert.Equal(0, driver.Status.UsController.Faults);

        // 交接后外部动作逐 tick 生效: 我方被切进 MANUAL(外部策略语义), 快照继续前进。
        Snapshot? latest = null;
        await WaitUntil(() =>
        {
            if (driver.TryTakeLatest(out var snapshot))
            {
                latest = snapshot;
            }
            return latest is not null && latest.Tick > 283;
        }, timeoutMs: 20000, message: "driver did not advance past the handoff tick");
        Assert.Equal("MANUAL", latest!.Robots[RoleNames.Us].State);
        Assert.Equal(0, driver.Status.UsController.Faults);
    }

    [Fact]
    public async Task ExhibitionDriver_RejectsLegacyScenarioAndKeepsTheBuiltInFsm()
    {
        // 壳层按 ControllerWiring 先拒绝; driver 侧是最后防线: 不拉起我方子进程,
        // 状态里响亮给出原因, 我方全程内置 FSM(不静默假装展演成功)。
        var executable = Path.Combine(AppContext.BaseDirectory, "EchoController.exe");
        var profile = new ControllerProfile
        {
            Mode = ControllerModes.External,
            Command = $"\"{executable}\" hang",
            TimeoutMs = 200,
        };
        var scenario = Samples.Scenario() with
        {
            Field = Samples.Scenario().Field with { MatchDuration = 1.0 },
        };
        using var driver = new DesktopLiveDriver(scenario, profile, new ControllerProfile(),
            exhibitionUs: true);
        driver.Start();
        driver.RequestArm();

        await WaitUntil(() => driver.Status.Tick > 5, message: "driver did not tick");
        Assert.Equal(ControllerWiring.RequiresMujocoScenario, driver.Status.HandoffReason);
        Assert.True(driver.Status.UsController.Configured);
        Assert.False(driver.Status.UsController.Running);
        // 拒绝路径不启动/不调用外部进程(hang 命令本会超时计 fault), 我方不是 MANUAL。
        Assert.Equal(0, driver.Status.UsController.Faults);
        Assert.True(driver.TryTakeLatest(out var snapshot));
        Assert.NotEqual("MANUAL", snapshot.Robots[RoleNames.Us].State);
        await WaitUntil(() => driver.Status.Done, timeoutMs: 5000, message: "match did not finish");
    }

    private static Scenario MujocoScenario()
    {
        // 与 MatchRunnerExhibitionTests 同一场景文件与 README 上手方式; 只改赛时避免长跑。
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "scenarios", "wushu-ring-2026-mujoco.json"));
        Assert.True(File.Exists(path), $"scenario fixture missing: {path}");
        var scenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"scenario '{path}' failed to deserialize");
        return scenario with { Seed = 42, Field = scenario.Field with { MatchDuration = 120.0 } };
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000, string? message = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(message ?? "Timed out waiting for desktop driver state.");
            }
            await Task.Delay(10);
        }
    }
}
