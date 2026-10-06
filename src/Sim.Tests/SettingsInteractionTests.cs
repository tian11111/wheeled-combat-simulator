using Sim.Protocol;
using Sim.GodotShell;
using System.Text.RegularExpressions;

namespace Sim.Tests;

/// <summary>
/// 批3 交互统一的回归:
/// R3.1 应用设置是否"只改了显示项"的深比较 (含 Vehicle ⇒ 只改小车也触发自动重开);
/// R3.2 传感器预设切换保留当前未应用的通道编辑 (同名保留 / 异名回保存档或基底默认);
/// R3.4 校验错误显示层中文化 (映射逐条断言 + 源扫描钉住 Validate 模板清单)。
/// </summary>
public sealed class SettingsInteractionTests
{
    // ---------- R3.1 Vehicle 计入变更检测 ----------

    private static DesktopSettings Base() => DesktopSettings.Default with
    {
        SimulationParameters = new Dictionary<string, double> { ["EDGE_THRESHOLD"] = 421 },
        Vision = new VisionSettings { Source = VisionSources.ClassifyRate, MaxAgeMs = 500 },
        UsController = new ControllerProfile { Mode = ControllerModes.BuiltIn, TimeoutMs = 100 },
    };

    [Fact]
    public void MatchRelevantEqual_DisplayOnlyChanges_DoNotCount()
    {
        var left = Base();
        var right = left with
        {
            Window = new WindowSettings { Width = 1600, Height = 900, Mode = DisplayModes.Fullscreen },
            UiScale = 1.3,
        };
        Assert.True(DesktopSettingsDiff.MatchRelevantEqual(left, right));
        Assert.True(DesktopSettingsDiff.MatchRelevantEqual(left, left));
    }

