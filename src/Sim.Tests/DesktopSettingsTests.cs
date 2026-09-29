using Sim.GodotShell;
using Sim.Protocol;

namespace Sim.Tests;

public class DesktopSettingsTests
{
    [Fact]
    public void Catalog_CoversEveryAcceptedSimulationParameterKey()
    {
        var acceptedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "EDGE_THRESHOLD", "FALL_THRESHOLD", "ON_STAGE_THRESHOLD", "grayNoise", "irNoise",
            "IR_TRIGGER", "MOUNT_SPEED", "classifyRate", "RECOVER_LIMIT", "STALL_TIME",
            "STALL_SPEED", "STALL_RELEASE", "STALL_DISPLACEMENT", "cmdLatencyFrames", "IR_HYST_BAND",
            "graySpotRadius", "BLOCK_STICK_SPEED", "BLOCK_MU_K", "COLLISION_RESTITUTION", "MOUNT_V_MIN",
            "MOUNT_ANGLE_MAX", "antiStallBladeAmp", "antiStallBladePeriodUs", "antiStallBladePeriodThem",
        };
        var catalogKeys = SimulationParameterCatalog.All.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

        Assert.True(acceptedKeys.SetEquals(catalogKeys),
            $"Catalog mismatch. Missing={string.Join(',', acceptedKeys.Except(catalogKeys))} "
            + $"Extra={string.Join(',', catalogKeys.Except(acceptedKeys))}");
    }

    [Fact]
    public void DefaultSettings_AreValid_AndUseCoreDefaultsByOmittingOverrides()
    {
        var settings = DesktopSettings.Default;

        Assert.Empty(settings.Validate());
        Assert.Empty(settings.SimulationParameters);
        Assert.Equal(1280, settings.Window.Width);
        Assert.Equal(720, settings.Window.Height);
        Assert.Equal(DisplayModes.Windowed, settings.Window.Mode);
    }

    [Fact]
    public void ParameterValidation_RejectsUnknownNonFiniteAndOutOfRangeValues()
    {
        var settings = DesktopSettings.Default with
        {
            SimulationParameters = new Dictionary<string, double>
            {
                ["not-a-core-key"] = 1,
                ["IR_TRIGGER"] = double.NaN,
                ["MOUNT_ANGLE_MAX"] = 1.2,
                ["cmdLatencyFrames"] = 1.5,
            },
        };

        var errors = settings.Validate().ToList();
        Assert.Contains(errors, error => error.Contains("unknown simulation parameter 'not-a-core-key'"));
        Assert.Contains(errors, error => error.Contains("IR_TRIGGER"));
        Assert.Contains(errors, error => error.Contains("MOUNT_ANGLE_MAX"));
        Assert.Contains(errors, error => error.Contains("cmdLatencyFrames"));
    }

    [Fact]
    public void VehicleSettings_DefaultsMatchTheRealVehicle_AndDeriveMaxSpeed()
    {
        var vehicle = DesktopSettings.Default.Vehicle;

        Assert.Equal(3.5, vehicle.Mass, 9);
        Assert.Equal(120, vehicle.MotorRpm, 9);
        Assert.Equal(1.72, vehicle.MotorTorque, 9);
        Assert.Equal(0.0325, vehicle.WheelRadius, 9);
        // 轮端极速 = rpm/60 × 2π × r ≈ 0.408 m/s (博创尚和 2342 减速后 120 RPM)
        Assert.Equal(120.0 / 60 * 2 * Math.PI * 0.0325, vehicle.MaxSpeed, 9);
        Assert.Equal(0.408, vehicle.MaxSpeed, 3);
        Assert.Empty(DesktopSettings.Default.Validate());
    }

    [Fact]
    public void VehicleSettings_ValidationRejectsOutOfRangeValues()
    {
        var settings = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with { Mass = 0.1 },
        };
        Assert.Contains(settings.Validate(), error => error.Contains("vehicle.mass"));

        settings = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with { MotorRpm = double.NaN },
        };
        Assert.Contains(settings.Validate(), error => error.Contains("vehicle.motorRpm"));
    }

    [Fact]
    public void ApplyVehicleOverrides_TouchesOnlyV2MujocoScenarios()
    {
        var settings = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with { Mass = 4.2, MotorRpm = 200 },
        };

        // v2 真车几何场景: us/them 质量与极速按设置覆盖。
        var v2 = new Scenario
        {
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
            Vehicles = new Dictionary<string, VehicleProfile>
            {
                [RoleNames.Us] = new() { Id = "glb-2026", Mass = 3.5, MaxSpeed = 0.408 },
                [RoleNames.Them] = new() { Id = "glb-2026", Mass = 3.5, MaxSpeed = 0.408 },
            },
        };
        var appliedV2 = settings.ApplyVehicleOverrides(v2);
        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            Assert.Equal(4.2, appliedV2.Vehicles[role].Mass, 9);
            Assert.Equal(200.0 / 60 * 2 * Math.PI * 0.0325, appliedV2.Vehicles[role].MaxSpeed, 9);
        }

        // legacy 与 v1 场景: 一律不触碰(行为逐位不变)。
        var legacy = new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks };
        Assert.Same(legacy, settings.ApplyVehicleOverrides(legacy));
        var v1 = legacy with
        {
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        };
        Assert.Same(v1, settings.ApplyVehicleOverrides(v1));
    }

    [Fact]
    public void ExternalController_RequiresCommand_AndValidTimeout()
    {
        var settings = DesktopSettings.Default with
        {
            UsController = new ControllerProfile { Mode = ControllerModes.External },
            ThemController = new ControllerProfile { Mode = ControllerModes.External, Command = "echo", TimeoutMs = 0 },
        };

        var errors = settings.Validate().ToList();
        Assert.Contains(errors, error => error.Contains("usController.command"));
        Assert.Contains(errors, error => error.Contains("themController.timeoutMs"));
    }

    [Fact]
    public void ParameterCatalog_ExposesCommonAndAdvancedGroups()
    {
        Assert.NotEmpty(SimulationParameterCatalog.ForGroup("常用"));
        Assert.NotEmpty(SimulationParameterCatalog.ForGroup("高级"));
        Assert.All(SimulationParameterCatalog.All, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Key));
            Assert.False(string.IsNullOrWhiteSpace(definition.Unit));
            Assert.True(definition.Minimum <= definition.Maximum);
        });
    }

    [Fact]
    public void SettingsStore_RoundTripsAndFallsBackForCorruptOrInvalidFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "wushu-ring-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var diagnostics = new List<string>();
            var store = new SettingsStore(Path.Combine(root, "settings.json"), diagnostics.Add);
            var expected = DesktopSettings.Default with
            {
                Window = new WindowSettings { Width = 1920, Height = 1080, Mode = DisplayModes.Fullscreen },
                UiScale = 1.15,
                SimulationParameters = new Dictionary<string, double> { ["IR_TRIGGER"] = 0.42 },
            };

            store.Save(expected);
            var loaded = store.Load();
            Assert.Equal(ProtocolJson.Serialize(expected), ProtocolJson.Serialize(loaded));
            Assert.Equal(0.42, loaded.SimulationParameters["IR_TRIGGER"]);
            Assert.Equal(DisplayModes.Fullscreen, loaded.Window.Mode);

            File.WriteAllText(Path.Combine(root, "settings.json"), "{not-json");
            Assert.Equal(ProtocolJson.Serialize(DesktopSettings.Default), ProtocolJson.Serialize(store.Load()));
            Assert.Contains(diagnostics, message => message.Contains("读取失败"));

            File.WriteAllText(Path.Combine(root, "settings.json"), "null");
            Assert.Equal(ProtocolJson.Serialize(DesktopSettings.Default), ProtocolJson.Serialize(store.Load()));
            Assert.Contains(diagnostics, message => message.Contains("读取失败"));

            File.WriteAllText(Path.Combine(root, "settings.json"), ProtocolJson.Serialize(expected with
            {
                SchemaVersion = DesktopSettings.CurrentSchemaVersion + 1,
            }));
            Assert.Equal(ProtocolJson.Serialize(DesktopSettings.Default), ProtocolJson.Serialize(store.Load()));
            Assert.Contains(diagnostics, message => message.Contains("校验失败"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApplySimulationParameters_ClonesAndOverlaysWithoutMutatingScenario()
    {
        var scenario = new Scenario
        {
            Parameters = new Dictionary<string, double> { ["IR_TRIGGER"] = 0.25 },
        };
        var settings = DesktopSettings.Default with
        {
            SimulationParameters = new Dictionary<string, double> { ["IR_TRIGGER"] = 0.42 },
        };

        var applied = settings.ApplySimulationParameters(scenario);

        Assert.Equal(0.25, scenario.Parameters!["IR_TRIGGER"]);
        Assert.Equal(0.42, applied.Parameters!["IR_TRIGGER"]);
        Assert.NotSame(scenario.Parameters, applied.Parameters);
    }
}
