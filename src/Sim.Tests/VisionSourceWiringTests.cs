using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 桌面视觉源工厂的接线回归(无 Godot 环境可跑): MatchSession(ctor 与 ResetToLive)
/// 与 DesktopLiveDriver(自建引擎)都必须经同一份工厂, 且工厂为 null 时保持默认
/// classifyRate 桩 —— 默认位逐位不变。
/// </summary>
public class VisionSourceWiringTests
{
    private const string HuntCsvPath = "src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv";

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
    public void DefaultFactory_KeepsTheClassifyRateStubIdentity()
    {
        using var session = new MatchSession(new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks });

        Assert.Null(DesktopSettings.Default.CreateVisionFactory());
        Assert.Equal("default", session.Engine.BuildReplayHeader().VisionMode);
        Assert.Equal("default", session.Engine.BuildSnapshot().Perception?.Vision?.Mode);
        // 默认源不注入: 无证据身份, 随机桩参数照旧上报(回放头/观测量逐位兼容)。
        Assert.Null(session.Engine.BuildReplayHeader().VisionEvidenceId);
        Assert.Null(session.Engine.BuildReplayHeader().VisionEvidenceSha256);
        Assert.NotNull(session.Engine.BuildSnapshot().Perception?.Vision?.ClassifyRate);
    }

    [Fact]
    public void MatchSession_InvokesTheFactoryForEveryNewEngine()
    {
        var settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings
            {
                Source = VisionSources.LiveBridge,
                CsvPath = FindRepoFile(HuntCsvPath),
            },
        };
        var factory = settings.CreateVisionFactory()!;
        var calls = 0;
        Func<IVisionAdapter?> counting = () => { calls++; return factory(); };

        using var session = new MatchSession(new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks }, counting);
        Assert.Equal(1, calls);
        Assert.Equal(LiveVisionBridge.ModeName, session.Engine.BuildReplayHeader().VisionMode);

        // F5 重置 = 新引擎: 必须再调一次工厂(游标/台账不跨场复用)。
        session.ResetToLive();
        Assert.Equal(2, calls);
        Assert.Equal(LiveVisionBridge.ModeName, session.Engine.BuildReplayHeader().VisionMode);
        Assert.Equal(LiveVisionBridge.ModeName, session.Engine.BuildSnapshot().Perception?.Vision?.Mode);
    }

    [Fact]
    public async Task DesktopLiveDriver_BuildsItsEngineThroughTheSameFactory()
    {
        var calls = 0;
        Func<IVisionAdapter?> counting = () => { Interlocked.Increment(ref calls); return null; };
        using var driver = new DesktopLiveDriver(
            Samples.Scenario() with { Field = Samples.Scenario().Field with { MatchDuration = 1.0 } },
            new ControllerProfile(),
            new ControllerProfile(),
            counting);

        driver.Start();
        driver.RequestArm();

        // 引擎是工作线程首个动作: Tick 前进即证明工厂已在建引擎时被调用。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (driver.Status.Tick == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(driver.Status.Tick > 0, "driver did not tick");
        Assert.Equal(1, Volatile.Read(ref calls));
    }
}
