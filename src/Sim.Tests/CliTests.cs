using Sim.Cli;

namespace Sim.Tests;

/// <summary>
/// In-process regression for the headless CLI entry points (implement.md item 5):
/// a recorded match must pass replay-check regardless of whether external
/// controllers were attached, and a corrupted replay must fail.
/// </summary>
public class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sim-cli-tests").FullName;
    private readonly TextWriter _stdout = Console.Out;

    private string Out(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        Console.SetOut(_stdout);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup on Windows
        }
    }

    private static int Run(params string[] args) => Program.Main(args);

    [Fact]
    public void RecordThenCheck_FsmOnly_Passes()
    {
        var file = Out("fsm.json");
        Assert.Equal(0, Run("replay-record", "--seed", "7", "--out", file));
        Assert.True(File.Exists(file));
        Assert.Equal(0, Run("replay-check", file));
    }

    [Fact]
    public void CheckTamperedReplay_Fails()
    {
        var file = Out("tamper.json");
        Assert.Equal(0, Run("replay-record", "--seed", "7", "--out", file));

        // Corrupt the expected final scores so the checker must detect divergence.
        var json = File.ReadAllText(file);
        var corrupted = json.Replace("\"finalScores\":{\"us\":", "\"finalScores\":{\"us\":99,\"__us\":");
        Assert.NotEqual(json, corrupted);
        File.WriteAllText(file, corrupted);
        Assert.NotEqual(0, Run("replay-check", file));
    }

    [Fact]
    public void MatchRunsToCompletion_WithEvents()
    {
        using var sink = new StringWriter();
        Console.SetOut(sink);
        var code = Run("match", "--seed", "7", "--events");
        Console.SetOut(_stdout);
        Assert.Equal(0, code);
        var output = sink.ToString();
        Assert.Contains("score 我方", output);
        Assert.Contains("done=", output);
    }
}
