using System.Text.Json;
using System.Text.Json.Nodes;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Serialize → deserialize round-trip checks. Because several DTOs contain
/// dictionaries (which get reference equality), equality is verified both as
/// canonical-JSON string equality and structural JsonNode deep equality.
/// </summary>
public class RoundTripTests
{
    private static void AssertRoundTrips<T>(T value)
    {
        var first = ProtocolJson.Serialize(value);
        var roundTripped = ProtocolJson.Deserialize<T>(first);
        Assert.NotNull(roundTripped);
        var second = ProtocolJson.Serialize(roundTripped);

        Assert.Equal(first, second);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(first), JsonNode.Parse(second)),
            "Structural JSON mismatch after round-trip.");
    }

    [Fact]
    public void Observation_RoundTrips()
        => AssertRoundTrips(Samples.Observation());

    [Fact]
    public void Action_RoundTrips_AndHasValueEquality()
    {
        var action = new RobotAction { V = 0.5, W = -0.25, RequestId = "12" };
        AssertRoundTrips(action);

        // Pure-scalar record: value equality must survive the round-trip.
        var roundTripped = ProtocolJson.Deserialize<RobotAction>(ProtocolJson.Serialize(action));
        Assert.Equal(action, roundTripped);
    }

    [Fact]
    public void Event_RoundTrips()
        => AssertRoundTrips(Samples.Event());

    [Fact]
    public void Snapshot_RoundTrips()
        => AssertRoundTrips(Samples.Snapshot());

    [Fact]
    public void Scenario_RoundTrips()
        => AssertRoundTrips(Samples.Scenario());

    [Fact]
    public void ReplayHeader_RoundTrips()
        => AssertRoundTrips(Samples.ReplayHeader());

    [Fact]
    public void SensorProfiles_RoundTrip()
    {
        AssertRoundTrips(SensorProfiles.Legacy14);
        AssertRoundTrips(SensorProfiles.WheeledCombat11);
    }

    [Fact]
    public void SensorChannel_Height_RoundTrips()
    {
        // null Height must be omitted from the wire; a set value must survive.
        var withHeight = SensorProfiles.WheeledCombat11.Channels[0]; // gray_front: height = 0.0025 (standing ground clearance)
        Assert.Equal(0.0025, withHeight.Height);
        var json = ProtocolJson.Serialize(withHeight);
        Assert.Contains("\"height\":0.0025", json);
        var restored = ProtocolJson.Deserialize<SensorChannel>(json);
        Assert.Equal(withHeight.Height, restored.Height);

        // Built-in layouts are referencable by id only (protocol add-only).
        var reference = ProtocolJson.Deserialize<SensorProfile>("""{"id":"wheeledCombat11"}""");
        Assert.NotNull(reference);
        Assert.Equal(SensorProfiles.WheeledCombat11, reference);
        Assert.Equal(11, reference!.Channels.Count);

        // An explicit (possibly partial) channel list still binds as before.
        var explicitProfile = ProtocolJson.Deserialize<SensorProfile>(
            """{"id":"custom","channels":[{"id":"gF","type":"gray","forward":0.11,"lateral":0}]}""");
        Assert.Equal("custom", explicitProfile!.Id);
        Assert.Single(explicitProfile.Channels);
        Assert.Null(explicitProfile.Channels[0].Height);
    }

    [Fact]
    public void Observation_UsesLegacyWireNames()
    {
        var json = ProtocolJson.Serialize(Samples.Observation());
        var root = JsonNode.Parse(json)!.AsObject();

        foreach (var key in new[]
                 {
                     "protocolVersion", "requestId", "tick", "t", "role", "timer", "scores",
                     "robot", "sensors", "rawSensors", "sensorLayout", "perception", "opponent", "objects",
                 })
        {
            Assert.True(root.ContainsKey(key), $"observation JSON must contain '{key}'.");
        }

        // Enum spellings must match the legacy protocol.
        Assert.Contains("\"type\":\"ir_edge\"", json);
        Assert.Contains("\"type\":\"gray\"", json);
        Assert.Contains("\"type\":\"digital\"", json);
    }

    [Fact]
    public void MatchPhase_SerializesAsLegacyUppercase()
    {
        Assert.Contains("\"phase\":\"PREP\"", ProtocolJson.Serialize(new Snapshot()));
        Assert.Contains("\"phase\":\"RUN\"", ProtocolJson.Serialize(Samples.Snapshot()));

        var deserialized = ProtocolJson.Deserialize<Snapshot>("""{"phase":"DONE","done":true,"doneReason":"比赛时间结束"}""");
        Assert.Equal(MatchPhase.Done, deserialized.Phase);
    }

    [Fact]
    public void EventKind_SerializesAsSnakeCase()
    {
        foreach (var (kind, wire) in new[]
                 {
                     (EventKind.BlockOff, "block_off"),
                     (EventKind.BlockScore, "block_score"),
                     (EventKind.RestartPenalty, "restart_penalty"),
                     (EventKind.SimultaneousDrop, "simultaneous_drop"),
                     (EventKind.Inactivity, "inactivity"),
                     (EventKind.Mount, "mount"),
                 })
        {
            var json = ProtocolJson.Serialize(new Event { Seq = 1, Type = kind });
            Assert.Contains($"\"type\":\"{wire}\"", json);

            var roundTripped = ProtocolJson.Deserialize<Event>(json);
            Assert.Equal(kind, roundTripped.Type);
        }
    }

    [Fact]
    public void AllMessages_StampCurrentVersion()
    {
        Assert.Equal(ProtocolVersion.Current, Samples.Observation().Version);
        Assert.Equal(ProtocolVersion.Current, new RobotAction().Version);
        Assert.Equal(ProtocolVersion.Current, Samples.Event().Version);
        Assert.Equal(ProtocolVersion.Current, Samples.Snapshot().Version);
        Assert.Equal(ProtocolVersion.Current, Samples.Scenario().Version);
        Assert.Equal(ProtocolVersion.Current, Samples.ReplayHeader().Version);
    }

    [Fact]
    public void AllSamples_PassValidation()
    {
        ProtocolValidator.EnsureValid(Samples.Observation());
        ProtocolValidator.EnsureValid(new RobotAction { V = 0.5, W = -0.25 });
        ProtocolValidator.EnsureValid(Samples.Event());
        ProtocolValidator.EnsureValid(Samples.Snapshot());
        ProtocolValidator.EnsureValid(Samples.Scenario());
        ProtocolValidator.EnsureValid(Samples.ReplayHeader());
    }
}
