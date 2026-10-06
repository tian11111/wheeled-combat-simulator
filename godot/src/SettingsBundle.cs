// 配置包 (settings bundle): 团队复现用的单文件导出/导入载体 (批1 R1.2)。
// - 场景/训练配置内嵌"文件原文"而不是路径引用, 跨机器不依赖原路径;
// - bundleSchemaVersion 独立于 DesktopSettings.schemaVersion 编号, 导入只接受
//   当前版本, 不做自动迁移 (不匹配 → 中文拒绝原因);
// - 序列化沿用 ProtocolJson 风格 (camelCase、null 省略), 落盘 .tmp + File.Move
//   原子替换 (与 SettingsStore 同模式);
// - 本文件不依赖 Godot: 序列化/校验语义在 Sim.Tests 里无渲染器回归。

using Sim.Protocol;
using System.Text.Json;

namespace Sim.GodotShell;

/// <summary>配置包内嵌的场景/布局文件 (原文 + 来源文件名, 不是路径引用)。</summary>
public sealed record SettingsBundleFile
{
    /// <summary>来源文件名 (仅文件名, 导入时写 user://imported/&lt;fileName&gt;)。</summary>
    public string FileName { get; init; } = "";

    /// <summary>场景 JSON 原文 (逐字内嵌, 不重新序列化)。</summary>
    public string Content { get; init; } = "";

    /// <summary>true = 布局编辑器产物 (arena-layout-v1)。</summary>
    public bool IsLayout { get; init; }
}

/// <summary>
/// 单文件配置包: 主设置全量 + 外观模型全量(可空) + 场景/布局文件(可空) +
/// 训练配置(可空)。字段名即 JSON 键 (camelCase 由 ProtocolJson 生成)。
/// </summary>
public sealed record SettingsBundle
{
    public const int CurrentBundleSchemaVersion = 1;

    /// <summary>缺省 0 = 反序列化时缺键; RejectUnsupportedVersion 会把它当"不是配置包"拒绝。</summary>
    public int BundleSchemaVersion { get; init; }

    /// <summary>导出时间 (UTC ISO-8601, 仅供人读/审计)。</summary>
    public string ExportedAt { get; init; } = "";

    /// <summary>
    /// wushu-ring-settings.json 全量; 导入端校验后走 ApplyDesktopSettings 链。
    /// 不设初值: JSON 里缺 "settings" 键与显式 null 一样落到 Validate 的"缺少主设置"
    /// 拒绝, 不会被记录初值静默填成默认档再覆盖用户设置 (Create 恒填本字段)。
    /// </summary>
    public DesktopSettings? Settings { get; init; }

    /// <summary>robot-models.json 全量; null = 导出机没有外观模型配置。</summary>
    public Dictionary<string, RobotModelConfig>? RobotModels { get; init; }

    /// <summary>当前场景/布局文件原文; null = 导出机使用官方内置布局。</summary>
    public SettingsBundleFile? ScenarioFile { get; init; }

    /// <summary>训练配置 JSON 对象 (train.py --config 格式); null = 未附带。</summary>
    public JsonElement? TrainConfig { get; init; }

    /// <summary>
    /// 组装导出内容: 版本号与时间戳由这里统一填写, 调用方只提供四类内容。
    /// </summary>
    public static SettingsBundle Create(DesktopSettings settings,
        IReadOnlyDictionary<string, RobotModelConfig>? robotModels,
        SettingsBundleFile? scenarioFile, JsonElement? trainConfig)
        => new()
        {
            BundleSchemaVersion = CurrentBundleSchemaVersion,
            ExportedAt = DateTimeOffset.UtcNow.ToString("O"),
            Settings = settings,
            RobotModels = robotModels is null
                ? null
                : new Dictionary<string, RobotModelConfig>(robotModels, StringComparer.Ordinal),
            ScenarioFile = scenarioFile,
            TrainConfig = trainConfig,
        };

