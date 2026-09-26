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
