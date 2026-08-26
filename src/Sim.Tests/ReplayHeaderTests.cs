using System.Text.Json.Nodes;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>Replay header version stamping and structural checks.</summary>
public class ReplayHeaderTests
{
    [Fact]
    public void Header_StampedWithCurrentVersions()
    {
        var header = Samples.ReplayHeader();

        Assert.Equal(ProtocolVersion.Current, header.Version);
        Assert.Equal(ProtocolVersion.ReplayFormat, header.ReplayVersion);
        Assert.Equal("replay-v1", header.ReplayVersion);

        var json = ProtocolJson.Serialize(header);
        Assert.Contains("\"protocolVersion\":\"v1\"", json);
        Assert.Contains("\"replayVersion\":\"replay-v1\"", json);
    }

    [Fact]
    public void Header_CarriesReproductionInputs()
    {
        var header = Samples.ReplayHeader();

        Assert.Equal("wushu-ring-2026", header.RulesetId);
        Assert.Equal(42, header.Seed);
        Assert.Equal("sim-core-0.1.0", header.CoreVersion);
        Assert.Equal("random_stub", header.VisionMode);
        Assert.Equal(0.05, header.Parameters!["grayNoise"]);
        Assert.Equal("hand_drawn", header.FieldGray.Id);
        Assert.NotNull(header.FieldGray.Hash);
        Assert.Contains(RoleNames.Us, header.Vehicles.Keys);
        Assert.Contains(RoleNames.Them, header.Vehicles.Keys);
        Assert.Equal(2, header.TickCount);
    }

    [Fact]
    public void Header_RecordsAcceptedActionsAndCommandsByTick()
    {
        var json = ProtocolJson.Serialize(Samples.ReplayHeader());
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.True(root.ContainsKey("ticks"));
        var ticks = root["ticks"]!.AsArray();
        Assert.Equal(2, ticks.Count);

        var first = ticks[0]!.AsObject();
        Assert.Equal(0, (int)first["tick"]!);
        Assert.Equal(1.2, (double)first["actions"]!["us"]!["v"]!);
        Assert.Equal(0, (double)first["actions"]!["us"]!["w"]!);
        Assert.Equal(-0.5, (double)first["actions"]!["them"]!["v"]!);

        var second = ticks[1]!.AsObject();
        var commands = second["commands"]!.AsArray();
        Assert.Equal("pause", (string)commands[0]!);
    }

    [Fact]
    public void FieldGrayRef_RoundTripsIdAndHash()
    {
        var json = ProtocolJson.Serialize(new ReplayHeader
        {
            RulesetId = "r",
            CoreVersion = "c",
            Seed = 1,
            FieldGray = new FieldGrayRef { Id = "measured-01", Hash = "deadbeef", Mode = "measured" },
        });

        Assert.Contains("\"fieldGray\":{\"id\":\"measured-01\"", json);

        var roundTripped = ProtocolJson.Deserialize<ReplayHeader>(json);
        Assert.Equal("measured-01", roundTripped.FieldGray.Id);
        Assert.Equal("deadbeef", roundTripped.FieldGray.Hash);
        Assert.Equal("measured", roundTripped.FieldGray.Mode);
    }

    [Fact]
    public void Validation_RejectsNonMonotonicTicks()
    {
        var header = Samples.ReplayHeader() with
        {
            Ticks =
            [
                new ReplayTick { Tick = 5 },
                new ReplayTick { Tick = 5 },
                new ReplayTick { Tick = 4 },
            ],
        };

        var errors = header.Validate().ToList();
        Assert.Contains(errors, e => e.Contains("strictly increasing"));
    }

    [Fact]
    public void Validation_RejectsMissingCoreInputs()
    {
        Assert.Contains(Validate(new ReplayHeader { Seed = -1 }), e => e.Contains("seed"));
        Assert.Contains(Validate(new ReplayHeader { CoreVersion = "" }), e => e.Contains("coreVersion"));
        Assert.Contains(Validate(new ReplayHeader { RulesetId = " " }), e => e.Contains("rulesetId"));
        Assert.Contains(Validate(new ReplayHeader { VisionMode = "" }), e => e.Contains("visionMode"));
        Assert.Contains(Validate(new ReplayHeader { FieldGray = new FieldGrayRef { Id = "" } }), e => e.Contains("fieldGray"));
        Assert.Contains(Validate(new ReplayHeader { Vehicles = [] }), e => e.Contains("vehicles must contain"));

        static IEnumerable<string> Validate(ReplayHeader header) => header.Validate();
    }

    [Fact]
    public void Validation_RejectsInvalidRecordedActions()
    {
        var header = Samples.ReplayHeader() with
        {
            Ticks =
            [
                new ReplayTick
                {
                    Tick = 0,
                    Actions = new Dictionary<string, RobotAction>
                    {
                        // An action that could never have been accepted by the bridge.
                        [RoleNames.Us] = new() { V = double.NaN, W = 0 },
                    },
                },
            ],
        };

        Assert.Contains(header.Validate(), e => e.Contains("v must be a finite number"));
    }

    [Fact]
    public void EnsureValid_ThrowsWithAllErrors()
    {
        var header = new ReplayHeader { Seed = -1, CoreVersion = "" };

        var exception = Assert.Throws<ProtocolValidationException>(() => ProtocolValidator.EnsureValid(header));
        Assert.Equal(typeof(ReplayHeader), exception.MessageType);
        Assert.True(exception.Errors.Count >= 2);
        Assert.False(ProtocolValidator.IsValid(header));
    }
}
