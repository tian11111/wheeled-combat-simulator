using System.Text.Json;
using Sim.Cli;
using Sim.Protocol;

namespace Sim.Tests;

public class RlEnvCommandTests
{
    [Fact]
    public void Observation_AppendsNormalizedOwnPositionAfterTheExistingNineValues()
    {
        var baseObservation = Enumerable.Range(1, 9).Select(value => value / 10.0).ToArray();
        var platform = new Region { MinX = 1.0, MinY = 2.0, MaxX = 5.0, MaxY = 6.0 };

        var observation = RlEnvCommand.AppendOwnPositionObservation(baseObservation, ownX: 5.0, ownY: 2.0, platform: platform);

        Assert.Equal(11, observation.Length);
        Assert.Equal(baseObservation, observation[..9]);
        Assert.Equal(1.0, observation[9], 12);
        Assert.Equal(-1.0, observation[10], 12);
    }

    [Fact]
    public void Observation_ClampsOwnPositionToPlatformSideRange()
    {
        var baseObservation = new double[9];
        var platform = new Region { MinX = 1.0, MinY = 2.0, MaxX = 5.0, MaxY = 6.0 };

        var observation = RlEnvCommand.AppendOwnPositionObservation(baseObservation, ownX: 100.0, ownY: -100.0, platform: platform);

        Assert.Equal(1.0, observation[9]);
        Assert.Equal(-1.0, observation[10]);
    }

    [Fact]
    public void TargetScore_RequiresLockedBlockExitAndSameTickOwnedScore()
    {
        var blocks = new[]
        {
            ("增益块", BlockKind.Buff, false, true),
            ("另一个", BlockKind.Buff, false, false),
        };
        var events = new[]
        {
            (EventKind.BlockScore, true, false, 12L, (string?)"增益块"),
        };

        var result = RlEnvCommand.ClassifyTargetOutcome(0, "增益块", blocks, events, 12);

        Assert.True(result.TargetScored);
        Assert.False(result.TargetLost);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void TargetScore_WithDuplicateNameExitsOnSameTick_IsAmbiguous()
    {
        var blocks = new[]
        {
            ("增益块", BlockKind.Buff, false, true),
            ("增益块", BlockKind.Buff, false, true),
        };
        var events = new[]
        {
            (EventKind.BlockScore, true, false, 12L, (string?)"增益块"),
        };

        var result = RlEnvCommand.ClassifyTargetOutcome(0, "增益块", blocks, events, 12);

        Assert.False(result.TargetScored);
        Assert.True(result.TargetLost);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public void TargetScore_IgnoresOtherTargetAndPriorTickEvents()
    {
        var blocks = new[]
        {
            ("增益块", BlockKind.Buff, false, true),
            ("另一个", BlockKind.Buff, false, false),
        };
        var events = new[]
        {
            (EventKind.BlockScore, true, false, 11L, (string?)"增益块"),
            (EventKind.BlockScore, true, false, 12L, (string?)"另一个"),
        };

        var result = RlEnvCommand.ClassifyTargetOutcome(0, "增益块", blocks, events, 12);

        Assert.False(result.TargetScored);
        Assert.True(result.TargetLost);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void UnownedBlockOff_IsCountedOnlyWhenTheLockedExitIsUnique()
    {
        var blocks = new[]
        {
            ("增益块", BlockKind.Buff, false, true),
            ("增益块", BlockKind.Buff, false, false),
        };
        var events = new[]
        {
            (EventKind.BlockOff, false, true, 12L, (string?)"增益块"),
        };

        var result = RlEnvCommand.ClassifyTargetOutcome(0, "增益块", blocks, events, 12);

        Assert.True(result.TargetLost);
        Assert.True(result.TargetBlockOff);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void MalformedRequestsReturnErrorsAndCloseStillCleansUp()
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("{bad}\n{}\n{\"op\":\"close\"}\n");
        using var output = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            Assert.Equal(0, RlEnvCommand.Run([]));
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(3, responses.Length);
            Assert.All(responses.Take(2), response => Assert.Equal("error", response.RootElement.GetProperty("type").GetString()));
            Assert.Equal("closed", responses[2].RootElement.GetProperty("type").GetString());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }
}
