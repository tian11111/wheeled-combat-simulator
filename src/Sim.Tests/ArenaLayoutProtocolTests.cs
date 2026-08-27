using System.Text.Json.Nodes;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Protocol-level tests for the arena layout extension (arena-layout-v1):
/// the optional Scenario.layoutVersion tag and the optional field pose.
/// Legacy JSON must keep its exact shape and deserialize to the identity
/// layout; new optional fields must round-trip losslessly.
/// </summary>
public class ArenaLayoutProtocolTests
{
    private static void AssertRoundTrips<T>(T value)
    {
        var first = ProtocolJson.Serialize(value);
        var second = ProtocolJson.Serialize(ProtocolJson.Deserialize<T>(first));
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(first), JsonNode.Parse(second)),
            $"JSON mismatch after round-trip:\n  {first}\n  {second}");
    }

    [Fact]
    public void Scenario_WithoutLayoutExtension_KeepsLegacyJsonShape()
    {
        var json = ProtocolJson.Serialize(Samples.Scenario());

        Assert.DoesNotContain("layoutVersion", json);
        Assert.DoesNotContain("\"pose\"", json);

        var parsed = ProtocolJson.Deserialize<Scenario>(json);
        Assert.Null(parsed.LayoutVersion);
        Assert.Null(parsed.Field.Pose);
        Assert.Empty(parsed.Validate());
    }

    [Fact]
    public void OfficialScenarioFile_ParsesCanonicalLayout_AndValidates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scenarios/wushu-ring-2026.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var json = File.ReadAllText(Path.Combine(dir!.FullName, "scenarios/wushu-ring-2026.json"));

        var scenario = ProtocolJson.Deserialize<Scenario>(json);
        Assert.Equal(ProtocolVersion.ArenaLayoutV1, scenario.LayoutVersion);
        Assert.NotNull(scenario.Field.Pose);
        Assert.Equal(0, scenario.Field.Pose.X);
        Assert.Equal(0, scenario.Field.Pose.Y);
        Assert.Equal(0, scenario.Field.Pose.Th);
        Assert.Empty(scenario.Validate());
    }

    /// <summary>
    /// Automated dimension regression against the 2026 rule drawing
    /// (外场 3.8m, 擂台 2.4m/6cm, 走道 0.7m, 围栏 0.2m, 出发区 0.5x0.4m
    /// 距台沿 0.2m, 能量块 0.15m 见方)。
    /// </summary>
    [Fact]
    public void OfficialScenario_DimensionsMatchRuleDrawing()
    {
        var field = FieldParams.Default;
        Assert.Equal(3.8, field.FieldSize);
        Assert.Equal(0.7, field.AisleWidth);
        Assert.Equal(0.2, field.FenceHeight);
        Assert.Equal(0.06, field.PlatformHeight);
        Assert.Equal(0.15, field.BlockSize);
        Assert.Equal(0.075, field.BlockRadius);
        Assert.Equal(2.4, field.Platform.MaxX - field.Platform.MinX, 12);
        Assert.Equal(2.4, field.Platform.MaxY - field.Platform.MinY, 12);
        Assert.Equal((0.7, 0.7, 3.1, 3.1),
            (field.Platform.MinX, field.Platform.MinY, field.Platform.MaxX, field.Platform.MaxY));

        var us = field.StartZones[RoleNames.Us];
        var them = field.StartZones[RoleNames.Them];
        Assert.Equal(0.5, us.MaxX - us.MinX, 12);
        Assert.Equal(0.4, us.MaxY - us.MinY, 12);
        Assert.Equal(0.5, them.MaxX - them.MinX, 12);
        Assert.Equal(0.4, them.MaxY - them.MinY, 12);
        // 0.2 m gap from the platform edge (yellow below the south edge, blue above the north edge).
        Assert.Equal(0.2, field.Platform.MinY - us.MaxY, 12);
        Assert.Equal(0.2, them.MinY - field.Platform.MaxY, 12);
        // West-aligned (yellow at the platform west edge x=0.7) / east-aligned (blue at x=3.1).
        Assert.Equal(field.Platform.MinX, us.MinX, 12);
        Assert.Equal(field.Platform.MaxX, them.MaxX, 12);

        Assert.Equal(0.95, field.Starts[RoleNames.Us].X, 12);
        Assert.Equal(0.30, field.Starts[RoleNames.Us].Y, 12);
        Assert.Equal(-Math.PI / 2, field.Starts[RoleNames.Us].Th, 12);
        Assert.Equal(2.85, field.Starts[RoleNames.Them].X, 12);
        Assert.Equal(3.50, field.Starts[RoleNames.Them].Y, 12);
        Assert.Equal(Math.PI / 2, field.Starts[RoleNames.Them].Th, 12);

        Assert.Equal(
            OfficialLayout.Blocks.Select(b => (b.Kind, b.X, b.Y)),
            [(BlockKind.Buff, (double?)1.35, (double?)1.35),
             (BlockKind.Buff, (double?)2.5, (double?)2.6),
             (BlockKind.Debuff, (double?)1.6, (double?)2.4)]);
    }

    [Fact]
    public void CanonicalScenario_WithNonIdentityPose_RoundTripsAndValidates()
    {
        var scenario = Samples.Scenario() with
        {
            LayoutVersion = ProtocolVersion.ArenaLayoutV1,
            Field = FieldParams.Default with
            {
                Pose = new Pose2 { X = 1.5, Y = -0.5, Th = -0.35 },
            },
        };
        AssertRoundTrips(scenario);
        var reloaded = ProtocolJson.Deserialize<Scenario>(ProtocolJson.Serialize(scenario));
        Assert.Empty(reloaded.Validate());
        Assert.Equal(-0.35, reloaded.Field.Pose!.Th, 15);
    }

    [Fact]
    public void LayoutVersion_AndFieldPose_RoundTrip()
    {
        var scenario = Samples.Scenario() with
        {
            LayoutVersion = ProtocolVersion.ArenaLayoutV1,
            Field = FieldParams.Default with
            {
                Pose = new Pose2 { X = 0.4, Y = -1.2, Th = 0.7853981633974483 },
            },
        };

        AssertRoundTrips(scenario);
        var parsed = ProtocolJson.Deserialize<Scenario>(ProtocolJson.Serialize(scenario));
        Assert.Equal(ProtocolVersion.ArenaLayoutV1, parsed.LayoutVersion);
        Assert.Equal(0.4, parsed.Field.Pose!.X);
        Assert.Equal(-1.2, parsed.Field.Pose.Y);
        Assert.Equal(0.7853981633974483, parsed.Field.Pose.Th);
        Assert.Empty(parsed.Validate());
    }

    [Fact]
    public void Validation_RejectsUnsupportedLayoutVersion()
    {
        var scenario = Samples.Scenario() with { LayoutVersion = "arena-layout-v2" };
        Assert.Contains(scenario.Validate(), e => e.Contains("unsupported layoutVersion"));
    }

    [Fact]
    public void Validation_AcceptsCurrentLayoutVersion_AndRejectsNonFinitePose()
    {
        var ok = Samples.Scenario() with { LayoutVersion = ProtocolVersion.ArenaLayoutV1 };
        Assert.DoesNotContain(ok.Validate(), e => e.Contains("layoutVersion"));

        var badPose = Samples.Scenario() with
        {
            Field = FieldParams.Default with { Pose = new Pose2 { X = double.NaN, Y = 0, Th = 0 } },
        };
        Assert.Contains(badPose.Validate(), e => e.Contains("pose x/y/th must be finite"));
    }

    [Fact]
    public void Validation_RejectsRegionsOutsideInnerField()
    {
        var zoneOutside = Samples.Scenario() with
        {
            Field = FieldParams.Default with
            {
                StartZones = new Dictionary<string, Region>
                {
                    [RoleNames.Us] = new() { MinX = -0.5, MinY = 0.1, MaxX = 0.0, MaxY = 0.5 },
                    [RoleNames.Them] = new() { MinX = 2.6, MinY = 3.3, MaxX = 3.1, MaxY = 3.7 },
                },
            },
        };
        Assert.Contains(zoneOutside.Validate(), e => e.Contains("start zone 'us' must stay inside"));

        var platformOutside = Samples.Scenario() with
        {
            Field = FieldParams.Default with
            {
                Platform = new Region { MinX = 0.7, MinY = 0.7, MaxX = 3.9, MaxY = 3.1 },
            },
        };
        Assert.Contains(platformOutside.Validate(), e => e.Contains("platform region must stay inside"));

        var blockOutside = Samples.Scenario() with
        {
            Blocks = [new BlockSpec { Kind = BlockKind.Buff, X = 4.1, Y = 1.2 }],
        };
        Assert.Contains(blockOutside.Validate(), e => e.Contains("inside the inner field"));
    }
}