    [Theory]
    [InlineData(4.2, 120, 1.72, 0.0325)]
    [InlineData(3.5, 200, 1.72, 0.0325)]
    [InlineData(3.5, 120, 5.0, 0.0325)]
    [InlineData(3.5, 120, 1.72, 0.05)]
    public void MatchRelevantEqual_VehicleBodyChanges_Count(double mass, double rpm, double torque, double radius)
    {
        var left = Base();
        var right = left with
        {
            Vehicle = left.Vehicle with
            {
                Mass = mass,
                MotorRpm = rpm,
                MotorTorque = torque,
                WheelRadius = radius,
            },
        };
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, right));
    }

    [Fact]
    public void MatchRelevantEqual_VehicleSensorOverrides_Count()
    {
        var left = Base();
        // 传感器预设切换
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            Vehicle = left.Vehicle with { SensorProfileId = SensorProfiles.Legacy14.Id },
        }));
        // 通道禁用清单
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            Vehicle = left.Vehicle with { SensorDisabled = ["uL"] },
        }));
        // 挂点偏移
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            Vehicle = left.Vehicle with
            {
                SensorOffsets = new Dictionary<string, SensorOffset> { ["uL"] = new(0.01, 0, 0, 0) },
            },
        }));
    }

    [Fact]
    public void MatchRelevantEqual_NullAndEmptyVehicleCollections_AreEqual()
    {
        var withNulls = Base() with
        {
            Vehicle = new VehicleSettings { SensorDisabled = null!, SensorOffsets = null! },
        };
        var withEmpties = Base() with
        {
            Vehicle = new VehicleSettings { SensorDisabled = [], SensorOffsets = new() },
        };
        Assert.True(DesktopSettingsDiff.MatchRelevantEqual(withNulls, withEmpties));
        Assert.True(DesktopSettingsDiff.MatchRelevantEqual(withEmpties, withNulls));

        // 顺序不承载含义: 同一禁用集合的不同顺序视为相等。
        var ordered = Base() with
        {
            Vehicle = new VehicleSettings { SensorDisabled = ["uL", "gF"] },
        };
        var reordered = Base() with
        {
            Vehicle = new VehicleSettings { SensorDisabled = ["gF", "uL"] },
        };
        Assert.True(DesktopSettingsDiff.MatchRelevantEqual(ordered, reordered));
    }

    [Fact]
    public void MatchRelevantEqual_OtherMatchRelevantFields_StillCount()
    {
        var left = Base();
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            SimulationParameters = new Dictionary<string, double> { ["EDGE_THRESHOLD"] = 422 },
        }));
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            Vision = left.Vision with { Source = VisionSources.VisionReplay, EvidencePath = "x" },
        }));
        // liveProcess 档只改命令行同样算对局相关变更 (每场子进程按 ProcessCommand 新建;
        // 漏比会让"只改命令"不重开且日志谎报显示设置已应用)。
        var liveProcess = left with
        {
            Vision = left.Vision with { Source = VisionSources.LiveProcess, ProcessCommand = "py a.py" },
        };
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(liveProcess, liveProcess with
        {
            Vision = liveProcess.Vision with { ProcessCommand = "py b.py" },
        }));
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            UsController = left.UsController with { TimeoutMs = 250 },
        }));
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            BlockLayout = new BlockLayoutSettings { BuffCount = 3, DebuffCount = 1 },
        }));
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            MatchOverrides = new MatchOverrides { Seed = 7 },
        }));
        Assert.False(DesktopSettingsDiff.MatchRelevantEqual(left, left with
        {
            DevContact = new DevContact { L3BlockWallBlock = false },
        }));
    }

    // ---------- R3.2 传感器预设切换保留未应用编辑 ----------

    /// <summary>预设基底在 UI 上按 legacy14 展示 (跟随场景档), 通道 id 为 gF/gB/...——切换用同基底。</summary>
    private static readonly SensorProfile LegacyBase = SensorProfiles.Legacy14;

    [Fact]
    public void Merge_SameNamedChannels_KeepUnappliedEdits()
    {
        var edited = new Dictionary<string, SensorChannelEdit>
        {
            ["uL"] = new(false, 0.02, -0.03, 0.004, 0.25),
            ["gF"] = new(true, 0.05, 0, 0, 0),
        };
        var merged = SensorChannelEdits.Merge(null, edited, LegacyBase);

        Assert.Equal(LegacyBase.Channels.Count, merged.Count);
        Assert.Equal(edited["uL"], merged["uL"]);
        Assert.Equal(edited["gF"], merged["gF"]);
        // 未编辑的同基底通道 = 基底默认 (启用 + 零偏移), 不漂移。
        Assert.Equal(SensorChannelEdit.Default, merged["gB"]);
        Assert.Equal(SensorChannelEdit.Default, merged["r"]);
    }

    [Fact]
    public void Merge_ChannelsOnlyInTheNewBase_FallBackToBaseDefaults()
    {
        // 从 legacy14 基底 (gF/uL/...) 切到 wheeledCombat11 基底: 通道名全不同,
        // 旧编辑随行消失 (异名重置), 新基底每路都是默认值 —— 不出现"上一套的偏移漂过来"。
        var edited = new Dictionary<string, SensorChannelEdit>
        {
            ["gF"] = new(false, 0.05, 0.05, 0.05, 0.5),
            ["uL"] = new(false, 0.01, 0, 0, 0),
        };
        var merged = SensorChannelEdits.Merge(null, edited, SensorProfiles.WheeledCombat11);

        Assert.Equal(SensorProfiles.WheeledCombat11.Channels.Count, merged.Count);
        Assert.DoesNotContain("gF", merged.Keys);
        Assert.All(merged.Values, edit => Assert.Equal(SensorChannelEdit.Default, edit));
    }

    [Fact]
    public void Merge_SavedValues_BackTheRowsAndCurrentEditsWin()
    {
        // 保存档打底: 新基底里当前控件没显示过的通道回上次保存值 (切走再切回不丢),
        // 同名通道仍以当前控件的未应用编辑为准 (R3.2 主语义)。
        var saved = new Dictionary<string, SensorChannelEdit>
        {
            ["shovel_front"] = new(false, 0.02, 0, 0, 0),
            ["uL"] = new(false, 0.01, 0, 0, 0),
        };
        var currentRows = new Dictionary<string, SensorChannelEdit>
        {
            ["uL"] = new(true, 0.05, 0, 0, 0),
        };

        var sameBase = SensorChannelEdits.Merge(saved, currentRows, LegacyBase);
        Assert.Equal(currentRows["uL"], sameBase["uL"]);
        Assert.Equal(SensorChannelEdit.Default, sameBase["gF"]);

        // 中间基底 (legacy14) 的控件值不含 wheeledCombat11 通道 → 回保存档而不是默认。
        var roundTrip = SensorChannelEdits.Merge(saved, currentRows, SensorProfiles.WheeledCombat11);
        Assert.Equal(saved["shovel_front"], roundTrip["shovel_front"]);
        Assert.Equal(SensorChannelEdit.Default, roundTrip["gray_front"]);
    }

    [Fact]
    public void Merge_InitialStateWithoutEdits_IsIdempotentAndDefault()
    {
        foreach (var profile in new[] { SensorProfiles.Legacy14, SensorProfiles.WheeledCombat11 })
        {
            var merged = SensorChannelEdits.Merge(null, null, profile);
            Assert.Equal(profile.Channels.Count, merged.Count);
            Assert.All(merged.Values, edit => Assert.Equal(SensorChannelEdit.Default, edit));

            // 空字典与 null 等价 (初始态不漂移)。
            var fromEmpty = SensorChannelEdits.Merge(null, new Dictionary<string, SensorChannelEdit>(), profile);
            Assert.Equal(merged.Count, fromEmpty.Count);
            foreach (var (channelId, edit) in merged)
            {
                Assert.Equal(edit, fromEmpty[channelId]);
            }
        }
    }

    // ---------- R3.4 校验错误文案中文化 ----------

    /// <summary>
    /// 逐条映射清单 (英文样例 → 期望中文)。这些样例文本与 DesktopSettings.Validate
    /// 的模板一一对应; 模板改版由 <see cref="ValidateSource_EverySettingsMessageLiteral_Localizes"/>
    /// 的源扫描抓出。
    /// </summary>
    public static TheoryData<string, string> ValidationCases() => new()
    {
        { "settings: unsupported schemaVersion '2'.", "设置文件版本不受支持：schemaVersion=2（本程序只支持 v1，不做自动迁移）。" },
        { "settings: window must be present.", "缺少窗口设置（window）。" },
        { "settings: window.width must be between 640 and 7680.", "窗口宽度必须在 640–7680 像素之间。" },
        { "settings: window.height must be between 360 and 4320.", "窗口高度必须在 360–4320 像素之间。" },
        { "settings: unsupported window.mode 'bogus'.", "窗口模式不受支持：'bogus'（可用：windowed / fullscreen）。" },
        { "settings: uiScale must be a finite value between 0.8 and 1.4.", "界面缩放必须是 0.8–1.4 之间的有限数值。" },
        { "settings: unknown simulation parameter 'foo'.", "未知仿真参数：'foo'。" },
        { "settings: simulation parameter 'EDGE_THRESHOLD' must be finite, >= 0 and <= 1000, and an integer.",
            "仿真参数 'EDGE_THRESHOLD' 必须是有限数值，范围 ≥ 0 且 ≤ 1000，且为整数。" },
        { "settings: simulation parameter 'MOUNT_ANGLE_MAX' must be finite, > 0 and < 1.2.",
            "仿真参数 'MOUNT_ANGLE_MAX' 必须是有限数值，范围 > 0 且 < 1.2。" },
        { "settings: vehicle must be present.", "缺少小车设置（vehicle）。" },
        { "settings: vehicle.mass must be between 0.2 and 20 kg.", "整车质量必须在 0.2–20 kg 之间。" },
        { "settings: vehicle.motorRpm must be between 10 and 2000.", "电机转速必须在 10–2000 RPM 之间。" },
        { "settings: vehicle.motorTorque must be between 0.05 and 50 N·m.", "电机扭矩必须在 0.05–50 N·m 之间。" },
        { "settings: vehicle.wheelRadius must be between 0.005 and 0.1 m.", "驱动轮半径必须在 0.005–0.1 m 之间。" },
        { "settings: vehicle.sensorProfileId must be null (follow scenario), 'wheeledCombat11' or 'legacy14'.",
            "传感器预设不受支持：只能留空（跟随场景）、'wheeledCombat11' 或 'legacy14'。" },
        { "settings: vehicle.sensorOffsets must be finite with |dx|,|dy| <= 0.5 m, |dz| <= 0.2 m, |yaw| <= π.",
            "传感器偏移必须是有限数值：|dx|、|dy| ≤ 0.5 m，|dz| ≤ 0.2 m，|yaw| ≤ π。" },
        { "settings: vision must be present.", "缺少视觉设置（vision）。" },
        { "settings: unsupported vision.source 'bogus'.", "视觉来源不受支持：'bogus'（可用：classifyRate / visionReplay / liveBridge / liveProcess）。" },
        { "settings: vision.evidencePath is required for the visionReplay source.", "视觉来源为 visionReplay 时必须填写证据包目录。" },
        { "settings: vision.csvPath is required for the liveBridge source.", "视觉来源为 liveBridge 时必须填写真车 CSV 路径。" },
        { "settings: vision.processCommand is required for the liveProcess source.", "视觉来源为 liveProcess 时必须填写推理进程命令行。" },
        { "settings: vision.maxAgeMs must be between 1 and 5000.", "帧过期窗口必须在 1–5000 ms 之间。" },
        { "settings: usController must be present.", "缺少控制器设置（usController）。" },
        { "settings: themController must be present.", "缺少控制器设置（themController）。" },
        { "settings: usController.mode must be 'builtin', 'mbri' or 'external'.", "我方控制器来源不受支持：只能为 builtin / mbri / external。" },
        { "settings: themController.mode must be 'builtin', 'mbri' or 'external'.", "对手控制器来源不受支持：只能为 builtin / mbri / external。" },
        { "settings: usController.command is required for an external controller.", "我方选择外部命令时必须填写启动命令。" },
        { "settings: themController.command is required for an external controller.", "对手选择外部命令时必须填写启动命令。" },
        { "settings: usController.timeoutMs must be between 1 and 5000.", "我方控制器超时必须在 1–5000 ms 之间。" },
        { "settings: themController.timeoutMs must be between 1 and 5000.", "对手控制器超时必须在 1–5000 ms 之间。" },
        { "settings: matchOverrides.physicsBackendOverride must be null (follow scenario), 'follow', 'legacy', 'mujoco-v1' or 'mujoco-v2'.",
            "物理后端覆盖不受支持：只能留空（跟随场景）、'follow'、'legacy'、'mujoco-v1' 或 'mujoco-v2'。" },
        { "settings: matchOverrides.matchDuration must be a finite value greater than 0.", "比赛时长必须是大于 0 的有限数值。" },
        { "settings: matchOverrides.seed must be between 0 and 4096.", "随机种子必须在 0–4096 之间。" },
    };

    [Theory]
    [MemberData(nameof(ValidationCases))]
    public void Localize_KnownValidateMessage_MapsToChinese(string english, string expected)
    {
        Assert.Equal(expected, SettingsValidationMessages.Localize(english));
    }

    [Fact]
    public void Localize_UnknownMessage_FallsBackToOriginalText()
    {
        const string unknownPrefixed = "settings: this template does not exist yet.";
        Assert.Equal(unknownPrefixed, SettingsValidationMessages.Localize(unknownPrefixed));
        Assert.Equal("boom", SettingsValidationMessages.Localize("boom"));
        Assert.Equal("", SettingsValidationMessages.Localize(""));
    }

    /// <summary>
    /// 真实 Validate() 输出的中文化冒烟: 构造一份到处越界的设置, 断言它产出的每条
    /// 消息都能被映射 (不残留 "settings:" 前缀 = 中文), 覆盖规则与真实取值格式的配合。
    /// </summary>
    [Fact]
    public void Localize_CoversRealValidateOutput()
    {
        var broken = DesktopSettings.Default with
        {
            SchemaVersion = 9,
            Window = new WindowSettings { Width = 1, Height = 1, Mode = "bogus" },
            UiScale = 5,
            SimulationParameters = new Dictionary<string, double> { ["nope"] = 1, ["EDGE_THRESHOLD"] = -5 },
            Vehicle = new VehicleSettings
            {
                Mass = 0,
                MotorRpm = 0,
                MotorTorque = 0,
                WheelRadius = 0,
                SensorProfileId = "bogus",
                SensorOffsets = new Dictionary<string, SensorOffset> { ["uL"] = new(9, 9, 9, 9) },
            },
            Vision = new VisionSettings { Source = "bogus", MaxAgeMs = 0 },
            UsController = new ControllerProfile { Mode = "bogus", TimeoutMs = 0 },
            ThemController = new ControllerProfile { Mode = ControllerModes.External, Command = "", TimeoutMs = 0 },
            MatchOverrides = new MatchOverrides { PhysicsBackendOverride = "bogus", MatchDuration = 0, Seed = 9999 },
        };
        AssertAllLocalized(broken);

        foreach (var source in new[] { VisionSources.VisionReplay, VisionSources.LiveBridge, VisionSources.LiveProcess })
        {
            var missingPath = DesktopSettings.Default with
            {
                Vision = new VisionSettings { Source = source },
            };
            AssertAllLocalized(missingPath);
        }
    }

    private static void AssertAllLocalized(DesktopSettings settings)
    {
        var errors = settings.Validate().ToArray();
        Assert.NotEmpty(errors);
        foreach (var error in errors)
        {
            var localized = SettingsValidationMessages.Localize(error);
            Assert.False(localized.StartsWith("settings:", StringComparison.Ordinal), $"未本地化: {error}");
        }
    }

    /// <summary>
    /// 源扫描 (对照清单): godot/src/DesktopSettings.cs 两个 Validate 区域里的每个
    /// "settings: " 消息字面量都必须能被映射中文化; 模板数量与 <see cref="ValidateTemplateCount"/>
    /// 不符 (新增/删除模板) 也要红 —— 逼着改映射表时同步清单。
    /// 字面量还必须是完整消息 (批4: 拼接片段只被正则吃到第一段, 会被这里抓住)。
    /// </summary>
    [Fact]
    public void ValidateSource_EverySettingsMessageLiteral_Localizes()
    {
        var literals = ScanValidateMessageLiterals();
        Assert.Equal(ValidateTemplateCount, literals.Count);

        foreach (var literal in literals)
        {
            // 完整消息以句号收尾 (插值模板以占位符收尾); 以逗号/空格结尾 = 被字符串
            // 拼接截断的片段 —— 只扫到片段时映射规则与覆盖清单都会失真。
            Assert.True(literal.EndsWith('.') || literal.EndsWith('}'),
                $"Validate 消息字面量疑似被字符串拼接截断 (正则只扫到片段): {literal}");
            var sample = SubstitutePlaceholders(literal);
            var localized = SettingsValidationMessages.Localize(sample);
            Assert.False(localized.StartsWith("settings:", StringComparison.Ordinal), $"未本地化: {literal}");
            Assert.NotEqual(sample, localized);
            Assert.DoesNotContain("must", localized, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Validate 区域里 "settings: " 字面量的对照数量 (模板增删必须先改映射并更新此数)。</summary>
    private const int ValidateTemplateCount = 28;

    private static List<string> ScanValidateMessageLiterals()
    {
        var text = File.ReadAllText(FindRepoFile("godot/src/DesktopSettings.cs"));
        var start = text.IndexOf("public IEnumerable<string> Validate()", StringComparison.Ordinal);
        var isValidator = text.IndexOf("public bool IsValid", StringComparison.Ordinal);
        var catalog = text.IndexOf("public static IEnumerable<string> Validate(IReadOnlyDictionary<string, double>? values)",
            StringComparison.Ordinal);
        Assert.True(start > 0 && isValidator > start, "DesktopSettings.Validate 区域定位失败");
        Assert.True(catalog > isValidator, "SimulationParameterCatalog.Validate 区域定位失败");

        var region = text[start..isValidator] + text[catalog..];
        var pattern = new Regex("\\\"(?<literal>settings: [^\\\"]*)\\\"");
        return pattern.Matches(region).Select(m => m.Groups["literal"].Value).ToList();
    }

    /// <summary>
    /// 把源码模板里的 C# 插值占位替换成真实样例值, 让字面量能走完整条映射规则。
    /// 新模板引入未知占位符 → 样例里残留 {xxx} → 映射失败 → 测试红 (提醒补样例/映射)。
    /// </summary>
    private static string SubstitutePlaceholders(string literal)
    {
        var samples = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{SchemaVersion}"] = "1",
            ["{Window.Mode}"] = "windowed",
            ["{name}"] = "usController",
            ["{Vision.Source}"] = "classifyRate",
            ["{key}"] = "EDGE_THRESHOLD",
            ["{bounds}"] = ">= 0",
            ["{upper}"] = "<= 1000",
            ["{SensorProfiles.WheeledCombat11.Id}"] = SensorProfiles.WheeledCombat11.Id,
            ["{SensorProfiles.Legacy14.Id}"] = SensorProfiles.Legacy14.Id,
        };
        var sample = literal;
        foreach (var (placeholder, value) in samples)
        {
            sample = sample.Replace(placeholder, value, StringComparison.Ordinal);
        }
        return sample;
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

    // ---------- R4.5 MakeOption 按 id 取义 ----------

    /// <summary>MakeOption 登记到 Meta 的 id 表语义 (下标只表示 UI 顺序)。</summary>
    [Fact]
    public void OptionIds_IndexToId_FollowsUiOrderAndFallsBack()
    {
        string[] ids = [DisplayModes.Windowed, DisplayModes.Fullscreen];

        Assert.Equal(DisplayModes.Windowed, SettingsOptionIds.IdAt(ids, 0, "fallback"));
        Assert.Equal(DisplayModes.Fullscreen, SettingsOptionIds.IdAt(ids, 1, "fallback"));
        // 越界/空表 = 控件未建好或表丢失: 退回既有默认档语义 (不臆造档位)。
        Assert.Equal("fallback", SettingsOptionIds.IdAt(ids, 2, "fallback"));
        Assert.Equal("fallback", SettingsOptionIds.IdAt(ids, -1, "fallback"));
        Assert.Equal("fallback", SettingsOptionIds.IdAt(null, 0, "fallback"));
        Assert.Equal("fallback", SettingsOptionIds.IdAt([], 0, "fallback"));
    }

    [Fact]
    public void OptionIds_IdToIndex_UnknownOrNullLandsOnFirstSlot()
    {
        string[] visionIds =
        [
            VisionSources.ClassifyRate, VisionSources.VisionReplay,
            VisionSources.LiveBridge, VisionSources.LiveProcess,
        ];

        Assert.Equal(0, SettingsOptionIds.IndexOf(visionIds, VisionSources.ClassifyRate));
        Assert.Equal(2, SettingsOptionIds.IndexOf(visionIds, VisionSources.LiveBridge));
        // 未收录/缺省 = 首档 (跟随场景/窗口化/内置 FSM 这类"行为不变"的默认档)。
        Assert.Equal(0, SettingsOptionIds.IndexOf(visionIds, "telepathy"));
        Assert.Equal(0, SettingsOptionIds.IndexOf(visionIds, null));
        Assert.Equal(0, SettingsOptionIds.IndexOf(null, VisionSources.LiveBridge));

        // 传感器预设的"跟随场景"档 id 是空串, 必须能精确定位到它。
        string[] profileIds = ["", SensorProfiles.WheeledCombat11.Id, SensorProfiles.Legacy14.Id];
        Assert.Equal(0, SettingsOptionIds.IndexOf(profileIds, ""));
        Assert.Equal(1, SettingsOptionIds.IndexOf(profileIds, SensorProfiles.WheeledCombat11.Id));

        // 能量块落位 (R4.5 档位常量) 往返。
        string[] placementIds = [BlockPlacementModes.Official, BlockPlacementModes.Random];
        Assert.Equal(1, SettingsOptionIds.IndexOf(placementIds, BlockPlacementModes.Random));
    }

    /// <summary>
    /// 源扫描: SettingsPanel 的读取端不得再对下标取义 (R4.5), 且每个 MakeOption 调用点
    /// 的 (label,id) 表内 id 唯一 (重复 id 会让 id→下标映射静默落到首档)。
    /// </summary>
    [Fact]
    public void MakeOptionCallSites_RegisterUniqueIds_AndReadersUseIdMapping()
    {
        var text = File.ReadAllText(FindRepoFile("godot/src/SettingsPanel.cs"));

        // 读取端一律走 GetSelectedId (下标仅表示 UI 顺序)。
        Assert.DoesNotContain(".Selected == ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Selected switch", text, StringComparison.Ordinal);

        var callSites = ScanCalls(text, "MakeOption").Where(call => call.Contains('"')).ToList();
        // 当前实况 6 处 (窗口模式/能量块落位/传感器预设/比赛后端/视觉来源/控制器来源)。
        Assert.True(callSites.Count >= 6, $"MakeOption 调用点数量异常: {callSites.Count}");
        foreach (var call in callSites)
        {
            var ids = Regex.Matches(call,
                    "\\(\\s*\"[^\"]*\"\\s*,\\s*(?<id>[A-Za-z_][A-Za-z0-9_.]*|\"[^\"]*\")\\s*\\)")
                .Select(match => match.Groups["id"].Value)
                .ToList();
            Assert.True(ids.Count >= 2, $"MakeOption 调用缺少 (label,id) 元组: {call}");
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>扫出源码里所有 <paramref name="name"/>+(...) 调用 (括号配平), 含嵌套参数。</summary>
    private static List<string> ScanCalls(string text, string name)
    {
        var calls = new List<string>();
        var search = 0;
        while (true)
        {
            var start = text.IndexOf(name + "(", search, StringComparison.Ordinal);
            if (start < 0)
            {
                return calls;
            }
            var depth = 0;
            var end = text.Length;
            for (var i = start + name.Length; i < text.Length; i++)
            {
                if (text[i] == '(')
                {
                    depth++;
                }
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i + 1;
                        break;
                    }
                }
            }
            calls.Add(text[start..end]);
            search = end;
        }
    }
}
