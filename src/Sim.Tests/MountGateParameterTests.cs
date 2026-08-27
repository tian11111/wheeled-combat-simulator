using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// The mount acceptance gate is now an explicit, calibratable scenario parameter
/// (MOUNT_V_MIN / MOUNT_ANGLE_MAX) instead of PhysicsWorld private constants.
/// Defaults must reproduce the historical gate bit-for-bit (covered by the replay
/// fixtures); overrides must change stage-wall behavior observably.
/// </summary>
public class MountGateParameterTests
{
    [Fact]
    public void Defaults_MatchHistoricalConstants()
    {
        var p = SimParameters.FromDictionary(null);
        Assert.Equal(0.3, p.MountVMin);
        Assert.Equal(0.26, p.MountAngleMax);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2.5)]
    [InlineData(double.NaN)]
    public void MountVMin_RangeIsValidated(double value)
    {
        Assert.Throws<ArgumentException>(() =>
            SimParameters.FromDictionary(new Dictionary<string, double> { ["MOUNT_V_MIN"] = value }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void MountAngleMax_RangeIsValidated(double value)
    {
        Assert.Throws<ArgumentException>(() =>
            SimParameters.FromDictionary(new Dictionary<string, double> { ["MOUNT_ANGLE_MAX"] = value }));
    }

    [Theory]
    [InlineData(null, true)]      // default VMin=0.3: a 0.4 m/s aligned push mounts
    [InlineData(1.0, false)]      // raised VMin=1.0: same push is blocked at the stage wall
    public void MountVMin_Override_ChangesStageWallAcceptance(double? mountVMin, bool expectMounted)
    {
        var starts = new Dictionary<string, Pose2>
        {
            [RoleNames.Us] = new() { X = 1.9, Y = 0.35, Th = Math.PI / 2 },
            [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
        };
        var scenario = new Scenario
        {
            Seed = 42,
            Blocks = OfficialLayout.Blocks,
            Field = FieldParams.Default with { Starts = starts },
            Parameters = mountVMin is { } v
                ? new Dictionary<string, double> { ["MOUNT_V_MIN"] = v }
                : null,
        };

        var engine = new MatchEngine(scenario);
        engine.Arm();
        var push = new RobotAction { V = 0.4, W = 0 };
        Snapshot last = null!;
        for (var i = 0; i < 200 && !engine.Done; i++)
        {
            last = engine.Tick(push, push);
        }

        Assert.Equal(expectMounted, last.Robots[RoleNames.Us].OnPlatform);
        if (!expectMounted)
        {
            Assert.True(last.Robots[RoleNames.Us].Y < 0.7, "blocked robot must stay walkway-side");
        }
    }

    [Fact]
    public void MountAngleMax_Override_BlocksObliqueMount()
    {
        // 6.9° off the wall normal: accepted by the default 15° gate, rejected
        // once MOUNT_ANGLE_MAX is tightened to ~0° (vt > vn·tan(0)).
        var starts = new Dictionary<string, Pose2>
        {
            [RoleNames.Us] = new() { X = 1.75, Y = 0.35, Th = Math.PI / 2 - 0.12 },
            [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
        };

        Snapshot Drive(double? angleMax)
        {
            var scenario = new Scenario
            {
                Seed = 42,
                Blocks = OfficialLayout.Blocks,
                Field = FieldParams.Default with { Starts = starts },
                Parameters = angleMax is { } a
                    ? new Dictionary<string, double> { ["MOUNT_ANGLE_MAX"] = a }
                    : null,
            };
            var engine = new MatchEngine(scenario);
            engine.Arm();
            var push = new RobotAction { V = 0.4, W = 0 };
            Snapshot last = null!;
            for (var i = 0; i < 200 && !engine.Done; i++)
            {
                last = engine.Tick(push, push);
            }
            return last;
        }

        Assert.True(Drive(null).Robots[RoleNames.Us].OnPlatform);
        var blocked = Drive(0.0001).Robots[RoleNames.Us];
        Assert.False(blocked.OnPlatform);
        Assert.True(blocked.Y < 0.7);
    }
}
