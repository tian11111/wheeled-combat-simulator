using Sim.Cli;
using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;
using Sim.VisionReplay;

namespace Sim.Tests;

public class DesktopSettingsTests : IDisposable
{
    private const string MiniFixtureDir = "src/Sim.Tests/fixtures/mbri-vision-mini";
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort temp cleanup
            }
        }
    }

    private static string FindRepo(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    /// <summary>Builds one hash-locked evidence package with the real `vision import` path.</summary>
    private string BuildEvidencePackage()
    {
        var work = Path.Combine(Path.GetTempPath(), $"desktop-vision-{Guid.NewGuid():N}");
        _tempDirs.Add(work);
        Directory.CreateDirectory(work);
        foreach (var file in Directory.EnumerateFiles(FindRepo(MiniFixtureDir)))
        {
            File.Copy(file, Path.Combine(work, Path.GetFileName(file)));
        }
        var evidenceDir = Path.Combine(work, "evidence");
        var exit = Program.Main(
        [
            "vision", "import",
            "--manifest", Path.Combine(work, "selection.manifest.json"),
            "--evidence-out", evidenceDir,
            "--out", Path.Combine(work, "import-report.json"),
            "--force",
        ]);
        Assert.Equal(0, exit);
        return evidenceDir;
    }

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
            // R4.1: tooltip 用中文说明且必须带上单位 (影响方向写在句子里)。
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.Contains(definition.Unit, definition.Description, StringComparison.Ordinal);
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

    // ---------- 视觉源三选一(默认位不变 / 校验 / 应用范围) ----------

    [Fact]
    public void VisionSettings_DefaultToTheClassifyRateStubWithoutInjection()
    {
        var vision = DesktopSettings.Default.Vision;

        Assert.Equal(VisionSources.ClassifyRate, vision.Source);
        Assert.Equal("", vision.EvidencePath);
        Assert.Equal("", vision.CsvPath);
        // 默认窗口与 CLI(`vision live --max-age-ms`)同值。
        Assert.Equal(500, vision.MaxAgeMs, 9);
        Assert.Equal(LiveVisionBridge.DefaultMaxAgeMs, vision.MaxAgeMs, 9);
        // 默认源 ⇒ 工厂为 null ⇒ 不注入 adapter(引擎内部 ClassifyRateVision, 逐位不变)。
        Assert.Null(DesktopSettings.Default.CreateVisionFactory());
        Assert.Empty(DesktopSettings.Default.Validate());
    }

    [Fact]
    public void VisionSettings_ValidationRejectsUnknownSourceMissingPathsAndBadWindow()
    {
        var settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings { Source = "yolo-process" },
        };
        Assert.Contains(settings.Validate(), error => error.Contains("vision.source"));

        // 显式选了非默认源就必须给出对应路径(visionReplay → 证据包目录)。
        settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings { Source = VisionSources.VisionReplay },
        };
        var errors = settings.Validate().ToList();
        Assert.Contains(errors, error => error.Contains("vision.evidencePath"));
        Assert.DoesNotContain(errors, error => error.Contains("vision.csvPath"));

        // liveBridge → 真车 CSV 路径 + 有限的正窗口。
        settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings
            {
                Source = VisionSources.LiveBridge,
                MaxAgeMs = double.NaN,
            },
        };
        errors = settings.Validate().ToList();
        Assert.Contains(errors, error => error.Contains("vision.csvPath"));
        Assert.Contains(errors, error => error.Contains("vision.maxAgeMs"));

        settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings
            {
                Source = VisionSources.LiveBridge,
                CsvPath = "hunt.csv",
                MaxAgeMs = 0,
            },
        };
        Assert.Contains(settings.Validate(), error => error.Contains("vision.maxAgeMs"));
    }

    [Fact]
    public void VisionSettings_ClassifyRateNeverInjectsEvenWhenStalePathsRemain()
    {
        // "仅显式非默认源才生效": 默认源即便留着上次的路径也不得注入外部源。
        var settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings
            {
                Source = VisionSources.ClassifyRate,
                EvidencePath = "does/not/exist",
                CsvPath = "does/not/exist.csv",
            },
        };

        Assert.Empty(settings.Validate());
        Assert.Null(settings.CreateVisionFactory());
    }

    [Fact]
    public void VisionSettings_LiveBridgeFactoryBuildsOneFreshAdapterPerMatch()
    {
        var settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings
            {
                Source = VisionSources.LiveBridge,
                CsvPath = FindRepoFile(Path.Combine(MiniFixtureDir, "hunt_drive_20260817_095205.csv")),
                MaxAgeMs = 250,
            },
        };
        Assert.Empty(settings.Validate());
        var factory = settings.CreateVisionFactory();
        Assert.NotNull(factory);

        var first = Assert.IsType<LiveVisionBridge>(factory!());
        var second = Assert.IsType<LiveVisionBridge>(factory!());
        // 适配器带每场台账与 SimT 0 基准: 工厂每次调用都新建实例, 绝不跨场复用。
        Assert.NotSame(first, second);
        Assert.Equal(LiveVisionBridge.ModeName, first.Id);
        Assert.Equal(250, first.MaxAgeMs, 9);
    }

    [Fact]
    public void VisionSettings_FactoriesFailFastInsteadOfSilentlyFallingBack()
    {
        var missingCsv = DesktopSettings.Default with
        {
            Vision = new VisionSettings { Source = VisionSources.LiveBridge, CsvPath = "missing-hunt.csv" },
        };
        Assert.Throws<VisionEvidenceException>(() => missingCsv.CreateVisionFactory());

        var missingEvidence = DesktopSettings.Default with
        {
            Vision = new VisionSettings { Source = VisionSources.VisionReplay, EvidencePath = "missing-evidence" },
        };
        Assert.Throws<VisionEvidenceException>(() => missingEvidence.CreateVisionFactory());
    }

    [Fact]
    public void VisionSettings_VisionReplayFactoryLoadsTheHashLockedPackage()
    {
        var evidenceDir = BuildEvidencePackage();
        var settings = DesktopSettings.Default with
        {
            Vision = new VisionSettings
            {
                Source = VisionSources.VisionReplay,
                EvidencePath = evidenceDir,
                MaxAgeMs = 300,
            },
        };
        Assert.Empty(settings.Validate());
        var factory = settings.CreateVisionFactory();
        Assert.NotNull(factory);

        var adapter = Assert.IsType<VisionReplayAdapter>(factory!());
        var importReport = ProtocolJson.Deserialize<VisionImportReport>(
            File.ReadAllText(Path.Combine(evidenceDir, VisionReplayIO.ImportReportFileName)));
        Assert.Equal(VisionReplayAdapter.ModeName, adapter.Id);
        Assert.Equal(300, adapter.MaxAgeMs, 9);
        // 桌面读的是同一份哈希锁定身份: 证据 ID/哈希必须与导入报告逐字一致。
        Assert.Equal(importReport.EvidenceId, adapter.EvidenceId);
        Assert.Equal(importReport.EvidenceSha256, adapter.EvidenceSha256);

        // 篡改 frames.jsonl 后建工厂必须直接拒绝 —— 绝不静默换回默认源。
        var framesPath = Path.Combine(evidenceDir, VisionReplayIO.FramesFileName);
        File.WriteAllText(framesPath, File.ReadAllText(framesPath)
            .Replace("\"sequence\":12", "\"sequence\":1200", StringComparison.Ordinal));
        Assert.Throws<VisionEvidenceException>(() => settings.CreateVisionFactory());
    }

    [Fact]
    public void Validate_AcceptsLiveProcess_AndRequiresProcessCommand()
    {
        var valid = DesktopSettings.Default with
        {
            Vision = new VisionSettings
            {
                Source = VisionSources.LiveProcess,
                ProcessCommand = "py tools/yolo-bridge/mbri_yolo_bridge.py --stub vision/stub.csv",
            },
        };
        Assert.Empty(valid.Validate());

        var missingCommand = valid with { Vision = valid.Vision with { ProcessCommand = " " } };
        Assert.Contains(missingCommand.Validate(), error => error.Contains("vision.processCommand"));

        // liveProcess 进白名单后未知源仍被拒, 报错文案不变。
        var unknown = valid with { Vision = valid.Vision with { Source = "telepathy" } };
        Assert.Contains(unknown.Validate(), error => error.Contains("unsupported vision.source"));
    }

    [Fact]
    public void VehicleSensorOverride_ValidationRejectsUnknownPresetAndOutOfRangeOffsets()
    {
        var unknownPreset = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with { SensorProfileId = "custom:whatever" },
        };
        Assert.Contains(unknownPreset.Validate(), error => error.Contains("vehicle.sensorProfileId"));

        var outOfRange = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with
            {
                SensorOffsets = new Dictionary<string, SensorOffset>
                {
                    ["uL"] = new(0.6, 0, 0, 0),
                    ["uR"] = new(0, 0, 0.3, 0),
                },
            },
        };
        Assert.Contains(outOfRange.Validate(), error => error.Contains("vehicle.sensorOffsets"));

        // 边界内合法: |dx|,|dy| <= 0.5 m, |dz| <= 0.2 m, |yaw| <= π。
        var inBounds = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with
            {
                SensorProfileId = SensorProfiles.WheeledCombat11.Id,
                SensorDisabled = ["gray_front"],
                SensorOffsets = new Dictionary<string, SensorOffset>
                {
                    ["diag_left_front"] = new(0.5, -0.5, 0.2, Math.PI),
                },
            },
        };
        Assert.Empty(inBounds.Validate());
    }

    [Fact]
    public void ApplyVehicleOverrides_SensorCloneLandsOnBothVehicles()
    {
        var settings = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with
            {
                SensorProfileId = SensorProfiles.WheeledCombat11.Id,
                SensorDisabled = ["gray_front"],
                SensorOffsets = new Dictionary<string, SensorOffset>
                {
                    ["diag_left_front"] = new(0.01, -0.02, 0.003, 0.1),
                },
            },
        };
        var scenario = new Scenario
        {
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
            Vehicles = new Dictionary<string, VehicleProfile>
            {
                // us 显式自带 wheeledCombat11, them 不带(走 Legacy14 fallback) —— 两条基底解析路径。
                [RoleNames.Us] = new() { Id = "glb-2026", Sensors = SensorProfiles.WheeledCombat11 },
                [RoleNames.Them] = new() { Id = "glb-2026" },
            },
        };

        var applied = settings.ApplyVehicleOverrides(scenario);
        var us = applied.Vehicles[RoleNames.Us].Sensors;
        Assert.NotNull(us);
        Assert.Equal("custom:wheeledCombat11", us.Id);
        Assert.True(us.Channels.First(c => c.Id == "gray_front").Disabled);
        var diag = us.Channels.First(c => c.Id == "diag_left_front");
        Assert.Equal(
            SensorProfiles.WheeledCombat11.Channels.First(c => c.Id == "diag_left_front").Forward + 0.01,
            diag.Forward, 12);
        // 未触碰通道逐位不变。
        Assert.False(us.Channels.First(c => c.Id == "gray_rear").Disabled);

        var them = applied.Vehicles[RoleNames.Them].Sensors;
        Assert.NotNull(them);
        Assert.Equal("custom:wheeledCombat11", them.Id);
        Assert.True(them.Channels.First(c => c.Id == "gray_front").Disabled);
    }

    [Fact]
    public void ApplyVehicleOverrides_NoSensorOverridesKeepsScenarioProfile()
    {
        var scenario = new Scenario
        {
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
            Vehicles = new Dictionary<string, VehicleProfile>
            {
                [RoleNames.Us] = new() { Id = "glb-2026", Sensors = SensorProfiles.WheeledCombat11 },
                [RoleNames.Them] = new() { Id = "glb-2026" },
            },
        };

        // 无覆盖(默认设置)必须原样保留场景自带 profile —— 返回 null 会把它清空成 fallback。
        var applied = DesktopSettings.Default.ApplyVehicleOverrides(scenario);
        Assert.Same(SensorProfiles.WheeledCombat11, applied.Vehicles[RoleNames.Us].Sensors);
        Assert.Null(applied.Vehicles[RoleNames.Them].Sensors);
    }

    [Fact]
    public void SettingsStore_RoundTripsSensorOverridesAndProcessCommand()
    {
        var work = Path.Combine(Path.GetTempPath(), $"desktop-store-{Guid.NewGuid():N}");
        _tempDirs.Add(work);
        Directory.CreateDirectory(work);
        var store = new SettingsStore(Path.Combine(work, "settings.json"));
        var settings = DesktopSettings.Default with
        {
            Vehicle = DesktopSettings.Default.Vehicle with
            {
                SensorProfileId = SensorProfiles.Legacy14.Id,
                SensorDisabled = ["gF"],
                SensorOffsets = new Dictionary<string, SensorOffset> { ["uL"] = new(0.01, 0.02, 0.003, -0.1) },
            },
            Vision = new VisionSettings
            {
                Source = VisionSources.LiveProcess,
                ProcessCommand = "py bridge.py --stub x.csv",
            },
            BlockLayout = new BlockLayoutSettings { BuffCount = 3, DebuffCount = 2, RandomPositions = true },
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(SensorProfiles.Legacy14.Id, loaded.Vehicle.SensorProfileId);
        Assert.Equal(["gF"], loaded.Vehicle.SensorDisabled);
        var offset = Assert.Single(loaded.Vehicle.SensorOffsets);
        Assert.Equal("uL", offset.Key);
        Assert.Equal(0.01, offset.Value.Dx, 12);
        Assert.Equal(-0.1, offset.Value.Yaw, 12);
        Assert.Equal(VisionSources.LiveProcess, loaded.Vision.Source);
        Assert.Equal("py bridge.py --stub x.csv", loaded.Vision.ProcessCommand);
        Assert.NotNull(loaded.BlockLayout);
        Assert.Equal(3, loaded.BlockLayout!.BuffCount);
        Assert.Equal(2, loaded.BlockLayout.DebuffCount);
        Assert.True(loaded.BlockLayout.RandomPositions);
    }

    // ---------- ApplyBlocks: 设置页能量块布局覆盖 (2026-10-04) ----------

    [Fact]
    public void ApplyBlocks_FollowScenario_KeepsScenarioUntouched()
    {
        var scenario = new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks };

        // 覆盖缺失(老设置文件)与两个数量都为 null(显式跟随场景)都原样返回。
        Assert.Same(scenario, DesktopSettings.Default.ApplyBlocks(scenario));
        var follow = DesktopSettings.Default with
        {
            BlockLayout = new BlockLayoutSettings { BuffCount = null, DebuffCount = null },
        };
        Assert.Same(scenario, follow.ApplyBlocks(scenario));
    }

    [Fact]
    public void ApplyBlocks_CustomCounts_OfficialCoordsFirst_ExtraSeeded()
    {
        var settings = DesktopSettings.Default with
        {
            BlockLayout = new BlockLayoutSettings { BuffCount = 3, DebuffCount = 2 },
        };
        var scenario = new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks };

        var applied = settings.ApplyBlocks(scenario);
        Assert.Equal(5, applied.Blocks.Count);
        Assert.Equal(
            [BlockKind.Buff, BlockKind.Buff, BlockKind.Buff, BlockKind.Debuff, BlockKind.Debuff],
            applied.Blocks.Select(b => b.Kind).ToList());
        // 前两个增益/第一个减益用官方冻结坐标, 多出的交裁判按 seed 放置(null)。
        Assert.Equal(1.35, applied.Blocks[0].X!.Value, 9);
        Assert.Equal(1.35, applied.Blocks[0].Y!.Value, 9);
        Assert.Equal(2.5, applied.Blocks[1].X!.Value, 9);
        Assert.Equal(2.6, applied.Blocks[1].Y!.Value, 9);
        Assert.Null(applied.Blocks[2].X);
        Assert.Equal(1.6, applied.Blocks[3].X!.Value, 9);
        Assert.Equal(2.4, applied.Blocks[3].Y!.Value, 9);
        Assert.Null(applied.Blocks[4].X);
        Assert.Empty(applied.Validate());
    }

    [Fact]
    public void ApplyBlocks_RandomPositions_AllSeeded()
    {
        var settings = DesktopSettings.Default with
        {
            BlockLayout = new BlockLayoutSettings { BuffCount = 2, DebuffCount = 1, RandomPositions = true },
        };
        var applied = settings.ApplyBlocks(new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks });
        Assert.Equal(3, applied.Blocks.Count);
        Assert.All(applied.Blocks, b => Assert.Null(b.X));
        Assert.All(applied.Blocks, b => Assert.Null(b.Y));
        Assert.Empty(applied.Validate());
    }

    [Fact]
    public void ApplyBlocks_ClampsTotalToMaxBlocks_BuffsFirst()
    {
        var settings = DesktopSettings.Default with
        {
            BlockLayout = new BlockLayoutSettings { BuffCount = 8, DebuffCount = 8 },
        };
        var applied = settings.ApplyBlocks(new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks });
        Assert.Equal(Scenario.MaxBlocks, applied.Blocks.Count);
        Assert.Equal(8, applied.Blocks.Count(b => b.Kind == BlockKind.Buff));
        Assert.Equal(4, applied.Blocks.Count(b => b.Kind == BlockKind.Debuff));
        Assert.Empty(applied.Validate());
    }

    [Fact]
    public void ApplyBlocks_ZeroBlocks_IsValidPureCombat()
    {
        var settings = DesktopSettings.Default with
        {
            BlockLayout = new BlockLayoutSettings { BuffCount = 0, DebuffCount = 0 },
        };
        var applied = settings.ApplyBlocks(new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks });
        Assert.Empty(applied.Blocks);
        Assert.Empty(applied.Validate());
    }
}
