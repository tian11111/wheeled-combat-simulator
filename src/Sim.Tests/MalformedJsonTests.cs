using System.Text.Json;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>Malformed JSON and structural validation rejection tests.</summary>
public class MalformedJsonTests
{
    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("{\"t\":}")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void Deserialize_RejectsMalformedJson(string json)
    {
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<Observation>(json));
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<RobotAction>(json));
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<ReplayHeader>(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"v\":\"abc\",\"w\":0}")]
    [InlineData("{\"v\":{},\"w\":0}")]
    [InlineData("{\"v\":0,\"w\":true}")]
    [InlineData("{\"v\":0,\"w\":0,\"requestId\":{}}")]
    public void TryDeserialize_ReturnsFalseOnMalformedJson(string json)
    {
        Assert.False(ProtocolJson.TryDeserialize<RobotAction>(json, out var action));
        Assert.Null(action);
    }

    [Fact]
    public void Deserialize_RejectsWrongTokenTypes()
    {
        // "t" must be a number, not an object.
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<Observation>("""{"t":{"x":1}}"""));

        // robot must be an object.
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<Observation>("""{"robot":5}"""));

        // sensors values must be numbers.
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<Observation>("""{"sensors":{"gF":"high"}}"""));

        // event type must be a known kind.
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<Event>("""{"seq":1,"type":"nonsense"}"""));

        // match phase must be a known legacy string.
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<Snapshot>("""{"phase":"WARMUP"}"""));

        // sensor channel type must be a known legacy spelling.
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<SensorProfile>("""{"channels":[{"id":"x","type":"lidar"}]}"""));
    }

    [Fact]
    public void Validation_RejectsUnknownRoles()
    {
        var observation = Samples.Observation() with { Role = "blue" };
        Assert.Contains(observation.Validate(), e => e.Contains("role"));

        Assert.Throws<ProtocolValidationException>(() => ProtocolValidator.EnsureValid(observation));

        var evt = Samples.Event() with { Role = "red" };
        Assert.Contains(evt.Validate(), e => e.Contains("role"));
    }

    [Fact]
    public void Validation_RejectsMissingOpponentOrBadTimes()
    {
        var observation = Samples.Observation() with { Opponent = null, Tick = -1, T = -0.5, Timer = -1 };
        var errors = observation.Validate().ToList();

        Assert.Contains(errors, e => e.Contains("opponent"));
        Assert.Contains(errors, e => e.Contains("tick"));
        Assert.Contains(errors, e => e.Contains("t must be"));
        Assert.Contains(errors, e => e.Contains("timer"));
    }

    [Fact]
    public void Validation_RejectsInvalidRawSensors()
    {
        var observation = Samples.Observation() with
        {
            RawSensors = new Dictionary<string, double>
            {
                ["gray_front"] = double.NaN,
                [""] = 5,
            },
        };

        Assert.Contains(observation.Validate(), e => e.Contains("gray_front"));
        Assert.Contains(observation.Validate(), e => e.Contains("keys must not be empty"));
    }

    [Fact]
    public void Validation_RejectsBadEventSequenceNumbers()
    {
        Assert.Contains(new Event { Seq = 0 }.Validate(), e => e.Contains("seq"));
        Assert.Contains(new Event { Seq = -3 }.Validate(), e => e.Contains("seq"));
    }

    [Fact]
    public void SnapshotValidation_RejectsNonIncreasingEventSeqs()
    {
        var snapshot = Samples.Snapshot() with
        {
            Events = [Samples.Event(seq: 7), Samples.Event(seq: 7)],
        };

        Assert.Contains(snapshot.Validate(), e => e.Contains("strictly increasing"));
    }

    [Fact]
    public void SnapshotValidation_RequiresDoneReasonWhenDone()
    {
        var snapshot = Samples.Snapshot() with { Done = true, DoneReason = null };
        Assert.Contains(snapshot.Validate(), e => e.Contains("doneReason"));

        var finished = Samples.Snapshot() with { Done = true, DoneReason = "比赛时间结束", Phase = MatchPhase.Done };
        Assert.Empty(finished.Validate());
    }

    [Fact]
    public void SnapshotValidation_RequiresBothRobots()
    {
        var snapshot = Samples.Snapshot() with { Robots = new Dictionary<string, RobotState> { [RoleNames.Us] = new() } };
        Assert.Contains(snapshot.Validate(), e => e.Contains("them"));
    }

    [Fact]
    public void ScenarioValidation_RejectsBadSeedsAndFields()
    {
        var scenario = Samples.Scenario() with { Seed = -1, Field = new FieldParams() with { FieldSize = 0 } };

        var errors = scenario.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("seed"));
        Assert.Contains(errors, e => e.Contains("fieldSize"));
    }

    [Fact]
    public void ScenarioValidation_RejectsHalfSpecifiedBlocks()
    {
        var scenario = Samples.Scenario() with
        {
            Blocks = [new BlockSpec { Kind = BlockKind.Buff, X = 1.0 }],
        };

        Assert.Contains(scenario.Validate(), e => e.Contains("both set or both null"));
    }

    [Fact]
    public void ScenarioValidation_RejectsBadVehicles()
    {
        var scenario = Samples.Scenario() with
        {
            Vehicles = new Dictionary<string, VehicleProfile>
            {
                [RoleNames.Us] = new() { Id = "broken", MaxSpeed = 0 },
                [RoleNames.Them] = new(),
            },
        };

        Assert.Contains(scenario.Validate(), e => e.Contains("maxSpeed"));
    }

    [Fact]
    public void SensorProfileValidation_RejectsDuplicateAndDanglingChannels()
    {
        var dangling = new SensorProfile
        {
            Id = "p",
            Channels = [new SensorChannel { Id = "a" }],
            Logical = new Dictionary<string, LogicalSensorMap>
            {
                ["gF"] = LogicalSensorMap.FromChannel("missing"),
            },
        };
        Assert.Contains(dangling.Validate(), e => e.Contains("unknown channel"));

        var duplicated = new SensorProfile
        {
            Id = "p",
            Channels = [new SensorChannel { Id = "a" }, new SensorChannel { Id = "a" }],
        };
        Assert.Contains(duplicated.Validate(), e => e.Contains("duplicate channel id"));
    }
}
