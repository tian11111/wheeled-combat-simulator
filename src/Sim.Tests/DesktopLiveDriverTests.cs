using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

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

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for desktop driver state.");
            }
            await Task.Delay(10);
        }
    }
}
