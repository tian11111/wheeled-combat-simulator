// 布局编辑入口的纯判定: 无 Godot 依赖, 链进 Sim.Tests 回归 (见 Sim.Tests.csproj)。
// 只回答「现在能不能进编辑模式」和「不能的话原因是什么」, 不触碰引擎或界面。

using Sim.Core;

namespace Sim.GodotShell;

/// <summary>
/// Pure gate for entering the layout editor.
///
/// 规则: 只要比赛**尚未发令**就能编辑 —— Prep 与 Ready 都算数, 因为快照把两者
/// 同样报成 <c>MatchPhase.Prep</c> (界面显示「发令准备」), 此时双方机器人仍处于
/// WAIT_START, 没有比分可言。
///
/// 2026-10-06 修复记录: 该门禁原先只认 Prep。而内核在发令准备倒计时(60 s)走完后
/// 会自行从 Prep 转为 Ready (MatchEngine 的 prep 计时), 界面看上去毫无变化, 编辑
/// 入口却静默失效 —— 用户只能看到一个"按 E 没反应"的窗口, 拒绝理由还被写进了没有
/// 控制台的日志文件。抽取成纯函数后, 这张判定矩阵由
/// <c>src/Sim.Tests/EditorGateTests.cs</c> 钉住。
/// </summary>
public static class EditorGate
{
    /// <summary>True when the layout editor may be entered right now.</summary>
    public static bool CanEnter(SessionMode mode, bool liveDriverActive, MatchControlPhase phase)
        => mode != SessionMode.Replay
           && !liveDriverActive
           && phase is MatchControlPhase.Prep or MatchControlPhase.Ready;

    /// <summary>
    /// Why the editor cannot be entered, or <c>null</c> when it can. The text is
    /// shown verbatim on the HUD, so keep it short and actionable.
    /// </summary>
    public static string? RejectReason(SessionMode mode, bool liveDriverActive, MatchControlPhase phase)
    {
        if (!CanEnter(mode, liveDriverActive, phase))
        {
            if (mode == SessionMode.Replay)
            {
                return "回放模式不能编辑 · 按 F5 回到实况";
            }
            if (liveDriverActive)
            {
                return "外部控制器运行中 · 按 F5 重置后再编辑";
            }
            return phase == MatchControlPhase.Finished
                ? "比赛已结束 · 按 F5 重置后再按 E"
                : "比赛已进行中 · 按 F5 重置后再按 E";
        }
        return null;
    }
}
