using System.Text.Json;
using System.Text.Json.Nodes;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Legacy compatibility checks: the <c>sensors</c> alias block must round-trip
/// alongside <c>rawSensors</c> exactly as CONTRACT.md requires, the sensor
/// layout must stay the authoritative channel source, and observations written
/// by the old prototype must still deserialize.
/// </summary>
public class LegacyAliasTests
{
    /// <summary>The observation example from the legacy SIMULATOR.md, verbatim shape.</summary>
    private const string LegacyObservationJson = """
        {"requestId":5,"t":12.3,"role":"us","timer":107.7,"scores":{"us":3,"them":0},
         "robot":{"x":1.9,"y":1.9,"th":0.5,"v":0.9,"w":0.1,"onPlatform":true,"hang":false,"state":"SEARCH","action":"旋转扫描"},
         "sensors":{"gF":940,"gB":920,"gL":300,"gR":310,"uL":1,"uR":1,"sFL":0.9,"sFR":0.8,
                     "dLF":0.1,"dRF":0.72,"dLB":0.0,"dRB":0.3,"f":0.98,"r":0.0},
         "rawSensors":{"gray_front":940,"gray_rear":920,"gray_left":300,"gray_right":310,
                        "diag_left_front":0.1,"diag_left_rear":0.0,"diag_right_front":0.72,"diag_right_rear":0.3,
                        "shovel_under_left":1,"shovel_under_right":1,"shovel_front":0.9},
         "sensorLayout":{"id":"wheeledCombat11","channels":[{"id":"gray_front","type":"gray","forward":0.11,"lateral":0}]},
         "perception":{"vision":{"mode":"default","external":{"roles":{"us":{"frameId":null,"detection":null}}}}},
         "opponent":{"x":2.6,"y":2.0,"th":-2.2,"onPlatform":true,"state":"SCORE_BLOCK"},
         "objects":{"buffs":[{"x":1.4,"y":1.3,"onPlatform":true}],"debuff":{"x":2.2,"y":2.5,"onPlatform":true}}}
        """;

