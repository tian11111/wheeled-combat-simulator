using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// Action limits, clamping and bridge-line acceptance semantics
/// (CONTRACT.md sections 2 and 4).
/// </summary>
public class ActionTests
{
    [Fact]
    public void Clamp_SaturatesSymmetrically_AndKeepsSign()
    {
        var limits = new ActionLimits { MaxSpeed = 1.5, MaxTurnRate = 4.0 };
        var action = new RobotAction { V = 99.0, W = -99.0 };

        var clamped = action.ClampTo(limits);
        Assert.Equal(1.5, clamped.V);
        Assert.Equal(-4.0, clamped.W);

        var negative = new RobotAction { V = -2.0, W = 0.1 }.ClampTo(limits);
        Assert.Equal(-1.5, negative.V);
        Assert.Equal(0.1, negative.W);
    }

    [Fact]
    public void Clamp_IsNoopWithinLimits()
    {
        var action = new RobotAction { V = 1.2, W = -3.9 };
        var clamped = action.ClampTo(ActionLimits.Default);
        Assert.Same(action, clamped);
    }

    [Fact]
    public void Clamp_UsesVehicleProfileLimits()
    {
        var vehicle = new VehicleProfile { Id = "slow", MaxSpeed = 0.8, MaxTurnRate = 2.0 };
        var clamped = new RobotAction { V = 5, W = -5 }.ClampTo(vehicle);
        Assert.Equal(0.8, clamped.V);
        Assert.Equal(-2.0, clamped.W);
    }

    [Fact]
    public void Clamp_DoesNotTouchNonFiniteValues()
    {
        // Non-finite values are rejected by validation before clamping;
        // ClampTo must not silently convert them into valid numbers.
        var action = new RobotAction { V = double.NaN, W = double.PositiveInfinity };
        var clamped = action.ClampTo(ActionLimits.Default);
        Assert.True(double.IsNaN(clamped.V));
        Assert.True(double.IsPositiveInfinity(clamped.W));
    }

    [Fact]
    public void Validation_RejectsNonFiniteValues()
    {
        Assert.Contains(new RobotAction { V = double.NaN, W = 0 }.Validate(), e => e.Contains("v must be a finite number"));
        Assert.Contains(new RobotAction { V = 0, W = double.NegativeInfinity }.Validate(), e => e.Contains("w must be a finite number"));
        Assert.Empty(new RobotAction { V = 0, W = 0 }.Validate());
        Assert.True(new RobotAction { V = 0.5, W = -0.25 }.IsFinite);
        Assert.False(new RobotAction { V = double.NaN, W = 0 }.IsFinite);
    }

    [Fact]
    public void ActionLimits_Validation()
    {
        Assert.Empty(ActionLimits.Default.Validate());
        Assert.Contains(new ActionLimits { MaxSpeed = 0 }.Validate(), e => e.Contains("maxSpeed"));
        Assert.Contains(new ActionLimits { MaxTurnRate = -1 }.Validate(), e => e.Contains("maxTurnRate"));
    }

    [Fact]
    public void ZeroAction_IsTheNeutralFallback()
    {
        Assert.Equal(0, RobotAction.Zero.V);
        Assert.Equal(0, RobotAction.Zero.W);
        Assert.Null(RobotAction.Zero.RequestId);
        Assert.True(RobotAction.Zero.IsFinite);
    }

    [Fact]
    public void RequestId_WritesAsNumberAndReadsBothForms()
    {
        var json = ProtocolJson.Serialize(new RobotAction { V = 0.5, W = 0, RequestId = "7" });
        Assert.Contains("\"requestId\":7", json);

        var fromNumber = ProtocolJson.Deserialize<RobotAction>("""{"v":0.5,"w":0,"requestId":7}""");
        Assert.Equal("7", fromNumber.RequestId);

        var fromString = ProtocolJson.Deserialize<RobotAction>("""{"v":0.5,"w":0,"requestId":"7"}""");
        Assert.Equal("7", fromString.RequestId);
    }

    [Theory]
    [InlineData("""{"v":0.5,"w":0,"requestId":7}""", true)]
    [InlineData("""{"v":0.5,"w":0,"requestId":"7"}""", true)]
    [InlineData("""{"v":-1.2,"w":3.4}""", true)]
    [InlineData("""{"v":0.5,"w":0,"extra":"ignored"}""", true)]
    [InlineData("""{"status":"ok"}""", false)]                 // no finite v/w → dropped by the bridge
    [InlineData("""{"v":0.5}""", false)]                        // missing w
    [InlineData("""{"v":"abc","w":0}""", false)]                // non-numeric v
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData("[1,2,3]", false)]
    public void TryParseActionLine_FollowsLegacyBridgeRules(string line, bool expectedAccepted)
    {
        var accepted = ProtocolJson.TryParseActionLine(line, out var action, out var error);

        Assert.Equal(expectedAccepted, accepted);
        if (expectedAccepted)
        {
            Assert.NotNull(action);
            Assert.True(action!.IsFinite);
            Assert.Null(error);
        }
        else
        {
            Assert.Null(action);
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void TryParseActionLine_EchoesRequestIdAsNumber()
    {
        var accepted = ProtocolJson.TryParseActionLine("""{"v":0.5,"w":0,"requestId":123}""", out var action, out _);

        Assert.True(accepted);
        Assert.Equal("123", action!.RequestId);
        // The id must survive re-serialization for replay recording.
        Assert.Contains("\"requestId\":123", ProtocolJson.Serialize(action));
    }
}
