using System.Text.Json;
using Sim.Cli;
using Sim.Protocol;

namespace Sim.Tests;

[Collection("cli-console")]
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
    public void Timing_IsOptInAndDoesNotAffectResetOrStepDeterminism()
    {
        using var withoutTiming = RunEpisode(includeResetTiming: false, enableTiming: false);
        using var withTiming = RunEpisode(includeResetTiming: true, enableTiming: true);

        var plainReset = withoutTiming.Responses[0].RootElement;
        var timedReset = withTiming.Responses[0].RootElement;
        var plainStep = withoutTiming.Responses[1].RootElement;
        var timedStep = withTiming.Responses[1].RootElement;
        var plainFsmStep = withoutTiming.Responses[2].RootElement;
        var timedFsmStep = withTiming.Responses[2].RootElement;

        Assert.False(plainReset.GetProperty("info").TryGetProperty("timing", out _));
        Assert.False(plainStep.GetProperty("info").TryGetProperty("timing", out _));
        Assert.False(plainFsmStep.GetProperty("info").TryGetProperty("timing", out _));
        AssertTimingFields(timedReset.GetProperty("info").GetProperty("timing"), "reset",
            "resetMs", "prerollMs", "totalMs");
        AssertTimingFields(timedStep.GetProperty("info").GetProperty("timing"), "step",
            "tickMs", "totalMs");
        AssertTimingFields(timedFsmStep.GetProperty("info").GetProperty("timing"), "step",
            "tickMs", "totalMs");

        var resetInfo = timedReset.GetProperty("info");
        var resetTiming = resetInfo.GetProperty("timing");
        if (resetInfo.GetProperty("entry_tick").GetInt32() >= 0)
        {
            Assert.Equal(resetInfo.GetProperty("entry_tick").GetInt32(),
                resetTiming.GetProperty("prerollTicks").GetInt32());
        }
        else
        {
            Assert.Equal(resetInfo.GetProperty("pre_roll_ticks").GetInt32(),
                resetTiming.GetProperty("prerollTicks").GetInt32());
        }

        Assert.Equal(plainReset.GetProperty("obs").GetRawText(), timedReset.GetProperty("obs").GetRawText());
        Assert.Equal(plainStep.GetProperty("obs").GetRawText(), timedStep.GetProperty("obs").GetRawText());
        Assert.Equal(plainStep.GetProperty("reward").GetDouble(), timedStep.GetProperty("reward").GetDouble());
        Assert.Equal(plainStep.GetProperty("terminated").GetBoolean(), timedStep.GetProperty("terminated").GetBoolean());
        Assert.Equal(plainStep.GetProperty("truncated").GetBoolean(), timedStep.GetProperty("truncated").GetBoolean());
    }

    private static void AssertTimingFields(JsonElement timing, string kind, params string[] durationFields)
    {
        Assert.Equal(kind, timing.GetProperty("kind").GetString());
        foreach (var field in durationFields)
        {
            var value = timing.GetProperty(field);
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            Assert.True(value.GetDouble() >= 0.0);
        }

        if (kind == "reset")
        {
            Assert.Equal(JsonValueKind.Number, timing.GetProperty("prerollTicks").ValueKind);
            Assert.True(timing.GetProperty("prerollTicks").TryGetInt32(out _));
        }
    }

    private static EpisodeResponses RunEpisode(bool includeResetTiming, bool enableTiming)
    {
        var scenarioPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "scenarios", "wushu-ring-2026-mujoco.json"));
        var resetTiming = includeResetTiming ? ",\"timing\":true" : string.Empty;
        var stepTiming = enableTiming ? "true" : "false";
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader(
            $"{{\"op\":\"reset\",\"seed\":42{resetTiming}}}\n" +
            $"{{\"op\":\"step\",\"v\":0.0,\"w\":0.0,\"timing\":{stepTiming}}}\n" +
            $"{{\"op\":\"step_fsm\",\"timing\":{stepTiming}}}\n" +
            "{\"op\":\"close\"}\n");
        using var output = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            Assert.Equal(0, RlEnvCommand.Run(["--scenario", scenarioPath]));
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.StartsWith("{", line));
        return new EpisodeResponses(lines.Select(line => JsonDocument.Parse(line)).ToArray());
    }

    private sealed class EpisodeResponses(JsonDocument[] responses) : IDisposable
    {
        public JsonDocument[] Responses { get; } = responses;

        public void Dispose()
        {
            foreach (var response in Responses) response.Dispose();
        }
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