    /// <summary>
    /// 导入版本校验: 匹配返回 null, 不匹配返回中文拒绝原因 (PRD R1.3, 无自动迁移)。
    /// 0 = JSON 没有 bundleSchemaVersion 键 (属性缺省值) —— 说明根本不是配置包文件
    /// (如 robot-models.json / 场景 json), 用明确措辞拒绝, 不走进"缺少主设置"这类
    /// 结构校验错误让用户误以为导出坏了 (2026-10-06 验收实测踩坑)。
    /// </summary>
    public static string? RejectUnsupportedVersion(int bundleSchemaVersion)
    {
        if (bundleSchemaVersion == CurrentBundleSchemaVersion)
        {
            return null;
        }
        return bundleSchemaVersion == 0
            ? "该文件不是配置包（缺少 bundleSchemaVersion 标记）：请选择\"导出配置包\"生成的 .json 文件"
            : $"配置包版本不兼容（v{bundleSchemaVersion}，本程序支持 v{CurrentBundleSchemaVersion}），已拒绝导入";
    }

    /// <summary>
    /// 结构校验 (先 Deserialize 再 Validate, type-safety 规范): 返回中文原因, 空 = 可导入。
    /// 内嵌场景在此完整解析 + 校验, 保证确认弹窗展示的内容真的能落地。
    /// </summary>
    public IEnumerable<string> Validate()
    {
        if (Settings is null)
        {
            yield return "配置包缺少主设置（settings）";
        }
        else
        {
            foreach (var error in Settings.Validate())
            {
                yield return $"主设置无效: {error}";
            }
        }

        if (RobotModels is not null)
        {
            foreach (var (role, config) in RobotModels)
            {
                if (string.IsNullOrWhiteSpace(role) || config is null)
                {
                    yield return "配置包外观模型含空角色或空条目";
                }
            }
        }

        if (ScenarioFile is { } file)
        {
            if (string.IsNullOrWhiteSpace(file.FileName))
            {
                yield return "配置包场景文件缺少文件名";
            }
            else if (file.FileName is "." or "..")
            {
                // ".." 能通过"无路径分隔符"检查, 但会拼出 user://imported/.. 并让
                // File.Move 落到目录上; 这里显式拒绝, 让导入在确认前失败。
                yield return "配置包场景文件名不能是 '.' 或 '..'";
            }
            else if (!string.Equals(file.FileName, Path.GetFileName(file.FileName), StringComparison.Ordinal))
            {
                yield return "配置包场景文件名不能包含路径分隔符";
            }
            if (string.IsNullOrWhiteSpace(file.Content))
            {
                yield return "配置包场景文件内容为空";
            }
            else
            {
                // yield return 不能出现在 catch 子句里: 先记下解析错误再统一产出。
                string? parseError = null;
                Scenario? scenario = null;
                try
                {
                    scenario = ProtocolJson.Deserialize<Scenario>(file.Content);
                }
                catch (JsonException error)
                {
                    parseError = error.Message;
                }
                if (parseError is not null)
                {
                    yield return $"配置包场景文件不是合法 JSON: {parseError}";
                }
                if (scenario is not null)
                {
                    foreach (var error in scenario.Validate())
                    {
                        yield return $"配置包场景文件无效: {error}";
                    }
                }
            }
        }

        if (TrainConfig is { ValueKind: not JsonValueKind.Object })
        {
            yield return "配置包训练配置必须是 JSON 对象";
        }
    }

    public bool IsValid => !Validate().Any();
}

/// <summary>
/// 配置包文件读写边界 (Godot-free, 与 SettingsStore 同模式): Save 前先校验并原子替换;
/// Load 失败/JSON 损坏直接抛出 —— 导入必须响亮报错, 不能像设置文件那样静默回退默认值。
/// </summary>
public sealed class SettingsBundleStore
{
    public const string DefaultFileName = "settings-bundle.json";

    private readonly string _path;

    public SettingsBundleStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Settings bundle path must not be empty.", nameof(path));
        }
        _path = Path.GetFullPath(path);
    }

    public SettingsBundle Load()
        => ProtocolJson.Deserialize<SettingsBundle>(File.ReadAllText(_path));

    public void Save(SettingsBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var errors = bundle.Validate().ToArray();
        if (errors.Length > 0)
        {
            throw new ArgumentException($"Invalid settings bundle: {string.Join(" | ", errors)}", nameof(bundle));
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, ProtocolJson.Serialize(bundle));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// 读取训练配置文件 (train.py --config 的 JSON) 并校验为对象, 内容原样内嵌配置包;
    /// 未知键/字段类型校验留在 train_config.py 侧 (桌面不复制训练 schema)。
    /// </summary>
    public static JsonElement ReadTrainConfigObject(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"训练配置必须是 JSON 对象: {path}");
        }
        return document.RootElement.Clone();
    }
}
