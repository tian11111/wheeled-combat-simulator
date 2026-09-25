using Sim.Protocol;

namespace Sim.Tests;

public class MujocoProtocolTests
{
    private const string ModelHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void LegacyMessages_OmitPhysicsFields_AndRoundTrip()
    {
        var scenario = Samples.Scenario();
        var header = Samples.ReplayHeader();
        var snapshot = Samples.Snapshot();

        foreach (var json in new[]
        {
            ProtocolJson.Serialize(scenario),
            ProtocolJson.Serialize(header),
            ProtocolJson.Serialize(snapshot),
        })
        {
            Assert.DoesNotContain("\"physics\"", json);
        }
        Assert.Equal(ProtocolJson.Serialize(scenario), ProtocolJson.RoundTripJson(scenario));
        Assert.Equal(ProtocolJson.Serialize(header), ProtocolJson.RoundTripJson(header));
        Assert.Equal(ProtocolJson.Serialize(snapshot), ProtocolJson.RoundTripJson(snapshot));
        Assert.Null(ProtocolJson.Deserialize<Scenario>(ProtocolJson.Serialize(scenario)).Physics);
        Assert.Null(ProtocolJson.Deserialize<ReplayHeader>(ProtocolJson.Serialize(header)).PhysicsBackend);
        Assert.Null(ProtocolJson.Deserialize<Snapshot>(ProtocolJson.Serialize(snapshot)).PhysicsPoses);
    }

    [Fact]
    public void MuJoCoScenario_RoundTrips_AndRejectsBadModeOrVersion()
    {
        var scenario = Samples.Scenario() with
        {
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        };
        var json = ProtocolJson.Serialize(scenario);
        Assert.Contains("\"physics\":{\"backend\":\"mujoco\",\"modelVersion\":\"wushu-mjcf-v1\"}", json);
        Assert.Equal(json, ProtocolJson.RoundTripJson(scenario));
        Assert.Empty(scenario.Validate());
        Assert.Contains((scenario with { Physics = new PhysicsSpec { Backend = "other" } }).Validate(), e => e.Contains("unsupported backend"));
        Assert.Contains((scenario with { Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco } }).Validate(), e => e.Contains("modelVersion"));
        Assert.Contains((scenario with { Physics = new PhysicsSpec { Backend = PhysicsSpec.Legacy, ModelVersion = PhysicsSpec.MujocoModelV1 } }).Validate(), e => e.Contains("legacy backend"));
    }

    [Fact]
    public void MuJoCoHeader_RequiresCompleteIdentity_AndRoundTrips()
    {
        var header = Samples.ReplayHeader() with
        {
            PhysicsBackend = PhysicsSpec.Mujoco,
            PhysicsEngineVersion = "3.3.0",
            PhysicsModelSha256 = ModelHash,
        };
        var json = ProtocolJson.Serialize(header);
        Assert.Equal(json, ProtocolJson.RoundTripJson(header));
        Assert.Contains("\"physicsBackend\":\"mujoco\"", json);
        Assert.Empty(header.Validate());
        Assert.Contains((header with { PhysicsEngineVersion = null }).Validate(), e => e.Contains("physicsEngineVersion"));
        Assert.Contains((header with { PhysicsModelSha256 = "bad" }).Validate(), e => e.Contains("physicsModelSha256"));
        Assert.Contains((header with { PhysicsBackend = null }).Validate(), e => e.Contains("require physicsBackend"));
        Assert.Contains((header with { PhysicsBackend = "other" }).Validate(), e => e.Contains("unsupported physicsBackend"));
    }

    [Fact]
    public void PhysicsPoses_RoundTrip_WithWorldHeightAndQuaternion()
    {
        var snapshot = Samples.Snapshot() with
        {
            PhysicsPoses = new PhysicsPoses
            {
                Robots = new Dictionary<string, PhysicsPose3>
                {
                    [RoleNames.Us] = new() { X = 1, Y = 2, Z = 0.08, Qz = 0.5, Qw = 0.8660254037844386 },
                    [RoleNames.Them] = new() { X = 3, Y = 4, Z = 0.12 },
                },
                Buffs = [new PhysicsPose3 { X = 1.4, Y = 1.3, Z = 0.135 }],
                Debuff = new PhysicsPose3 { X = 2.2, Y = 2.5, Z = 0.135 },
            },
        };
        var json = ProtocolJson.Serialize(snapshot);
        Assert.Contains("\"physicsPoses\"", json);
        Assert.Equal(json, ProtocolJson.RoundTripJson(snapshot));
        Assert.Empty(snapshot.Validate());
        Assert.Equal(0.08, ProtocolJson.Deserialize<Snapshot>(json).PhysicsPoses!.Robots[RoleNames.Us].Z);
        Assert.Contains((snapshot with { PhysicsPoses = new PhysicsPoses() }).Validate(), e => e.Contains("must contain 'us'"));
    }

    [Fact]
    public void BatchIdentity_IsAdditive_AndFailedRowsDoNotCarryIt()
    {
        var row = new BatchMatchResult
        {
            ScenarioId = "wushu-ring-2026", Ticks = 1, Scores = new Scores(), Penalties = new Scores(),
            DoneReason = "done", Faults = new BatchFaults(), EventCount = 0,
            EventFingerprint = ModelHash, ResultFingerprint = ModelHash,
            PhysicsBackend = PhysicsSpec.Mujoco, PhysicsModelSha256 = ModelHash,
        };
        var json = ProtocolJson.Serialize(row);
        Assert.Equal(json, ProtocolJson.RoundTripJson(row));
        Assert.Empty(row.Validate());
        Assert.DoesNotContain("\"physics", ProtocolJson.Serialize(row with { PhysicsBackend = null, PhysicsModelSha256 = null }));
        Assert.Contains((row with { PhysicsModelSha256 = null }).Validate(), e => e.Contains("physicsModelSha256"));
        Assert.Contains((row with { Status = BatchMatchResult.StatusFailed }).Validate(), e => e.Contains("must not carry partial"));
    }
}
