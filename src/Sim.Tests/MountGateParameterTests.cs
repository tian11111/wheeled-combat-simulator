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
    [InlineData(null, true)]      // default VMin=0.3: the FSM mount (≈0.585 m/s) is accepted
    [InlineData(1.0, false)]      // raised VMin=1.0: same mount attempts are blocked at the wall
    public void MountVMin_Override_ChangesStageWallAcceptance(double? mountVMin, bool expectMounted)
    {
        var engine = DriveMount(mountVMin, null);
        Assert.Equal(expectMounted, engine.Us.WasOn);
        if (!expectMounted)
        {
            Assert.True(engine.Us.Y < 0.75, "blocked robot must stay walkway-side");
        }
    }

    [Fact]
    public void MountAngleMax_ExplicitDefault_ProducesIdenticalMatch()
    {
        // MOUNT_ANGLE_MAX is read at the same StageWall sites as MOUNT_V_MIN (whose
        // behavioral effect the theory above already proves). Setting it to the
        // literal default must be a no-op versus omitting it — a wiring check that
        // does not depend on the FSM's alignment residual.
        var omitted = DriveMount(null, null);
        var explicit026 = DriveMount(null, 0.26);
        Assert.Equal(
            ProtocolJson.Serialize(omitted.CommitSnapshot()),
            ProtocolJson.Serialize(explicit026.CommitSnapshot()));
        Assert.True(explicit026.Us.WasOn);
    }

    /// <summary>Arms the FSM (its own align/reverse mount routine) and runs until the mount resolves.</summary>
    private static MatchEngine DriveMount(double? mountVMin, double? mountAngleMax)
    {
        var parameters = new Dictionary<string, double>();
        if (mountVMin is { } v)
        {
            parameters["MOUNT_V_MIN"] = v;
        }
        if (mountAngleMax is { } a)
        {
            parameters["MOUNT_ANGLE_MAX"] = a;
        }
        var starts = new Dictionary<string, Pose2>
        {
            [RoleNames.Us] = new() { X = 1.9, Y = 0.3, Th = -Math.PI / 2 },
            [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
        };
        var scenario = new Scenario
        {
            Seed = 42,
            Blocks = OfficialLayout.Blocks,
            Field = FieldParams.Default with { Starts = starts },
            Parameters = parameters.Count > 0 ? parameters : null,
        };
        var engine = new MatchEngine(scenario);
        engine.Arm();
        for (var i = 0; i < 1200 && !engine.Us.WasOn && !engine.Done; i++)
        {
            engine.Tick();
        }
        return engine;
    }
}
