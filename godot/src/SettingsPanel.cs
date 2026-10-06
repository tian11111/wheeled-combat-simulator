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
    private TabContainer? _tabs;
    private Label? _error;
    private Label? _pendingNote;
    private Button? _apply;
    private SpinBox? _width;
    private SpinBox? _height;
    private OptionButton? _windowMode;
    private SpinBox? _uiScale;
    private SpinBox? _vehicleMass;
    private SpinBox? _vehicleRpm;
    private SpinBox? _vehicleTorque;
    private SpinBox? _vehicleWheelRadius;
    private Label? _vehicleNote;
    private OptionButton? _visionSource;
    private LineEdit? _visionEvidencePath;
    private LineEdit? _visionCsvPath;
    private LineEdit? _visionProcessCommand;
    private SpinBox? _visionMaxAge;
    private Label? _visionNote;
    private OptionButton? _sensorProfile;
    private GridContainer? _sensorChannelGrid;
    private CheckButton? _blockCustom;
    private SpinBox? _blockBuffCount;
    private SpinBox? _blockDebuffCount;
    private OptionButton? _blockPlacement;
    private Label? _blockNote;
    private readonly List<(string ChannelId, CheckButton Enabled, SpinBox Dx, SpinBox Dy, SpinBox Dz, SpinBox Yaw)> _sensorChannelRows = new();
    private Label? _sensorBaseNote;
    private readonly Dictionary<string, LineEdit> _modelPathInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpinBox[]> _modelTransformInputs = new(StringComparer.Ordinal);
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
    private OptionButton? _matchBackend;
    private LineEdit? _matchScenarioPath;
    private CheckButton? _matchDurationOverride;
    private SpinBox? _matchDuration;
    private CheckButton? _matchSeedOverride;
    private SpinBox? _matchSeed;
    private Button? _matchRestartSeed;
    private Label? _matchNote;
    private Button? _devToggle;
    private Control? _devSection;
    private CheckButton? _devL1;
    private CheckButton? _devL2;
    private CheckButton? _devL3;
    private FileDialog? _scenarioDialog;
    private FileDialog? _bundleSaveDialog;
    private FileDialog? _bundleOpenDialog;
    private FileDialog? _trainConfigDialog;
    private string? _pendingBundleExportPath;
    private DesktopSettings _settings = DesktopSettings.Default;
    private IReadOnlyDictionary<string, RobotModelConfig> _robotModels =
        new Dictionary<string, RobotModelConfig>();
    private int _preflightBusy;

    public event Action<DesktopSettings>? Applied;

    /// <summary>Raised on apply with the edited render-only appearance bindings; Main persists robot-models.json.</summary>
    public event Action<IReadOnlyDictionary<string, RobotModelConfig>>? RobotModelsApplied;

    /// <summary>导出配置包请求 (bundle 落点 + 可选训练配置文件路径; 内容收集/落盘在 Main)。</summary>
    public event Action<string, string?>? ExportBundleRequested;

    /// <summary>导入配置包请求 (bundle 路径; 版本校验/确认/应用在 Main)。</summary>
    public event Action<string>? ImportBundleRequested;

    /// <summary>
    /// "换种子重开"请求 (批2 R2.1): Main 把种子写进比赛覆盖并立即按 F5 语义重建会话
    /// (回放/布局编辑中挂待生效)。面板不关闭, 其他未应用的草稿编辑保持不动。
    /// </summary>
    public event Action<int>? RestartWithSeedRequested;

    public event Action? Cancelled;

    /// <summary>Raised on the main thread when a role's preflight probe settles.</summary>
    public event Action<string, bool, string>? PreflightCompleted;

    public bool IsOpen => Visible;

    // 标签页稳定键 (与 Build 里的注册顺序一一对应): --settings-tab 支持页名寻页,
    // 新增页只追加在下标末尾, 旧下标仍指向原页。
    private static readonly string[] TabKeys =
        ["display", "simulation", "controller", "vehicle", "vision", "blocks", "match"];

    /// <summary>
    /// QA/冒烟用: 无交互切换到指定页。支持稳定页名 (display/simulation/controller/
    /// vehicle/vision/blocks/match) 或中文页名, 也兼容旧的下标 (0=显示 1=仿真
    /// 2=控制器 3=小车 4=视觉 5=能量块 6=比赛/场景)。
    /// </summary>
    public void SelectTab(string page)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return;
        }
        if (int.TryParse(page, out var index))
        {
            SelectTab(index);
            return;
        }
        if (_tabs is null)
        {
            return;
        }
        var keyIndex = Array.FindIndex(TabKeys, key => string.Equals(key, page, StringComparison.OrdinalIgnoreCase));
        if (keyIndex >= 0)
        {
            SelectTab(keyIndex);
            return;
        }
        for (var i = 0; i < _tabs.GetTabCount(); i++)
        {
            if (string.Equals(_tabs.GetTabTitle(i), page, StringComparison.Ordinal))
            {
                SelectTab(i);
                return;
            }
        }
        GD.PrintErr($"[settings-smoke] 未知标签页 '{page}'；可用: {string.Join('/', TabKeys)} 或下标 0-{TabKeys.Length - 1}");
    }

    /// <summary>按序号切页 (越界钳制); 新页只追加在末尾。</summary>
    public void SelectTab(int index)
    {
        if (_tabs is not null)
        {
            _tabs.CurrentTab = Mathf.Clamp(index, 0, _tabs.GetTabCount() - 1);
        }
    }

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

    public void Open(DesktopSettings settings, bool pendingSimulationChanges,
        IReadOnlyDictionary<string, RobotModelConfig>? robotModels = null)
    {
        SyncViewportRect();
        _settings = settings;
        _robotModels = robotModels ?? new Dictionary<string, RobotModelConfig>();
        LoadControls(settings);
        if (_pendingNote is not null)
        {
            _pendingNote.Text = pendingSimulationChanges
                ? "已有修改待下一场生效（回放/编辑布局中不自动重置）· F5 可立即重置并应用"
                : "显示设置立即生效 · 仿真/控制器/视觉/能量块设置保存后自动重置生效（回放/编辑布局中为下一场生效）";
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
        _tabs = tabs;
        tabs.AddChild(BuildDisplayPage());
        tabs.SetTabTitle(0, "显示与窗口");
        tabs.AddChild(BuildSimulationPage());
        tabs.SetTabTitle(1, "仿真参数");
        tabs.AddChild(BuildControllerPage());
        tabs.SetTabTitle(2, "小车控制器");
        tabs.AddChild(BuildVehiclePage());
        tabs.SetTabTitle(3, "小车");
        tabs.AddChild(BuildVisionPage());
        tabs.SetTabTitle(4, "视觉");
        tabs.AddChild(BuildBlocksPage());
        tabs.SetTabTitle(5, "能量块");
        tabs.AddChild(BuildMatchPage());
        tabs.SetTabTitle(6, "比赛/场景");

        _pendingNote = AddLabel(root,
            "显示设置立即生效 · 仿真/控制器/视觉/能量块设置保存后自动重置生效（回放/编辑布局中为下一场生效）",
            11, Yellow);
        _pendingNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        root.AddChild(footer);
        _error = AddLabel(footer, "", 11, Red);
        _error.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _error.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _error.CustomMinimumSize = new Vector2(0, 36);

        // 配置包两按钮 (批1 R1.1): 只负责选文件与转发请求, bundle 内容由 Main 收集/落盘。
        var exportBundle = MakeButton("导出配置包…", Blue, new Vector2(116, 38));
        exportBundle.TooltipText = "把主设置 / 外观模型 / 当前场景（+可选训练配置）导出为单个 JSON 配置包";
        exportBundle.Pressed += RequestExportBundle;
        footer.AddChild(exportBundle);
        var importBundle = MakeButton("导入配置包…", Blue, new Vector2(116, 38));
        importBundle.TooltipText = "导入配置包：版本不匹配或内容无效会被拒绝；确认后覆盖主设置 / 外观模型 / 场景 / 训练配置";
        importBundle.Pressed += RequestImportBundle;
        footer.AddChild(importBundle);

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

        BuildBundleDialogs();
        BuildScenarioDialog();
    }

    /// <summary>
    /// "比赛/场景"页的场景文件选择对话框 (OpenFile, *.json)。批4 会把它并进
    /// MakePathInput 的通用选择器; 现在先按 LayoutEditor 的用法独立建一个。
    /// </summary>
    private void BuildScenarioDialog()
    {
        _scenarioDialog = new FileDialog
        {
            Title = "选择场景文件（*.json）",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.json ; 场景 (Scenario)" },
        };
        _scenarioDialog.FileSelected += path =>
        {
            if (_matchScenarioPath is not null)
            {
                _matchScenarioPath.Text = path;
            }
        };
        AddChild(_scenarioDialog);
    }

    /// <summary>
    /// 页脚"导出/导入配置包"的文件对话框 (Access/Filters 用法参照 LayoutEditor.Bind)。
    /// 导出成功选落点后再弹一次训练配置文件选择, 取消该步 = 不附带训练配置。
    /// </summary>
    private void BuildBundleDialogs()
    {
        _bundleSaveDialog = new FileDialog
        {
            Title = "导出配置包",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Filters = new[] { "*.json ; 配置包 (Settings Bundle)" },
            CurrentFile = SettingsBundleStore.DefaultFileName,
        };
        _bundleOpenDialog = new FileDialog
        {
            Title = "导入配置包",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.json ; 配置包 (Settings Bundle)" },
        };
        _trainConfigDialog = new FileDialog
        {
            Title = "附带训练配置文件（取消 = 不附带）",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.json ; 训练配置 (train.py --config)" },
        };
        _bundleSaveDialog.FileSelected += OnBundleExportPathSelected;
        _bundleOpenDialog.FileSelected += path => ImportBundleRequested?.Invoke(path);
        _trainConfigDialog.FileSelected += OnTrainConfigSelected;
        _trainConfigDialog.Canceled += OnTrainConfigSkipped;
        AddChild(_bundleSaveDialog);
        AddChild(_bundleOpenDialog);
        AddChild(_trainConfigDialog);
    }

    private void RequestExportBundle()
    {
        ClearError();
        _bundleSaveDialog?.PopupCentered(new Vector2I(860, 620));
    }

    private void RequestImportBundle()
    {
        ClearError();
        _bundleOpenDialog?.PopupCentered(new Vector2I(860, 620));
    }

    private void OnBundleExportPathSelected(string path)
    {
        _pendingBundleExportPath = path;
        _trainConfigDialog?.PopupCentered(new Vector2I(860, 620));
    }

    private void OnTrainConfigSelected(string path)
    {
        var bundlePath = _pendingBundleExportPath;
        _pendingBundleExportPath = null;
        if (bundlePath is not null)
        {
            ExportBundleRequested?.Invoke(bundlePath, path);
        }
    }

    private void OnTrainConfigSkipped()
    {
        var bundlePath = _pendingBundleExportPath;
        _pendingBundleExportPath = null;
        if (bundlePath is not null)
        {
            ExportBundleRequested?.Invoke(bundlePath, null);
        }
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
        AddDevSection(root);
        return scroll;
    }

    /// <summary>
    /// 高级/开发者折叠区 (批2 R2.2, 默认收起): L1/L2/L3 legacy 接触扩展开关。
    /// 默认全开 = 现行为; 改动影响碰撞判定, 回放身份会失配 —— 必须原样披露 (同
    /// ContactResolveOptions 注释: 开启态回放需以同开关构造引擎)。仅 legacy 后端消费。
    /// </summary>
    private void AddDevSection(VBoxContainer parent)
    {
        var section = new VBoxContainer { Visible = false };
        section.AddThemeConstantOverride("separation", 6);
        _devSection = section;

        _devToggle = MakeButton("▸ 高级 / 开发者", Secondary, new Vector2(0, 34));
        _devToggle.TooltipText = "legacy 物理接触求解扩展（L1/L2/L3）与开发者开关；默认全开 = 现行为";
        _devToggle.Pressed += () =>
        {
            var visible = !section.Visible;
            section.Visible = visible;
            if (_devToggle is not null)
            {
                _devToggle.Text = visible ? "▾ 高级 / 开发者" : "▸ 高级 / 开发者";
            }
        };
        parent.AddChild(_devToggle);
        parent.AddChild(section);

        var warning = AddLabel(section,
            "改动影响碰撞判定，回放身份会失配：开启态录制的 legacy 回放只有在相同的 L1/L2/L3 组合下才可复现。"
            + "默认全开 = 现行为；仅 legacy 后端消费这三个开关（mujoco 不看）。改动保存后自动重开当前对局。",
            11, Yellow, new Vector2(0, 44));
        warning.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _devL1 = MakeDevCheck(section, "L1 车-车 OBB 稳态分离 + 台壁位移钳位",
            "顶牛互穿修复（0.120 → 0.001 m）；关闭 = 回到旧 legacy 接触路径");
        _devL2 = MakeDevCheck(section, "L2 车-块 OBB 分离 + 推块速度镜像",
            "推块/卡角互穿修复；关闭 = 回到旧 legacy 接触路径");
        _devL3 = MakeDevCheck(section, "L3 块-台壁阻挡",
            "块-台沿高速穿墙/压入封挡；关闭 = 回到旧 legacy 接触路径");
    }

    private static CheckButton MakeDevCheck(Container parent, string label, string tooltip)
    {
        var check = new CheckButton
        {
            Text = label,
            TooltipText = tooltip,
            FocusMode = FocusModeEnum.None,
        };
        ApplyCheckButtonTheme(check);
        parent.AddChild(check);
        return check;
    }

    private Control BuildControllerPage()
    {
        // 展演说明加入后内容超出页高: 与仿真/小车页同为滚动容器。
        var scroll = new ScrollContainer
        {
            Name = "ControllerSettings",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 10);
        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(page);
        var root = page;
        AddLabel(root, "外部小车控制器", 16, Primary);
        var warning = AddLabel(root,
            "使用外部命令/脚本通过既有 JSONL stdio 协议控制小车，不启动 Godot 内嵌代码编辑器。外部进程拥有本机权限，请只运行可信代码。",
            11, Yellow);
        warning.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(BuildControllerSection("我方 / BLUE（RL 展演）", RoleNames.Us,
            "例如：py -3.12 -X utf8 ../tools/rl-bridge/rl_desktop_runner.py --checkpoint <zip>"));
        root.AddChild(BuildControllerSection("对手 / RED", RoleNames.Them));
        var note = AddLabel(root,
            "协议：每行输入 observation JSON，输出 {\"v\":...,\"w\":...,\"requestId\":...}；超时或坏行会安全回退为零动作并显示 fault。",
            11, Secondary);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // 我方 external 的新语义（RL 展演）与边界：与 Main 的装配决策同一口径。
        var exhibitionNote = AddLabel(root,
            "我方 external = SCORE_BLOCK 展演（RL 策略）：只在 physics.backend=mujoco 的场景启用；"
            + "legacy 场景会被拒绝并回退内置 FSM（设置仍保存，换回 mujoco 场景重应用即恢复）。"
            + "应用设置时自动预检一次（启动→握手→立刻释放）；预检不覆盖首帧模型加载时间，建议超时 ≥ 5000 ms。"
            + "控制器子进程以 godot/ 为工作目录，相对脚本路径写 ../tools/rl-bridge/rl_desktop_runner.py。"
            + "展演为非门禁证据（不写回放、不晋升 fidelity）。",
            11, Yellow);
        exhibitionNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return scroll;
    }

    private Control BuildControllerSection(string title, string role, string? commandPlaceholder = null)
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
        var mode = MakeOption(
            ("内置 FSM", ControllerModes.BuiltIn),
            ("内置 MBri", ControllerModes.Mbri),
            ("外部命令", ControllerModes.External));
        row.AddChild(mode);
        AddLabel(row, "超时", 11, Secondary, new Vector2(36, 0));
        var timeout = MakeSpin(1, 5000, 1, "ms");
        timeout.CustomMinimumSize = new Vector2(120, 32);
        row.AddChild(timeout);

        var command = new LineEdit
        {
            PlaceholderText = commandPlaceholder ?? "例如：python my_controller.py",
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
        // 内置档 (FSM/MBri) 无子进程可预检: 直接给出说明, 不启动空命令。
        if (mode.Selected != 2)
        {
            var label = role == RoleNames.Us ? _usPreflightResult : _themPreflightResult;
            if (label is not null)
            {
                label.Text = mode.Selected == 1
                    ? "内置 MBri 无需预检（场景内控制器，不启动子进程）"
                    : "内置 FSM 无需预检";
                label.AddThemeColorOverride("font_color", Secondary);
            }
            return;
        }
        if (Interlocked.CompareExchange(ref _preflightBusy, 1, 0) != 0)
        {
            return;
        }
        var profile = new ControllerProfile
        {
            Mode = ControllerModes.External,
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
        if (_vehicleMass is not null)
        {
            _vehicleMass.Value = settings.Vehicle?.Mass ?? 3.5;
        }
        if (_vehicleRpm is not null)
        {
            _vehicleRpm.Value = settings.Vehicle?.MotorRpm ?? 120;
        }
        if (_vehicleTorque is not null)
        {
            _vehicleTorque.Value = settings.Vehicle?.MotorTorque ?? 1.72;
        }
        if (_vehicleWheelRadius is not null)
        {
            _vehicleWheelRadius.Value = settings.Vehicle?.WheelRadius ?? 0.0325;
        }
        UpdateVehicleNote();

        var blocks = settings.BlockLayout;
        if (_blockCustom is not null)
        {
            _blockCustom.ButtonPressed = blocks is not null && !blocks.IsFollowScenario;
        }
        if (_blockBuffCount is not null)
        {
            _blockBuffCount.Value = blocks?.BuffCount ?? 2;
        }
        if (_blockDebuffCount is not null)
        {
            _blockDebuffCount.Value = blocks?.DebuffCount ?? 1;
        }
        if (_blockPlacement is not null)
        {
            _blockPlacement.Select(blocks?.RandomPositions == true ? 1 : 0);
        }
        UpdateBlockInputs();

        if (_sensorProfile is not null)
        {
            var presetId = settings.Vehicle?.SensorProfileId;
            _sensorProfile.Select(
                presetId == SensorProfiles.WheeledCombat11.Id ? 1
                : presetId == SensorProfiles.Legacy14.Id ? 2
                : 0);
        }
        RebuildSensorChannelRows();

        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            _robotModels.TryGetValue(role, out var config);
            config ??= new RobotModelConfig();
            if (_modelPathInputs.TryGetValue(role, out var pathInput))
            {
                pathInput.Text = config.Path;
            }
            if (_modelTransformInputs.TryGetValue(role, out var transforms))
            {
                transforms[0].Value = config.Scale > 0 ? config.Scale : 1.0;
                transforms[1].Value = config.YawOffset;
                transforms[2].Value = config.HeightOffset;
            }
        }

        var vision = settings.Vision ?? new VisionSettings();
        if (_visionSource is not null)
        {
            _visionSource.Select(vision.Source switch
            {
                VisionSources.VisionReplay => 1,
                VisionSources.LiveBridge => 2,
                VisionSources.LiveProcess => 3,
                _ => 0,
            });
        }
        if (_visionEvidencePath is not null)
        {
            _visionEvidencePath.Text = vision.EvidencePath;
        }
        if (_visionCsvPath is not null)
        {
            _visionCsvPath.Text = vision.CsvPath;
        }
        if (_visionProcessCommand is not null)
        {
            _visionProcessCommand.Text = vision.ProcessCommand;
        }
        if (_visionMaxAge is not null)
        {
            _visionMaxAge.Value = vision.MaxAgeMs;
        }
        UpdateVisionInputs();

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

        // 批2 比赛/场景页 + 高级折叠区: 缺省档显示"跟随/全开", 不写新字段。
        var match = settings.MatchOverrides ?? new MatchOverrides();
        if (_matchBackend is not null)
        {
            _matchBackend.Select(match.PhysicsBackendOverride switch
            {
                MatchBackendOverrides.Legacy => 1,
                MatchBackendOverrides.MujocoV1 => 2,
                MatchBackendOverrides.MujocoV2 => 3,
                _ => 0,
            });
        }
        if (_matchScenarioPath is not null)
        {
            _matchScenarioPath.Text = match.ScenarioPath;
        }
        if (_matchDurationOverride is not null)
        {
            _matchDurationOverride.ButtonPressed = match.MatchDuration is not null;
        }
        if (_matchDuration is not null)
        {
            _matchDuration.Value = match.MatchDuration ?? 120;
        }
        if (_matchSeedOverride is not null)
        {
            _matchSeedOverride.ButtonPressed = match.Seed is not null;
        }
        if (_matchSeed is not null)
        {
            _matchSeed.Value = match.Seed ?? 42;
        }
        var dev = settings.DevContact ?? new DevContact();
        if (_devL1 is not null)
        {
            _devL1.ButtonPressed = dev.L1VehicleVehicleObb;
        }
        if (_devL2 is not null)
        {
            _devL2.ButtonPressed = dev.L2VehicleBlockObb;
        }
        if (_devL3 is not null)
        {
            _devL3.ButtonPressed = dev.L3BlockWallBlock;
        }
        UpdateMatchInputs();
    }

    private static void LoadController(ControllerProfile? profile, OptionButton? mode,
        LineEdit? command, SpinBox? timeout)
    {
        profile ??= new ControllerProfile();
        // 档位顺序: 0 内置 FSM / 1 内置 MBri / 2 外部命令 (BuildControllerSection 同序)。
        mode?.Select(profile.Mode switch
        {
            ControllerModes.External => 2,
            ControllerModes.Mbri => 1,
            _ => 0,
        });
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
            BlockLayout = _blockCustom is { ButtonPressed: true }
                ? new BlockLayoutSettings
                {
                    BuffCount = (int)(_blockBuffCount?.Value ?? 2),
                    DebuffCount = (int)(_blockDebuffCount?.Value ?? 1),
                    RandomPositions = _blockPlacement?.Selected == 1,
                }
                : null,
            Vehicle = new VehicleSettings
            {
                Mass = _vehicleMass?.Value ?? 3.5,
                MotorRpm = _vehicleRpm?.Value ?? 120,
                MotorTorque = _vehicleTorque?.Value ?? 1.72,
                WheelRadius = _vehicleWheelRadius?.Value ?? 0.0325,
                SensorProfileId = SelectedSensorProfileId(),
                SensorDisabled = _sensorChannelRows
                    .Where(row => !row.Enabled.ButtonPressed)
                    .Select(row => row.ChannelId)
                    .ToList(),
                SensorOffsets = CollectSensorOffsets(),
            },
            Vision = new VisionSettings
            {
                Source = SelectedVisionSource(),
                EvidencePath = _visionEvidencePath?.Text.Trim() ?? "",
                CsvPath = _visionCsvPath?.Text.Trim() ?? "",
                ProcessCommand = _visionProcessCommand?.Text.Trim() ?? "",
                MaxAgeMs = _visionMaxAge?.Value ?? LiveVisionBridge.DefaultMaxAgeMs,
            },
            UsController = ReadController(_usMode, _usCommand, _usTimeout),
            ThemController = ReadController(_themMode, _themCommand, _themTimeout),
            // 批2: 全跟随/全开 = null (不落盘新字段, 与老配置逐位等价)。
            MatchOverrides = ReadMatchOverridesDraft(),
            DevContact = ReadDevContactDraft(),
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
        RobotModelsApplied?.Invoke(ReadRobotModelsDraft());
    }

    private static ControllerProfile ReadController(OptionButton? mode, LineEdit? command, SpinBox? timeout)
        => new()
        {
            Mode = mode?.Selected switch
            {
                2 => ControllerModes.External,
                1 => ControllerModes.Mbri,
                _ => ControllerModes.BuiltIn,
            },
            Command = command?.Text.Trim() ?? "",
            TimeoutMs = timeout?.Value ?? 100,
        };

    /// <summary>
    /// 比赛/场景覆盖 draft: 全跟随 = null (不落盘新字段, 老配置/旧 bundle 语义逐位不变)。
    /// </summary>
    private MatchOverrides? ReadMatchOverridesDraft()
    {
        var draft = new MatchOverrides
        {
            PhysicsBackendOverride = SelectedMatchBackend(),
            ScenarioPath = _matchScenarioPath?.Text.Trim() ?? "",
            MatchDuration = _matchDurationOverride is { ButtonPressed: true }
                ? _matchDuration?.Value ?? 120
                : null,
            Seed = _matchSeedOverride is { ButtonPressed: true }
                ? (int)Math.Round(_matchSeed?.Value ?? 42)
                : null,
        };
        return draft.IsFollowScenario ? null : draft;
    }

    /// <summary>高级/开发者接触开关 draft: 全开 = null (等价 ContactResolveOptions 默认)。</summary>
    private DevContact? ReadDevContactDraft()
    {
        var draft = new DevContact
        {
            L1VehicleVehicleObb = _devL1?.ButtonPressed ?? true,
            L2VehicleBlockObb = _devL2?.ButtonPressed ?? true,
            L3BlockWallBlock = _devL3?.ButtonPressed ?? true,
        };
        return draft.L1VehicleVehicleObb && draft.L2VehicleBlockObb && draft.L3BlockWallBlock
            ? null
            : draft;
    }

    private Control BuildVehiclePage()
    {
        // 传感器/外观两区展开后远超一页: 与仿真参数页同为滚动容器。
        var scroll = new ScrollContainer
        {
            Name = "VehicleSettings",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 10);
        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(page);

        AddLabel(page, "小车", 16, Primary);
        AddLabel(page,
            "比赛双方同款真车的物理规格；应用于 v2 真车几何场景，下一场或 F5 重置后生效。",
            11, Secondary);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        page.AddChild(grid);

        AddLabel(grid, "整车质量（含电池/电机/主控）", 12, Secondary);
        _vehicleMass = MakeSpin(0.2, 20, 0.05, "kg");
        grid.AddChild(_vehicleMass);

        AddLabel(grid, "电机减速后转速（空载）", 12, Secondary);
        _vehicleRpm = MakeSpin(10, 2000, 1, "RPM");
        grid.AddChild(_vehicleRpm);

        AddLabel(grid, "电机输出扭矩（额定）", 12, Secondary);
        _vehicleTorque = MakeSpin(0.05, 50, 0.01, "N·m");
        grid.AddChild(_vehicleTorque);

        AddLabel(grid, "驱动轮半径（装配实测）", 12, Secondary);
        _vehicleWheelRadius = MakeSpin(0.005, 0.1, 0.0001, "m");
        grid.AddChild(_vehicleWheelRadius);

        foreach (var spin in new[] { _vehicleMass, _vehicleRpm, _vehicleTorque, _vehicleWheelRadius })
        {
            spin.ValueChanged += _ => UpdateVehicleNote();
        }

        _vehicleNote = AddLabel(page, "", 12, Blue, new Vector2(0, 44));
        _vehicleNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        UpdateVehicleNote();

        AddLabel(page, "传感器覆盖", 16, Primary);
        var sensorIntro = AddLabel(page,
            "以预设为基底克隆自定义 profile 写入双方车辆：整路禁用（读数恒为下限，FSM 门限不触发）"
            + "或按车体系偏移挂点（实车“挪探头”标定语义，dx=前向 / dy=横向 / dz=高度 / dyaw=朝向）。下一场生效。",
            11, Secondary);
        sensorIntro.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 8);
        page.AddChild(presetRow);
        AddLabel(presetRow, "传感器预设", 12, Secondary, new Vector2(88, 0));
        _sensorProfile = MakeOption(
            ("跟随场景（不改）", ""),
            ("真车 11 路（wheeledCombat11）", SensorProfiles.WheeledCombat11.Id),
            ("兼容 14 路（legacy14）", SensorProfiles.Legacy14.Id));
        presetRow.AddChild(_sensorProfile);
        _sensorProfile.ItemSelected += _ => RebuildSensorChannelRows();

        _sensorBaseNote = AddLabel(page, "", 11, Blue, new Vector2(0, 22));
        _sensorBaseNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        _sensorChannelGrid = new GridContainer { Columns = 6 };
        _sensorChannelGrid.AddThemeConstantOverride("h_separation", 6);
        _sensorChannelGrid.AddThemeConstantOverride("v_separation", 4);
        page.AddChild(_sensorChannelGrid);

        AddLabel(page, "外观模型（渲染层）", 16, Primary);
        var modelIntro = AddLabel(page,
            "us/them 的 glb/gltf 外观绑定：只改渲染，不影响仿真；留空回退 primitive 分件。"
            + "应用后写入 robot-models.json 并立即生效。模型约定：车头 +Z、原点在地面，"
            + "scale / yawOffset / heightOffset 三个修正项。",
            11, Secondary);
        modelIntro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(BuildRobotModelSection(RoleNames.Us, "我方 / BLUE", Blue));
        page.AddChild(BuildRobotModelSection(RoleNames.Them, "对手 / RED", Red));

        page.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        return scroll;
    }

    /// <summary>单个 role 的外观模型输入块: glb 路径 + scale/yawOffset/heightOffset 三修正项。</summary>
    private Control BuildRobotModelSection(string role, string title, Color accent)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        AddLabel(box, title, 13, accent);
        var path = MakePathInput($"例如：C:/models/{role}.glb 或 res://models/{role}.glb（留空 = primitive 分件）");
        box.AddChild(path);
        _modelPathInputs[role] = path;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddLabel(row, "缩放", 11, Secondary, new Vector2(40, 0));
        var scale = MakeSpin(0.05, 10, 0.01, "x");
        scale.CustomMinimumSize = new Vector2(120, 32);
        row.AddChild(scale);
        AddLabel(row, "朝向偏移", 11, Secondary, new Vector2(64, 0));
        var yaw = MakeSpin(-2 * Math.PI, 2 * Math.PI, 0.01, "rad");
        yaw.CustomMinimumSize = new Vector2(150, 32);
        row.AddChild(yaw);
        AddLabel(row, "高度偏移", 11, Secondary, new Vector2(64, 0));
        var height = MakeSpin(-0.2, 0.5, 0.001, "m");
        height.CustomMinimumSize = new Vector2(150, 32);
        row.AddChild(height);
        box.AddChild(row);
        _modelTransformInputs[role] = new[] { scale, yaw, height };
        return box;
    }

    /// <summary>当前预设选择对应的 profile id (null = 跟随场景)。</summary>
    private string? SelectedSensorProfileId() => (_sensorProfile?.Selected ?? 0) switch
    {
        1 => SensorProfiles.WheeledCombat11.Id,
        2 => SensorProfiles.Legacy14.Id,
        _ => null,
    };

    /// <summary>
    /// 按当前预设选择重建通道行 (启用勾选 + dx/dy/dz/dyaw 偏移), 值回填自 vehicle 覆盖。
    /// “跟随场景”的基底随场景自带 profile, UI 按 legacy14 展示通道清单 (与解析端 fallback 一致)。
    /// </summary>
    private void RebuildSensorChannelRows()
    {
        if (_sensorChannelGrid is null)
        {
            return;
        }
        var presetId = SelectedSensorProfileId();
        var baseProfile = presetId == SensorProfiles.WheeledCombat11.Id
            ? SensorProfiles.WheeledCombat11
            : SensorProfiles.Legacy14;
        var vehicle = _settings.Vehicle ?? new VehicleSettings();
        var disabled = new HashSet<string>(vehicle.SensorDisabled);

        foreach (var child in _sensorChannelGrid.GetChildren())
        {
            child.QueueFree();
        }
        _sensorChannelRows.Clear();

        if (_sensorBaseNote is not null)
        {
            _sensorBaseNote.Text = presetId is null
                ? $"跟随场景基底（无覆盖时逐位不变）；下表按 {baseProfile.Id} 展示通道，实际基底随场景自带 profile。"
                : $"基底预设 {baseProfile.Id}（{baseProfile.Label}）· 共 {baseProfile.Channels.Count} 路";
        }

        AddLabel(_sensorChannelGrid, "启用", 11, Secondary, new Vector2(46, 0));
        AddLabel(_sensorChannelGrid, "通道", 11, Secondary, new Vector2(170, 0));
        AddLabel(_sensorChannelGrid, "dx 前向", 11, Secondary, new Vector2(110, 0));
        AddLabel(_sensorChannelGrid, "dy 横向", 11, Secondary, new Vector2(110, 0));
        AddLabel(_sensorChannelGrid, "dz 高度", 11, Secondary, new Vector2(110, 0));
        AddLabel(_sensorChannelGrid, "dyaw 朝向", 11, Secondary, new Vector2(110, 0));

        foreach (var channel in baseProfile.Channels)
        {
            var enabled = new CheckButton { FocusMode = FocusModeEnum.None };
            enabled.ButtonPressed = !disabled.Contains(channel.Id);
            ApplyCheckButtonTheme(enabled);
            _sensorChannelGrid.AddChild(enabled);

            var name = AddLabel(_sensorChannelGrid, $"{channel.Id} · {channel.Label}", 11, Primary, new Vector2(170, 0));
            name.TooltipText = channel.Id;
            name.ClipText = true;

            vehicle.SensorOffsets.TryGetValue(channel.Id, out var off);
            var dx = MakeOffsetSpin(_sensorChannelGrid, off, o => o.Dx, -0.5, 0.5, 0.001, "m");
            var dy = MakeOffsetSpin(_sensorChannelGrid, off, o => o.Dy, -0.5, 0.5, 0.001, "m");
            var dz = MakeOffsetSpin(_sensorChannelGrid, off, o => o.Dz, -0.2, 0.2, 0.001, "m");
            var dyaw = MakeOffsetSpin(_sensorChannelGrid, off, o => o.Yaw, -Math.PI, Math.PI, 0.01, "rad");
            _sensorChannelRows.Add((channel.Id, enabled, dx, dy, dz, dyaw));
        }
    }

    private static SpinBox MakeOffsetSpin(GridContainer grid, SensorOffset? offset,
        Func<SensorOffset, double> pick, double min, double max, double step, string suffix)
    {
        var spin = MakeSpin(min, max, step, suffix);
        spin.CustomMinimumSize = new Vector2(110, 30);
        spin.Value = offset is null ? 0 : pick(offset);
        grid.AddChild(spin);
        return spin;
    }

    /// <summary>收集非零偏移通道 (零偏移不写入, 保持 settings 精简且语义 = 未覆盖)。</summary>
    private Dictionary<string, SensorOffset> CollectSensorOffsets()
    {
        var offsets = new Dictionary<string, SensorOffset>(StringComparer.Ordinal);
        foreach (var row in _sensorChannelRows)
        {
            var offset = new SensorOffset(row.Dx.Value, row.Dy.Value, row.Dz.Value, row.Yaw.Value);
            if (Math.Abs(offset.Dx) > 1e-12 || Math.Abs(offset.Dy) > 1e-12
                || Math.Abs(offset.Dz) > 1e-12 || Math.Abs(offset.Yaw) > 1e-12)
            {
                offsets[row.ChannelId] = offset;
            }
        }
        return offsets;
    }

    /// <summary>外观模型 draft: 路径留空的 role 不产生条目 (= 回退 primitive)。</summary>
    private Dictionary<string, RobotModelConfig> ReadRobotModelsDraft()
    {
        var models = new Dictionary<string, RobotModelConfig>(StringComparer.Ordinal);
        foreach (var (role, input) in _modelPathInputs)
        {
            var path = input.Text.Trim();
            if (path.Length == 0 || !_modelTransformInputs.TryGetValue(role, out var t))
            {
                continue;
            }
            models[role] = new RobotModelConfig
            {
                Path = path,
                Scale = t[0].Value,
                YawOffset = t[1].Value,
                HeightOffset = t[2].Value,
            };
        }
        return models;
    }

    private void UpdateVehicleNote()
    {
        if (_vehicleNote is null)
        {
            return;
        }
        var rpm = _vehicleRpm?.Value ?? 120;
        var wheelRadius = _vehicleWheelRadius?.Value ?? 0.0325;
        var maxSpeed = rpm / 60.0 * 2 * Math.PI * wheelRadius;
        _vehicleNote.Text =
            $"默认配套：博创尚和 2342 开环电机（12V，减速后 {rpm:0} RPM）。"
            + $"轮端极速 ≈ {maxSpeed:0.000} m/s；登台/恢复时限随极速自动缩放；"
            + "扭矩当前仅存档（仿真为速度伺服）。";
    }

    /// <summary>
    /// 能量块设置页(2026-10-04): 自定义开关关闭 = 跟随场景(逐位不变); 开启后可调
    /// 增益/减益数量与落位方式。应用写入 DesktopSettings.BlockLayout, 下一场生效。
    /// </summary>
    private Control BuildBlocksPage()
    {
        var scroll = new ScrollContainer
        {
            Name = "BlockLayoutSettings",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 10);
        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(page);

        AddLabel(page, "能量块布局", 16, Primary);
        AddLabel(page,
            "自定义比赛的能量块数量与类型：增益块被推上台我方 +3，减益块被推上台对方 +6。"
            + "关闭自定义 = 跟随场景/官方布局（2 增益 + 1 减益，行为逐位不变）。下一场或 F5 重置后生效。",
            11, Secondary);

        _blockCustom = new CheckButton { Text = "自定义能量块布局", FocusMode = FocusModeEnum.None };
        ApplyCheckButtonTheme(_blockCustom);
        _blockCustom.Toggled += _ => UpdateBlockInputs();
        page.AddChild(_blockCustom);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        page.AddChild(grid);

        AddLabel(grid, "增益块数量", 12, Secondary);
        _blockBuffCount = MakeSpin(0, Scenario.MaxBlocks, 1, "个");
        grid.AddChild(_blockBuffCount);
        AddLabel(grid, "减益块数量", 12, Secondary);
        _blockDebuffCount = MakeSpin(0, Scenario.MaxBlocks, 1, "个");
        grid.AddChild(_blockDebuffCount);
        AddLabel(grid, "落位方式", 12, Secondary);
        _blockPlacement = MakeOption(
            ("官方坐标优先，多出的由裁判随机放置", "official"),
            ("全部随机位置（裁判按种子放置）", "random"));
        grid.AddChild(_blockPlacement);

        _blockBuffCount.ValueChanged += _ => UpdateBlockNote();
        _blockDebuffCount.ValueChanged += _ => UpdateBlockNote();
        _blockPlacement.ItemSelected += _ => UpdateBlockNote();

        _blockNote = AddLabel(page, "", 12, Blue, new Vector2(0, 44));
        _blockNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        UpdateBlockInputs();

        page.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return scroll;
    }

    /// <summary>自定义开关只控制数量/落位输入的可用性；关闭 = 跟随场景(不改布局)。</summary>
    private void UpdateBlockInputs()
    {
        var custom = _blockCustom?.ButtonPressed ?? false;
        if (_blockBuffCount is not null)
        {
            _blockBuffCount.Editable = custom;
        }
        if (_blockDebuffCount is not null)
        {
            _blockDebuffCount.Editable = custom;
        }
        if (_blockPlacement is not null)
        {
            _blockPlacement.Disabled = !custom;
        }
        UpdateBlockNote();
    }

    /// <summary>实时数量提示: 合计超上限时预告截断规则, 落位方式说明同步刷新。</summary>
    private void UpdateBlockNote()
    {
        if (_blockNote is null)
        {
            return;
        }
        if (_blockCustom is not { ButtonPressed: true })
        {
            _blockNote.Text = "跟随场景：使用场景文件/官方布局的能量块（行为逐位不变）。";
            return;
        }
        var buffs = (int)(_blockBuffCount?.Value ?? 2);
        var debuffs = (int)(_blockDebuffCount?.Value ?? 1);
        var total = buffs + debuffs;
        var placement = _blockPlacement?.Selected == 1
            ? "全部块由裁判按种子确定性放置（禁区：避台沿 0.35m / 避两车 0.8m / 避中央 0.6m / 块间 0.5m）"
            : "前两个增益块与第一个减益块用官方坐标，多出的块由裁判确定性放置";
        var clamp = total > Scenario.MaxBlocks
            ? $"（合计 {total} 超过上限 {Scenario.MaxBlocks}，应用时按增益优先截断）"
            : "";
        _blockNote.Text = $"增益 {buffs} + 减益 {debuffs} = {total} 块{clamp}；{placement}。0 块 = 纯对抗。";
    }

    /// <summary>
    /// "比赛/场景"页 (批2 R2.1): 物理后端覆盖 / 场景文件 / 比赛时长 / 随机种子。
    /// 全部控件缺省 = 跟随场景/启动值 (老配置不写新字段, 行为逐位不变); 显式覆盖后
    /// 保存即自动重开当前对局生效 (回放/布局编辑中为下一场或 F5)。
    /// 审计事实必须原样披露: 小车页的质量/转速/轮径/传感器覆盖只在 mujoco v2 生效
    /// (DesktopSettings.ApplyVehicleOverrides), 覆盖成 legacy/v1 时它们静默无效。
    /// </summary>
    private Control BuildMatchPage()
    {
        var scroll = new ScrollContainer
        {
            Name = "MatchSettings",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 10);
        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(page);

        AddLabel(page, "比赛 / 场景", 16, Primary);
        // 注: WordSmart 自动换行的 Label 在本项目 Godot 4.7 组合下若不给最小高度会以
        // 0 高度参与布局 (全页多处说明文字同病, 批4 统一修); 本页说明是覆盖项唯一
        // 文字披露, 先按 _matchNote/_blockNote 的既有做法给高度。
        var intro = AddLabel(page,
            "只在明确覆盖时改场景字段；跟随档与现状逐位一致。应用设置后自动重开当前对局生效（回放/布局编辑中为下一场或 F5）。",
            11, Secondary, new Vector2(0, 30));
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        page.AddChild(grid);

        AddLabel(grid, "物理后端", 12, Secondary);
        _matchBackend = MakeOption(
            ("跟随场景（不改）", MatchBackendOverrides.Follow),
            ("legacy 2D（旧物理）", MatchBackendOverrides.Legacy),
            ("MuJoCo v1（真车几何）", MatchBackendOverrides.MujocoV1),
            ("MuJoCo v2（真车几何 + 小车参数）", MatchBackendOverrides.MujocoV2));
        _matchBackend.TooltipText =
            "覆盖场景 physics.backend/modelVersion。仅 mujoco v2 支持小车页的质量/转速/轮径/传感器覆盖；"
            + "覆盖成 legacy/v1 时这些参数静默无效。应用后自动重开当前对局生效。";
        _matchBackend.ItemSelected += _ => UpdateMatchInputs();
        grid.AddChild(_matchBackend);

        AddLabel(grid, "场景文件", 12, Secondary);
        var scenarioRow = new HBoxContainer();
        scenarioRow.AddThemeConstantOverride("separation", 8);
        _matchScenarioPath = MakePathInput("留空 = 跟随启动场景");
        _matchScenarioPath.TooltipText =
            "场景 JSON 文件路径；留空 = 跟随启动场景。修改后应用设置即重载（回放/布局编辑中为下一场或 F5）。";
        scenarioRow.AddChild(_matchScenarioPath);
        var browse = MakeButton("浏览…", Blue, new Vector2(76, 34));
        browse.TooltipText = "选择场景 JSON 文件";
        browse.Pressed += () => _scenarioDialog?.PopupCentered(new Vector2I(860, 620));
        scenarioRow.AddChild(browse);
        grid.AddChild(scenarioRow);

        AddLabel(grid, "比赛时长", 12, Secondary);
        var durationRow = new HBoxContainer();
        durationRow.AddThemeConstantOverride("separation", 8);
        _matchDurationOverride = new CheckButton
        {
            Text = "覆盖",
            TooltipText = "勾选后覆盖场景 field.matchDuration；不勾 = 跟随场景",
            FocusMode = FocusModeEnum.None,
        };
        ApplyCheckButtonTheme(_matchDurationOverride);
        durationRow.AddChild(_matchDurationOverride);
        _matchDuration = MakeSpin(1, 3600, 1, "s");
        _matchDuration.TooltipText = "比赛时长（秒），覆盖场景 field.matchDuration";
        durationRow.AddChild(_matchDuration);
        grid.AddChild(durationRow);

        AddLabel(grid, "随机种子", 12, Secondary);
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        _matchSeedOverride = new CheckButton
        {
            Text = "覆盖",
            TooltipText = "勾选后覆盖启动/场景种子；不勾 = 跟随启动 seed",
            FocusMode = FocusModeEnum.None,
        };
        ApplyCheckButtonTheme(_matchSeedOverride);
        seedRow.AddChild(_matchSeedOverride);
        _matchSeed = MakeSpin(0, 4096, 1, "");
        _matchSeed.TooltipText = "确定性种子（0-4096，与 batch 种子上限一致）；同 seed 同布局轨迹一致";
        seedRow.AddChild(_matchSeed);
        _matchRestartSeed = MakeButton("换种子重开", Green, new Vector2(112, 34));
        _matchRestartSeed.TooltipText =
            "把当前种子写进设置并立即按 F5 语义重开（回放/布局编辑中为下一场生效）；不关闭设置页";
        _matchRestartSeed.Pressed += RequestRestartWithSeed;
        seedRow.AddChild(_matchRestartSeed);
        grid.AddChild(seedRow);

        _matchDurationOverride.Toggled += _ => UpdateMatchInputs();
        _matchSeedOverride.Toggled += _ => UpdateMatchInputs();
        _matchScenarioPath.TextChanged += _ => UpdateMatchNote();

        _matchNote = AddLabel(page, "", 12, Blue, new Vector2(0, 70));
        _matchNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        UpdateMatchInputs();

        page.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return scroll;
    }

    /// <summary>覆盖勾选控制输入可用性；未勾选 = 跟随 (老配置/第一次打开即此态)。</summary>
    private void UpdateMatchInputs()
    {
        if (_matchDuration is not null)
        {
            _matchDuration.Editable = _matchDurationOverride is { ButtonPressed: true };
        }
        if (_matchSeed is not null)
        {
            _matchSeed.Editable = _matchSeedOverride is { ButtonPressed: true };
        }
        UpdateMatchNote();
    }

    /// <summary>当前后端选择 (null = 跟随场景; 其余为 MatchBackendOverrides 档位串)。</summary>
    private string? SelectedMatchBackend() => (_matchBackend?.Selected ?? 0) switch
    {
        1 => MatchBackendOverrides.Legacy,
        2 => MatchBackendOverrides.MujocoV1,
        3 => MatchBackendOverrides.MujocoV2,
        _ => null,
    };

    /// <summary>
    /// 实时说明: 各覆盖项当前取值 + 后端与小车页参数的生效关系 (审计已知事实)。
    /// </summary>
    private void UpdateMatchNote()
    {
        if (_matchNote is null)
        {
            return;
        }
        var backend = SelectedMatchBackend();
        var backendText = backend switch
        {
            MatchBackendOverrides.Legacy => "物理后端：覆盖为 legacy 2D（旧物理）",
            MatchBackendOverrides.MujocoV1 => "物理后端：覆盖为 MuJoCo v1（真车几何）",
            MatchBackendOverrides.MujocoV2 => "物理后端：覆盖为 MuJoCo v2（真车几何 + 小车参数）",
            _ => "物理后端：跟随场景（physics 字段不改）",
        };
        var duration = _matchDurationOverride is { ButtonPressed: true }
            ? $"比赛时长：{(_matchDuration?.Value ?? 120):0} s（覆盖场景）"
            : "比赛时长：跟随场景";
        var seed = _matchSeedOverride is { ButtonPressed: true }
            ? $"种子：{(_matchSeed?.Value ?? 42):0}（覆盖启动 seed）"
            : "种子：跟随启动 seed";
        var scenario = string.IsNullOrWhiteSpace(_matchScenarioPath?.Text)
            ? "场景：跟随启动场景"
            : $"场景：{_matchScenarioPath!.Text.Trim()}";
        var disclosure = backend is MatchBackendOverrides.Legacy or MatchBackendOverrides.MujocoV1
            ? " 注意：小车页的质量/转速/轮径/传感器覆盖只在 mujoco v2 生效，当前后端档下这些参数会静默无效。"
            : "";
        _matchNote.Text = $"{scenario}；{backendText}；{duration}；{seed}。{disclosure}";
    }

    /// <summary>
    /// "换种子重开": 用种子框当前值写入覆盖 (面板本地状态与 Main 持久化同步), 然后请求
    /// Main 立即重开。面板不关闭, 其他未应用的草稿编辑保持不变。
    /// </summary>
    private void RequestRestartWithSeed()
    {
        if (_matchSeed is null)
        {
            return;
        }
        var seed = (int)Math.Round(_matchSeed.Value);
        if (_matchSeedOverride is not null)
        {
            _matchSeedOverride.ButtonPressed = true;
        }
        _matchSeed.Editable = true;
        var match = _settings.MatchOverrides ?? new MatchOverrides();
        _settings = _settings with { MatchOverrides = match with { Seed = seed } };
        UpdateMatchInputs();
        RestartWithSeedRequested?.Invoke(seed);
    }

    private Control BuildVisionPage()
    {
        var page = MakePage();
        AddLabel(page, "视觉源", 16, Primary);
        AddLabel(page,
            "四选一：默认识别率模型不注入外部源（行为与既有比赛逐位一致）；证据包回放与实时 CSV 桥读取本机文件；"
            + "外部推理进程每场启动子进程消费 stdout JSONL。下一场或 F5 重置后生效。",
            11, Secondary);

        var grid = new GridContainer { Columns = 2, CustomMinimumSize = new Vector2(0, 210) };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        page.AddChild(grid);

        AddLabel(grid, "视觉来源", 12, Secondary);
        _visionSource = MakeOption(
            ("默认识别率（classifyRate）", VisionSources.ClassifyRate),
            ("证据包回放（visionReplay）", VisionSources.VisionReplay),
            ("实时 CSV 桥（liveBridge）", VisionSources.LiveBridge),
            ("外部推理进程（liveProcess）", VisionSources.LiveProcess));
        grid.AddChild(_visionSource);

        AddLabel(grid, "证据包目录", 12, Secondary);
        _visionEvidencePath = MakePathInput("例如：vision/evidence-mini（含 frames.jsonl + import-report.json）");
        grid.AddChild(_visionEvidencePath);

        AddLabel(grid, "真车 CSV 路径", 12, Secondary);
        _visionCsvPath = MakePathInput("例如：vision/hunt_drive_20260817_095205.csv（MBri 73 列方言）");
        grid.AddChild(_visionCsvPath);

        AddLabel(grid, "推理进程命令行", 12, Secondary);
        _visionProcessCommand = MakePathInput(
            "例如：py tools/yolo-bridge/mbri_yolo_bridge.py --stub vision/stub.csv（stdout 逐帧 JSONL）");
        grid.AddChild(_visionProcessCommand);

        AddLabel(grid, "帧过期窗口", 12, Secondary);
        _visionMaxAge = MakeSpin(1, 5000, 1, "ms");
        grid.AddChild(_visionMaxAge);

        _visionSource.ItemSelected += _ => UpdateVisionInputs();
        if (_visionMaxAge is not null)
        {
            _visionMaxAge.ValueChanged += _ => UpdateVisionNote();
        }
        if (_visionEvidencePath is not null)
        {
            _visionEvidencePath.TextChanged += _ => UpdateVisionNote();
        }
        if (_visionCsvPath is not null)
        {
            _visionCsvPath.TextChanged += _ => UpdateVisionNote();
        }
        if (_visionProcessCommand is not null)
        {
            _visionProcessCommand.TextChanged += _ => UpdateVisionNote();
        }

        _visionNote = AddLabel(page, "", 12, Blue, new Vector2(0, 44));
        _visionNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        UpdateVisionInputs();

        page.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return page;
    }

    /// <summary>只让当前来源用到的输入可编辑；默认源的路径/命令框保持可见但禁用。</summary>
    private void UpdateVisionInputs()
    {
        var source = SelectedVisionSource();
        if (_visionEvidencePath is not null)
        {
            _visionEvidencePath.Editable = source == VisionSources.VisionReplay;
        }
        if (_visionCsvPath is not null)
        {
            _visionCsvPath.Editable = source == VisionSources.LiveBridge;
        }
        if (_visionProcessCommand is not null)
        {
            _visionProcessCommand.Editable = source == VisionSources.LiveProcess;
        }
        if (_visionMaxAge is not null)
        {
            _visionMaxAge.Editable = source != VisionSources.ClassifyRate;
        }
        UpdateVisionNote();
    }

    private void UpdateVisionNote()
    {
        if (_visionNote is null)
        {
            return;
        }
        var maxAge = _visionMaxAge?.Value ?? LiveVisionBridge.DefaultMaxAgeMs;
        _visionNote.Text = SelectedVisionSource() switch
        {
            VisionSources.VisionReplay =>
                $"证据包回放：哈希锁定读包后按 {maxAge:0} ms 窗口供帧；包缺文件或哈希不一致会在应用设置时直接报错。",
            VisionSources.LiveBridge =>
                $"实时 CSV 桥：按仿真时间释放真车检测流，帧龄超过 {maxAge:0} ms 记 stale（unknown）；路径不可用会在应用设置时直接报错。",
            VisionSources.LiveProcess =>
                $"外部推理进程：应用设置时预检启动一次并回收（坏命令行当场报错）；每场新起进程消费 stdout JSONL，"
                + $"帧龄超过 {maxAge:0} ms 记 stale（unknown）。子进程必须逐帧 flush。",
            _ => "默认视觉源：引擎内部识别率模型（classifyRate），不注入外部源，行为与既有比赛逐位一致。",
        };
    }

    private string SelectedVisionSource() => (_visionSource?.Selected ?? 0) switch
    {
        1 => VisionSources.VisionReplay,
        2 => VisionSources.LiveBridge,
        3 => VisionSources.LiveProcess,
        _ => VisionSources.ClassifyRate,
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

    /// <summary>路径输入框（证据包目录 / 真车 CSV）: 只做文本编辑, 校验与读取留给应用时。</summary>
    private static LineEdit MakePathInput(string placeholder)
    {
        var line = new LineEdit
        {
            PlaceholderText = placeholder,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "本机绝对或相对路径；留空时该来源不可用（应用时直接报错）",
        };
        ApplyLineEditTheme(line);
        return line;
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
