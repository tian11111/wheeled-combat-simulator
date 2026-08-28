// HUD 面板: 深色赛事控制台。所有比赛/编辑/回放状态仍来自 Main 传入的现有投影，
// 本文件只负责控件层级、主题和动态文本，不计算规则、不改变快捷键或回调顺序。

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class HudPanel : Control
{
    private static readonly Color CardColor = new(0.055f, 0.075f, 0.105f, 0.94f);
    private static readonly Color CardColorRaised = new(0.075f, 0.095f, 0.13f, 0.97f);
    private static readonly Color CardBorder = new(0.20f, 0.25f, 0.34f, 0.9f);
    private static readonly Color TextPrimary = new(0.91f, 0.94f, 0.98f);
    private static readonly Color TextSecondary = new(0.57f, 0.64f, 0.74f);
    private static readonly Color AccentYellow = new(0.98f, 0.72f, 0.22f);
    private static readonly Color AccentBlue = new(0.31f, 0.58f, 1.0f);
    private static readonly Color AccentRed = new(1.0f, 0.35f, 0.32f);
    private static readonly Color AccentGreen = new(0.30f, 0.85f, 0.68f);

    private PanelContainer? _status;
    private Label? _statusMode;
    private Label? _statusPhase;
    private Label? _statusTimer;
    private Label? _statusScore;
    private Label? _statusPenalty;
    private Label? _statusEnd;
    private Label? _usStatus;
    private Label? _themStatus;

    private PanelContainer? _events;
    private Label? _eventsBody;
    private PanelContainer? _help;
    private Label? _helpBody;

    private PanelContainer? _replayBar;
    private Label? _replayTick;
    private HSlider? _timeline;
    private Button? _playButton;

    private PanelContainer? _editorBar;
    private Label? _editorInfo;
    private Label? _editorStatus;
    private Button? _editorApply;
    private bool _editorActive;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        BuildStatusCard();
        BuildEventCard();
        BuildHelpCard();
        BuildReplayBar();
        BuildEditorBar();
    }

    private void BuildStatusCard()
    {
        _status = MakeCard(new Vector2(380, 224), AccentBlue);
        SetAnchoredRect(_status, 0, 0, 0, 0, 16, 16, 396, 240);
        AddChild(_status);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        _status.AddChild(vbox);

        _statusMode = AddLabel(vbox, "WUSHU RING  /  MATCH CONTROL", 11, AccentBlue);
        _statusPhase = AddLabel(vbox, "准备", 18, TextPrimary);

        var clockRow = new HBoxContainer();
        clockRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(clockRow);
        AddLabel(clockRow, "剩余时间", 11, TextSecondary, new Vector2(74, 0));
        _statusTimer = AddLabel(clockRow, "--.- s", 16, AccentYellow);

        var scoreRow = new HBoxContainer();
        scoreRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(scoreRow);
        AddLabel(scoreRow, "比分", 11, TextSecondary, new Vector2(74, 0));
        _statusScore = AddLabel(scoreRow, "0 : 0", 20, TextPrimary);
        _statusScore.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        _statusScore.AddThemeConstantOverride("shadow_offset_x", 1);
        _statusScore.AddThemeConstantOverride("shadow_offset_y", 1);

        _statusPenalty = AddLabel(vbox, "重启判罚  我方 0 / 对手 0", 11, TextSecondary);

        var teamRow = new HBoxContainer();
        teamRow.AddThemeConstantOverride("separation", 6);
        teamRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(teamRow);
        _usStatus = AddTeamCard(teamRow, "我方  /  BLUE", AccentBlue);
        _themStatus = AddTeamCard(teamRow, "对手  /  RED", AccentRed);

        _statusEnd = AddLabel(vbox, "裁判台在线 · 等待指令", 11, AccentGreen);
        _statusEnd.ClipText = true;
    }

    private void BuildEventCard()
    {
        _events = MakeCard(new Vector2(456, 154), AccentYellow);
        PositionEventCard(replayVisible: false);
        AddChild(_events);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        _events.AddChild(vbox);
        AddLabel(vbox, "LIVE EVENT FEED  /  最近事件", 11, AccentYellow);
        _eventsBody = AddLabel(vbox, "暂无事件", 12, TextPrimary);
        _eventsBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _eventsBody.ClipText = true;
        _eventsBody.SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    private void BuildHelpCard()
    {
        _help = MakeCard(new Vector2(306, 194), CardBorder);
        SetAnchoredRect(_help, 1, 0, 1, 0, -322, 16, -16, 222);
        AddChild(_help);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 5);
        _help.AddChild(vbox);
        AddLabel(vbox, "OPERATOR PANEL  /  操作台", 11, AccentBlue);
        _helpBody = AddLabel(vbox, "镜头 概览", 12, TextPrimary);
        _helpBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _helpBody.ClipText = true;
        _helpBody.SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    /// <summary>顶栏: 布局编辑模式的选择/数值检视 + 操作按钮 (Main 在编辑器激活时刷新)。</summary>
    private void BuildEditorBar()
    {
        _editorBar = MakeCard(new Vector2(620, 152), AccentYellow);
        SetAnchoredRect(_editorBar, 0, 0, 1, 0, 12, 12, -12, 176);
        AddChild(_editorBar);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 5);
        _editorBar.AddChild(vbox);

        _editorInfo = AddLabel(vbox, "LAYOUT EDITOR  /  未选择对象", 14, TextPrimary);
        _editorInfo.ClipText = true;

        _editorStatus = AddLabel(vbox, "拖动选择对象 · [ ] 旋转场地 · ←→↑↓ 微调", 12, AccentYellow);
        _editorStatus.ClipText = true;

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 5);
        hbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(hbox);
        _editorApply = AddEditorButton(hbox, "应用布局\n(Enter)", AccentGreen);
        AddEditorButton(hbox, "撤销\nCtrl+Z", TextPrimary);
        AddEditorButton(hbox, "重做\nCtrl+Y", TextPrimary);
        AddEditorButton(hbox, "恢复官方", TextPrimary);
        AddEditorButton(hbox, "打开场景", TextPrimary);
        AddEditorButton(hbox, "另存为", TextPrimary);
        AddEditorButton(hbox, "退出编辑\n(E)", AccentRed);
        _editorBar.Visible = false;
    }

    private static Button AddEditorButton(HBoxContainer parent, string text, Color accent)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(78, 42),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            TooltipText = text.Replace("\n", " "),
        };
        ApplyButtonTheme(button, accent);
        parent.AddChild(button);
        return button;
    }

    /// <summary>Wires editor-bar buttons to shell callbacks (called once by Main after Bind).</summary>
    public void ConfigureEditor(Action onApply, Action onUndo, Action onRedo, Action onRestore,
        Action onOpen, Action onSave, Action onClose)
    {
        var buttons = new List<Button>();
        CollectButtons(_editorBar!, buttons);
        // 顺序与 BuildEditorBar 添加顺序一致: 应用/撤销/重做/恢复/打开/另存/退出。
        var handlers = new Action[] { onApply, onUndo, onRedo, onRestore, onOpen, onSave, onClose };
        for (var i = 0; i < buttons.Count && i < handlers.Length; i++)
        {
            var handler = handlers[i];
            buttons[i].Pressed += () => handler();
        }
    }

    private static void CollectButtons(Node parent, List<Button> result)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Button button)
            {
                result.Add(button);
            }
            else
            {
                CollectButtons(child, result);
            }
        }
    }

    /// <summary>Refreshes editor-bar content; hidden when not editing.</summary>
    public void UpdateEditor(bool active, string selected, string inspector, string status, bool canApply)
    {
        _editorActive = active;
        if (_editorBar is null)
        {
            return;
        }

        _editorBar.Visible = active;
        _status!.Visible = !active;
        _events!.Visible = !active;
        _help!.Visible = !active;
        if (!active)
        {
            return;
        }

        if (_editorInfo is not null)
        {
            _editorInfo.Text = $"LAYOUT EDITOR  /  选中: {selected} · {inspector}";
        }
        if (_editorStatus is not null)
        {
            _editorStatus.Text = string.IsNullOrEmpty(status)
                ? "拖动选择对象 · [ ] 旋转场地 · S 吸附 · ←→↑↓ 微调"
                : status;
            _editorStatus.AddThemeColorOverride("font_color", canApply ? AccentGreen : AccentYellow);
        }
        if (_editorApply is not null)
        {
            _editorApply.Disabled = !canApply;
            _editorApply.Text = canApply ? "应用布局\n(Enter)" : "应用布局\n(不可用)";
        }
    }

    private void BuildReplayBar()
    {
        _replayBar = MakeCard(new Vector2(0, 66), AccentBlue);
        SetAnchoredRect(_replayBar, 0, 1, 1, 1, 12, -76, -12, -10);
        AddChild(_replayBar);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 6);
        hbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _replayBar.AddChild(hbox);

        var meta = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(108, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        meta.AddThemeConstantOverride("separation", 1);
        hbox.AddChild(meta);
        AddLabel(meta, "REPLAY CONTROL  /  回放", 10, AccentBlue);
        _replayTick = AddLabel(meta, "tick -- / --", 12, TextPrimary);

        AddReplayButton(hbox, "⏮", "replay_seek_start", "首帧 (Home)");
        AddReplayButton(hbox, "◀", "replay_step_back", "上一步 (←)");
        _playButton = AddReplayButton(hbox, "▶", "replay_toggle", "播放/暂停 (Space)");
        AddReplayButton(hbox, "▶", "replay_step_fwd", "下一步 (→)");
        AddReplayButton(hbox, "⏭", "replay_seek_end", "末帧 (End)");

        _timeline = new HSlider
        {
            CustomMinimumSize = new Vector2(220, 22),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "拖动时间轴跳转",
        };
        _timeline.AddThemeColorOverride("font_color", AccentBlue);
        hbox.AddChild(_timeline);
        _replayBar.Visible = false;
    }

    private Button AddReplayButton(HBoxContainer parent, string text, string action, string tooltip)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(42, 38),
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip,
        };
        ApplyButtonTheme(button, AccentBlue);
        button.Pressed += () => Input.ParseInputEvent(NewActionEvent(action));
        parent.AddChild(button);
        return button;
    }

    private static InputEventAction NewActionEvent(string action)
    {
        var evt = new InputEventAction { Action = action, Pressed = true };
        return evt;
    }

    private static PanelContainer MakeCard(Vector2 minimumSize, Color accent)
    {
        var panel = new PanelContainer
        {
            CustomMinimumSize = minimumSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", MakeCardStyle(accent));
        return panel;
    }

    private static StyleBoxFlat MakeCardStyle(Color accent)
    {
        var style = new StyleBoxFlat
        {
            BgColor = CardColor,
            BorderColor = new Color(accent, 0.82f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12,
            ContentMarginTop = 10,
            ContentMarginRight = 12,
            ContentMarginBottom = 10,
        };
        style.SetBorderWidthAll(1);
        return style;
    }

    private static Label AddLabel(Container parent, string text, int fontSize, Color color,
        Vector2? minimumSize = null)
    {
        var label = new Label
        {
            Text = text,
            CustomMinimumSize = minimumSize ?? Vector2.Zero,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipText = true,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label);
        return label;
    }

    private static Label AddTeamCard(HBoxContainer parent, string title, Color accent)
    {
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 48),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", MakeTeamStyle(accent));
        parent.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 1);
        panel.AddChild(vbox);
        AddLabel(vbox, title, 10, accent);
        return AddLabel(vbox, "未知", 12, TextPrimary);
    }

    private static StyleBoxFlat MakeTeamStyle(Color accent)
    {
        var style = MakeCardStyle(accent);
        style.BgColor = CardColorRaised;
        style.ContentMarginLeft = 8;
        style.ContentMarginTop = 6;
        style.ContentMarginRight = 8;
        style.ContentMarginBottom = 6;
        return style;
    }

    private static void ApplyButtonTheme(Button button, Color accent)
    {
        button.AddThemeColorOverride("font_color", TextPrimary);
        button.AddThemeColorOverride("font_hover_color", accent);
        button.AddThemeColorOverride("font_pressed_color", accent);
        button.AddThemeColorOverride("font_disabled_color", new Color(TextSecondary, 0.55f));
        button.AddThemeStyleboxOverride("normal", MakeButtonStyle(CardColorRaised, CardBorder));
        button.AddThemeStyleboxOverride("hover", MakeButtonStyle(new Color(0.11f, 0.14f, 0.19f, 1), accent));
        button.AddThemeStyleboxOverride("pressed", MakeButtonStyle(new Color(0.13f, 0.16f, 0.22f, 1), accent));
        button.AddThemeStyleboxOverride("disabled", MakeButtonStyle(new Color(0.045f, 0.055f, 0.075f, 0.9f), CardBorder));
    }

    private static StyleBoxFlat MakeButtonStyle(Color background, Color border)
    {
        var style = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = new Color(border, 0.8f),
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 6,
            ContentMarginTop = 4,
            ContentMarginRight = 6,
            ContentMarginBottom = 4,
        };
        style.SetBorderWidthAll(1);
        return style;
    }

    private static void SetAnchoredRect(Control control, float anchorLeft, float anchorTop,
        float anchorRight, float anchorBottom, float offsetLeft, float offsetTop,
        float offsetRight, float offsetBottom)
    {
        control.AnchorLeft = anchorLeft;
        control.AnchorTop = anchorTop;
        control.AnchorRight = anchorRight;
        control.AnchorBottom = anchorBottom;
        control.OffsetLeft = offsetLeft;
        control.OffsetTop = offsetTop;
        control.OffsetRight = offsetRight;
        control.OffsetBottom = offsetBottom;
    }

    private void PositionEventCard(bool replayVisible)
    {
        if (_events is null)
        {
            return;
        }

        // The card has a 154px minimum height. Lift its top edge when the
        // replay bar is present so the two bottom-anchored panels never overlap.
        var topOffset = replayVisible ? -246 : -178;
        SetAnchoredRect(_events, 0, 1, 0, 1, 16, topOffset, 472, -86);
    }

    /// <summary>Refreshes all HUD content from the latest render frame + shell state.</summary>
    public void UpdateFrame(RenderFrame frame, SessionMode mode, long replayTick, long replayTotal, bool replayPlaying, CameraMode camera)
    {
        var hud = frame.Hud;
        var phaseName = hud.Done ? "比赛结束"
            : hud.Paused ? "暂停"
            : hud.Phase == MatchPhase.Prep ? "发令准备"
            : hud.Phase == MatchPhase.Run ? "进行中" : "其他";
        var phaseColor = hud.Done ? AccentRed : hud.Paused ? AccentYellow : AccentGreen;
        var us = frame.Us;
        var them = frame.Them;

        _statusMode!.Text = $"WUSHU RING  /  {(mode == SessionMode.Replay ? "REPLAY" : "LIVE")}  ·  {MatchEngine.CoreVersion}";
        _statusPhase!.Text = phaseName;
        _statusPhase.AddThemeColorOverride("font_color", phaseColor);
        _statusTimer!.Text = $"{hud.Timer:0.0} s";
        _statusScore!.Text = $"{hud.ScoreUs:0.#}  :  {hud.ScoreThem:0.#}";
        _statusPenalty!.Text = $"重启判罚  我方 {hud.RestartPenaltyUs:0.#}  /  对手 {hud.RestartPenaltyThem:0.#}";
        _statusEnd!.Text = hud.Done
            ? $"终局 · {hud.DoneReason}"
            : hud.Paused ? "比赛暂停 · 等待裁判指令" : "裁判台在线 · 状态同步中";
        _statusEnd.AddThemeColorOverride("font_color", hud.Done ? AccentRed : phaseColor);

        _usStatus!.Text = $"{StateChip(us, "我")}\n{us.Action ?? "待命"}";
        _themStatus!.Text = $"{StateChip(them, "对")}\n{them.Action ?? "待命"}";

        _eventsBody!.Text = hud.RecentEvents.Count > 0
            ? string.Join("\n", hud.RecentEvents)
            : "(暂无事件)";

        _helpBody!.Text =
            $"镜头  {CameraName(camera)}  (C 切换)"
            + (_editorActive
                ? "\n编辑模式: 拖动选择/移动对象"
                : mode == SessionMode.Replay
                    ? ""
                    : "\nEnter 发令 · P 暂停/继续")
            + (_editorActive ? "" : "\nR 我方重启 · T 对手重启 (+4)")
            + (_editorActive ? "\nE 退出编辑" : "\nF5 重置同 seed · L 打开回放")
            + (mode == SessionMode.Replay && !_editorActive
                ? "\n空格 播放/暂停 · ←→ 单步 · Home/End 首尾"
                : "");

        if (_replayBar is not null)
        {
            var replayVisible = mode == SessionMode.Replay && !_editorActive;
            _replayBar.Visible = replayVisible;
            PositionEventCard(replayVisible);
        }
        if (_playButton is not null)
        {
            _playButton.Text = replayPlaying ? "⏸" : "▶";
        }
        if (_replayTick is not null)
        {
            _replayTick.Text = replayTotal > 0 ? $"tick {replayTick}/{replayTotal}" : "tick -- / --";
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