    [Fact]
    public void LegacyObservation_Deserializes()
    {
        var observation = ProtocolJson.Deserialize<Observation>(LegacyObservationJson);

        Assert.Equal(5, observation.RequestId);
        Assert.Equal(12.3, observation.T);
        Assert.Equal(RoleNames.Us, observation.Role);
        Assert.Equal(107.7, observation.Timer, 5);
        Assert.Equal(3, observation.Scores.Us);
        Assert.Equal(0, observation.Scores.Them);

        // New fields default sensibly for legacy payloads.
        Assert.Equal(0, observation.Tick);
        Assert.Equal(ProtocolVersion.Current, observation.Version);

        // Legacy aliases and raw channels must both be present and independent.
        Assert.NotNull(observation.Sensors);
        Assert.Equal(940, observation.Sensors!.GrayFront);
        Assert.Equal(0.9, observation.Sensors.ShovelFrontLeft);
        Assert.Equal(0.98, observation.Sensors.Front);
        Assert.Equal(0.0, observation.Sensors.Rear);
        Assert.Equal(11, observation.RawSensors!.Count);
        Assert.Equal(940, observation.RawSensors["gray_front"]);
        Assert.Equal(0.9, observation.RawSensors["shovel_front"]);

        Assert.Equal("wheeledCombat11", observation.SensorLayout!.Id);
        Assert.Single(observation.SensorLayout.Channels);
        Assert.Equal(SensorType.Gray, observation.SensorLayout.Channels[0].Type);

        Assert.Equal("SCORE_BLOCK", observation.Opponent!.State);
        Assert.Single(observation.Objects!.Buffs);
        Assert.NotNull(observation.Objects.Debuff);
        Assert.True(observation.Perception!.Vision!.External!.Value.GetProperty("roles").GetProperty("us").GetProperty("detection").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void SensorsAlias_RoundTripsAlongsideRawSensors()
    {
        var observation = Samples.Observation();
        var json = ProtocolJson.Serialize(observation);
        var root = JsonNode.Parse(json)!.AsObject();

        // Both blocks must exist independently on the wire.
        Assert.True(root.ContainsKey("sensors"), "'sensors' alias block missing.");
        Assert.True(root.ContainsKey("rawSensors"), "'rawSensors' block missing.");
        Assert.True(root.ContainsKey("sensorLayout"), "'sensorLayout' block missing.");

        var sensors = root["sensors"]!.AsObject();
        foreach (var alias in new[] { "gF", "gB", "gL", "gR", "uL", "uR", "sFL", "sFR", "dLF", "dRF", "dLB", "dRB", "f", "r" })
        {
            Assert.True(sensors.ContainsKey(alias), $"legacy alias '{alias}' missing from sensors block.");
        }
        Assert.Equal(940, (double)sensors["gF"]!);
        Assert.Equal(0.98, (double)sensors["f"]!);
        Assert.Equal(0.0, (double)sensors["r"]!);

        var raw = root["rawSensors"]!.AsObject();
        Assert.Equal(11, raw.Count);
        Assert.Equal(940, (double)raw["gray_front"]!);

        // Round-trip must preserve both blocks verbatim.
        var roundTripped = ProtocolJson.Deserialize<Observation>(json);
        Assert.Equal(940, roundTripped.Sensors!.GrayFront);
        Assert.Equal(0.9, roundTripped.Sensors.ShovelFrontLeft);
        Assert.Equal(940, roundTripped.RawSensors!["gray_front"]);
        Assert.Equal(0.9, roundTripped.RawSensors["shovel_front"]);
        Assert.Equal("wheeledCombat11", roundTripped.SensorLayout!.Id);
    }

    [Fact]
    public void WheeledCombat11_CompatMapping_FollowsContract()
    {
        var profile = SensorProfiles.WheeledCombat11;

        Assert.Equal(11, profile.Channels.Count);
        Assert.Equal("wheeledCombat11", profile.Id);

        // Single shovel-front channel feeds both sFL and sFR aliases.
        Assert.Equal("shovel_front", profile.Logical!["sFL"].Channel);
        Assert.Equal("shovel_front", profile.Logical["sFR"].Channel);

        // r is explicitly unmapped (compatibility value 0, never a faked channel).
        Assert.True(profile.Logical["r"].IsNull);

        // f is a virtual max() over the two front diagonal channels.
        var front = profile.Logical["f"];
        Assert.Null(front.Channel);
        Assert.Equal(["diag_left_front", "diag_right_front"], front.Channels);
        Assert.Equal("max", front.Reducer);
        Assert.True(front.Virtual);

        // Serialized logical mapping keeps the legacy JSON shapes.
        var json = ProtocolJson.Serialize(profile);
        Assert.Contains("\"sFL\":\"shovel_front\"", json);
        Assert.Contains("\"sFR\":\"shovel_front\"", json);
        Assert.Contains("\"r\":null", json);
        Assert.Contains("\"reducer\":\"max\"", json);
        Assert.Contains("\"virtual\":true", json);

        // ...and survives the round-trip.
        var roundTripped = ProtocolJson.Deserialize<SensorProfile>(json);
        Assert.True(roundTripped.Logical!["r"].IsNull);
        Assert.Equal("shovel_front", roundTripped.Logical["sFL"].Channel);
        Assert.Equal(["diag_left_front", "diag_right_front"], roundTripped.Logical["f"].Channels);
    }

    [Fact]
    public void Legacy14_Profile_MapsAliasesToTheirOwnChannels()
    {
        var profile = SensorProfiles.Legacy14;

        Assert.Equal(14, profile.Channels.Count);
        Assert.Equal("legacy14", profile.Id);
        foreach (var alias in new[] { "gF", "gB", "gL", "gR", "uL", "uR", "sFL", "sFR", "dLF", "dRF", "dLB", "dRB", "f", "r" })
        {
            Assert.Equal(alias, profile.Logical![alias].Channel);
        }
        Assert.Empty(profile.Validate());
    }

    [Fact]
    public void VehicleProfile_Default_UsesLegacy14Sensors()
    {
        var vehicle = VehicleProfile.Default;
        Assert.Equal("legacy14", vehicle.Sensors!.Id);
        Assert.Equal(0.26, vehicle.Length);
        Assert.Equal(1.5, vehicle.MaxSpeed);
        Assert.Equal(4.0, vehicle.MaxTurnRate);
        Assert.Equal(0.16, vehicle.CollisionRadius);
        Assert.Empty(vehicle.Validate());
    }
}
