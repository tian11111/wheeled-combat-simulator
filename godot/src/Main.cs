// 脚手架 (未编译验证 — 需要 Godot 4 .NET):
// 根控制器。以固定 0.05s 步长驱动 Sim.Core 权威内核，把每帧快照投影为
// RenderFrame 交给 ArenaVisualizer 渲染；所有状态变更都走内核指令
// (Arm/Pause/Resume/RestartPenalty)，渲染端不复刻任何规则。
//
// 操作:
//   Enter (ui_accept) — 发令 arm（PREP/READY 阶段）
//   P                 — 暂停 / 继续
//   R                 — 我方重启（+4 判罚给对手）
// 外部策略接入与回放请用 Sim.Cli；本客户端面向内置 FSM 的观察与调试。

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class Main : Node
{
    /// <summary>确定性种子；与 Sim.Cli 相同种子产生相同比赛。</summary>
    [Export]
    public long Seed { get; set; } = 42;

    /// <summary>可选场景文件路径（scenarios/*.json）；为空时使用官方默认布局。</summary>
    [Export]
    public string ScenarioPath { get; set; } = "";

    private MatchEngine _engine = null!;
    private readonly Queue<Snapshot> _pending = new();
    private double _accumulator;
    private ArenaVisualizer? _visualizer;

    public override void _Ready()
    {
        var scenario = string.IsNullOrEmpty(ScenarioPath)
            ? new Scenario { Seed = Seed, Blocks = OfficialLayout.Blocks }
            : ProtocolJson.Deserialize<Scenario>(System.IO.File.ReadAllText(ScenarioPath));
        _engine = new MatchEngine(scenario);
        _visualizer = GetNodeOrNull<ArenaVisualizer>("ArenaVisualizer");
        GD.Print($"[shell] core={MatchEngine.CoreVersion} seed={Seed} tick={scenario.Field.TickSeconds}s");
    }

    public override void _Process(double delta)
    {
        HandleCommands();

        // 固定步长推进: 渲染掉帧不改变仿真时钟 (design: 渲染与仿真解耦)。
        _accumulator += delta;
        var tickSeconds = _engine.Scenario.Field.TickSeconds;
        while (_accumulator >= tickSeconds && !_engine.Done)
        {
            _accumulator -= tickSeconds;
            _pending.Enqueue(_engine.Tick());
        }

        // 每渲染帧只消费最新一帧快照（旧帧丢弃，避免追赶式快放）。
        Snapshot? latest = null;
        while (_pending.Count > 0)
        {
            latest = _pending.Dequeue();
        }
        if (latest is not null)
        {
            _visualizer?.ShowFrame(SnapshotView.From(latest));
        }
    }

    private void HandleCommands()
    {
        if (Input.IsActionJustPressed("ui_accept")
            && _engine.Phase is MatchControlPhase.Prep or MatchControlPhase.Ready)
        {
            _engine.Arm();
        }
        if (Input.IsActionJustPressed("pause_toggle"))
        {
            if (_engine.Paused)
            {
                _engine.Resume();
            }
            else
            {
                _engine.Pause("桌面端手动暂停");
            }
        }
        if (Input.IsActionJustPressed("restart_us"))
        {
            _engine.RestartPenalty(RoleNames.Us, "restart");
        }
    }
}
