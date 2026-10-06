// 设置页的显示层文案 (批3 R3.4/R3.5): 页脚生效时机标注的统一常量 + 校验错误的
// 中文映射。无 Godot 依赖, 链进 Sim.Tests 回归 (见 Sim.Tests.csproj) ——
// DesktopSettings.Validate() 的结构化英文消息不改, 中文化只发生在这里。

namespace Sim.GodotShell;

/// <summary>
/// 设置页统一文案 (R3.5)。生效时机口径与 Main.ReloadSessionForScenarioTemplate 的
/// 实际行为一致: 实况且非布局编辑中 = 应用设置后自动重开当前对局; 回放/布局编辑中
/// = 挂待生效, 下一场或 F5 重置时应用。显示项 (窗口尺寸/模式/界面缩放) 不触发重置。
/// </summary>
public static class SettingsText
{
    /// <summary>显示页页脚: 显示项即时生效, 不重置对局。</summary>
    public const string DisplayApplyFooter = "生效时机：立即生效（显示设置不重置对局）。";

    /// <summary>其余页页脚: 应用设置后自动重开当前对局 (例外为回放/布局编辑中)。</summary>
    public const string AutoReloadApplyFooter =
        "生效时机：应用设置后自动重开当前对局；回放/布局编辑中为下一场生效（F5 可立即重置并应用）。";

    /// <summary>面板顶部: 没有待生效修改时的说明。</summary>
    public const string NoPendingChangesNote =
        "显示设置立即生效 · 其余设置保存后自动重开当前对局（回放/布局编辑中为下一场生效）";

    /// <summary>面板顶部: 已有修改挂在待生效 (回放/布局编辑中应用过设置)。</summary>
    public const string PendingChangesNote =
        "已有修改待下一场生效（回放/布局编辑中不自动重置）· F5 可立即重置并应用";

    // ---------- 批4 打磨: 控件文案/示例路径集中区 ----------
    // 说明 (R4.6): 用户可见的固定文案一律引用这里, 面板里不再散落字面量;
    // 动态文案 (随取值/状态变化的 note) 用下面的方法生成。

    /// <summary>控制器页标题。</summary>
    public const string ControllerTitle = "外部小车控制器";

    /// <summary>我方 (RL 展演) 命令行占位示例。</summary>
    public const string ControllerCommandPlaceholderUs =
        "例如：py -3.12 -X utf8 ../tools/rl-bridge/rl_desktop_runner.py --checkpoint <zip>";

    /// <summary>对手命令行占位示例。</summary>
    public const string ControllerCommandPlaceholderThem = "例如：python my_controller.py";

    /// <summary>外观模型路径占位示例 (按角色区分文件名)。</summary>
    public static string ModelPathPlaceholder(string role)
        => $"例如：C:/models/{role}.glb 或 res://models/{role}.glb（留空 = primitive 分件）";

    /// <summary>文件对话框过滤器: 外观模型。</summary>
    public const string ModelFilter = "*.glb,*.gltf ; 机器人外观模型 (glTF)";

    /// <summary>文件对话框过滤器: 场景 JSON。</summary>
    public const string ScenarioFilter = "*.json ; 场景 (Scenario)";

    /// <summary>文件对话框过滤器: 真车 CSV。</summary>
    public const string CsvFilter = "*.csv ; 真车检测流 (MBri 方言)";

    /// <summary>能量块页说明 (R4.6 集中的硬编码文案)。</summary>
    public const string BlocksIntro =
        "自定义比赛的能量块数量与类型：增益块被推上台我方 +3，减益块被推上台对方 +6。"
        + "关闭自定义 = 跟随场景/官方布局（2 增益 + 1 减益，行为逐位不变）。改动保存后自动重开当前对局生效。";

    /// <summary>能量块页: 跟随场景档的实时说明。</summary>
    public const string BlocksFollowNote = "跟随场景：使用场景文件/官方布局的能量块（行为逐位不变）。";

    /// <summary>落位方式 (官方坐标优先) 的实时说明。</summary>
    public const string BlockPlacementOfficialText = "前两个增益块与第一个减益块用官方坐标，多出的块由裁判确定性放置";

    /// <summary>落位方式 (全部随机) 的实时说明。</summary>
    public const string BlockPlacementRandomText =
        "全部块由裁判按种子确定性放置（禁区：避台沿 0.35m / 避两车 0.8m / 避中央 0.6m / 块间 0.5m）";

    /// <summary>小车页实时派生提示 (转速/轮径 → 轮端极速 → 登台/恢复时限)。</summary>
    public static string VehicleNote(double rpm, double maxSpeed)
        => $"默认配套：博创尚和 2342 开环电机（12V，减速后 {rpm:0} RPM）。"
            + $"轮端极速 ≈ {maxSpeed:0.000} m/s；登台/恢复时限随极速自动缩放；"
            + "扭矩当前仅存档（仿真为速度伺服）。";

