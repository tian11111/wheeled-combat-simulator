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
    /// <summary>灰字: 当前来源不使用的输入说明 (批4 R4.5)。</summary>
    private static readonly Color Muted = new(0.44f, 0.49f, 0.57f);

    // 对话框设计尺寸; 实际尺寸 = min(设计尺寸, 视口 × 0.9 / uiScale), 见 UpdateDialogRect。
    private const float DialogDesignWidth = 980f;
    private const float DialogDesignHeight = 620f;

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
    private Label? _visionEvidenceLabel;
    private Label? _visionCsvLabel;
    private Label? _visionProcessLabel;
    private OptionButton? _sensorProfile;
    private GridContainer? _sensorChannelGrid;
    private CheckButton? _blockCustom;
    private SpinBox? _blockBuffCount;
    private SpinBox? _blockDebuffCount;
    private OptionButton? _blockPlacement;
    private Label? _blockNote;
    // R3.3: 当前场景是否带 layoutVersion (布局编辑器产物; Open 时由 Main 传入),
    // 以及"自定义能量块布局"覆盖确认弹窗的未决标记。
    private bool _scenarioHasLayoutVersion;
    private ConfirmationDialog? _blockLayoutConfirm;
    private bool _blockLayoutConfirmPending;
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
    // 面板级共享路径选择器 (批4 R4.2): 目录/文件两种模式参数化, 一处实例服务所有
    // "浏览…"按钮 (证据包目录 / CSV / 场景 / 外观模型); 同时记录当前回填目标。
    private FileDialog? _pathDialog;
    private LineEdit? _pathDialogTarget;
    /// <summary>路径输入 → 其"浏览…"按钮 (按来源联动禁用时一起置灰)。</summary>
    private readonly Dictionary<LineEdit, Button> _pathBrowseButtons = new();
    private FileDialog? _bundleSaveDialog;
    private FileDialog? _bundleOpenDialog;

    /// <summary>
    /// 上次导出配置包的目录: 导入/再次导出的对话框跟随它打开。缺省时 Godot 文件
    /// 对话框会停在 godot/ —— robot-models.json 就在那里, 验收时被误选成配置包
    /// (2026-10-06 实测)。
    /// </summary>
    private string? _lastBundleDirectory;
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
        UpdateDialogRect();
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
        // 缩放后可用局部尺寸变化: 重新按"视口×0.9/scale"取对话框尺寸 (批4 R4.3)。
        UpdateDialogRect();
    }

    /// <param name="scenarioHasLayoutVersion">
    /// 当前场景是否带 layoutVersion (布局编辑器/布局文件产物)。批3 R3.3: 能量块页
    /// "自定义能量块布局"开启前据此弹确认, 避免静默覆盖编辑器的摆位。
    /// </param>
    public void Open(DesktopSettings settings, bool pendingSimulationChanges,
        IReadOnlyDictionary<string, RobotModelConfig>? robotModels = null,
        bool scenarioHasLayoutVersion = false)
    {
        SyncViewportRect();
        _settings = settings;
        _robotModels = robotModels ?? new Dictionary<string, RobotModelConfig>();
        _scenarioHasLayoutVersion = scenarioHasLayoutVersion;
        // 重新打开 = 上一次的未决确认作废 (视为取消), 免得残留标记把下次开关误判成已确认。
        CloseBlockLayoutConfirm(cancelEdits: true);
        UpdateBlockCustomTooltip();
        LoadControls(settings);
        if (_pendingNote is not null)
        {
            _pendingNote.Text = pendingSimulationChanges
                ? SettingsText.PendingChangesNote
                : SettingsText.NoPendingChangesNote;
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
            if (_blockLayoutConfirm?.Visible == true)
            {
                // Esc 由确认弹窗自己处理 (等同"取消", 由 Canceled 回弹开关), 不关闭设置面板。
                return;
            }
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
            MouseFilter = MouseFilterEnum.Stop,
        };
        _dialog.AddThemeStyleboxOverride("panel", MakePanelStyle(Blue));
        AddChild(_dialog);

        // 外层滚动兜底 (批4 R4.3): 视口小或 uiScale 大时对话框按视口反缩放取 min,
        // 内容超出矩形就滚动 —— 不再是"标签页内滚动、对话框本身被裁掉页脚"。
        var dialogScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        _dialog.AddChild(dialogScroll);

        var margin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        dialogScroll.AddChild(margin);

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
            // 最小高度只是"页面压缩地板" (批4 R4.3): 每页内容各自滚动, 视口/uiScale
            // 变小时标签区可以压到 260 仍可操作 —— 1280×720 @ uiScale 1.4 的对话框
            // 因此刚好放下页脚 (完整可见); 更极端的 640×360 才由外层兜底滚动接管。
            CustomMinimumSize = new Vector2(0, 260),
            TabsVisible = true,
        };
        root.AddChild(tabs);
        _tabs = tabs;
        // 每页 = 内容 + 页脚生效时机 note (批3 R3.5, 文案常量在 SettingsText)。
        AddSettingsTab(tabs, BuildDisplayPage(), "显示与窗口", SettingsText.DisplayApplyFooter);
        AddSettingsTab(tabs, BuildSimulationPage(), "仿真参数", SettingsText.AutoReloadApplyFooter);
        AddSettingsTab(tabs, BuildControllerPage(), "小车控制器", SettingsText.AutoReloadApplyFooter);
        AddSettingsTab(tabs, BuildVehiclePage(), "小车", SettingsText.AutoReloadApplyFooter);
        AddSettingsTab(tabs, BuildVisionPage(), "视觉", SettingsText.AutoReloadApplyFooter);
        AddSettingsTab(tabs, BuildBlocksPage(), "能量块", SettingsText.AutoReloadApplyFooter);
        AddSettingsTab(tabs, BuildMatchPage(), "比赛/场景", SettingsText.AutoReloadApplyFooter);

        _pendingNote = AddNoteLabel(root, SettingsText.NoPendingChangesNote, 11, Yellow);

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        root.AddChild(footer);
        _error = AddNoteLabel(footer, "", 11, Red, new Vector2(0, 36));
        _error.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        // 配置包两按钮 (批1 R1.1): 只负责选文件与转发请求, bundle 内容由 Main 收集/落盘。
        var exportBundle = MakeButton("导出配置包…", Blue, new Vector2(116, 38));
        exportBundle.TooltipText = "把主设置 / 外观模型 / 当前场景导出为单个 JSON 配置包；导出目录旁有 train-config.json 时自动附带";
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
        BuildPathDialog();
        BuildBlockLayoutConfirmDialog();
        UpdateDialogRect();
    }

    /// <summary>
    /// 对话框自适应尺寸 (批4 R4.3): min(980, 视口×0.9) × min(620, 视口×0.9) 的 UI 局部
    /// 尺寸 —— 面板整体按 uiScale 反缩放 (Scale 缩放 + PivotOffset 居中), 故局部尺寸必须
    /// 除以 scale 才是屏幕尺寸, uiScale 1.4 与 640×360 小窗下都不会超出视口。
    /// </summary>
    private void UpdateDialogRect()
    {
        if (_dialog is null || !IsInsideTree())
        {
            return;
        }
        var viewport = GetViewport().GetVisibleRect().Size;
        var scale = Mathf.Max(Scale.X, 0.1f);
        var width = Mathf.Min(DialogDesignWidth, viewport.X * 0.9f / scale);
        var height = Mathf.Min(DialogDesignHeight, viewport.Y * 0.9f / scale);
        SetCenteredRect(_dialog, width, height);
    }

    /// <summary>
    /// 把一个标签页内容包成"内容(占满) + 页脚生效时机 note" (批3 R3.5): note 固定在
    /// 标签页底部, 不随页内滚动跑掉。最小高度 22 (UI 局部单位) 保留 —— 单行页脚在
    /// 任何比例下都有稳定占位; 换行后由 AddNoteLabel 的自动换行最小高度接管。
    /// </summary>
    private static void AddSettingsTab(TabContainer tabs, Control content, string title, string footerNote)
    {
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 6);
        content.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(content);
        AddNoteLabel(page, footerNote, 11, Secondary, new Vector2(0, 22));
        tabs.AddChild(page);
        tabs.SetTabTitle(tabs.GetTabCount() - 1, title);
    }

    /// <summary>
    /// R3.3: 当前场景带 layoutVersion (布局编辑器产物) 时, 开启"自定义能量块布局"会
    /// 整体替换 scenario.Blocks, 覆盖编辑器摆位 —— 用 Godot 原生 ConfirmationDialog
    /// 显式确认。Exclusive = 弹窗期间面板不接收输入 (防重复触发/切标签), 取消 = 回弹开关。
    /// </summary>
    private void BuildBlockLayoutConfirmDialog()
    {
        _blockLayoutConfirm = new ConfirmationDialog
        {
            Title = "覆盖布局编辑器结果？",
            DialogText = "自定义能量块布局将覆盖布局编辑器的摆位：应用设置后按下面的数量与落位方式"
                + "重建双方能量块，编辑器保存/冻结的块坐标不会保留。\n\n继续开启自定义布局？",
            OkButtonText = "继续开启",
            CancelButtonText = "取消",
            Exclusive = true,
        };
        _blockLayoutConfirm.Confirmed += OnBlockLayoutConfirmAccepted;
        _blockLayoutConfirm.Canceled += OnBlockLayoutConfirmDeclined;
        AddChild(_blockLayoutConfirm);
    }

    /// <summary>
    /// 面板级共享路径选择对话框 (批4 R4.2): 目录/文件两种模式按"浏览…"按钮的意图
    /// 参数化 (FileDialog 原生字段切换), 一处实例服务场景文件 / 证据包目录 / CSV /
    /// 外观模型四类输入 (Access/Filters 用法参照 LayoutEditor.Bind)。
    /// </summary>
    private void BuildPathDialog()
    {
        _pathDialog = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            // OS 原生文件对话框 (验收拍板 2026-10-06): 与系统文件管理器观感一致。
            UseNativeDialog = true,
        };
        _pathDialog.FileSelected += path =>
        {
            if (_pathDialogTarget is not null)
            {
                _pathDialogTarget.Text = path;
            }
        };
        _pathDialog.DirSelected += path =>
        {
            if (_pathDialogTarget is not null)
            {
                _pathDialogTarget.Text = path;
            }
        };
        AddChild(_pathDialog);
    }

    /// <summary>
    /// 弹共享路径选择器 (批4 R4.2)。directory=true 选目录 (OpenDir, 走 DirSelected),
    /// 否则选文件; 已有文本且落在磁盘上时把对话框定位到那里 (相对路径先绝对化, 免得
    /// FileDialog 报无效路径)。
    /// </summary>
    private void BrowsePath(LineEdit target, bool directory, string title, string filter)
    {
        if (_pathDialog is null)
        {
            return;
        }
        _pathDialogTarget = target;
        // 顺序有讲究: FileDialog 的 FileMode setter 会重写窗口标题 (Godot 内置
        // "Open a File"/"Open a Directory"), 所以中文 Title 必须在它之后设置。
        _pathDialog.FileMode = directory
            ? FileDialog.FileModeEnum.OpenDir
            : FileDialog.FileModeEnum.OpenFile;
        _pathDialog.Filters = filter.Length == 0 ? Array.Empty<string>() : new[] { filter };
        _pathDialog.Title = title;
        var raw = target.Text.Trim();
        if (raw.Length > 0 && !raw.StartsWith("res://", StringComparison.Ordinal)
            && !raw.StartsWith("user://", StringComparison.Ordinal) && TryAbsolute(raw, out var absolute))
        {
            if (directory && Directory.Exists(absolute))
            {
                _pathDialog.CurrentDir = absolute;
            }
            else if (!directory && File.Exists(absolute))
            {
                _pathDialog.CurrentPath = absolute;
            }
        }
        _pathDialog.PopupCentered(new Vector2I(860, 620));
    }

    private static bool TryAbsolute(string raw, out string absolute)
    {
        try
        {
            absolute = Path.GetFullPath(raw);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            absolute = "";
            return false;
        }
    }

    /// <summary>
    /// 页脚"导出/导入配置包"的文件对话框 (Access/Filters 用法参照 LayoutEditor.Bind)。
    /// 导出一步完成 (验收拍板 2026-10-06): 选定落点即导出, 训练配置不弹第二步 ——
    /// 导出目录旁有 train-config.json 时自动附带 (见 FindSidecarTrainConfig)。
    /// </summary>
    private void BuildBundleDialogs()
    {
        // 注意属性初始化顺序: FileMode setter 会把 Title 重写成 Godot 内置英文标题
        // ("Save a File"/"Open a File"), 所以中文 Title 必须写在 FileMode 之后
        // (同 BrowsePath 的共享路径选择器)。
        _bundleSaveDialog = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Title = "导出配置包",
            Filters = new[] { "*.json ; 配置包 (Settings Bundle)" },
            CurrentFile = SettingsBundleStore.DefaultFileName,
            UseNativeDialog = true,
        };
        _bundleOpenDialog = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Title = "导入配置包",
            Filters = new[] { "*.json ; 配置包 (Settings Bundle)" },
            UseNativeDialog = true,
        };
        _bundleSaveDialog.FileSelected += OnBundleExportPathSelected;
        _bundleOpenDialog.FileSelected += path => ImportBundleRequested?.Invoke(path);
        AddChild(_bundleSaveDialog);
        AddChild(_bundleOpenDialog);
    }

    private void RequestExportBundle()
    {
        ClearError();
        FollowLastBundleDirectory(_bundleSaveDialog);
        _bundleSaveDialog?.PopupCentered(new Vector2I(860, 620));
    }

    private void RequestImportBundle()
    {
        ClearError();
        FollowLastBundleDirectory(_bundleOpenDialog);
        _bundleOpenDialog?.PopupCentered(new Vector2I(860, 620));
    }

    /// <summary>对话框从上次导出目录打开 (目录仍在磁盘上才跟随)。</summary>
    private void FollowLastBundleDirectory(FileDialog? dialog)
    {
        if (dialog is not null && _lastBundleDirectory is not null && Directory.Exists(_lastBundleDirectory))
        {
            dialog.CurrentDir = _lastBundleDirectory;
        }
    }

    private void OnBundleExportPathSelected(string path)
    {
        _lastBundleDirectory = Path.GetDirectoryName(path);
        ExportBundleRequested?.Invoke(path, FindSidecarTrainConfig(path));
    }

    /// <summary>
    /// 训练配置自动附带: 导出目录旁放着 train-config.json 就打进配置包 (内容与
    /// train.py --config 同一 schema), 没有则不附带。替代原先"导出后必弹第二步
    /// 选择框"的两步流程 (验收拍板 2026-10-06)。
    /// </summary>
    private static string? FindSidecarTrainConfig(string bundlePath)
    {
        var directory = Path.GetDirectoryName(bundlePath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }
        var candidate = Path.Combine(directory, "train-config.json");
        return File.Exists(candidate) ? candidate : null;
    }

    private Control BuildDisplayPage()
    {
        var scroll = MakeScrollPage("DisplaySettings", out var root);
        AddLabel(root, "渲染窗口", 16, Primary);
        AddNoteLabel(root, "沿用 1280×720 设计视口，窗口尺寸只改变显示比例，不改变仿真几何。", 11, Secondary);

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
        // 宽高/模式的 tooltip 与可用性由 UpdateDisplayInputs 统一维护 (全屏联动)。
        AddLabel(grid, "窗口模式", 12, Secondary);
        _windowMode = MakeOption(("窗口化", DisplayModes.Windowed), ("全屏", DisplayModes.Fullscreen));
        grid.AddChild(_windowMode);
        AddLabel(grid, "界面缩放", 12, Secondary);
        _uiScale = MakeSpin(0.8, 1.4, 0.05, "x");
        _uiScale.TooltipText =
            "桌面界面缩放（0.8–1.4×）：只影响 HUD/设置窗等桌面 UI，不进入 Scenario、Snapshot 或回放指纹。";
        grid.AddChild(_uiScale);
        // R4.5 低优先级项: 全屏时宽高被忽略, 输入框联动禁用 (不再"可编辑但无效")。
        _windowMode.ItemSelected += _ => UpdateDisplayInputs();

        AddNoteLabel(root,
            "提示：全屏下仍按屏幕比例缩放控制台；界面缩放只影响桌面 UI，不进入 Scenario、Snapshot 或回放指纹。",
            12, Blue);
        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return scroll;
    }

    /// <summary>
    /// 页内滚动页 (批4 R4.3): 每页内容各自可滚动, 标签区被压缩时内容仍可达
    /// (HorizontalScrollMode=Disabled 保持窄宽度下的列宽语义)。
    /// </summary>
    private static ScrollContainer MakeScrollPage(string name, out VBoxContainer root)
    {
        var scroll = new ScrollContainer
        {
            Name = name,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        root.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(root);
        return scroll;
    }

    /// <summary>显示页联动 (批4 R4.5): 全屏时窗口宽高被忽略, 输入框禁用并说明原因。</summary>
    private void UpdateDisplayInputs()
    {
        var fullscreen = GetSelectedId(_windowMode, DisplayModes.Windowed) == DisplayModes.Fullscreen;
        if (_windowMode is not null)
        {
            _windowMode.TooltipText = fullscreen
                ? "全屏：按屏幕比例缩放控制台，窗口宽高被忽略（仍存档，供切回窗口化时恢复）。"
                : "窗口化：按下面的宽高创建窗口；切换为全屏后宽高输入会被禁用。";
        }
        if (_width is not null)
        {
            _width.Editable = !fullscreen;
            _width.TooltipText = fullscreen
                ? "全屏模式不使用窗口宽度（输入已禁用）；切回窗口化后生效。"
                : "窗口化模式下的渲染窗口宽度（640–7680 px）；全屏时忽略，仅存档。";
        }
        if (_height is not null)
        {
            _height.Editable = !fullscreen;
            _height.TooltipText = fullscreen
                ? "全屏模式不使用窗口高度（输入已禁用）；切回窗口化后生效。"
                : "窗口化模式下的渲染窗口高度（360–4320 px）；全屏时忽略，仅存档。";
        }
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
        AddNoteLabel(root,
            "只保存你明确修改的参数；“自动”表示沿用 Sim.Core 默认值。实验性参数用于标定和回放复现，请谨慎使用。",
            11, Secondary);
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

        var warning = AddNoteLabel(section,
            "改动影响碰撞判定，回放身份会失配：开启态录制的 legacy 回放只有在相同的 L1/L2/L3 组合下才可复现。"
            + "默认全开 = 现行为；仅 legacy 后端消费这三个开关（mujoco 不看）。改动保存后自动重开当前对局。",
            11, Yellow, new Vector2(0, 44));
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
        AddLabel(root, SettingsText.ControllerTitle, 16, Primary);
        AddNoteLabel(root,
            "使用外部命令/脚本通过既有 JSONL stdio 协议控制小车，不启动 Godot 内嵌代码编辑器。外部进程拥有本机权限，请只运行可信代码。",
            11, Yellow);
        root.AddChild(BuildControllerSection("我方 / BLUE（RL 展演）", RoleNames.Us,
            SettingsText.ControllerCommandPlaceholderUs));
        root.AddChild(BuildControllerSection("对手 / RED", RoleNames.Them,
            SettingsText.ControllerCommandPlaceholderThem));
        AddNoteLabel(root,
            "协议：每行输入 observation JSON，输出 {\"v\":...,\"w\":...,\"requestId\":...}；超时或坏行会安全回退为零动作并显示 fault。",
            11, Secondary);
        // 我方 external 的新语义（RL 展演）与边界：与 Main 的装配决策同一口径。
        AddNoteLabel(root,
            "我方 external = SCORE_BLOCK 展演（RL 策略）：只在 physics.backend=mujoco 的场景启用；"
            + "legacy 场景会被拒绝并回退内置 FSM（设置仍保存，换回 mujoco 场景重应用即恢复）。"
            + "应用设置时自动预检一次（启动→握手→立刻释放）；预检不覆盖首帧模型加载时间，建议超时 ≥ 5000 ms。"
            + "控制器子进程以 godot/ 为工作目录，相对脚本路径写 ../tools/rl-bridge/rl_desktop_runner.py。"
            + "展演为非门禁证据（不写回放、不晋升 fidelity）。",
            11, Yellow);
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
        mode.TooltipText = "内置 FSM：常规策略；内置 MBri：MBri 移植（场景内控制器）；"
            + "外部命令：按 JSONL stdio 启动子进程，超时/坏行回退零动作。";
        row.AddChild(mode);
        AddLabel(row, "超时", 11, Secondary, new Vector2(36, 0));
        var timeout = MakeSpin(1, 5000, 1, "ms");
        timeout.CustomMinimumSize = new Vector2(120, 32);
        timeout.TooltipText = "外部控制器单帧应答超时（1–5000 ms）：超时按零动作回退并在 HUD 标 fault。";
        row.AddChild(timeout);

        var command = new LineEdit
        {
            PlaceholderText = commandPlaceholder ?? SettingsText.ControllerCommandPlaceholderThem,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "外部控制器启动命令；留空时使用内置 FSM。子进程工作目录为 godot/。",
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
        // 档位取义按 id (批4 R4.5), 下标只表示 UI 顺序。
        var modeId = GetSelectedId(mode, ControllerModes.BuiltIn);
        if (modeId != ControllerModes.External)
        {
            var label = role == RoleNames.Us ? _usPreflightResult : _themPreflightResult;
            if (label is not null)
            {
                label.Text = modeId == ControllerModes.Mbri
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
            // R4.1: tooltip 用目录里的中文说明 (含影响方向与单位), 不再暴露内部键名。
            label.TooltipText = definition.Description;
            var input = MakeSpin(definition.Minimum, definition.Maximum, definition.Step, definition.Unit);
            input.CustomMinimumSize = new Vector2(160, 32);
            input.AllowLesser = true;
            input.AllowGreater = true;
            input.TooltipText = $"{definition.Description} 范围 {ParameterRangeText(definition)}，默认 {FormatNumber(definition.DefaultValue)}。";
            row.AddChild(input);
            // 单位只在 SpinBox 的 Suffix 上出现一次 (批4 R4.1: 删掉重复的单位列)。
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
                    TooltipText = "勾选 = 不写入该覆盖，沿用 Sim.Core 默认值；取消勾选后才保存这里的数值。",
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

    /// <summary>参数范围文本 (开区间用 &gt;/&lt;, 与 Validate/回放身份同一语义), 供 tooltip 用。</summary>
    private static string ParameterRangeText(SimulationParameterDefinition definition)
    {
        var lower = definition.MinimumExclusive
            ? $"> {FormatNumber(definition.Minimum)}"
            : $"≥ {FormatNumber(definition.Minimum)}";
        var upper = definition.MaximumExclusive
            ? $"< {FormatNumber(definition.Maximum)}"
            : $"≤ {FormatNumber(definition.Maximum)}";
        return $"{lower} 且 {upper} {definition.Unit}";
    }

    /// <summary>数值文本: 去掉多余小数零 (0.10 → 0.1, 400.0 → 400)。</summary>
    private static string FormatNumber(double value)
        => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

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
            SelectById(_windowMode, settings.Window?.Mode);
        }
        UpdateDisplayInputs();
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
            // SetPressedNoSignal: 回填不是用户操作, 不得触发 R3.3 的覆盖确认弹窗
            // (UpdateBlockInputs 紧跟其后显式刷新)。
            _blockCustom.SetPressedNoSignal(blocks is not null && !blocks.IsFollowScenario);
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
            SelectById(_blockPlacement,
                blocks?.RandomPositions == true ? BlockPlacementModes.Random : BlockPlacementModes.Official);
        }
        UpdateBlockInputs();

        if (_sensorProfile is not null)
        {
            SelectById(_sensorProfile, settings.Vehicle?.SensorProfileId);
        }
        // 打开面板/恢复默认: 从设置回填, 不带任何未应用的旧编辑 (preserveEdits: false)。
        RebuildSensorChannelRows(preserveEdits: false);

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
            SelectById(_visionSource, vision.Source);
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
            SelectById(_matchBackend, match.PhysicsBackendOverride);
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
        // 档位取义按 id (批4 R4.5): 下标只表示 UI 顺序。
        SelectById(mode, profile.Mode);
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
        // 未决的覆盖确认等同取消 (R3.3): 不把未经确认的自定义布局带进本次草稿。
        CloseBlockLayoutConfirm(cancelEdits: true);
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
                Mode = GetSelectedId(_windowMode, DisplayModes.Windowed),
            },
            UiScale = _uiScale?.Value ?? 1.0,
            SimulationParameters = values,
            BlockLayout = _blockCustom is { ButtonPressed: true }
                ? new BlockLayoutSettings
                {
                    BuffCount = (int)(_blockBuffCount?.Value ?? 2),
                    DebuffCount = (int)(_blockDebuffCount?.Value ?? 1),
                    RandomPositions = GetSelectedId(_blockPlacement, BlockPlacementModes.Official)
                        == BlockPlacementModes.Random,
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
            // R3.4: 校验消息本体保持结构化英文 (DesktopSettings.Validate 不动), 显示层中文化。
            ShowError(string.Join("\n", errors.Select(LocalizeValidationError)));
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
            Mode = GetSelectedId(mode, ControllerModes.BuiltIn),
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
        // 批3 R3.1/R3.5: 小车设置已计入变更检测, 保存即自动重开当前对局 —— 不再写"下一场或 F5"。
        AddNoteLabel(page,
            "比赛双方同款真车的物理规格；应用于 v2 真车几何场景。改动保存后自动重开当前对局生效（回放/布局编辑中为下一场）。",
            11, Secondary);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        page.AddChild(grid);

        AddLabel(grid, "整车质量（含电池/电机/主控）", 12, Secondary);
        _vehicleMass = MakeSpin(0.2, 20, 0.05, "kg");
        _vehicleMass.TooltipText =
            "整车质量（0.2–20 kg）：写入 us/them 的 VehicleProfile.Mass，影响碰撞与惯性；只在 mujoco v2 场景生效。";
        grid.AddChild(_vehicleMass);

        AddLabel(grid, "电机减速后转速（空载）", 12, Secondary);
        _vehicleRpm = MakeSpin(10, 2000, 1, "RPM");
        _vehicleRpm.TooltipText =
            "减速箱输出空载转速（10–2000 RPM）：与轮径一起推导轮端极速（MaxSpeed）；只在 mujoco v2 场景生效。";
        grid.AddChild(_vehicleRpm);

        AddLabel(grid, "电机输出扭矩（额定）", 12, Secondary);
        _vehicleTorque = MakeSpin(0.05, 50, 0.01, "N·m");
        _vehicleTorque.TooltipText =
            "减速箱输出额定扭矩（0.05–50 N·m）：当前仿真为速度伺服，该值仅存档（不进对局）；后续力矩级建模再消费。";
        grid.AddChild(_vehicleTorque);

        AddLabel(grid, "驱动轮半径（装配实测）", 12, Secondary);
        _vehicleWheelRadius = MakeSpin(0.005, 0.1, 0.0001, "m");
        _vehicleWheelRadius.TooltipText =
            "驱动轮半径（0.005–0.1 m）：与转速一起推导轮端极速；只在 mujoco v2 场景生效。";
        grid.AddChild(_vehicleWheelRadius);

        foreach (var spin in new[] { _vehicleMass, _vehicleRpm, _vehicleTorque, _vehicleWheelRadius })
        {
            spin.ValueChanged += _ => UpdateVehicleNote();
        }

        _vehicleNote = AddNoteLabel(page, "", 12, Blue, new Vector2(0, 44));
        UpdateVehicleNote();

        AddLabel(page, "传感器覆盖", 16, Primary);
        AddNoteLabel(page,
            "以预设为基底克隆自定义 profile 写入双方车辆：整路禁用（读数恒为下限，FSM 门限不触发）"
            + "或按车体系偏移挂点（实车“挪探头”标定语义，dx=前向 / dy=横向 / dz=高度 / dyaw=朝向）。"
            + "切换预设会保留当前未应用的通道编辑（同名通道保留编辑，其余回上次保存值或基底默认）。",
            11, Secondary);

        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 8);
        page.AddChild(presetRow);
        AddLabel(presetRow, "传感器预设", 12, Secondary, new Vector2(88, 0));
        _sensorProfile = MakeOption(
            ("跟随场景（不改）", ""),
            ("真车 11 路（wheeledCombat11）", SensorProfiles.WheeledCombat11.Id),
            ("兼容 14 路（legacy14）", SensorProfiles.Legacy14.Id));
        _sensorProfile.TooltipText =
            "跟随场景 = 不改双方车辆自带 profile（逐位不变）；显式预设会以该 profile 为基底，叠加下面的通道禁用/偏移。";
        presetRow.AddChild(_sensorProfile);
        // 切换预设: 保留当前控件上未应用的通道编辑 (批3 R3.2)。
        _sensorProfile.ItemSelected += _ => RebuildSensorChannelRows(preserveEdits: true);

        _sensorBaseNote = AddNoteLabel(page, "", 11, Blue, new Vector2(0, 22));

        _sensorChannelGrid = new GridContainer { Columns = 6 };
        _sensorChannelGrid.AddThemeConstantOverride("h_separation", 6);
        _sensorChannelGrid.AddThemeConstantOverride("v_separation", 4);
        page.AddChild(_sensorChannelGrid);

        AddLabel(page, "外观模型（渲染层）", 16, Primary);
        AddNoteLabel(page,
            "us/them 的 glb/gltf 外观绑定：只改渲染，不影响仿真；留空回退 primitive 分件。"
            + "应用后写入 robot-models.json 并立即生效。模型约定：车头 +Z、原点在地面，"
            + "scale / yawOffset / heightOffset 三个修正项。",
            11, Secondary);
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
        var path = MakePathRow(SettingsText.ModelPathPlaceholder(role), "选择外观模型（*.glb / *.gltf）",
            SettingsText.ModelFilter, directory: false, out var pathInput);
        pathInput.TooltipText =
            "glb/gltf 外观模型路径；留空 = primitive 分件。相对路径按进程工作目录解析"
            + "（`--path godot` 启动时即 godot/）；res:// 走已导入资源。只改渲染，不影响仿真。";
        box.AddChild(path);
        _modelPathInputs[role] = pathInput;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddLabel(row, "缩放", 11, Secondary, new Vector2(40, 0));
        var scale = MakeSpin(0.05, 10, 0.01, "x");
        scale.CustomMinimumSize = new Vector2(120, 32);
        scale.TooltipText = "模型缩放修正（0.05–10×）：只作用于导入的外观节点，不影响仿真几何。";
        row.AddChild(scale);
        AddLabel(row, "朝向偏移", 11, Secondary, new Vector2(64, 0));
        var yaw = MakeSpin(-2 * Math.PI, 2 * Math.PI, 0.01, "rad");
        yaw.CustomMinimumSize = new Vector2(150, 32);
        yaw.TooltipText = "绕 Z 轴朝向修正（rad，±2π）：模型车头 +Z 与实际车头对齐用。";
        row.AddChild(yaw);
        AddLabel(row, "高度偏移", 11, Secondary, new Vector2(64, 0));
        var height = MakeSpin(-0.2, 0.5, 0.001, "m");
        height.CustomMinimumSize = new Vector2(150, 32);
        height.TooltipText = "竖直修正（m，-0.2–0.5）：模型原点默认在地面，抬升/下压用。";
        row.AddChild(height);
        box.AddChild(row);
        _modelTransformInputs[role] = new[] { scale, yaw, height };
        return box;
    }

    /// <summary>当前预设选择对应的 profile id (null = 跟随场景, 档位 id 为空串)。</summary>
    private string? SelectedSensorProfileId()
    {
        var id = GetSelectedId(_sensorProfile, "");
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// 按当前预设选择重建通道行 (启用勾选 + dx/dy/dz/dyaw 偏移)。
    /// preserveEdits=true (用户切换预设): 保存档打底、当前控件上的未应用编辑覆盖其上,
    /// 同名通道保留编辑值 —— 不再静默丢掉刚敲的偏移/禁用; 合成规则在
    /// SensorChannelEdits.Merge (纯逻辑, Sim.Tests 回归)。保存档打底保证"切到别的基底
    /// 再切回来"仍回上次保存值, 而不是被清成基底默认。
    /// preserveEdits=false (打开面板/恢复默认): 只用保存档 (_settings.Vehicle)。
    /// “跟随场景”的基底随场景自带 profile, UI 按 legacy14 展示通道清单 (与解析端 fallback 一致)。
    /// </summary>
    private void RebuildSensorChannelRows(bool preserveEdits)
    {
        if (_sensorChannelGrid is null)
        {
            return;
        }
        var presetId = SelectedSensorProfileId();
        var baseProfile = presetId == SensorProfiles.WheeledCombat11.Id
            ? SensorProfiles.WheeledCombat11
            : SensorProfiles.Legacy14;

        var merged = SensorChannelEdits.Merge(
            SavedChannelEdits(),
            preserveEdits ? CurrentChannelRowEdits() : null,
            baseProfile);

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
            var edit = merged.TryGetValue(channel.Id, out var kept)
                ? kept
                : SensorChannelEdit.Default;
            var enabled = new CheckButton
            {
                FocusMode = FocusModeEnum.None,
                TooltipText = "取消勾选 = 整路禁用：读数恒为下限，FSM 门限不触发（实车拔探头语义）。",
            };
            enabled.ButtonPressed = edit.Enabled;
            ApplyCheckButtonTheme(enabled);
            _sensorChannelGrid.AddChild(enabled);

            var name = AddLabel(_sensorChannelGrid, $"{channel.Id} · {channel.Label}", 11, Primary, new Vector2(170, 0));
            name.TooltipText = $"通道 {channel.Id} · {channel.Label}；id 用于设置文件与协议，改名会让旧覆盖失配。";
            name.ClipText = true;

            var dx = MakeOffsetSpin(_sensorChannelGrid, edit.Dx, -0.5, 0.5, 0.001, "m",
                "挂点前向偏移 dx（-0.5–0.5 m，车体系）：正 = 探头前移。");
            var dy = MakeOffsetSpin(_sensorChannelGrid, edit.Dy, -0.5, 0.5, 0.001, "m",
                "挂点横向偏移 dy（-0.5–0.5 m，车体系）：正 = 探头左移。");
            var dz = MakeOffsetSpin(_sensorChannelGrid, edit.Dz, -0.2, 0.2, 0.001, "m",
                "挂点高度偏移 dz（-0.2–0.2 m）：实车垫高/压低探头用。");
            var dyaw = MakeOffsetSpin(_sensorChannelGrid, edit.Yaw, -Math.PI, Math.PI, 0.01, "rad",
                "挂点朝向偏移 dyaw（rad，±π）：实车探头转角标定，0 = 保持基底朝向。");
            _sensorChannelRows.Add((channel.Id, enabled, dx, dy, dz, dyaw));
        }
    }

    /// <summary>当前设置里已保存的通道禁用/偏移 (未覆盖的通道不进表 = 基底默认)。</summary>
    private Dictionary<string, SensorChannelEdit> SavedChannelEdits()
    {
        var edited = new Dictionary<string, SensorChannelEdit>(StringComparer.Ordinal);
        var vehicle = _settings.Vehicle ?? new VehicleSettings();
        var disabledIds = vehicle.SensorDisabled ?? new List<string>();
        var disabled = new HashSet<string>(disabledIds, StringComparer.Ordinal);
        foreach (var (channelId, offset) in vehicle.SensorOffsets ?? new Dictionary<string, SensorOffset>())
        {
            edited[channelId] = new SensorChannelEdit(!disabled.Contains(channelId),
                offset.Dx, offset.Dy, offset.Dz, offset.Yaw);
        }
        foreach (var channelId in disabledIds)
        {
            edited.TryAdd(channelId, new SensorChannelEdit(false, 0, 0, 0, 0));
        }
        return edited;
    }

    /// <summary>当前通道行控件上的值 (切换预设时的"未应用编辑"来源)。</summary>
    private Dictionary<string, SensorChannelEdit> CurrentChannelRowEdits()
    {
        var edited = new Dictionary<string, SensorChannelEdit>(StringComparer.Ordinal);
        foreach (var row in _sensorChannelRows)
        {
            edited[row.ChannelId] = new SensorChannelEdit(row.Enabled.ButtonPressed,
                row.Dx.Value, row.Dy.Value, row.Dz.Value, row.Yaw.Value);
        }
        return edited;
    }

    private static SpinBox MakeOffsetSpin(GridContainer grid, double value,
        double min, double max, double step, string suffix, string tooltip)
    {
        var spin = MakeSpin(min, max, step, suffix);
        spin.CustomMinimumSize = new Vector2(110, 30);
        spin.TooltipText = tooltip;
        spin.Value = value;
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
        _vehicleNote.Text = SettingsText.VehicleNote(rpm, maxSpeed);
    }

    /// <summary>
    /// 能量块设置页(2026-10-04): 自定义开关关闭 = 跟随场景(逐位不变); 开启后可调
    /// 增益/减益数量与落位方式。应用写入 DesktopSettings.BlockLayout, 应用后自动重开
    /// 当前对局生效; 当前场景带 layoutVersion 时开启前先确认覆盖 (批3 R3.3)。
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
        AddNoteLabel(page, SettingsText.BlocksIntro, 11, Secondary);

        _blockCustom = new CheckButton { Text = "自定义能量块布局", FocusMode = FocusModeEnum.None };
        ApplyCheckButtonTheme(_blockCustom);
        UpdateBlockCustomTooltip();
        _blockCustom.Toggled += OnBlockCustomToggled;
        page.AddChild(_blockCustom);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 10);
        page.AddChild(grid);

        AddLabel(grid, "增益块数量", 12, Secondary);
        _blockBuffCount = MakeSpin(0, Scenario.MaxBlocks, 1, "个");
        _blockBuffCount.TooltipText = $"台上的增益块数量（0–{Scenario.MaxBlocks}）：被推上台我方 +3 分；合计超上限时按增益优先截断。";
        grid.AddChild(_blockBuffCount);
        AddLabel(grid, "减益块数量", 12, Secondary);
        _blockDebuffCount = MakeSpin(0, Scenario.MaxBlocks, 1, "个");
        _blockDebuffCount.TooltipText = $"台上的减益块数量（0–{Scenario.MaxBlocks}）：被推上台对方 +6 分；合计超上限时按增益优先截断。";
        grid.AddChild(_blockDebuffCount);
        AddLabel(grid, "落位方式", 12, Secondary);
        _blockPlacement = MakeOption(
            ("官方坐标优先，多出的由裁判随机放置", BlockPlacementModes.Official),
            ("全部随机位置（裁判按种子放置）", BlockPlacementModes.Random));
        _blockPlacement.TooltipText =
            "官方坐标优先 = 前两个增益/第一个减益用官方冻结坐标；全部随机 = 所有块由裁判按种子确定性放置"
            + "（禁区：避台沿 0.35 m / 避两车 0.8 m / 避中央 0.6 m / 块间 0.5 m）。";
        grid.AddChild(_blockPlacement);

        _blockBuffCount.ValueChanged += _ => UpdateBlockNote();
        _blockDebuffCount.ValueChanged += _ => UpdateBlockNote();
        _blockPlacement.ItemSelected += _ => UpdateBlockNote();

        _blockNote = AddNoteLabel(page, "", 12, Blue, new Vector2(0, 44));
        UpdateBlockInputs();

        page.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return scroll;
    }

    /// <summary>
    /// R3.3: "自定义能量块布局"开关的行为。当前场景带 layoutVersion (布局编辑器产物)
    /// 且是往"开启"方向切换 → 先弹确认; 取消则回弹开关 (SetPressedNoSignal, 不递归)。
    /// 弹窗未决期间再触发只保持开关状态, 不叠加第二个弹窗。
    /// </summary>
    private void OnBlockCustomToggled(bool pressed)
    {
        if (pressed && _scenarioHasLayoutVersion && !_blockLayoutConfirmPending
            && _blockLayoutConfirm is not null)
        {
            _blockLayoutConfirmPending = true;
            _blockLayoutConfirm.PopupCentered(new Vector2I(600, 240));
        }
        UpdateBlockInputs();
    }

    private void OnBlockLayoutConfirmAccepted()
    {
        _blockLayoutConfirmPending = false;
        UpdateBlockInputs();
    }

    private void OnBlockLayoutConfirmDeclined()
    {
        CloseBlockLayoutConfirm(cancelEdits: true);
    }

    /// <summary>
    /// 收掉未决的覆盖确认弹窗。cancelEdits=true = 取消语义 (用户点取消 / 面板关闭 /
    /// 重新打开面板 / 应用或恢复默认): 回弹开关, 不把未确认的覆盖静默带进草稿。
    /// </summary>
    private void CloseBlockLayoutConfirm(bool cancelEdits)
    {
        _blockLayoutConfirm?.Hide();
        if (!_blockLayoutConfirmPending)
        {
            return;
        }
        _blockLayoutConfirmPending = false;
        if (cancelEdits)
        {
            _blockCustom?.SetPressedNoSignal(false);
            UpdateBlockInputs();
        }
    }

    /// <summary>R3.3: 开关 tooltip 随当前场景来源更新 (Build 时还未收到场景状态)。</summary>
    private void UpdateBlockCustomTooltip()
    {
        if (_blockCustom is null)
        {
            return;
        }
        _blockCustom.TooltipText = _scenarioHasLayoutVersion
            ? "当前场景来自布局编辑器：开启前会确认是否覆盖编辑器的摆位"
            : "关闭时跟随场景/官方布局；开启后自定义数量与落位（应用后自动重开当前对局生效）";
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
            _blockNote.Text = SettingsText.BlocksFollowNote;
            return;
        }
        var buffs = (int)(_blockBuffCount?.Value ?? 2);
        var debuffs = (int)(_blockDebuffCount?.Value ?? 1);
        var total = buffs + debuffs;
        var placement = GetSelectedId(_blockPlacement, BlockPlacementModes.Official) == BlockPlacementModes.Random
            ? SettingsText.BlockPlacementRandomText
            : SettingsText.BlockPlacementOfficialText;
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
        AddNoteLabel(page,
            "只在明确覆盖时改场景字段；跟随档与现状逐位一致。应用设置后自动重开当前对局生效（回放/布局编辑中为下一场或 F5）。",
            11, Secondary, new Vector2(0, 30));

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
        var scenarioRow = MakePathRow("留空 = 跟随启动场景", "选择场景文件（*.json）",
            SettingsText.ScenarioFilter, directory: false, out var scenarioInput);
        _matchScenarioPath = scenarioInput;
        _matchScenarioPath.TooltipText =
            "场景 JSON 文件路径；留空 = 跟随启动场景。相对路径按 CWD → 仓库根依次尝试"
            + "（与 --scenario-path 同规则）；修改后应用设置即重载（回放/布局编辑中为下一场或 F5）。";
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

        _matchNote = AddNoteLabel(page, "", 12, Blue, new Vector2(0, 70));
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
    private string? SelectedMatchBackend()
    {
        var id = GetSelectedId(_matchBackend, MatchBackendOverrides.Follow);
        return id == MatchBackendOverrides.Follow ? null : id;
    }

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
        var scroll = MakeScrollPage("VisionSettings", out var page);
        AddLabel(page, "视觉源", 16, Primary);
        AddNoteLabel(page,
            "四选一：默认识别率模型不注入外部源（行为与既有比赛逐位一致）；证据包回放与实时 CSV 桥读取本机文件；"
            + "外部推理进程每场启动子进程消费 stdout JSONL。改动保存后自动重开当前对局生效。",
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
        _visionSource.TooltipText =
            "默认识别率 = 引擎内部随机桩（逐位不变）；证据包回放 = 哈希锁定读包；"
            + "实时 CSV 桥 = 真车检测流按仿真时间释放；外部推理进程 = 每场启动子进程消费 stdout JSONL。";
        grid.AddChild(_visionSource);

        _visionEvidenceLabel = AddLabel(grid, SettingsText.VisionEvidenceTitle, 12, Secondary);
        var evidenceRow = MakePathRow(SettingsText.VisionEvidencePlaceholder, "选择证据包目录（含 frames.jsonl + import-report.json）",
            filter: "", directory: true, out _visionEvidencePath);
        _visionEvidencePath.TooltipText =
            "visionReplay 的证据包目录（frames.jsonl + import-report.json）；相对路径以进程工作目录为基准"
            + "（`--path godot` 启动时即 godot/，与场景文件的 CWD→仓库根规则不同），缺文件/哈希不符会在应用设置时直接报错。";
        grid.AddChild(evidenceRow);

        _visionCsvLabel = AddLabel(grid, SettingsText.VisionCsvTitle, 12, Secondary);
        var csvRow = MakePathRow(SettingsText.VisionCsvPlaceholder, "选择真车 CSV（MBri 73 列方言）",
            SettingsText.CsvFilter, directory: false, out _visionCsvPath);
        _visionCsvPath.TooltipText =
            "liveBridge 的真车 MBri hunt 方言 CSV（73 列）；相对路径以进程工作目录为基准"
            + "（`--path godot` 启动时即 godot/），路径/方言不可用会在应用设置时直接报错。";
        grid.AddChild(csvRow);

        _visionProcessLabel = AddLabel(grid, SettingsText.VisionProcessTitle, 12, Secondary);
        // 命令行是文本（含参数），不给文件对话框 (批4 R4.2 范围)。
        _visionProcessCommand = MakePathInput(SettingsText.VisionProcessPlaceholder);
        _visionProcessCommand.TooltipText =
            "外部推理进程命令行（含参数）：每场新起子进程消费 stdout JSONL，子进程必须逐帧 flush；"
            + "应用设置时先预检启动一次并回收，坏命令当场报错。相对路径按进程工作目录（godot/）解析。";
        grid.AddChild(_visionProcessCommand);

        AddLabel(grid, "帧过期窗口", 12, Secondary);
        _visionMaxAge = MakeSpin(1, 5000, 1, "ms");
        _visionMaxAge.TooltipText = "帧过期窗口（1–5000 ms）：旧于窗口的帧按 stale（unknown）处理，默认 500 ms 与 CLI 同值。";
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

        _visionNote = AddNoteLabel(page, "", 12, Blue, new Vector2(0, 44));
        UpdateVisionInputs();

        page.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        return scroll;
    }

    /// <summary>
    /// 只让当前来源用到的输入可编辑；不相关的路径框保留可见 (避免布局跳动) 但禁用,
    /// 标签追加灰字“（当前来源不使用）”说明 (批4 R4.5) —— 不再让用户把禁用误读成坏掉。
    /// </summary>
    private void UpdateVisionInputs()
    {
        var source = SelectedVisionSource();
        UpdateVisionInput(_visionEvidencePath, _visionEvidenceLabel, SettingsText.VisionEvidenceTitle,
            source == VisionSources.VisionReplay);
        UpdateVisionInput(_visionCsvPath, _visionCsvLabel, SettingsText.VisionCsvTitle,
            source == VisionSources.LiveBridge);
        UpdateVisionInput(_visionProcessCommand, _visionProcessLabel, SettingsText.VisionProcessTitle,
            source == VisionSources.LiveProcess);
        if (_visionMaxAge is not null)
        {
            _visionMaxAge.Editable = source != VisionSources.ClassifyRate;
        }
        UpdateVisionNote();
    }

    private void UpdateVisionInput(LineEdit? input, Label? label, string title, bool used)
    {
        if (input is not null)
        {
            input.Editable = used;
            if (_pathBrowseButtons.TryGetValue(input, out var browse))
            {
                browse.Disabled = !used;
            }
        }
        if (label is not null)
        {
            label.Text = used ? title : $"{title}{SettingsText.NotUsedByCurrentSource}";
            label.AddThemeColorOverride("font_color", used ? Secondary : Muted);
        }
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

    private string SelectedVisionSource() => GetSelectedId(_visionSource, VisionSources.ClassifyRate);

    /// <summary>
    /// "恢复默认": 主设置回 <see cref="DesktopSettings.Default"/> 之外, 外观模型区控件
    /// 同时归零 (路径空 / scale 1 / 偏移 0, 批3 R3.6) —— 只改草稿标记, 落盘仍走"应用设置"
    /// (那时 ReadRobotModelsDraft 为空 = 清除外观绑定)。
    /// </summary>
    private void RestoreDefaults()
    {
        CloseBlockLayoutConfirm(cancelEdits: true);
        _settings = DesktopSettings.Default;
        LoadControls(_settings);
        ResetRobotModelControls();
        ClearError();
    }

    /// <summary>外观模型区控件归零 (R3.6; 实际清除要等"应用设置"写 robot-models.json)。</summary>
    private void ResetRobotModelControls()
    {
        foreach (var input in _modelPathInputs.Values)
        {
            input.Text = "";
        }
        foreach (var transforms in _modelTransformInputs.Values)
        {
            transforms[0].Value = 1.0;
            transforms[1].Value = 0;
            transforms[2].Value = 0;
        }
    }

    private void Cancel()
    {
        // 面板关闭时未决的覆盖确认等同取消 (R3.3), 弹窗不得残留在已关闭的面板下。
        CloseBlockLayoutConfirm(cancelEdits: true);
        Visible = false;
        ClearError();
        Cancelled?.Invoke();
    }

    /// <summary>
    /// 校验错误显示层中文化 (批3 R3.4): 映射表在 SettingsValidationMessages (无 Godot
    /// 依赖, Sim.Tests 逐条断言 + 源扫描漏项报警)。未识别消息原文兜底。
    /// </summary>
    private static string LocalizeValidationError(string error)
        => SettingsValidationMessages.Localize(error);

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

    /// <summary>路径输入框: 只做文本编辑, 校验与读取留给应用时 (tooltip 由调用方按语义补)。</summary>
    private static LineEdit MakePathInput(string placeholder)
    {
        var line = new LineEdit
        {
            PlaceholderText = placeholder,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        ApplyLineEditTheme(line);
        return line;
    }

    /// <summary>
    /// 路径输入行 (批4 R4.2): 文本框 + "浏览…"按钮, 共用面板级 FileDialog。目录/文件
    /// 两种模式与标题/过滤器由调用方参数化 (目录走 DirSelected, 文件走 FileSelected);
    /// 文本读取仍走返回的 <paramref name="input"/>。
    /// </summary>
    private HBoxContainer MakePathRow(string placeholder, string browseTitle, string filter,
        bool directory, out LineEdit input)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        input = MakePathInput(placeholder);
        row.AddChild(input);
        var browse = MakeButton("浏览…", Blue, new Vector2(76, 34));
        browse.TooltipText = browseTitle;
        var target = input;
        browse.Pressed += () => BrowsePath(target, directory, browseTitle, filter);
        row.AddChild(browse);
        _pathBrowseButtons[input] = browse;
        return row;
    }

    private static OptionButton MakeOption(params (string Label, string Id)[] items)
    {
        var option = new OptionButton
        {
            CustomMinimumSize = new Vector2(150, 34),
            FocusMode = FocusModeEnum.All,
        };
        // (label, id) 表按 UI 顺序登记到 Meta (批4 R4.5): 读取端由 GetSelectedId 取义,
        // 加/删/换序选项不会再让下标语义错位。
        var ids = new string[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            option.AddItem(items[i].Label);
            ids[i] = items[i].Id;
        }
        option.SetMeta(SettingsOptionIds.MetaKey, ids);
        ApplyOptionTheme(option);
        return option;
    }

    /// <summary>控件登记的 id 表 (MakeOption 写入 Meta; 缺失 = 空表, 走 fallback)。</summary>
    private static string[] OptionIds(OptionButton? option)
        => option is not null && option.HasMeta(SettingsOptionIds.MetaKey)
            ? option.GetMeta(SettingsOptionIds.MetaKey).AsStringArray()
            : [];

    /// <summary>当前选中项的 id (批4 R4.5): 下标只表示 UI 顺序, 取义一律走这里。</summary>
    private static string GetSelectedId(OptionButton? option, string fallback)
        => SettingsOptionIds.IdAt(OptionIds(option), option?.Selected ?? 0, fallback);

    /// <summary>把设置值映射成下拉选中下标 (未收录/缺省 = 首档, 即既有默认档)。</summary>
    private static void SelectById(OptionButton? option, string? id)
        => option?.Select(SettingsOptionIds.IndexOf(OptionIds(option), id));

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

    /// <summary>
    /// 说明/提示行 (批4 R4.3): 自动换行 + <c>ClipText=false</c>。
    /// 坑: Godot 4.7 的 Label 在"AutowrapMode=WordSmart + ClipText=true"组合下
    /// get_minimum_size 退化为 (1,1) —— 放进 VBox 就是 0 高度, 说明文字实际不显示
    /// (批2 起用"最小高度 22"局部规避)。关掉 ClipText 后最小高度按换行后的行数计算,
    /// 长说明不再被截断; 需要单行裁剪的定宽标签仍用 AddLabel。
    /// </summary>
    private static Label AddNoteLabel(Container parent, string text, int fontSize, Color color,
        Vector2? minimumSize = null)
    {
        var label = AddLabel(parent, text, fontSize, color, minimumSize);
        label.ClipText = false;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
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
        // 视口尺寸是对话框自适应尺寸的输入 (批4 R4.3): 每帧/每步缩放都对一次。
        UpdateDialogRect();
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
