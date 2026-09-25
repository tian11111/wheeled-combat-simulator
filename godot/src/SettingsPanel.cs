// Modal desktop settings UI. It edits a draft only; Main owns when the draft
// becomes a new simulation session and keeps replay/core state authoritative.

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class SettingsPanel : Control
{
    private static readonly Color Glass = new(0.055f, 0.085f, 0.14f, 0.82f);
    private static readonly Color GlassRaised = new(0.10f, 0.14f, 0.21f, 0.88f);
    private static readonly Color Backdrop = new(0.005f, 0.012f, 0.028f, 0.72f);
    private static readonly Color Border = new(0.30f, 0.52f, 0.82f, 0.82f);
    private static readonly Color Blue = new(0.35f, 0.67f, 1.0f);
    private static readonly Color Green = new(0.30f, 0.90f, 0.70f);
    private static readonly Color Yellow = new(1.0f, 0.76f, 0.25f);
    private static readonly Color Red = new(1.0f, 0.36f, 0.35f);
    private static readonly Color Primary = new(0.92f, 0.96f, 1.0f);
    private static readonly Color Secondary = new(0.62f, 0.70f, 0.82f);

    private readonly Dictionary<string, SpinBox> _parameterInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckButton> _automaticInputs = new(StringComparer.Ordinal);

    private PanelContainer? _dialog;
    private Label? _error;
    private Label? _pendingNote;
    private Button? _apply;
    private SpinBox? _width;
    private SpinBox? _height;
    private OptionButton? _windowMode;
    private SpinBox? _uiScale;
    private OptionButton? _usMode;
    private LineEdit? _usCommand;
    private SpinBox? _usTimeout;
    private Button? _usPreflight;
    private Label? _usPreflightResult;
    private OptionButton? _themMode;
    private LineEdit? _themCommand;
    private SpinBox? _themTimeout;
    private Button? _themPreflight;
    private Label? _themPreflightResult;
    private Button? _restore;
    private DesktopSettings _settings = DesktopSettings.Default;
    private int _preflightBusy;

    public event Action<DesktopSettings>? Applied;

    public event Action? Cancelled;

    /// <summary>Raised on the main thread when a role's preflight probe settles.</summary>
    public event Action<string, bool, string>? PreflightCompleted;

    public bool IsOpen => Visible;

    /// <summary>True while a preflight probe is in flight; Arm must wait for it.</summary>
    public bool PreflightInProgress => Volatile.Read(ref _preflightBusy) != 0;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
        Resized += UpdatePivot;
        Visible = false;
        SyncViewportRect();
        Build();
        UpdatePivot();
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (Visible)
        {
            // CanvasLayer is not a Control parent, so runtime-created children
            // do not receive automatic anchor sizing on every window resize.
            SyncViewportRect();
        }
    }

    /// <summary>Scales the modal around the viewport center with the HUD.</summary>
    public void SetUiScale(double scale)
    {
        var clamped = Mathf.Clamp((float)scale, 0.8f, 1.4f);
        Scale = new Vector2(clamped, clamped);
        UpdatePivot();
    }

    public void Open(DesktopSettings settings, bool pendingSimulationChanges)
    {
        SyncViewportRect();
        _settings = settings;
        LoadControls(settings);
        if (_pendingNote is not null)
        {
            _pendingNote.Text = pendingSimulationChanges
                ? "已有仿真/控制器修改待下一场生效 · F5 可立即重置并应用"
                : "显示设置立即生效 · 仿真与控制器设置在下一场或 F5 重置后生效";
        }
        ClearError();
        Visible = true;
        _apply?.GrabFocus();
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }
        if (key.Keycode == Key.Escape)
        {
            Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Build()
    {
        var backdrop = new ColorRect
        {
            Color = Backdrop,
            MouseFilter = MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        _dialog = new PanelContainer
        {
            CustomMinimumSize = new Vector2(980, 620),
            MouseFilter = MouseFilterEnum.Stop,
        };
        SetCenteredRect(_dialog, 980, 620);
        _dialog.AddThemeStyleboxOverride("panel", MakePanelStyle(Blue));
        AddChild(_dialog);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        _dialog.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        root.AddChild(header);
        var titleBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titleBox.AddThemeConstantOverride("separation", 2);
        header.AddChild(titleBox);
        AddLabel(titleBox, "SYSTEM SETTINGS  /  系统设置", 11, Blue);
        AddLabel(titleBox, "桌面控制台配置", 23, Primary);
        var close = MakeButton("×", Secondary, new Vector2(42, 36));
        close.TooltipText = "关闭 (Esc)";
        close.Pressed += Cancel;
        header.AddChild(close);

        var tabs = new TabContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 455),
            TabsVisible = true,
        };
        root.AddChild(tabs);
        tabs.AddChild(BuildDisplayPage());
        tabs.SetTabTitle(0, "显示与窗口");
        tabs.AddChild(BuildSimulationPage());
        tabs.SetTabTitle(1, "仿真参数");
        tabs.AddChild(BuildControllerPage());
        tabs.SetTabTitle(2, "小车控制器");

        _pendingNote = AddLabel(root,
            "显示设置立即生效 · 仿真与控制器设置在下一场或 F5 重置后生效",
            11, Yellow);
        _pendingNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        root.AddChild(footer);
        _error = AddLabel(footer, "", 11, Red);
        _error.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _error.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _error.CustomMinimumSize = new Vector2(0, 36);

        _restore = MakeButton("恢复默认", Secondary, new Vector2(108, 38));
        _restore.Pressed += RestoreDefaults;
        footer.AddChild(_restore);
        var cancel = MakeButton("取消", Secondary, new Vector2(88, 38));
        cancel.Pressed += Cancel;
        footer.AddChild(cancel);
        _apply = MakeButton("应用设置", Green, new Vector2(120, 38));
        _apply.FocusMode = FocusModeEnum.All;
        _apply.Pressed += ApplyDraft;
        footer.AddChild(_apply);
    }

    private Control BuildDisplayPage()
    {
        var page = MakePage();
        var root = page;
        AddLabel(root, "渲染窗口", 16, Primary);
        AddLabel(root, "沿用 1280×720 设计视口，窗口尺寸只改变显示比例，不改变仿真几何。", 11, Secondary);

        var grid = new GridContainer { Columns = 2, CustomMinimumSize = new Vector2(0, 150) };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        root.AddChild(grid);
        AddLabel(grid, "窗口宽度", 12, Secondary);
        _width = MakeSpin(640, 7680, 1, "px");
        grid.AddChild(_width);
        AddLabel(grid, "窗口高度", 12, Secondary);
        _height = MakeSpin(360, 4320, 1, "px");
        grid.AddChild(_height);
        AddLabel(grid, "窗口模式", 12, Secondary);
        _windowMode = MakeOption(("窗口化", DisplayModes.Windowed), ("全屏", DisplayModes.Fullscreen));
        grid.AddChild(_windowMode);
        AddLabel(grid, "界面缩放", 12, Secondary);
        _uiScale = MakeSpin(0.8, 1.4, 0.05, "x");
        grid.AddChild(_uiScale);

        var note = AddLabel(root,
            "提示：全屏下仍按屏幕比例缩放控制台；界面缩放只影响桌面 UI，不进入 Scenario、Snapshot 或回放指纹。",
            12, Blue);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return page;
    }

    private Control BuildSimulationPage()
    {
        var scroll = new ScrollContainer
        {
            Name = "SimulationParameters",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        root.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(root);
        AddLabel(root, "核心参数覆盖", 16, Primary);
        var intro = AddLabel(root,
            "只保存你明确修改的参数；“自动”表示沿用 Sim.Core 默认值。实验性参数用于标定和回放复现，请谨慎使用。",
            11, Secondary);
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddParameterGroup(root, "常用", "比赛判定、传感器与恢复相关", Blue);
        AddParameterGroup(root, "高级", "堵转、摩擦、碰撞与登台门控", Yellow);
        return scroll;
    }

    private Control BuildControllerPage()
    {
        var page = MakePage();
        var root = page;
        AddLabel(root, "外部小车控制器", 16, Primary);
        var warning = AddLabel(root,
            "使用外部命令/脚本通过既有 JSONL stdio 协议控制小车，不启动 Godot 内嵌代码编辑器。外部进程拥有本机权限，请只运行可信代码。",
            11, Yellow);
        warning.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(BuildControllerSection("我方 / BLUE", RoleNames.Us));
        root.AddChild(BuildControllerSection("对手 / RED", RoleNames.Them));
        var note = AddLabel(root,
            "协议：每行输入 observation JSON，输出 {\"v\":...,\"w\":...,\"requestId\":...}；超时或坏行会安全回退为零动作并显示 fault。",
            11, Secondary);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return page;
    }

    private Control BuildControllerSection(string title, string role)
    {
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 120),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        panel.AddThemeStyleboxOverride("panel", MakePanelStyle(role == RoleNames.Us ? Blue : Red));
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        panel.AddChild(margin);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 7);
        margin.AddChild(root);
        AddLabel(root, title, 13, role == RoleNames.Us ? Blue : Red);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        root.AddChild(row);
        AddLabel(row, "来源", 11, Secondary, new Vector2(42, 0));
        var mode = MakeOption(("内置 FSM", ControllerModes.BuiltIn), ("外部命令", ControllerModes.External));
        row.AddChild(mode);
        AddLabel(row, "超时", 11, Secondary, new Vector2(36, 0));
        var timeout = MakeSpin(1, 5000, 1, "ms");
        timeout.CustomMinimumSize = new Vector2(120, 32);
        row.AddChild(timeout);

        var command = new LineEdit
        {
            PlaceholderText = "例如：python my_controller.py",
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "外部控制器启动命令；留空时使用内置 FSM",
        };
        ApplyLineEditTheme(command);
        root.AddChild(command);

        // 发令前预检: 用正式比赛相同的桥语义做一次启动+握手+回收,
        // 让"命令不可执行/不应答"在开赛前暴露, 而不是赛后 fault。
        var preflightRow = new HBoxContainer();
        preflightRow.AddThemeConstantOverride("separation", 8);
        root.AddChild(preflightRow);
        var preflight = MakeButton("预检", Blue, new Vector2(72, 30));
        preflight.TooltipText = "启动一次外部控制器并等待单帧应答, 校验命令与协议; 预检进程随即回收";
        preflightRow.AddChild(preflight);
        var preflightResult = new Label
        {
            Text = "",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 26),
        };
        preflightResult.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        preflightRow.AddChild(preflightResult);
        preflight.Pressed += () => RequestPreflight(role, mode, command, timeout);

        if (role == RoleNames.Us)
        {
            _usMode = mode;
            _usCommand = command;
            _usTimeout = timeout;
            _usPreflight = preflight;
            _usPreflightResult = preflightResult;
        }
        else
        {
            _themMode = mode;
            _themCommand = command;
            _themTimeout = timeout;
            _themPreflight = preflight;
            _themPreflightResult = preflightResult;
        }
        return panel;
    }

    private void RequestPreflight(string role, OptionButton mode, LineEdit command, SpinBox timeout)
    {
        if (Interlocked.CompareExchange(ref _preflightBusy, 1, 0) != 0)
        {
            return;
        }
        var profile = new ControllerProfile
        {
            Mode = mode.Selected == 1 ? ControllerModes.External : ControllerModes.BuiltIn,
            Command = command.Text.Trim(),
            TimeoutMs = timeout.Value,
        };
        SetPreflightBusy(true);
        var result = role == RoleNames.Us ? _usPreflightResult : _themPreflightResult;
        if (result is not null)
        {
            result.Text = "预检中…";
            result.AddThemeColorOverride("font_color", Yellow);
        }
        Task.Run(() =>
        {
            var outcome = ControllerPreflight.Run(profile);
            CallDeferred(nameof(FinishPreflight), role, outcome.Ok, outcome.Message);
        });
    }

    private void FinishPreflight(string role, bool ok, string message)
    {
        Volatile.Write(ref _preflightBusy, 0);
        SetPreflightBusy(false);
        var result = role == RoleNames.Us ? _usPreflightResult : _themPreflightResult;
        if (result is not null)
        {
            result.Text = $"{(ok ? "✓ " : "✗ ")}{message}";
            result.AddThemeColorOverride("font_color", ok ? Green : Red);
        }
        PreflightCompleted?.Invoke(role, ok, message);
    }

    private void SetPreflightBusy(bool busy)
    {
        if (_usPreflight is not null) _usPreflight.Disabled = busy;
        if (_themPreflight is not null) _themPreflight.Disabled = busy;
        if (_apply is not null) _apply.Disabled = busy;
        if (_restore is not null) _restore.Disabled = busy;
    }

    private void AddParameterGroup(VBoxContainer parent, string group, string description, Color accent)
    {
        var heading = new HBoxContainer();
        heading.AddThemeConstantOverride("separation", 10);
        parent.AddChild(heading);
        AddLabel(heading, group, 14, accent, new Vector2(70, 0));
        AddLabel(heading, description, 11, Secondary);

        foreach (var definition in SimulationParameterCatalog.ForGroup(group))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            parent.AddChild(row);
            var label = AddLabel(row, definition.Label, 12, Primary, new Vector2(188, 32));
            label.TooltipText = definition.Key;
            var input = MakeSpin(definition.Minimum, definition.Maximum, definition.Step, definition.Unit);
            input.CustomMinimumSize = new Vector2(160, 32);
            input.AllowLesser = true;
            input.AllowGreater = true;
            row.AddChild(input);
            AddLabel(row, definition.Unit, 11, Secondary, new Vector2(72, 32));
            if (definition.Experimental)
            {
                AddLabel(row, "实验性", 10, Yellow, new Vector2(46, 32));
            }
            if (definition.AllowAutomatic)
            {
                var automatic = new CheckButton
                {
                    Text = "自动",
                    CustomMinimumSize = new Vector2(70, 32),
                    FocusMode = FocusModeEnum.None,
                };
                ApplyCheckButtonTheme(automatic);
                automatic.Toggled += pressed => input.Editable = !pressed;
                row.AddChild(automatic);
                _automaticInputs[definition.Key] = automatic;
            }
            else
            {
                AddLabel(row, "默认", 10, Secondary, new Vector2(46, 32));
            }
            _parameterInputs[definition.Key] = input;
        }
    }

    private void LoadControls(DesktopSettings settings)
    {
        if (_width is not null)
        {
            _width.Value = settings.Window?.Width ?? 1280;
        }
        if (_height is not null)
        {
            _height.Value = settings.Window?.Height ?? 720;
        }
        if (_windowMode is not null)
        {
            _windowMode.Select(settings.Window?.Mode == DisplayModes.Fullscreen ? 1 : 0);
        }
        if (_uiScale is not null)
        {
            _uiScale.Value = settings.UiScale;
        }

        var values = settings.SimulationParameters ?? new Dictionary<string, double>();
        foreach (var definition in SimulationParameterCatalog.All)
        {
            if (!_parameterInputs.TryGetValue(definition.Key, out var input))
            {
                continue;
            }
            input.Value = values.TryGetValue(definition.Key, out var value) ? value : definition.DefaultValue;
            if (_automaticInputs.TryGetValue(definition.Key, out var automatic))
            {
                automatic.ButtonPressed = !values.ContainsKey(definition.Key);
                input.Editable = !automatic.ButtonPressed;
            }
        }
        LoadController(settings.UsController, _usMode, _usCommand, _usTimeout);
        LoadController(settings.ThemController, _themMode, _themCommand, _themTimeout);
    }

    private static void LoadController(ControllerProfile? profile, OptionButton? mode,
        LineEdit? command, SpinBox? timeout)
    {
        profile ??= new ControllerProfile();
        mode?.Select(profile.Mode == ControllerModes.External ? 1 : 0);
        if (command is not null)
        {
            command.Text = profile.Command;
        }
        if (timeout is not null)
        {
            timeout.Value = profile.TimeoutMs;
        }
    }

    private void ApplyDraft()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var definition in SimulationParameterCatalog.All)
        {
            if (!_parameterInputs.TryGetValue(definition.Key, out var input))
            {
                continue;
            }
            if (_automaticInputs.TryGetValue(definition.Key, out var automatic) && automatic.ButtonPressed)
            {
                continue;
            }
            var value = definition.Integer ? Math.Round(input.Value) : input.Value;
            if (Math.Abs(value - definition.DefaultValue) > 1e-9 || !definition.AllowAutomatic)
            {
                values[definition.Key] = value;
            }
        }

        var draft = _settings with
        {
            Window = new WindowSettings
            {
                Width = (int)Math.Round(_width?.Value ?? 1280),
                Height = (int)Math.Round(_height?.Value ?? 720),
                Mode = _windowMode?.Selected == 1 ? DisplayModes.Fullscreen : DisplayModes.Windowed,
            },
            UiScale = _uiScale?.Value ?? 1.0,
            SimulationParameters = values,
            UsController = ReadController(_usMode, _usCommand, _usTimeout),
            ThemController = ReadController(_themMode, _themCommand, _themTimeout),
        };
        var errors = draft.Validate().ToArray();
        if (errors.Length > 0)
        {
            ShowError(string.Join("\n", errors));
            return;
        }
        _settings = draft;
        Visible = false;
        Applied?.Invoke(draft);
    }

    private static ControllerProfile ReadController(OptionButton? mode, LineEdit? command, SpinBox? timeout)
        => new()
        {
            Mode = mode?.Selected == 1 ? ControllerModes.External : ControllerModes.BuiltIn,
            Command = command?.Text.Trim() ?? "",
            TimeoutMs = timeout?.Value ?? 100,
        };

    private void RestoreDefaults()
    {
        _settings = DesktopSettings.Default;
        LoadControls(_settings);
        ClearError();
    }

    private void Cancel()
    {
        Visible = false;
        ClearError();
        Cancelled?.Invoke();
    }

    private void ShowError(string message)
    {
        if (_error is null)
        {
            return;
        }
        _error.Text = message;
        _error.AddThemeColorOverride("font_color", Red);
    }

    private void ClearError()
    {
        if (_error is not null)
        {
            _error.Text = "";
        }
    }

    private static VBoxContainer MakePage()
    {
        var page = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 420),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        page.AddThemeConstantOverride("separation", 10);
        return page;
    }

    private static SpinBox MakeSpin(double min, double max, double step, string suffix)
    {
        var spin = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = min,
            AllowLesser = false,
            AllowGreater = false,
            CustomMinimumSize = new Vector2(180, 34),
            FocusMode = FocusModeEnum.All,
            Suffix = suffix,
        };
        ApplySpinTheme(spin);
        return spin;
    }

    private static OptionButton MakeOption(params (string Label, string Id)[] items)
    {
        var option = new OptionButton
        {
            CustomMinimumSize = new Vector2(150, 34),
            FocusMode = FocusModeEnum.All,
        };
        foreach (var item in items)
        {
            option.AddItem(item.Label);
        }
        ApplyOptionTheme(option);
        return option;
    }

    private static Button MakeButton(string text, Color accent, Vector2 minimumSize)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = minimumSize,
            FocusMode = FocusModeEnum.None,
        };
        ApplyButtonTheme(button, accent);
        return button;
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

    private static void SetCenteredRect(Control control, float width, float height)
    {
        control.AnchorLeft = 0.5f;
        control.AnchorTop = 0.5f;
        control.AnchorRight = 0.5f;
        control.AnchorBottom = 0.5f;
        control.OffsetLeft = -width / 2;
        control.OffsetTop = -height / 2;
        control.OffsetRight = width / 2;
        control.OffsetBottom = height / 2;
    }

    private void UpdatePivot()
    {
        PivotOffset = Size / 2.0f;
    }

    private void SyncViewportRect()
    {
        if (!IsInsideTree())
        {
            return;
        }
        var viewportSize = GetViewport().GetVisibleRect().Size;
        if (Size != viewportSize || Position != Vector2.Zero
            || AnchorLeft != 0 || AnchorTop != 0 || AnchorRight != 0 || AnchorBottom != 0)
        {
            // Reset anchors before assigning an explicit rect; otherwise Godot
            // warns that unequal anchors will overwrite Size after _ready().
            AnchorLeft = 0;
            AnchorTop = 0;
            AnchorRight = 0;
            AnchorBottom = 0;
            Position = Vector2.Zero;
            Size = viewportSize;
            UpdatePivot();
        }
    }

    private static StyleBoxFlat MakePanelStyle(Color accent)
    {
        var style = new StyleBoxFlat
        {
            BgColor = Glass,
            BorderColor = new Color(accent, 0.82f),
            ShadowColor = new Color(0, 0, 0, 0.42f),
            ShadowSize = 14,
            ShadowOffset = new Vector2(0, 5),
            CornerRadiusTopLeft = 14,
            CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14,
            CornerRadiusBottomRight = 14,
            ContentMarginLeft = 12,
            ContentMarginTop = 10,
            ContentMarginRight = 12,
            ContentMarginBottom = 10,
        };
        style.SetBorderWidthAll(1);
        return style;
    }

    private static StyleBoxFlat MakeInputStyle(Color background, Color border)
    {
        var style = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = new Color(border, 0.80f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginTop = 5,
            ContentMarginRight = 8,
            ContentMarginBottom = 5,
        };
        style.SetBorderWidthAll(1);
        return style;
    }

    private static void ApplyButtonTheme(Button button, Color accent)
    {
        button.AddThemeColorOverride("font_color", Primary);
        button.AddThemeColorOverride("font_hover_color", accent);
        button.AddThemeColorOverride("font_pressed_color", accent);
        button.AddThemeStyleboxOverride("normal", MakeInputStyle(GlassRaised, Border));
        button.AddThemeStyleboxOverride("hover", MakeInputStyle(new Color(0.15f, 0.22f, 0.32f, 0.94f), accent));
        button.AddThemeStyleboxOverride("pressed", MakeInputStyle(new Color(0.18f, 0.26f, 0.36f, 0.98f), accent));
    }

    private static void ApplySpinTheme(SpinBox spin)
    {
        spin.AddThemeColorOverride("font_color", Primary);
        spin.AddThemeColorOverride("font_uneditable_color", Secondary);
        spin.AddThemeStyleboxOverride("normal", MakeInputStyle(GlassRaised, Border));
        spin.AddThemeStyleboxOverride("read_only", MakeInputStyle(new Color(0.05f, 0.07f, 0.11f, 0.7f), Border));
    }

    private static void ApplyOptionTheme(OptionButton option)
    {
        option.AddThemeColorOverride("font_color", Primary);
        ApplyButtonTheme(option, Blue);
    }

    private static void ApplyLineEditTheme(LineEdit line)
    {
        line.AddThemeColorOverride("font_color", Primary);
        line.AddThemeColorOverride("font_placeholder_color", new Color(Secondary, 0.75f));
        line.AddThemeStyleboxOverride("normal", MakeInputStyle(GlassRaised, Border));
        line.AddThemeStyleboxOverride("focus", MakeInputStyle(new Color(0.12f, 0.19f, 0.28f, 0.96f), Blue));
    }

    private static void ApplyCheckButtonTheme(CheckButton check)
    {
        check.AddThemeColorOverride("font_color", Primary);
        check.AddThemeColorOverride("font_hover_color", Green);
    }
}