    /// <summary>视觉页: 证据包目录字段标题。</summary>
    public const string VisionEvidenceTitle = "证据包目录";

    /// <summary>视觉页: 证据包目录占位示例。</summary>
    public const string VisionEvidencePlaceholder = "例如：vision/evidence-mini（含 frames.jsonl + import-report.json）";

    /// <summary>视觉页: 真车 CSV 字段标题。</summary>
    public const string VisionCsvTitle = "真车 CSV 路径";

    /// <summary>视觉页: 真车 CSV 占位示例。</summary>
    public const string VisionCsvPlaceholder = "例如：vision/hunt_drive_20260817_095205.csv（MBri 73 列方言）";

    /// <summary>视觉页: 推理进程命令行字段标题。</summary>
    public const string VisionProcessTitle = "推理进程命令行";

    /// <summary>视觉页: 推理进程命令行占位示例。</summary>
    public const string VisionProcessPlaceholder =
        "例如：py tools/yolo-bridge/mbri_yolo_bridge.py --stub vision/stub.csv（stdout 逐帧 JSONL）";

    /// <summary>视觉页: 当前来源不使用的输入, 标题后缀灰字说明 (R4.6)。</summary>
    public const string NotUsedByCurrentSource = "（当前来源不使用）";
}

/// <summary>
/// <see cref="DesktopSettings.Validate"/> 英文消息模板 → 中文的显示层映射 (批3 R3.4)。
/// <see cref="DesktopSettings.Validate"/> 本体保持结构化英文键不动 (它是设置文件
/// schema 诊断/CLI 契约); 未识别的消息一律原文返回, 绝不让错误消失 (映射表漏项由
/// SettingsInteractionTests 的源扫描测试报警: Validate 里出现新模板即红)。
/// </summary>
public static class SettingsValidationMessages
{
    private const string Prefix = "settings: ";

    private static readonly (string Name, string Role)[] ControllerNames =
        [("usController", "我方"), ("themController", "对手")];

    /// <summary>把一条 Validate 消息翻成中文; 非 "settings: " 前缀或未识别模板 → 原文返回。</summary>
    public static string Localize(string message)
    {
        if (string.IsNullOrEmpty(message) || !message.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return message;
        }
        return LocalizeBody(message[Prefix.Length..]) ?? message;
    }

    private static string? LocalizeBody(string body)
    {
        switch (body)
        {
            case "window must be present.":
                return "缺少窗口设置（window）。";
            case "window.width must be between 640 and 7680.":
                return "窗口宽度必须在 640–7680 像素之间。";
            case "window.height must be between 360 and 4320.":
                return "窗口高度必须在 360–4320 像素之间。";
            case "uiScale must be a finite value between 0.8 and 1.4.":
                return "界面缩放必须是 0.8–1.4 之间的有限数值。";
            case "vehicle must be present.":
                return "缺少小车设置（vehicle）。";
            case "vehicle.mass must be between 0.2 and 20 kg.":
                return "整车质量必须在 0.2–20 kg 之间。";
            case "vehicle.motorRpm must be between 10 and 2000.":
                return "电机转速必须在 10–2000 RPM 之间。";
            case "vehicle.motorTorque must be between 0.05 and 50 N·m.":
                return "电机扭矩必须在 0.05–50 N·m 之间。";
            case "vehicle.wheelRadius must be between 0.005 and 0.1 m.":
                return "驱动轮半径必须在 0.005–0.1 m 之间。";
            case "vehicle.sensorOffsets must be finite with |dx|,|dy| <= 0.5 m, |dz| <= 0.2 m, |yaw| <= π.":
                return "传感器偏移必须是有限数值：|dx|、|dy| ≤ 0.5 m，|dz| ≤ 0.2 m，|yaw| ≤ π。";
            case "vision must be present.":
                return "缺少视觉设置（vision）。";
            case "vision.evidencePath is required for the visionReplay source.":
                return "视觉来源为 visionReplay 时必须填写证据包目录。";
            case "vision.csvPath is required for the liveBridge source.":
                return "视觉来源为 liveBridge 时必须填写真车 CSV 路径。";
            case "vision.processCommand is required for the liveProcess source.":
                return "视觉来源为 liveProcess 时必须填写推理进程命令行。";
            case "vision.maxAgeMs must be between 1 and 5000.":
                return "帧过期窗口必须在 1–5000 ms 之间。";
            case "matchOverrides.matchDuration must be a finite value greater than 0.":
                return "比赛时长必须是大于 0 的有限数值。";
            case "matchOverrides.seed must be between 0 and 4096.":
                return "随机种子必须在 0–4096 之间。";
        }

        // 批4 起源码里是完整单字面量; 仍按前缀匹配 (后缀参数组合的展示不敏感),
        // 源扫描测试同时保证该字面量以句号收尾、不被拼接截断。
        const string backendHead = "matchOverrides.physicsBackendOverride must be null (follow scenario), ";
        if (body.StartsWith(backendHead, StringComparison.Ordinal))
        {
            return "物理后端覆盖不受支持：只能留空（跟随场景）、'follow'、'legacy'、"
                + "'mujoco-v1' 或 'mujoco-v2'。";
        }
        if (TryQuoted(body, "unsupported schemaVersion '", out var schemaVersion))
        {
            return $"设置文件版本不受支持：schemaVersion={schemaVersion}（本程序只支持 v1，不做自动迁移）。";
        }
        if (TryQuoted(body, "unsupported window.mode '", out var windowMode))
        {
            return $"窗口模式不受支持：'{windowMode}'（可用：windowed / fullscreen）。";
        }
        if (TryQuoted(body, "unknown simulation parameter '", out var unknownParameter))
        {
            return $"未知仿真参数：'{unknownParameter}'。";
        }
        if (TryQuoted(body, "unsupported vision.source '", out var visionSource))
        {
            return $"视觉来源不受支持：'{visionSource}'（可用：classifyRate / visionReplay / "
                + "liveBridge / liveProcess）。";
        }
        if (LocalizeSimulationParameterBounds(body) is { } bounds)
        {
            return bounds;
        }
        if (LocalizeSensorProfileId(body) is { } sensorProfile)
        {
            return sensorProfile;
        }
        foreach (var (name, role) in ControllerNames)
        {
            if (body == $"{name} must be present.")
            {
                return $"缺少控制器设置（{name}）。";
            }
            if (body == $"{name}.mode must be 'builtin', 'mbri' or 'external'.")
            {
                return $"{role}控制器来源不受支持：只能为 builtin / mbri / external。";
            }
            if (body == $"{name}.command is required for an external controller.")
            {
                return $"{role}选择外部命令时必须填写启动命令。";
            }
            if (body == $"{name}.timeoutMs must be between 1 and 5000.")
            {
                return $"{role}控制器超时必须在 1–5000 ms 之间。";
            }
        }
        return null;
    }

