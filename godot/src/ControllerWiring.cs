// 控制器来源装配决策：桌面壳与 Sim.Tests 共用的纯逻辑（无 Godot 节点、无引擎、
// 无进程/文件 IO）。进程真正的启停留在 DesktopLiveDriver / ControllerPreflight，
// 这里只回答"这次设置应该跑哪个控制器、是否允许展演交接、失败时如何响亮回退"。
//
// 我方外部控制器 = SCORE_BLOCK 展演（RL 策略）入口：只在 mujoco 线场景启用
// （scenarios/wushu-ring-2026-mujoco.json / v2，训练与评测的同一后端）；
// legacy 场景（physics.backend 未写）的物理/FSM 轨迹与训练分布不同，选择外部
// 进程必须被拒绝并回退内置 FSM —— 设置本身照旧保存，换回 mujoco 场景后重应用即恢复。

using Sim.Protocol;

namespace Sim.GodotShell;

/// <summary>装配结果说明的类别（响亮回退区分"拒绝"与"仅告警"）。</summary>
public static class ControllerNoticeKinds
{
    /// <summary>外部控制器被拒绝：本场回退内置 FSM（设置已保存，问题修复后重应用即恢复）。</summary>
    public const string Rejected = "rejected";

    /// <summary>预检未通过但不足以拒绝（如首帧模型加载超时）：保留外部控制器并告警。</summary>
    public const string Warning = "warning";
}

/// <summary>一条装配说明：<see cref="Kind"/> 见 <see cref="ControllerNoticeKinds"/>。</summary>
public sealed record ControllerNotice(string Kind, string Message)
{
    public bool IsRejection => string.Equals(Kind, ControllerNoticeKinds.Rejected, StringComparison.Ordinal);
}

/// <summary>
/// 实际生效的控制器装配：替换后的双方 profile + 是否启用 SCORE_BLOCK 展演交接 +
/// 需要向控制台/HUD 响亮展示的说明（null = 无）。
/// </summary>
public sealed record ControllerAssignment(
    ControllerProfile Us,
    ControllerProfile Them,
    bool Exhibition,
    ControllerNotice? Notice)
{
    public static ControllerAssignment BuiltIn { get; } = new(new ControllerProfile(), new ControllerProfile(), false, null);
}

public static class ControllerWiring
{
    /// <summary>拒绝原因（legacy 场景选择外部进程控制器）：中文短句，HUD/日志直接展示。</summary>
    public const string RequiresMujocoScenario =
        "展演需要 mujoco 场景（physics.backend 未写 = legacy，训练线为 mujoco）；已回退内置 FSM";

    /// <summary>我方外部控制器只在 mujoco 后端场景启用（legacy 缺省视为不启用）。</summary>
    public static bool ScenarioSupportsExhibition(Scenario? scenario)
        => scenario?.Physics is { Backend: PhysicsSpec.Mujoco };

    /// <summary>
    /// 装配决策（纯函数）：
    /// ① 我方 external 且场景非 mujoco ⇒ 拒绝（回退内置 FSM，说明见
    ///    <see cref="RequiresMujocoScenario"/>）；
    /// ② 我方 external 且应用时预检给出确定性失败（启动失败/协议坏应答）⇒ 拒绝并带上预检文案；
    /// ③ 预检超时（<c>timeout</c>，首帧模型加载可能慢于 TimeoutMs）⇒ 保留外部控制器但告警；
    /// ④ 其余保持设置的来源不变。对手沿用既有语义（不被场景门控，也不参与展演）。
    /// 空命令行视为内置 FSM（<see cref="DesktopSettings.Validate"/> 已要求 external 必填命令）。
    /// </summary>
    public static ControllerAssignment Resolve(Scenario? scenario, ControllerProfile? us,
        ControllerProfile? them, ControllerPreflightResult? usPreflight = null)
    {
        us ??= new ControllerProfile();
        them ??= new ControllerProfile();
        if (!IsRunnableExternal(us))
        {
            return new ControllerAssignment(new ControllerProfile(), them, false, null);
        }

        if (!ScenarioSupportsExhibition(scenario))
        {
            return new ControllerAssignment(new ControllerProfile(), them, false,
                new ControllerNotice(ControllerNoticeKinds.Rejected, RequiresMujocoScenario));
        }

        if (usPreflight is { Ok: false })
        {
            var timeout = string.Equals(usPreflight.FailureKind, ControllerPreflightKinds.Timeout,
                StringComparison.Ordinal);
            var notice = new ControllerNotice(
                timeout ? ControllerNoticeKinds.Warning : ControllerNoticeKinds.Rejected,
                timeout
                    ? $"外部控制器预检超时，保留外部控制器（可能是首帧模型加载慢于 TimeoutMs，建议 ≥ 5000 ms）：{usPreflight.Message}"
                    : $"外部控制器预检失败，本场回退内置 FSM：{usPreflight.Message}（设置已保存，修复后重新应用即可）");
            return timeout
                ? new ControllerAssignment(us, them, true, notice)
                : new ControllerAssignment(new ControllerProfile(), them, false, notice);
        }

        return new ControllerAssignment(us, them, true, null);
    }

    /// <summary>external 且命令非空才算"真的能跑"（其余按内置 FSM 处理）。</summary>
    public static bool IsRunnableExternal(ControllerProfile? profile)
        => profile is { IsExternal: true } && !string.IsNullOrWhiteSpace(profile.Command);

    /// <summary>HUD 来源描述：内置 FSM / 外部进程 · &lt;进程或脚本名&gt;。</summary>
    public static string DescribeSource(ControllerProfile? profile)
    {
        if (!IsRunnableExternal(profile))
        {
            return "内置 FSM";
        }
        var name = CommandName(profile!.Command);
        return name.Length == 0 ? "外部进程" : $"外部进程 · {name}";
    }

    /// <summary>
    /// 命令行里的可读身份：优先取最后一个 <c>*.py</c> 脚本 token（RL 桥真实入口
    /// 形如 <c>py -3.12 … tools/rl-bridge/rl_desktop_runner.py --checkpoint …</c>），
    /// 否则取首个 token；去引号、去目录，保留扩展名。空命令返回空串。
    /// </summary>
    public static string CommandName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "";
        }
        var tokens = SplitCommandTokens(command);
        if (tokens.Count == 0)
        {
            return "";
        }
        var script = tokens.LastOrDefault(token =>
            token.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        return BaseName(script ?? tokens[0]);
    }

    /// <summary>引号感知的空白分词（与 Sim.Controller 的命令拆分口径同形）。</summary>
    private static List<string> SplitCommandTokens(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }

    private static string BaseName(string token)
    {
        var normalized = token.Replace('/', '\\');
        var slash = normalized.LastIndexOf('\\');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }
}
