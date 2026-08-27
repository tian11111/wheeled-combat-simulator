// HUD 面板: 左侧状态卡 (剩余时间/比分/阶段/双方状态/判罚/最近事件), 右上角
// 镜头与操作说明, 底部回放控制条。所有控件使用锚点布局与固定尺寸文本, 动态
// 内容 (事件/剩余时间) 只改文本不改布局, 不遮挡主场景。

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class HudPanel : Control
{
    private Label? _status;
    private Label? _events;
    private Label? _help;
    private PanelContainer? _replayBar;
    private Label? _replayTick;
    private HSlider? _timeline;
    private Button? _playButton;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var pad = new Vector2(12, 12);

        _status = MakeLabel(new Vector2(12, 12), new Vector2(420, 176), 15);
        AddChild(_status);

        _events = MakeLabel(new Vector2(12, -220), new Vector2(560, 150), 13);
        _events.AnchorTop = 1;
        _events.AnchorBottom = 1;
        _events.ClipText = true;
        AddChild(_events);

        _help = MakeLabel(new Vector2(-332, 12), new Vector2(320, 190), 13);
        _help.AnchorLeft = 1;
        _help.AnchorRight = 1;
        _help.ClipText = true;
        AddChild(_help);

        BuildReplayBar();
    }

    private void BuildReplayBar()
    {
        _replayBar = new PanelContainer();
        var bar = (Control)_replayBar;
        bar.AnchorTop = 1;
        bar.AnchorBottom = 1;
        bar.GrowVertical = GrowDirection.Begin;
        bar.OffsetTop = -64;
        bar.OffsetBottom = -8;
        bar.OffsetLeft = -620;
        bar.OffsetRight = -12;
        bar.AnchorLeft = 1;
        bar.AnchorRight = 1;
        AddChild(_replayBar);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 6);
        _replayBar.AddChild(hbox);

        AddReplayButton(hbox, "⏮", "replay_seek_start");
        AddReplayButton(hbox, "◀", "replay_step_back");
        _playButton = AddReplayButton(hbox, "▶", "replay_toggle");
        AddReplayButton(hbox, "▶", "replay_step_fwd");
        AddReplayButton(hbox, "⏭", "replay_seek_end");

        _replayTick = MakeLabel(new Vector2(), new Vector2(220, 20), 13);
        _replayTick.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        hbox.AddChild(_replayTick);

        _timeline = new HSlider
        {
            CustomMinimumSize = new Vector2(320, 18),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        hbox.AddChild(_timeline);
        _replayBar.Visible = false;
    }

    private Button AddReplayButton(HBoxContainer parent, string text, string action)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(34, 34),
            FocusMode = FocusModeEnum.None,
        };
        button.Pressed += () => Input.ParseInputEvent(NewActionEvent(action));
        parent.AddChild(button);
        return button;
    }

    private static InputEventAction NewActionEvent(string action)
    {
        var evt = new InputEventAction { Action = action, Pressed = true };
        return evt;
    }

    private static Label MakeLabel(Vector2 offset, Vector2 size, int fontSize)
    {
        var label = new Label
        {
            Position = new Vector2(0, 0),
            Size = size,
            VerticalAlignment = VerticalAlignment.Top,
            ClipText = false,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.OffsetLeft = offset.X;
        label.OffsetTop = offset.Y;
        label.OffsetRight = offset.X + size.X;
        label.OffsetBottom = offset.Y + size.Y;
        return label;
    }

    /// <summary>Refreshes all HUD content from the latest render frame + shell state.</summary>
    public void UpdateFrame(RenderFrame frame, SessionMode mode, long replayTick, long replayTotal, bool replayPlaying, CameraMode camera)
    {
        var hud = frame.Hud;
        var phaseName = hud.Done ? "比赛结束"
            : hud.Paused ? "暂停"
            : hud.Phase == MatchPhase.Prep ? "发令准备"
            : hud.Phase == MatchPhase.Run ? "进行中" : "其他";
        var us = frame.Us;
        var them = frame.Them;

        _status!.Text =
            $"武术擂台 2026 · {(mode == SessionMode.Replay ? "回放" : "实况")} ({MatchEngine.CoreVersion})"
            + $"\n阶段 {phaseName} · 剩余 {hud.Timer,6:0.0} s"
            + $"\n比分: 我方 {hud.ScoreUs:0.#} : {hud.ScoreThem:0.#} 对手"
            + $"  (重启判罚 {hud.RestartPenaltyUs:0.#}/{hud.RestartPenaltyThem:0.#})"
            + (hud.Done ? $"\n结束: {hud.DoneReason}" : "")
            + $"\ntick {hud.Tick} · t={hud.T:0.0}s"
            + $"\n我方 {StateChip(us, "我")} {us.Action ?? ""}"
            + $"\n对手 {StateChip(them, "对")} {them.Action ?? ""}";

        _events!.Text = hud.RecentEvents.Count > 0
            ? string.Join("\n", hud.RecentEvents)
            : "(暂无事件)";

        _help!.Text =
            $"镜头 {CameraName(camera)}  (C 切换)"
            + "\nEnter 发令 · P 暂停/继续"
            + "\nR 我方重启 · T 对手重启 (+4)"
            + "\nF5 重置同 seed · L 打开回放"
            + (mode == SessionMode.Replay
                ? "\n空格 播放/暂停 · ←→ 单步"
                : "");

        if (_replayBar is not null)
        {
            _replayBar.Visible = mode == SessionMode.Replay;
        }
        if (_playButton is not null)
        {
            _playButton.Text = replayPlaying ? "⏸" : "▶";
        }
        if (_replayTick is not null)
        {
            _replayTick.Text = replayTotal > 0 ? $"tick {replayTick}/{replayTotal}" : "";
        }
        if (_timeline is not null && replayTotal > 1)
        {
            _timeline.MaxValue = replayTotal - 1;
            SyncSlider(replayTick - 1);
        }
    }

    /// <summary>Connects the timeline slider to a seek callback (main thread hook).</summary>
    public void ConfigureTimeline(Action<long> seek)
    {
        if (_timeline is null)
        {
            return;
        }
        _timeline.ValueChanged += value =>
        {
            if (_syncingSlider)
            {
                return;
            }
            seek((long)value + 1);
        };
    }

    private bool _syncingSlider;
    private long _sliderTick = -1;

    private void SyncSlider(long index)
    {
        if (_timeline is null || index == _sliderTick)
        {
            return;
        }
        _sliderTick = index;
        _syncingSlider = true;
        _timeline.Value = index;
        _syncingSlider = false;
    }

    private static string CameraName(CameraMode mode) => mode switch
    {
        CameraMode.Overview => "概览",
        CameraMode.Follow => "跟随",
        _ => "俯视",
    };

    private static string StateChip(RobotVisual robot, string side)
        => robot.State switch
        {
            null or "" => $"{side}方 未知",
            _ => $"{side}方 [{robot.State}]",
        };
}