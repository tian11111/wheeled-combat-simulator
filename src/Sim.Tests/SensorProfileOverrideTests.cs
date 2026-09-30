using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 传感器覆盖层 (SensorProfileCustomizer + SensorChannel.Disabled):
/// 克隆不改基底、禁用通道读数恒为下限且逻辑别名降级、偏移按车体系叠加、
/// 2D 语义通道 (Height=null) 在零偏移下保持位不变。
/// </summary>
public class SensorProfileOverrideTests
{
    [Fact]
    public void Apply_CloneDoesNotMutateBase()
    {
        var original = SensorProfiles.Legacy14;
        var customized = SensorProfileCustomizer.Apply(
            original, "custom:test", ["gF"], new Dictionary<string, SensorProfileCustomizer.ChannelOffset>
            {
                ["uL"] = new(0.01, -0.02, 0.003, 0.1),
            });

        Assert.Equal("custom:test", customized.Id);
        Assert.Equal(original.Channels.Count, customized.Channels.Count);
        Assert.False(original.Channels.First(c => c.Id == "gF").Disabled, "基底不得被改写");
        Assert.True(customized.Channels.First(c => c.Id == "gF").Disabled);
        var uL = customized.Channels.First(c => c.Id == "uL");
        var baseUL = original.Channels.First(c => c.Id == "uL");
        Assert.Equal(baseUL.Forward + 0.01, uL.Forward, 12);
        Assert.Equal(baseUL.Lateral - 0.02, uL.Lateral, 12);
        Assert.Equal(baseUL.Angle + 0.1, uL.Angle, 12);
    }

    [Fact]
    public void Apply_ZeroOffsetKeepsTwoDimensionalChannelsNullHeight()
    {
        var twoD = new SensorProfile
        {
            Id = "two-d",
            Channels = [new SensorChannel { Id = "x", Type = SensorType.Gray, Forward = 0.1, Height = null }],
        };
        var untouched = SensorProfileCustomizer.Apply(twoD, "custom:a");
        Assert.Null(untouched.Channels[0].Height);

        var lifted = SensorProfileCustomizer.Apply(
            twoD, "custom:b", null, new Dictionary<string, SensorProfileCustomizer.ChannelOffset>
            {
                ["x"] = new(0, 0, 0.0025, 0),
            });
        Assert.Equal(0.0025, lifted.Channels[0].Height!.Value, 12);
    }

    [Fact]
    public void DisabledChannel_SamplesAtMinAndDegradesLogicalAliases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 同 seed 两场: A 默认 Legacy14; B 的 us 传感器全部禁用 —— 灰度/红外读数
        // 恒为下限(0), 逻辑别名经缺读数容错自然降级, FSM 门限不触发但引擎照常推进。
        var baseScenario = new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks };
        var disabledProfile = SensorProfileCustomizer.Apply(
            SensorProfiles.Legacy14, "custom:all-off",
            SensorProfiles.Legacy14.Channels.Select(c => c.Id).ToList());
        var disabledScenario = baseScenario with
        {
            Vehicles = new Dictionary<string, VehicleProfile>
            {
                ["us"] = new VehicleProfile() with { Sensors = disabledProfile },
                ["them"] = new VehicleProfile(),
            },
        };

        using var a = MatchEngineHost.Create(baseScenario);
        using var b = MatchEngineHost.Create(disabledScenario);
        a.Arm();
        b.Arm();
        Assert.True(a.Us.Sens.Count > 0, "默认场景应有逻辑别名读数");
        for (var i = 0; i < 40 && !a.Done; i++)
        {
            a.Tick();
            b.Tick();
            // 全禁用 ⇒ 所有逻辑别名读数恒为下限 0 (缺读数容错降级, 不炸不触发门限)。
            foreach (var kv in b.Us.Sens)
            {
                Assert.Equal(0.0, kv.Value, 9);
            }
        }
    }

    [Fact]
    public void Apply_PreservesLogicalMappingIds()
    {
        var customized = SensorProfileCustomizer.Apply(SensorProfiles.WheeledCombat11, "custom:keep");
        if (SensorProfiles.WheeledCombat11.Logical is not null)
        {
            Assert.NotNull(customized.Logical);
            Assert.Equal(SensorProfiles.WheeledCombat11.Logical.Keys, customized.Logical!.Keys);
        }
    }
}