    /// <summary>
    /// `simulation parameter 'K' must be finite, &gt;= 0 and &lt;= 1000[, and an integer].`
    /// → 中文 (保留键名与上下界, 半开区间语义不变)。
    /// </summary>
    private static string? LocalizeSimulationParameterBounds(string body)
    {
        const string head = "simulation parameter '";
        const string marker = "must be finite, ";
        if (!body.StartsWith(head, StringComparison.Ordinal))
        {
            return null;
        }
        var keyEnd = body.IndexOf('\'', head.Length);
        if (keyEnd < 0)
        {
            return null;
        }
        var key = body[head.Length..keyEnd];
        var markerIndex = body.IndexOf(marker, keyEnd, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }
        var tail = body[(markerIndex + marker.Length)..];
        var integerRequired = tail.EndsWith("and an integer.", StringComparison.Ordinal);
        if (integerRequired)
        {
            tail = tail[..^"and an integer.".Length].TrimEnd().TrimEnd(',');
        }
        var parts = tail.TrimEnd('.').Split(" and ", StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }
        var suffix = integerRequired ? "，且为整数" : "";
        return $"仿真参数 '{key}' 必须是有限数值，范围 {CnBound(parts[0])} 且 {CnBound(parts[1])}{suffix}。";
    }

    /// <summary>`vehicle.sensorProfileId must be null (follow scenario), 'a' or 'b'.` → 中文。</summary>
    private static string? LocalizeSensorProfileId(string body)
    {
        const string head = "vehicle.sensorProfileId must be null (follow scenario), ";
        if (!body.StartsWith(head, StringComparison.Ordinal))
        {
            return null;
        }
        var parts = body[head.Length..].TrimEnd('.').Split(" or ", StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? $"传感器预设不受支持：只能留空（跟随场景）、{parts[0]} 或 {parts[1]}。"
            : null;
    }

    /// <summary>`&gt;=`/`&lt;=` 换成全角符号 (消息里其余数值原样)。</summary>
    private static string CnBound(string bound)
        => bound.Replace(">=", "≥", StringComparison.Ordinal)
            .Replace("<=", "≤", StringComparison.Ordinal);

    /// <summary>取 `head` 之后到下一个 `'` 之间的内容 (head 不匹配返回 false)。</summary>
    private static bool TryQuoted(string body, string head, out string value)
    {
        value = "";
        if (!body.StartsWith(head, StringComparison.Ordinal))
        {
            return false;
        }
        var rest = body[head.Length..];
        var end = rest.IndexOf('\'');
        if (end < 0)
        {
            return false;
        }
        value = rest[..end];
        return true;
    }
}
