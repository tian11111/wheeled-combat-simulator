using Sim.Core;
using Sim.GodotShell;
using Sim.Protocol;
using System.Diagnostics;
using System.Text.Json;

namespace Sim.Tests;

/// <summary>
/// 配置包 (批1 R1.2/R1.3) 的序列化往返、空段省略、版本拒绝与内嵌场景校验回归,
/// 外加 train.py --config 的"显式 CLI 覆盖配置值"语义断言 (stdlib-only 的
/// train_config.py 直接调 argparse, 不要求 SB3/gymnasium 环境)。
/// </summary>
public class SettingsBundleTests : IDisposable
{
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

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"settings-bundle-{Guid.NewGuid():N}");
        _tempDirs.Add(dir);
        Directory.CreateDirectory(dir);
        return dir;
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

    private static DesktopSettings SampleSettings() => DesktopSettings.Default with
    {
        Window = new WindowSettings { Width = 1600, Height = 900, Mode = DisplayModes.Fullscreen },
        UiScale = 1.2,
        SimulationParameters = new Dictionary<string, double> { ["EDGE_THRESHOLD"] = 421, ["irNoise"] = 0.03 },
        Vehicle = new VehicleSettings
        {
            Mass = 4.2,
            MotorRpm = 150,
            SensorProfileId = SensorProfiles.Legacy14.Id,
            SensorDisabled = ["gF"],
            SensorOffsets = new Dictionary<string, SensorOffset> { ["uL"] = new(0.01, 0.02, 0.003, -0.1) },
        },
        BlockLayout = new BlockLayoutSettings { BuffCount = 3, DebuffCount = 2, RandomPositions = true },
        Vision = new VisionSettings { Source = VisionSources.ClassifyRate, MaxAgeMs = 640 },
        UsController = new ControllerProfile { Mode = ControllerModes.External, Command = "py runner.py", TimeoutMs = 250 },
        ThemController = new ControllerProfile { Mode = ControllerModes.Mbri },
    };

    [Fact]
    public void Bundle_RoundTripsAllFourSectionsThroughStore()
    {
        var work = NewTempDir();
        var scenario = new Scenario { Seed = 7, Blocks = OfficialLayout.Blocks };
        var scenarioContent = ProtocolJson.Serialize(scenario);
        var trainConfigPath = Path.Combine(work, "train-config.json");
        File.WriteAllText(trainConfigPath,
            """{"steps": 1000, "out": "runs/x", "train_seed": 20260927, "reward": "aggression-v1"}""");

        var bundle = SettingsBundle.Create(
            SampleSettings(),
            new Dictionary<string, RobotModelConfig>(StringComparer.Ordinal)
            {
                [RoleNames.Us] = new RobotModelConfig { Path = "res://models/us.glb", Scale = 1.1, HeightOffset = 0.02 },
                [RoleNames.Them] = new RobotModelConfig { Path = "C:/models/them.gltf", YawOffset = -0.3 },
            },
            new SettingsBundleFile { FileName = "wushu-ring-2026.json", Content = scenarioContent },
            SettingsBundleStore.ReadTrainConfigObject(trainConfigPath));

        var path = Path.Combine(work, "bundle.json");
        var store = new SettingsBundleStore(path);
        store.Save(bundle);
        var loaded = store.Load();

        Assert.Empty(loaded.Validate());
        Assert.Equal(SettingsBundle.CurrentBundleSchemaVersion, loaded.BundleSchemaVersion);
        Assert.True(DateTimeOffset.TryParse(loaded.ExportedAt, out _), loaded.ExportedAt);

        Assert.NotNull(loaded.Settings);
        Assert.Equal(1600, loaded.Settings!.Window.Width);
        Assert.Equal(DisplayModes.Fullscreen, loaded.Settings.Window.Mode);
        Assert.Equal(1.2, loaded.Settings.UiScale, 12);
        Assert.Equal(421, loaded.Settings.SimulationParameters["EDGE_THRESHOLD"]);
        Assert.Equal(4.2, loaded.Settings.Vehicle.Mass, 12);
        Assert.Equal(SensorProfiles.Legacy14.Id, loaded.Settings.Vehicle.SensorProfileId);
        Assert.Equal(["gF"], loaded.Settings.Vehicle.SensorDisabled);
        Assert.Equal(-0.1, loaded.Settings.Vehicle.SensorOffsets["uL"].Yaw, 12);
        Assert.Equal(3, loaded.Settings.BlockLayout!.BuffCount);
        Assert.True(loaded.Settings.BlockLayout.RandomPositions);
        Assert.Equal(ControllerModes.External, loaded.Settings.UsController.Mode);
        Assert.Equal("py runner.py", loaded.Settings.UsController.Command);
        Assert.True(loaded.Settings.ThemController.IsMbri);

        Assert.NotNull(loaded.RobotModels);
        Assert.Equal(2, loaded.RobotModels!.Count);
        Assert.Equal("res://models/us.glb", loaded.RobotModels[RoleNames.Us].Path);
        Assert.Equal(1.1, loaded.RobotModels[RoleNames.Us].Scale, 12);
        Assert.Equal(-0.3, loaded.RobotModels[RoleNames.Them].YawOffset, 12);

        Assert.NotNull(loaded.ScenarioFile);
        Assert.Equal("wushu-ring-2026.json", loaded.ScenarioFile!.FileName);
        Assert.False(loaded.ScenarioFile.IsLayout);
        // 场景内容逐字内嵌: 不重新序列化, 不引用原路径。
        Assert.Equal(scenarioContent, loaded.ScenarioFile.Content);
        Assert.DoesNotContain(work, loaded.ScenarioFile.Content);

        Assert.NotNull(loaded.TrainConfig);
        Assert.Equal(JsonValueKind.Object, loaded.TrainConfig!.Value.ValueKind);
        Assert.Equal(1000, loaded.TrainConfig.Value.GetProperty("steps").GetInt32());
        Assert.Equal("aggression-v1", loaded.TrainConfig.Value.GetProperty("reward").GetString());

        // ProtocolJson 风格: 紧凑 + camelCase 键 + 原子写不留 .tmp。
        var text = File.ReadAllText(path);
        Assert.Contains("\"bundleSchemaVersion\":1", text);
        Assert.Contains("\"scenarioFile\":{", text);
        Assert.DoesNotContain("\n", text);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Bundle_OmitsNullSectionsAndRejectsInvalidContent()
    {
        var work = NewTempDir();
        var path = Path.Combine(work, "bundle.json");
        var store = new SettingsBundleStore(path);
        store.Save(SettingsBundle.Create(DesktopSettings.Default, null, null, null));

        var loaded = store.Load();
        Assert.NotNull(loaded.Settings);
        Assert.Null(loaded.RobotModels);
        Assert.Null(loaded.ScenarioFile);
        Assert.Null(loaded.TrainConfig);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\"robotModels\"", text);
        Assert.DoesNotContain("\"scenarioFile\"", text);
        Assert.DoesNotContain("\"trainConfig\"", text);

        // 缺主设置 / 场景内容坏 JSON / 文件名带路径 / 训练配置不是对象 → 全部拒绝。
        var broken = new SettingsBundle
        {
            Settings = null,
            ScenarioFile = new SettingsBundleFile { FileName = "sub/dir.json", Content = "not json" },
            TrainConfig = JsonDocument.Parse("[1, 2]").RootElement.Clone(),
        };
        var errors = broken.Validate().ToArray();
        Assert.Contains(errors, error => error.Contains("缺少主设置"));
        Assert.Contains(errors, error => error.Contains("路径分隔符"));
        Assert.Contains(errors, error => error.Contains("不是合法 JSON"));
        Assert.Contains(errors, error => error.Contains("必须是 JSON 对象"));
        Assert.Throws<ArgumentException>(() => new SettingsBundleStore(path).Save(broken));

        var emptyScenario = new SettingsBundle
        {
            Settings = DesktopSettings.Default,
            ScenarioFile = new SettingsBundleFile { FileName = "x.json", Content = "  " },
        };
        Assert.Contains(emptyScenario.Validate(), error => error.Contains("内容为空"));

        // 场景文件名 "." / ".." 通过"无路径分隔符"检查但会拼出目录路径 → 显式拒绝。
        var dotName = new SettingsBundle
        {
            Settings = DesktopSettings.Default,
            ScenarioFile = new SettingsBundleFile { FileName = "..", Content = "{}" },
        };
        Assert.Contains(dotName.Validate(), error => error.Contains("'.' 或 '..'"));

        // JSON 里缺 "settings" 键 (而不是显式 null): 反序列化后同样被判缺失,
        // 不能由记录初值静默填成默认档。
        var missingSettingsPath = Path.Combine(work, "missing-settings.json");
        File.WriteAllText(missingSettingsPath, """{"bundleSchemaVersion":1}""");
        var missingSettings = new SettingsBundleStore(missingSettingsPath).Load();
        Assert.Null(missingSettings.Settings);
        Assert.Contains(missingSettings.Validate(), error => error.Contains("缺少主设置"));
        Assert.Throws<ArgumentException>(() => new SettingsBundleStore(missingSettingsPath).Save(missingSettings));
    }

    [Fact]
    public void Bundle_LayoutFlagAndFileName_RoundTripWithEmbeddedLayout()
    {
        var work = NewTempDir();
        var path = Path.Combine(work, "bundle.json");
        var layout = new Scenario
        {
            Seed = 7,
            Blocks = OfficialLayout.Blocks,
            LayoutVersion = ProtocolVersion.ArenaLayoutV1,
        };
        new SettingsBundleStore(path).Save(SettingsBundle.Create(DesktopSettings.Default, null,
            new SettingsBundleFile
            {
                FileName = "arena-layout.json",
                Content = ProtocolJson.Serialize(layout),
                IsLayout = true,
            }, null));

        Assert.Contains("\"isLayout\":true", File.ReadAllText(path));
        var loaded = new SettingsBundleStore(path).Load();
        Assert.NotNull(loaded.ScenarioFile);
        Assert.True(loaded.ScenarioFile!.IsLayout);
        Assert.Equal("arena-layout.json", loaded.ScenarioFile.FileName);
        Assert.Empty(loaded.Validate());
    }

    [Fact]
    public void Bundle_UnsupportedSchemaVersion_IsRejectedWithChineseReason()
    {
        Assert.Null(SettingsBundle.RejectUnsupportedVersion(SettingsBundle.CurrentBundleSchemaVersion));
        Assert.Equal(
            "配置包版本不兼容（v2，本程序支持 v1），已拒绝导入",
            SettingsBundle.RejectUnsupportedVersion(2));

        // 篡改磁盘上的 bundleSchemaVersion 后, 导入端必须在应用任何内容前拒绝 (R1.3)。
        var work = NewTempDir();
        var path = Path.Combine(work, "bundle.json");
        new SettingsBundleStore(path).Save(SettingsBundle.Create(DesktopSettings.Default, null, null, null));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"bundleSchemaVersion\":1", "\"bundleSchemaVersion\":2"));
        var tampered = new SettingsBundleStore(path).Load();
        Assert.Equal(2, tampered.BundleSchemaVersion);
        var rejection = SettingsBundle.RejectUnsupportedVersion(tampered.BundleSchemaVersion);
        Assert.NotNull(rejection);
        Assert.Contains("配置包版本不兼容", rejection);
        Assert.Contains("已拒绝导入", rejection);
    }

    [Fact]
    public void Bundle_MissingSchemaVersionKey_IsRejectedAsNotABundle()
    {
        // 缺 bundleSchemaVersion 键 = 根本不是配置包 (如 robot-models.json)。
        // 必须用"不是配置包"措辞明确拒绝, 不能走进"缺少主设置"结构校验误导用户
        // (2026-10-06 验收实测: 导入选到外观模型文件, 报错让人以为是导出坏了)。
        Assert.Equal(0, new SettingsBundle { Settings = DesktopSettings.Default }.BundleSchemaVersion);
        var rejection = SettingsBundle.RejectUnsupportedVersion(0);
        Assert.NotNull(rejection);
        Assert.Contains("不是配置包", rejection);
        Assert.DoesNotContain("缺少主设置", rejection);

        // 端到端: 磁盘上没有 bundleSchemaVersion 键的 json, 版本门先行拒绝 ——
        // 导入流程在结构校验之前就返回"不是配置包", 用户不会看到"缺少主设置"。
        var work = NewTempDir();
        var path = Path.Combine(work, "not-a-bundle.json");
        File.WriteAllText(path, """{"us":{"path":"x.glb"}}""");
        var loaded = new SettingsBundleStore(path).Load();
        Assert.Equal(0, loaded.BundleSchemaVersion);
        Assert.NotNull(SettingsBundle.RejectUnsupportedVersion(loaded.BundleSchemaVersion));
    }

    [Fact]
    public void TrainConfigReader_RequiresJsonObject()
    {
        var work = NewTempDir();
        var path = Path.Combine(work, "train-config.json");

        File.WriteAllText(path, """{"steps": 10, "out": "runs/a"}""");
        var configuration = SettingsBundleStore.ReadTrainConfigObject(path);
        Assert.Equal(JsonValueKind.Object, configuration.ValueKind);
        Assert.Equal(10, configuration.GetProperty("steps").GetInt32());

        File.WriteAllText(path, "[1, 2]");
        Assert.Throws<InvalidDataException>(() => SettingsBundleStore.ReadTrainConfigObject(path));

        // train.py 的配置字段是 snake_case: 导出只做对象层校验, 未知键由 train_config.py 拒绝。
        File.WriteAllText(path, """{"stepz": 1}""");
        Assert.Equal(JsonValueKind.Object, SettingsBundleStore.ReadTrainConfigObject(path).ValueKind);
    }

    [Fact]
    public void TrainConfigCli_PythonArgparse_ExplicitFlagsOverrideConfigFile()
    {
        var python = FindPython();
        if (python is null)
        {
            // 环境没有 python 时不做断言 (解析语义的权威回归在
            // controllers/score_block_rl/selftest.py 的 "--config file semantics" 检查)。
            return;
        }

        var workDir = Path.GetDirectoryName(FindRepoFile("controllers/score_block_rl/train_config.py"))!;
        var script = """
import json
import tempfile
from pathlib import Path

import train_config

with tempfile.TemporaryDirectory() as tmp:
    config_path = Path(tmp) / "train-config.json"
    config_path.write_text(json.dumps({
        "steps": 1000, "out": "config-out", "train_seed": 20260927,
        "n_envs": 2, "dotnet": None, "cli_dll": "config-cli.dll",
        "scenario": "config-scenario.json", "reward": "aggression-v1",
        "checkpoint_interval": 25600,
    }), encoding="utf-8")
    parsed = train_config.build_argument_parser(train_config.load_train_config(config_path))
    args = parsed.parse_args(["--steps", "2000", "--reward", "v4"])
    assert (args.steps, args.reward) == (2000, "v4"), vars(args)
    assert (args.out, args.train_seed, args.n_envs) == ("config-out", 20260927, 2), vars(args)
    assert (args.dotnet, args.cli_dll, args.scenario, args.checkpoint_interval) == (
        None, "config-cli.dll", "config-scenario.json", 25600), vars(args)

    baseline = train_config.build_argument_parser().parse_args(["--out", "run-out"])
    assert (baseline.steps, baseline.train_seed, baseline.n_envs) == (500000, 20260925, 1), vars(baseline)
    assert (baseline.checkpoint_interval, baseline.reward, baseline.dotnet) == (51200, "v4", None), vars(baseline)

    bad_path = Path(tmp) / "bad.json"
    bad_path.write_text(json.dumps({"stepz": 1}), encoding="utf-8")
    try:
        train_config.load_train_config(bad_path)
    except ValueError as exc:
        assert "stepz" in str(exc), str(exc)
    else:
        raise AssertionError("unknown config key was accepted")

try:
    import train
    assert train.build_argument_parser is train_config.build_argument_parser
    assert (train.TRAIN_SEED, train.CHECKPOINT_INTERVAL_STEPS) == (
        train_config.TRAIN_SEED, train_config.CHECKPOINT_INTERVAL_STEPS)
except ImportError as exc:  # 无 SB3/gymnasium 环境: 只跳过接线断言, 解析断言已全部执行
    print("train import skipped:", exc)

print("train_config ok")
""";
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "python 断言超时");
        Assert.True(process.ExitCode == 0, $"python 退出码 {process.ExitCode}\nstdout: {stdout}\nstderr: {stderr}");
        Assert.Contains("train_config ok", stdout);
    }

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (probe is null)
                {
                    continue;
                }
                probe.WaitForExit(10_000);
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 候选解释器缺失: 试下一个。
            }
        }
        return null;
    }
}
