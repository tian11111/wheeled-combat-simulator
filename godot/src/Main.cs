// 桌面壳入口: 组装 MatchSession / ArenaVisualizer / HudPanel / MatchCamera,
// 把裁判指令 (发令/暂停/重启/重置/回放导航) 路由到 Sim.Core, 渲染层只消费
// SnapshotView 投影, 不复刻任何规则。
//
// 操作 (另见 HUD 右上角帮助):
//   Enter 发令 · P 暂停/继续 · R 我方重启 · T 对手重启 (真实重启, 对手 +3)
//   F5 重置同 seed 比赛 (回放模式回到实况并同步场景/相机) · C 切换镜头 · L 打开回放文件
//   镜头: 非编辑模式左键拖动转动视角 (概览/跟随环绕, 俯视自旋), 右键拖动平移 (抓取语义),
//         滚轮缩放 (限幅)
//   回放模式: 空格 播放/暂停 · ←/→ 单步 · Home/End 到首/末帧 · 拖动时间轴跳转
//
// 无头模式: `godot --headless --path godot -- --parity-check <replay.json>`
// 使用与 Sim.Cli replay-check 相同的语义比对最终比分/结束原因/末帧/事件指纹。
// `--edit-smoke` / `--camera-smoke` 为无人值守交互冒烟 (见下文)。

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class Main : Node
{
    /// <summary>确定性种子; 与 Sim.Cli 相同种子产生相同比赛。</summary>
    [Export]
    public long Seed { get; set; } = 42;

    /// <summary>可选场景文件路径 (scenarios/*.json); 为空时使用官方默认布局。</summary>
    [Export]
    public string ScenarioPath { get; set; } = "";

    /// <summary>启动时自动加载的回放文件路径 (可选)。</summary>
    [Export]
    public string ReplayPath { get; set; } = "";

    private MatchSession _session = null!;
    private ArenaVisualizer _visualizer = null!;
    private HudPanel _hud = null!;
    private MatchCamera _camera = null!;
    private LayoutEditor _editor = null!;
    private FileDialog _fileDialog = null!;
    private SettingsStore _settingsStore = null!;
    private SettingsPanel _settingsPanel = null!;
    private DesktopSettings _settings = DesktopSettings.Default;
    // 视觉源工厂: 与 _settings.Vision 同步重建; null = 默认 classifyRate 桩(不注入
    // adapter, 行为逐位不变)。每场(ReplaceSession/ResetLiveSession/驱动)都调一次
    // 工厂新建适配器 —— 台账与 SimT 0 基准不跨场复用。
    private Func<IVisionAdapter?>? _visionFactory;
    private Scenario _scenarioTemplate = null!;
    // 比赛/场景页的落点 (批2 R2.1):
    // - _startupScenarioPath = 启动场景来源 (--scenario-path 或空=官方布局), "跟随启动
    //   场景"档恢复时用; 运行时导入/布局编辑器只改 ScenarioPath, 不动这里。
    // - _startupScenarioTemplate = 启动场景原始模板 (无桌面覆盖), 同上。
    // - _appliedMatchScenarioPath = 设置里已生效的场景覆盖路径 (空 = 跟随), 用于区分
    //   "用户改了场景文件"与"点了一次应用"。
    private string _startupScenarioPath = "";
    private Scenario? _startupScenarioTemplate;
    private string _appliedMatchScenarioPath = "";
    // 设置里的场景覆盖文件读坏/不存在: 本次运行内降级为启动场景, 不反复重试卡启动。
    private bool _matchOverrideScenarioFailed;
    private DesktopLiveDriver? _liveDriver;
    private Snapshot? _driverSnapshot;
    // 控制器装配（我方 external = SCORE_BLOCK 展演）: 决策/解析在 ControllerWiring
    // 纯文件, 应用设置时预检一次(结论缓存在 _usPreflight, 供后续场次复用)。
    private ControllerAssignment _controllerAssignment = ControllerAssignment.BuiltIn;
    private ControllerPreflightResult? _usPreflight;
    private bool _pendingMatchSettings;
    private Dictionary<string, RobotModelConfig>? _robotModels;
    private string? _robotModelsPath;
    private double _replayAlphaAccumulator;
    // 暂停/收尾时插值 alpha 的收敛速率 (由旧实现 0.02/帧 @60fps 折算, 与帧率解耦)。
    private const double ReplayAlphaSettlePerSecond = 1.2;
    private int _captureFramesLeft = -1;
    private string _capturePath = "";
    private string _captureStats = "";
    private int _settingsSmokeFramesLeft = -1;
    private int _smokeExit;
    private int _visualFrameStatsLeft = -1;
    private int _visualFrameStatsCount;
    private double _visualFrameStatsSumMs;
    private double _visualFrameStatsMinMs = double.PositiveInfinity;
    private double _visualFrameStatsMaxMs;
    // 事件栏累积缓冲: 引擎快照的事件是增量 (自上次提交), 直接显示会闪现一帧
    // 即清空; 这里跨帧保留最近 N 条, 模式切换/场景重建时清空。
    private readonly List<string> _eventBuffer = [];
    private long _lastEventTick = -1;
    private SessionMode _lastEventMode;

    public override void _Ready()
    {
        if (TryRunParityCheck())
        {
            return;
        }

        _visualizer = GetNode<ArenaVisualizer>("ArenaVisualizer");
        _hud = GetNode<HudPanel>("Hud/HudPanel");
        _camera = GetNode<MatchCamera>("Camera3D");
        SetupDefaultFont();
        LoadDesktopSettings();
        BuildFileDialog();

        var userArgs = OS.GetCmdlineUserArgs();
        var spIndex = Array.IndexOf(userArgs, "--scenario-path");
        if (spIndex >= 0 && spIndex + 1 < userArgs.Length)
        {
            ScenarioPath = ResolveUserPath(userArgs[spIndex + 1]);
        }
        // 批2"比赛/场景"页的"跟随启动场景"档锚点 (后续导入/编辑器/覆盖都只改
        // 运行时 ScenarioPath, 不动启动档)。
        _startupScenarioPath = ScenarioPath;

        ApplyVisualQaOverrides(userArgs);
        ConfigureVisualFrameStats(userArgs);

        var scenario = BuildScenarioWithFallback();
        ReplaceSession(scenario);
        ApplyScenarioToShell(scenario);
        // 控制器装配（加载的持久化设置 + 当前场景）: 我方 external 只在 mujoco 场景
        // 启用展演; 应用时预检一次, 拒绝/回退响亮报出来 (同视觉源先例)。
        _controllerAssignment = RebuildControllerWiring(scenario, probeExternal: true);
        PublishControllerWiring();

        _editor = new LayoutEditor { Name = "LayoutEditor" };
        AddChild(_editor);
        _editor.Bind(_camera, _visualizer);
        _editor.Applied += ApplyLayoutScenario;
        _editor.Closed += RestoreShellScenario;
        _hud.ConfigureEditor(
            onApply: () => _editor.RequestApply(),
            onUndo: () => _editor.RequestUndo(),
            onRedo: () => _editor.RequestRedo(),
            onRestore: () => _editor.RequestRestoreOfficial(),
            onOpen: () => _editor.RequestOpen(),
            onSave: () => _editor.RequestSave(),
            onClose: () => _editor.Close());

        _settingsPanel = new SettingsPanel { Name = "SettingsPanel" };
        // The modal must be a direct CanvasLayer child. HudPanel is a layout
        // control for the anchored cards, not the full-screen input root.
        GetNode<CanvasLayer>("Hud").AddChild(_settingsPanel);
        _settingsPanel.SetUiScale(_settings.UiScale);
        _settingsPanel.Applied += ApplyDesktopSettings;
        _settingsPanel.RobotModelsApplied += SaveRobotModels;
        // 配置包 (批1): 面板只选文件, 内容收集/校验/落盘都在壳层。
        _settingsPanel.ExportBundleRequested += ExportSettingsBundle;
        _settingsPanel.ImportBundleRequested += ImportSettingsBundle;
        // 批2 "换种子重开": 写种子覆盖 + 立即按 F5 语义重建 (回放/编辑中挂待生效)。
        _settingsPanel.RestartWithSeedRequested += RestartWithSeed;
        _settingsPanel.PreflightCompleted += (role, ok, message)
            => _hud.ShowPreflightNotice(role, ok, message);
        _hud.ConfigureSettings(OpenSettings);

        _hud.ConfigureTimeline(tick => _session.ReplaySeekTick(tick));

        var replayArgIndex = Array.IndexOf(userArgs, "--replay-path");
        var autoReplay = replayArgIndex >= 0 && replayArgIndex + 1 < userArgs.Length
            ? userArgs[replayArgIndex + 1]
            : ReplayPath;
        if (!string.IsNullOrEmpty(autoReplay))
        {
            LoadReplay(autoReplay);
            var rtIndex = Array.IndexOf(userArgs, "--replay-tick");
            if (rtIndex >= 0 && rtIndex + 1 < userArgs.Length && long.TryParse(userArgs[rtIndex + 1], out var tick))
            {
                _session.ReplaySeekTick(tick);
                GD.Print($"[shell] --replay-tick: 跳到 tick {tick}");
            }
        }

        var settingsSmoke = Array.IndexOf(userArgs, "--settings-smoke") >= 0;
        var smokeMode = Array.IndexOf(userArgs, "--camera-smoke") >= 0
            || Array.IndexOf(userArgs, "--edit-smoke") >= 0
            || settingsSmoke;
        if (_session.Mode == SessionMode.Live && !smokeMode)
        {
            StartLiveDriverIfConfigured(scenario);
        }

        if (string.IsNullOrEmpty(autoReplay) && Array.IndexOf(userArgs, "--auto-arm") >= 0)
        {
            ArmLive();
            GD.Print("[shell] --auto-arm: 已发令进入 RUNNING");
        }

        var captureIndex = Array.IndexOf(userArgs, "--capture");
        if (captureIndex >= 0 && captureIndex + 1 < userArgs.Length)
        {
            _capturePath = ResolveUserPath(userArgs[captureIndex + 1]);
            if (settingsSmoke)
            {
                // The settings smoke has no asynchronous assertion routine;
                // render the modal for the normal settling window, then save.
                _captureFramesLeft = 30;
                GD.Print($"[capture] 设置页渲染 30 帧后保存到 {_capturePath}");
            }
            else if (Array.IndexOf(userArgs, "--edit-smoke") >= 0
                || Array.IndexOf(userArgs, "--camera-smoke") >= 0)
            {
                // 冒烟模式自己控制截图时机 (结束后统一倒计时); 提前倒计时会把冒烟中途杀掉。
                GD.Print($"[capture] 冒烟结束后保存视口到 {_capturePath}");
            }
            else
            {
                // 默认 30 帧; --capture-frames 可加长等待 (相机阻尼收敛/比赛推进后再截)。
                _captureFramesLeft = 30;
                var framesIndex = Array.IndexOf(userArgs, "--capture-frames");
                if (framesIndex >= 0 && framesIndex + 1 < userArgs.Length
                    && int.TryParse(userArgs[framesIndex + 1], out var frames) && frames > 0)
                {
                    _captureFramesLeft = frames;
                }
                GD.Print($"[capture] {_captureFramesLeft} 帧后保存视口到 {_capturePath}");
            }
        }

        // 视觉 QA 取景辅助: 启动即切换镜头模式 (0=概览 1=跟随 2=俯视), 仅表现层,
        // 供双分辨率 capture 留存三种机位证据; 不改变任何交互/仿真语义。
        var cameraCycleIndex = Array.IndexOf(userArgs, "--camera-cycle");
        if (cameraCycleIndex >= 0 && cameraCycleIndex + 1 < userArgs.Length
            && int.TryParse(userArgs[cameraCycleIndex + 1], out var cameraCycles))
        {
            for (var i = 0; i < Math.Min(Math.Max(cameraCycles, 0), 2); i++)
            {
                _camera.CycleMode();
            }
            GD.Print($"[capture] 启动镜头模式: {_camera.Mode}");
        }

        // 视觉 QA 取景辅助: 启动即设置概览环绕角 "<yaw>,<pitch>" (度), 复现"左键
        // 拖动后"的机位供 capture 证据留存; 与拖动共享同一状态与限幅 (MatchCamera.
        // SetOverviewOrbit), 仅表现层, 不注入输入事件、不触碰仿真。
        var cameraOrbitIndex = Array.IndexOf(userArgs, "--camera-orbit");
        if (cameraOrbitIndex >= 0 && cameraOrbitIndex + 1 < userArgs.Length)
        {
            var parts = userArgs[cameraOrbitIndex + 1].Split(',');
            if (parts.Length == 2
                && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var orbitYaw)
                && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var orbitPitch))
            {
                _camera.SetOverviewOrbit(orbitYaw, orbitPitch);
                GD.Print($"[capture] 启动概览环绕角: yaw={_camera.OverviewYaw:0.#}° pitch={_camera.OverviewPitch:0.#}°");
            }
            else
            {
                GD.PrintErr($"[capture] --camera-orbit 需要 <yaw>,<pitch> 度数, 收到: {userArgs[cameraOrbitIndex + 1]}");
            }
        }

        LoadRobotModelPreferences(userArgs);
        ApplyRobotModels();

        if (settingsSmoke)
        {
            OpenSettings();
            // --settings-tab 支持稳定页名与中文页名 (批2 新增"比赛/场景"页; 旧下标仍兼容):
            // 例如 --settings-tab match / --settings-tab "比赛/场景" / --settings-tab 6。
            // 中文按 TabContainer 标题全等匹配, 不认简称 (--settings-tab 比赛 会报未知页)。
            var tabIndex = Array.IndexOf(userArgs, "--settings-tab");
            if (tabIndex >= 0 && tabIndex + 1 < userArgs.Length)
            {
                _settingsPanel.SelectTab(userArgs[tabIndex + 1]);
            }
            if (_capturePath.Length == 0)
            {
                // Without --capture this remains a short UI construction smoke
                // and does not leave a generated artifact in the repository.
                _settingsSmokeFramesLeft = 30;
            }
            GD.Print("[settings-smoke] 设置面板已打开; 使用 --capture <png> 可保存真实渲染截图");
            return;
        }

        if (Array.IndexOf(userArgs, "--edit-smoke") >= 0)
        {
            _ = RunEditSmokeAsync();
            return;
        }

        if (Array.IndexOf(userArgs, "--camera-smoke") >= 0)
        {
            _ = RunCameraSmokeAsync();
            return;
        }

        GD.Print($"[shell] core={MatchEngine.CoreVersion} seed={scenario.Seed}"
            + $" tick={scenario.Field.TickSeconds}s duration={scenario.Field.MatchDuration}s"
            + $" mode={_session.Mode}");
    }

    /// <summary>
    /// Applies presentation-only A/B switches before the first scenario render.
    /// These flags deliberately live in the shell; they never enter Scenario,
    /// Snapshot, replay hashes, or Sim.Core state.
    /// </summary>
    private void ApplyVisualQaOverrides(string[] userArgs)
    {
        var baseline = Array.IndexOf(userArgs, "--visual-baseline") >= 0;
        var environment = GetNode<WorldEnvironment>("WorldEnvironment").Environment;
        if (environment is not null)
        {
            if (baseline || Array.IndexOf(userArgs, "--visual-no-sdfgi") >= 0)
            {
                environment.SdfgiEnabled = false;
            }
            if (baseline || Array.IndexOf(userArgs, "--visual-no-fog") >= 0)
            {
                environment.VolumetricFogEnabled = false;
            }
            if (baseline || Array.IndexOf(userArgs, "--visual-no-glow") >= 0)
            {
                environment.GlowEnabled = false;
            }
        }

        if (_camera.Attributes is CameraAttributesPractical practical
            && (baseline || Array.IndexOf(userArgs, "--visual-no-dof") >= 0))
        {
            practical.DofBlurFarEnabled = false;
        }

        _visualizer.MaterialDetailEnabled = !baseline
            && Array.IndexOf(userArgs, "--visual-no-material-noise") < 0;
        GD.Print($"[visual-qa] sdfgi={environment?.SdfgiEnabled ?? false} "
            + $"fog={environment?.VolumetricFogEnabled ?? false} "
            + $"glow={environment?.GlowEnabled ?? false} "
            + $"dof={_camera.Attributes is CameraAttributesPractical dof && dof.DofBlurFarEnabled} "
            + $"materialNoise={_visualizer.MaterialDetailEnabled}");
    }

    private void ConfigureVisualFrameStats(string[] userArgs)
    {
        var index = Array.IndexOf(userArgs, "--visual-frame-stats");
        if (index >= 0 && index + 1 < userArgs.Length
            && int.TryParse(userArgs[index + 1], out var frames) && frames > 0)
        {
            _visualFrameStatsLeft = Math.Clamp(frames, 1, 10_000);
            GD.Print($"[visual-qa] frame stats: {_visualFrameStatsLeft} frames");
        }
    }

    // ---------- automated camera smoke (--camera-smoke) ----------

    /// <summary>True when running on the headless dummy display (64×64 input surface).</summary>
    private static bool IsHeadlessDisplay => DisplayServer.GetName() == "headless";

    private static void InjectWheel(int steps)
    {
        // steps > 0 = 滚轮下滚 (拉远), steps < 0 = 上滚 (拉近)。
        var button = steps > 0 ? MouseButton.WheelDown : MouseButton.WheelUp;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = button,
                Pressed = true,
                Position = Vector2.Zero,
                GlobalPosition = Vector2.Zero,
            });
        }
    }

    private void InjectLeftDrag(Vector2 from, Vector2 to)
        => InjectButtonDrag(from, to, MouseButton.Left);

    private void InjectRightDrag(Vector2 from, Vector2 to)
        => InjectButtonDrag(from, to, MouseButton.Right);

    /// <summary>
    /// Injects a press/move/release drag. <paramref name="from"/>/
    /// <paramref name="to"/> are viewport-canvas coordinates (the space
    /// <c>Camera3D.UnprojectPosition</c> reports); ParseInputEvent expects
    /// window-surface coordinates, so convert by the stretch ratio: the real
    /// window's client size over the visible rect, or the 64×64 headless dummy
    /// input surface against the 1280px design canvas (1/20).
    /// </summary>
    private void InjectButtonDrag(Vector2 from, Vector2 to, MouseButton button)
    {
        var inputScale = IsHeadlessDisplay
            ? new Vector2(1f / 20f, 1f / 20f)
            : (Vector2)DisplayServer.WindowGetSize() / GetViewport().GetVisibleRect().Size;
        from *= inputScale;
        to *= inputScale;
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = button,
            Pressed = true,
            Position = from,
            GlobalPosition = from,
        });
        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Position = to,
            GlobalPosition = to,
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = button,
            Pressed = false,
            Position = to,
            GlobalPosition = to,
        });
    }

    /// <summary>
    /// Deterministic camera input evidence through the real input pipeline:
    /// Overview framing, wheel zoom (×1.1 steps + clamp), Top orientation
    /// (-90° pitch, full-field coverage), left-drag orbit/spin under the
    /// reversed-direction contract (right/left/down/up drags each asserted;
    /// Top spins at fixed pitch), right-drag ground-plane grab pan in
    /// Top/Overview, Follow zoom, and the editor-ownership hook (the camera
    /// must ignore the pointer while the layout editor is active). No texture
    /// reads, so it is safe headless. Exits 0 when all checks pass.
    /// </summary>
    private async Task RunCameraSmokeAsync()
    {
        var failures = new List<string>();
        void Check(bool ok, string step)
        {
            if (!ok)
            {
                step += $" [focus=({_camera.FocusPoint.X:0.000},{_camera.FocusPoint.Z:0.000})"
                    + $" mode={_camera.Mode} dist={_camera.OverviewDistance:0.00} topH={_camera.TopHeight:0.00}"
                    + $" phase={_session.Engine.Phase} tick={_session.Engine.TickIndex}]";
            }
            GD.Print($"[camera-smoke] {(ok ? "ok" : "FAIL")} {step}");
            if (!ok)
            {
                failures.Add(step);
            }
        }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-3;

        var fieldSize = _session.Scenario.Field.FieldSize;
        var model = new Sim.Core.FieldModel(_session.Scenario.Field);
        var (cx, cyy) = model.CenterWorld;
        var center = new Vector3((float)cx, 0f, (float)cyy);

        static Vector3? GroundPointAt(Camera3D cam, Vector2 pos)
        {
            var from = cam.ProjectRayOrigin(pos);
            var dir = cam.ProjectRayNormal(pos);
            if (Mathf.Abs(dir.Y) < 1e-6f)
            {
                return null;
            }
            var t = -from.Y / dir.Y;
            return t <= 0f ? null : from + dir * t;
        }

        // 与 MatchCamera.OrbitDir 相同的环绕方向公式 (yaw 0 = +Z, 俯仰自地面起算)。
        static Vector3 OrbitDirExpected(float yawDeg, float pitchDeg)
        {
            var yaw = Mathf.DegToRad(yawDeg);
            var pitch = Mathf.DegToRad(pitchDeg);
            return new Vector3(
                Mathf.Sin(yaw) * Mathf.Cos(pitch),
                Mathf.Sin(pitch),
                Mathf.Cos(yaw) * Mathf.Cos(pitch));
        }
        var viewportSize = GetViewport().GetVisibleRect().Size;
        // canvas_items + aspect=expand 在 dummy headless 驱动下会把可见矩形
        // 报成基准高度对应的方形，但 ParseInputEvent 仍按 64×64 dummy 视口
        // 接收坐标；使用固定 dummy 中心，避免 smoke 的 8px 拖动被放大。
        var screenCenter = IsHeadlessDisplay ? new Vector2(32f, 32f) : viewportSize / 2f;

        // Overview: 焦点在场地中心, 机位 = 焦点 + (0,0.82,0.68) 归一化 × 距离
        // (与 MatchCamera.DefaultOverviewHeight/BackRatio 同一取景比例)。
        Check(_camera.Mode == CameraMode.Overview, "starts in Overview");
        await WaitFrames(3);
        Check(Near(_camera.FocusPoint.X, center.X) && Near(_camera.FocusPoint.Z, center.Z),
            "overview focus = arena center");
        var expectedPos = center + new Vector3(0, 0.82f, 0.68f).Normalized() * _camera.OverviewDistance;
        Check(_camera.Position.DistanceTo(expectedPos) < 0.05f,
            "overview position = focus + framing direction * distance");

        // PRD R1 取景占比: 完整场地包围盒 (含围栏顶) 投影到 16:9 视口 (720p/1080p,
        // vfov 75° KEEP_HEIGHT) 的宽/高占比必须落在 45–65% × 45–75%。用纯针孔投影
        // 从相机实际位姿计算, 不依赖无头 64×64 视口; 数值与真实 renderer capture 实测一致。
        static (double W, double H) ArenaExtentFraction(Camera3D cam, Vector3 c, double half, double fenceTop)
        {
            var basis = cam.GlobalTransform.Basis;
            var fwd = -basis.Column2;
            var tanV = Math.Tan(cam.Fov * Math.PI / 360.0);
            var tanH = tanV * 16.0 / 9.0;
            double minX = 2, maxX = -2, minY = 2, maxY = -2;
            foreach (var p in new[]
            {
                new Vector3((float)(c.X - half), 0f, (float)(c.Z - half)),
                new Vector3((float)(c.X + half), 0f, (float)(c.Z - half)),
                new Vector3((float)(c.X - half), 0f, (float)(c.Z + half)),
                new Vector3((float)(c.X + half), 0f, (float)(c.Z + half)),
                new Vector3((float)(c.X - half), (float)fenceTop, (float)(c.Z - half)),
                new Vector3((float)(c.X + half), (float)fenceTop, (float)(c.Z - half)),
                new Vector3((float)(c.X - half), (float)fenceTop, (float)(c.Z + half)),
                new Vector3((float)(c.X + half), (float)fenceTop, (float)(c.Z + half)),
            })
            {
                var v = p - cam.GlobalPosition;
                var zv = v.Dot(fwd);
                // 不做 [-1,1] 钳制: 角点越出视锥时占比必须 >1 而判 FAIL,
                // 钳制会把越界角点拉回视口内, 掩盖"场地被裁掉"的回归。
                // 默认取景下所有角点都在相机前方 ~4m, 不存在退化投影。
                var nx = v.Dot(basis.Column0) / zv / tanH;
                var ny = v.Dot(basis.Column1) / zv / tanV;
                minX = Math.Min(minX, nx); maxX = Math.Max(maxX, nx);
                minY = Math.Min(minY, ny); maxY = Math.Max(maxY, ny);
            }
            return ((maxX - minX) / 2, (maxY - minY) / 2);
        }
        var extent = ArenaExtentFraction(_camera, center, fieldSize / 2, _session.Scenario.Field.FenceHeight);
        Check(extent.W is >= 0.45 and <= 0.65 && extent.H is >= 0.45 and <= 0.75,
            $"overview frames arena at {extent.W * 100:0.0}% x {extent.H * 100:0.0}% of 16:9 viewport (PRD 45-65% x 45-75%)");

        // 滚轮缩放: ×1.1 步进 + 限幅 (放大钳在基准, 缩小钳在上限)。
        var baseDistance = _camera.OverviewDistance;
        InjectWheel(+1);
        await WaitFrames(2);
        Check(Near(_camera.OverviewDistance, baseDistance * 1.1), "wheel down zooms out ×1.1");
        InjectWheel(-1);
        await WaitFrames(2);
        Check(Near(_camera.OverviewDistance, baseDistance), "wheel up zooms in back to base");
        for (var i = 0; i < 40; i++)
        {
            InjectWheel(+1);
        }
        await WaitFrames(2);
        var clamped = _camera.OverviewDistance;
        for (var i = 0; i < 5; i++)
        {
            InjectWheel(+1);
        }
        await WaitFrames(2);
        Check(Near(_camera.OverviewDistance, clamped), "overview zoom clamps at max distance");
        for (var i = 0; i < 60; i++)
        {
            InjectWheel(-1);
        }
        await WaitFrames(2);
        Check(Near(_camera.OverviewDistance, baseDistance * 0.3f), "overview zoom clamps at min distance");

        // 左键转动视角 (2026-08-29 实机反馈反向修正契约): 右拖减小偏航 (-0.3°/px),
        // 下拖抬高俯仰 (+0.25°/px), 机位按 OrbitDir(yaw, pitch) × 距离重建
        // (阻尼收敛后取值)。右/左/下/上四个方向分别断言, 防止把旧方向固定回来。
        // 拖动用小像素步长: 无头 dummy 视口只有 64×64, 大位移会撞上限幅, 破坏等值断言。
        const int dragPx = 8;
        const float yawPerPx = 0.3f;
        const float pitchPerPx = 0.25f;
        await WaitSettled();
        var yaw0 = _camera.OverviewYaw;
        var pitch0 = _camera.OverviewPitch;
        var dist0 = _camera.OverviewDistance;
        InjectLeftDrag(screenCenter, screenCenter + new Vector2(dragPx, 0)); // 右拖
        await WaitFrames(2);
        Check(Near(_camera.OverviewYaw, yaw0 - dragPx * yawPerPx), "left-drag right orbits yaw (-0.3°/px, reversed)");
        await WaitSettled();
        var orbitExpected = center + OrbitDirExpected(_camera.OverviewYaw, pitch0) * dist0;
        Check(_camera.Position.DistanceTo(orbitExpected) < 0.05f,
            "overview orbit position matches yaw/pitch formula");
        InjectLeftDrag(screenCenter, screenCenter - new Vector2(dragPx, 0)); // 左拖
        await WaitFrames(2);
        Check(Near(_camera.OverviewYaw, yaw0), "left-drag left restores yaw (+0.3°/px, symmetric)");
        InjectLeftDrag(screenCenter, screenCenter + new Vector2(0, dragPx)); // 下拖
        await WaitFrames(2);
        Check(Near(_camera.OverviewPitch, Mathf.Clamp(pitch0 + dragPx * pitchPerPx, 10f, 85f)),
            "left-drag down raises pitch (+0.25°/px, reversed)");
        InjectLeftDrag(screenCenter, screenCenter - new Vector2(0, dragPx)); // 上拖
        await WaitFrames(2);
        Check(Near(_camera.OverviewPitch, pitch0), "left-drag up lowers pitch (-0.25°/px, symmetric)");
        Check(Near(_camera.OverviewYaw, yaw0) && Near(_camera.OverviewPitch, pitch0),
            "four-direction drags end at the default orbit angles");

        // Follow: 缩放只改跟拍距离; 焦点仍由渲染帧驱动。
        await InjectActionUntil("camera_cycle", () => _camera.Mode == CameraMode.Follow);
        Check(_camera.Mode == CameraMode.Follow, "cycles to Follow");
        var followZoom = _camera.FollowZoom;
        InjectWheel(+1);
        await WaitFrames(2);
        Check(Near(_camera.FollowZoom, followZoom * 1.1), "follow wheel zooms the follow offset");
        InjectWheel(-1);
        await WaitFrames(2);
        Check(Near(_camera.FollowZoom, followZoom), "follow zoom back to 1×");

        // Follow 左键环绕: 与概览同一 RotateView 反向契约 (AC1 把 Follow 列入四方向)。
        var followYaw0 = _camera.FollowYaw;
        var followPitch0 = _camera.FollowPitch;
        InjectLeftDrag(screenCenter, screenCenter + new Vector2(dragPx, 0)); // 右拖
        await WaitFrames(2);
        Check(Near(_camera.FollowYaw, followYaw0 - dragPx * yawPerPx),
            "follow left-drag right orbits yaw (-0.3°/px, reversed)");
        InjectLeftDrag(screenCenter, screenCenter - new Vector2(dragPx, 0)); // 左拖
        await WaitFrames(2);
        Check(Near(_camera.FollowYaw, followYaw0), "follow left-drag left restores yaw");
        InjectLeftDrag(screenCenter, screenCenter + new Vector2(0, dragPx)); // 下拖
        await WaitFrames(2);
        Check(Near(_camera.FollowPitch, Mathf.Clamp(followPitch0 + dragPx * pitchPerPx, 10f, 85f)),
            "follow left-drag down raises pitch (+0.25°/px, reversed)");
        InjectLeftDrag(screenCenter, screenCenter - new Vector2(0, dragPx)); // 上拖
        await WaitFrames(2);
        Check(Near(_camera.FollowPitch, followPitch0), "follow left-drag up lowers pitch (symmetric)");

        // Top: 绕 X 轴 -90° 正俯视, 高度覆盖完整场地, 焦点在场地中心。
        await InjectActionUntil("camera_cycle", () => _camera.Mode == CameraMode.Top);
        Check(_camera.Mode == CameraMode.Top, "cycles to Top");
        Check(Near(_camera.RotationDegrees.X, -90f), "top pitches -90° (straight down)");
        var halfTan = Math.Tan(_camera.Fov * Math.PI / 360.0);
        Check(_camera.TopHeight * halfTan >= fieldSize * 0.7071 - 0.01,
            $"top covers full field (height {_camera.TopHeight:0.00} m)");
        Check(Near(_camera.FocusPoint.X, center.X) && Near(_camera.FocusPoint.Z, center.Z),
            "top focus = arena center");

        // Top 左键自旋: 绕视线轴转图 (俯仰保持 -90° 正俯视), 反向契约右拖 -0.3°/px。
        InjectLeftDrag(screenCenter, screenCenter + new Vector2(dragPx, 0));
        await WaitFrames(2);
        Check(Near(_camera.TopYaw, -dragPx * 0.3f), "top left-drag spins view (-0.3°/px, reversed)");
        Check(Near(_camera.RotationDegrees.X, -90f), "top spin keeps straight-down pitch");
        InjectLeftDrag(screenCenter + new Vector2(dragPx, 0), screenCenter);
        await WaitFrames(2);
        Check(Near(_camera.TopYaw, 0f), "inverse top spin restores heading");

        // Top 右键拖动: 抓取语义 — 焦点按地面射线位移的反方向平移。
        await WaitSettled();
        var topS1 = screenCenter;
        var topS2 = screenCenter + new Vector2(dragPx, 0);
        var tg1 = GroundPointAt(_camera, topS1);
        var tg2 = GroundPointAt(_camera, topS2);
        Check(tg1 is not null && tg2 is not null, "top rays hit the ground plane");
        if (tg1 is { } g1 && tg2 is { } g2)
        {
            InjectRightDrag(topS1, topS2);
            await WaitFrames(2);
            Check(Near(_camera.FocusPoint.X, center.X - (g2.X - g1.X))
                && Near(_camera.FocusPoint.Z, center.Z - (g2.Z - g1.Z)),
                "top right-drag pans focus opposite the ground delta (grab semantics)");
            // 拖回原位。
            InjectRightDrag(topS2, topS1);
            await WaitFrames(2);
            Check(Near(_camera.FocusPoint.X, center.X) && Near(_camera.FocusPoint.Z, center.Z),
                "top right-drag back restores the center focus");
        }

        // 俯视缩放限幅。
        for (var i = 0; i < 40; i++)
        {
            InjectWheel(+1);
        }
        await WaitFrames(2);
        var topClamped = _camera.TopHeight;
        for (var i = 0; i < 5; i++)
        {
            InjectWheel(+1);
        }
        await WaitFrames(2);
        Check(Near(_camera.TopHeight, topClamped), "top height clamps at max");

        // Overview 右键拖动: 同样的抓取语义 (世界跟随光标)。等待阻尼收敛后再取地面射线,
        // 保证预期值与事件冲洗时的机位一致。
        await InjectActionUntil("camera_cycle", () => _camera.Mode == CameraMode.Overview);
        await WaitSettled();
        Check(_camera.Mode == CameraMode.Overview, "cycles back to Overview");
        var ovS1 = screenCenter;
        var ovS2 = screenCenter + new Vector2(0, dragPx);
        var og1 = GroundPointAt(_camera, ovS1);
        var og2 = GroundPointAt(_camera, ovS2);
        if (og1 is { } og1v && og2 is { } og2v)
        {
            InjectRightDrag(ovS1, ovS2);
            await WaitFrames(2);
            var focusNow = _camera.FocusPoint;
            Check(Near(focusNow.X, center.X - (og2v.X - og1v.X))
                && Near(focusNow.Z, center.Z - (og2v.Z - og1v.Z)),
                "overview right-drag pans focus with grab semantics");
        }

        // 编辑器拥有鼠标时相机让位: 左键旋转/右键平移都不再生效。先等机位收敛, 再 F5 复位
        // (实况新比赛, TickIndex=0 才允许进入编辑模式 — 复位后同步进入, 不留 tick 窗口),
        // 然后注入拖动验证相机不消费指针。
        await WaitSettled();
        _session.ResetToLive();
        ApplyScenarioToShell(_session.Engine.Scenario);
        TryToggleEditor();
        Check(_editor.Active, "editor active (owns the pointer)");
        var focusBeforeEditorDrag = _camera.FocusPoint;
        var yawBeforeEditorDrag = _camera.OverviewYaw;
        InjectRightDrag(screenCenter, screenCenter + new Vector2(dragPx, dragPx / 2));
        InjectLeftDrag(screenCenter, screenCenter + new Vector2(dragPx, dragPx / 2));
        await WaitFrames(2);
        Check(Near(_camera.FocusPoint.X, focusBeforeEditorDrag.X)
            && Near(_camera.FocusPoint.Z, focusBeforeEditorDrag.Z)
            && Near(_camera.OverviewYaw, yawBeforeEditorDrag),
            "camera ignores pointer while layout editor is active");
        await InjectActionUntil("editor_toggle", () => !_editor.Active);
        Check(!_editor.Active, "editor closed");

        Check(failures.Count == 0,
            failures.Count == 0 ? "all checks passed" : $"failures: {string.Join(" | ", failures)}");
        GD.Print($"[camera-smoke] end state: mode={_camera.Mode} dist={_camera.OverviewDistance:0.00} topH={_camera.TopHeight:0.00} pos={_camera.Position}");
        if (_capturePath.Length == 0)
        {
            _capturePath = Path.GetFullPath("docs/desktop-camerasmoke-720.png");
        }
        // 30 帧而非 2 帧: 编辑器段落里做过 ResetToLive, 等 ResetToLive 后首个 tick
        // 提交, HUD 显示真实实况状态而不是复位瞬间的空帧。
        _captureFramesLeft = 30;
        _smokeExit = failures.Count == 0 ? 0 : 1;

        async Task WaitFrames(int n)
        {
            for (var i = 0; i < n; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }

        // 阻尼收敛等待: 概览/跟随机位按 lerp 滑向目标, 地面射线断言必须等机位稳定,
        // 否则预期值取自旧机位 (无头 64×64 视口下尤其敏感)。
        async Task WaitSettled(int maxFrames = 240)
        {
            for (var i = 0; i < maxFrames; i++)
            {
                var prev = _camera.Position;
                await WaitFrames(1);
                if (_camera.Position.DistanceTo(prev) < 1e-4f)
                {
                    return;
                }
            }
        }

        // 注入动作并等待效果落地: 输入缓冲的冲洗时机在无头下有 1-2 帧抖动,
        // 固定等 2 帧会偶发竞态; 以可观察状态到位为准 (上限 60 帧)。
        async Task InjectActionUntil(string action, Func<bool> applied, int maxFrames = 60)
        {
            Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
            await WaitFrames(1);
            Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
            for (var i = 0; !applied() && i < maxFrames; i++)
            {
                await WaitFrames(1);
            }
        }
    }

    // ---------- automated layout-editor smoke (--edit-smoke) ----------

    /// <summary>
    /// Drives the real editor stack without a human: enter edit mode, rotate
    /// via injected key actions, undo/redo, select+drag zone and field through
    /// the same Pick/NudgeSelected paths the mouse uses, restore official,
    /// apply, and verify the rebuilt session carries the edited geometry.
    /// </summary>
    private async Task RunEditSmokeAsync()
    {
        var failures = new List<string>();
        void Check(bool ok, string step)
        {
            GD.Print($"[edit-smoke] {(ok ? "ok" : "FAIL")} {step}");
            if (!ok)
            {
                failures.Add(step);
            }
        }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        static double Deg(double d) => d * Math.PI / 180;

        // 人工路径回归: 等待若干帧让实况 Prep 空转推进 TickIndex (人工按 E 必然
        // 发生在重置之后若干 tick), 编辑器门禁只看阶段仍必须放行。
        await WaitFrames(30);
        Check(_session.Engine.TickIndex > 0, "prep idle ticks advanced TickIndex");
        TryToggleEditor(); // 进入编辑模式 (走真实入口条件检查)
        Check(_editor.Active, "enter edit mode");
        if (!_editor.Active)
        {
            Finish();
            return;
        }

        var draft = _editor.Draft!;

        // Rotate through the injected key-action path (5° steps, snap on).
        await WaitFrames(2);
        InjectAction("editor_rotate_cw");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, Deg(5)), "rotate +5° via key action");
        InjectAction("editor_rotate_cw");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, Deg(10)), "rotate +5° again");
        InjectAction("editor_rotate_ccw");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, Deg(5)), "rotate -5° (ccw key)");

        // Undo/redo walk the step history (states pushed: 0,5,10; ccw pushed 5 → U=[0,5,10]).
        InjectAction("editor_undo");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, Deg(10)), "undo restores previous step");
        InjectAction("editor_redo");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, Deg(5)), "redo re-applies step");
        InjectAction("editor_undo");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, Deg(10)), "undo again steps back");
        InjectAction("editor_undo");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, Deg(5)), "undo walks the stack");
        InjectAction("editor_undo");
        await WaitFrames(2);
        Check(Near(draft.State.Pose.Th, 0), "undo back to identity");

        // Snap toggle is a HUD-visible switch (snap math is unit-tested headlessly).
        Check(_editor.InspectorLine.Contains("吸附=开"), "inspector shows snap on");
        InjectAction("editor_snap_toggle");
        await WaitFrames(2);
        Check(_editor.InspectorLine.Contains("吸附=关"), "snap toggle switches");
        InjectAction("editor_snap_toggle");
        await WaitFrames(2);

        // Drag the whole field through the same pick/nudge path the mouse uses.
        _editor.SelectAtGround(1.9, 1.9);
        _editor.NudgeSelectedBy(0.12, 0.07);
        Check(Near(draft.State.Pose.X, 0.12) && Near(draft.State.Pose.Y, 0.07),
            "select field + drag moves field pose");
        _editor.RequestUndo();
        Check(Near(draft.State.Pose.X, 0) && Near(draft.State.Pose.Y, 0), "undo field drag");

        // Select the yellow zone (world point via the current pose transform) and drag it.
        // The pick point is the zone's west-south corner: the entity-first picker
        // selects the vehicle standing at the zone center, so the corner keeps
        // this assertion on the ground fallback (zone rectangle).
        var pose = draft.State.Pose;
        var t = new Sim.Core.FieldTransform(pose.X, pose.Y, pose.Th);
        var zone = draft.State.StartZones[RoleNames.Us];
        var (zwx, zwy) = t.LocalToWorldPoint(zone.MinX + 0.03, zone.MinY + 0.03);
        _editor.SelectAtGround(zwx, zwy);
        Check(_editor.SelectedLabel == "黄色出发区", "zone corner pick selects the yellow zone (vehicle proxy owns the center)");
        var startBefore = draft.State.Starts[RoleNames.Us];
        _editor.NudgeSelectedBy(-0.05, 0.02);
        var zoneNow = draft.State.StartZones[RoleNames.Us];
        var startNow = draft.State.Starts[RoleNames.Us];
        Check(Near(zoneNow.MinX, zone.MinX - 0.05) && Near(zoneNow.MaxY, zone.MaxY + 0.02),
            "select yellow zone + drag moves zone");
        Check(Near(startNow.X, startBefore.X - 0.05) && Near(startNow.Y, startBefore.Y + 0.02),
            "dragging zone drags its start pose");

        // Drag a block; the engine's spawn must follow the frozen coordinate.
        var block = draft.State.Blocks[0];
        var (bwx, bwy) = t.LocalToWorldPoint(block.X!.Value, block.Y!.Value);
        _editor.SelectAtGround(bwx, bwy);
        _editor.NudgeSelectedBy(-0.04, -0.03);
        var blockNow = draft.State.Blocks[0];
        Check(Near(blockNow.X!.Value, block.X.Value - 0.04) && Near(blockNow.Y!.Value, block.Y.Value - 0.03),
            "select block + drag fixes new position");

        // ---------- block layout editing: add (B) / toggle kind (K) / remove (Del) ----------
        // All three walk the key-action path and land in the undo stack; the
        // sequence nets out to zero change so later sections keep this baseline.
        var blocksBeforeBlockOps = draft.State.Blocks.ToList();
        InjectAction("editor_block_add");
        await WaitFrames(2);
        Check(draft.State.Blocks.Count == blocksBeforeBlockOps.Count + 1, "B adds one block");
        Check(_editor.SelectedLabel.Contains("增益"), "added buff block is selected");
        InjectAction("editor_block_kind");
        await WaitFrames(2);
        Check(draft.State.Blocks[^1].Kind == BlockKind.Debuff, "K toggles the new block to debuff");
        InjectAction("editor_block_remove");
        await WaitFrames(2);
        Check(draft.State.Blocks.Count == blocksBeforeBlockOps.Count, "Del removes the selected block");
        _editor.RequestUndo();
        Check(draft.State.Blocks.Count == blocksBeforeBlockOps.Count + 1
            && draft.State.Blocks[^1].Kind == BlockKind.Debuff, "undo restores the removed block");
        _editor.RequestRedo();
        Check(draft.State.Blocks.Count == blocksBeforeBlockOps.Count
            && draft.State.Blocks.Select((b, i) => b.Equals(blocksBeforeBlockOps[i])).All(ok => ok),
            "redo re-removes; original blocks untouched");

        // ---------- entity picking: drag a vehicle through the real input pipeline ----------
        // The injected left-drag walks the exact press/motion/release path the
        // mouse uses: entity pick (analytic proxy at the vehicle body) → ground
        // delta → field-local → snap → LayoutDraft.MoveStart, with the whole
        // drag grouped as one undo entry. Assertions target draft state, not
        // pixel geometry (injected drags scale through the window surface via
        // InjectButtonDrag). Press points come from UnprojectPosition so they
        // ride the same screen-space pipeline as a real mouse click.
        await WaitSettled();
        var poseBeforeVehicleDrag = draft.State.Pose;
        var zoneUsBeforeVehicleDrag = draft.State.StartZones[RoleNames.Us];
        var zoneThemBeforeVehicleDrag = draft.State.StartZones[RoleNames.Them];
        var themBeforeVehicleDrag = draft.State.Starts[RoleNames.Them];
        var usBeforeVehicleDrag = draft.State.Starts[RoleNames.Us];
        var blocksBeforeVehicleDrag = draft.State.Blocks.ToList();
        var usCenter = _editor.PreviewFrame!.Us.Position;
        var vehiclePress = _camera.UnprojectPosition(
            new Vector3((float)usCenter.X, (float)usCenter.Up, (float)usCenter.Z));
        InjectLeftDrag(vehiclePress, vehiclePress + new Vector2(24, 12));
        await WaitUntil(() => !draft.State.Starts[RoleNames.Us].Equals(usBeforeVehicleDrag));
        await WaitFrames(2);
        var usAfterVehicleDrag = draft.State.Starts[RoleNames.Us];
        Check(_editor.SelectedLabel == "我方小车", "screen press on vehicle body selects 我方小车");
        Check(!usAfterVehicleDrag.Equals(usBeforeVehicleDrag), "vehicle drag moves its start");
        Check(Near(usAfterVehicleDrag.Th, usBeforeVehicleDrag.Th), "vehicle drag preserves heading");
        Check(draft.State.Pose.Equals(poseBeforeVehicleDrag), "vehicle drag leaves the field pose untouched");
        Check(draft.State.StartZones[RoleNames.Us].Equals(zoneUsBeforeVehicleDrag)
            && draft.State.StartZones[RoleNames.Them].Equals(zoneThemBeforeVehicleDrag),
            "vehicle drag leaves start zones untouched");
        Check(draft.State.Starts[RoleNames.Them].Equals(themBeforeVehicleDrag),
            "vehicle drag leaves the opponent untouched");
        Check(blocksBeforeVehicleDrag.Select((b, i) => b.Equals(draft.State.Blocks[i])).All(ok => ok),
            "vehicle drag leaves blocks untouched");
        _editor.RequestUndo();
        Check(draft.State.Starts[RoleNames.Us].Equals(usBeforeVehicleDrag),
            "undo restores the whole vehicle drag (one drag = one entry)");
        _editor.RequestRedo();
        Check(draft.State.Starts[RoleNames.Us].Equals(usAfterVehicleDrag), "redo replays the whole vehicle drag");

        // Pick + drag a block through the same screen picker (analytic proxy on
        // the preview cube). Any block may be the nearest hit along the ray, so
        // assert "some block moved" plus isolation, not a fixed index.
        var blockVisual = _editor.PreviewFrame!.Blocks[1];
        var blockPress = _camera.UnprojectPosition(new Vector3(
            (float)blockVisual.Position.X,
            (float)(blockVisual.Position.Up + 0.07),
            (float)blockVisual.Position.Z));
        InjectLeftDrag(blockPress, blockPress + new Vector2(-24, -12));
        await WaitUntil(() => draft.State.Blocks
            .Select((b, i) => (b, i))
            .Any(x => !x.b.Equals(blocksBeforeVehicleDrag[x.i])));
        await WaitFrames(2);
        Check(_editor.SelectedLabel.Contains("能量块"), "screen press on a block selects the block");
        Check(draft.State.Blocks.Select((b, i) => (b, i)).Any(x => !x.b.Equals(blocksBeforeVehicleDrag[x.i])),
            "block drag fixes a new position");
        Check(draft.State.Starts[RoleNames.Us].Equals(usAfterVehicleDrag)
            && draft.State.Starts[RoleNames.Them].Equals(themBeforeVehicleDrag),
            "block drag leaves vehicle starts untouched");

        // Low-angle guard (PRD R4): the pick point is raised to the proxy
        // mid-height (≈ visual body center; the spawn ZG alone sits at ground
        // level, where the ray's y=0 crossing would equal the robot's own
        // position and prove nothing). At a 10° pitch the ray through that
        // raised point crosses y=0 ~0.7m behind the robot — outside its start
        // zone — so a y=0-only guess lands on the zone/field, never the
        // vehicle; the entity proxy must win. Yaw 180 puts the camera on the
        // robot's side of the platform, so the approach corridor runs over
        // off-platform ground where no block proxy can stand between the
        // camera and the vehicle.
        var usCenterAfterRedo = _editor.PreviewFrame!.Us.Position;
        var pitchBeforeLowAngle = _camera.OverviewPitch;
        _camera.SetOverviewOrbit(180f, 10f);
        await WaitSettled();
        _editor.SelectAtScreen(_camera.UnprojectPosition(new Vector3(
            (float)usCenterAfterRedo.X, (float)(usCenterAfterRedo.Up + 0.15), (float)usCenterAfterRedo.Z)));
        Check(_editor.SelectedLabel == "我方小车",
            "low-angle entity-center pick selects the robot (not a y=0 guess)");
        _camera.SetOverviewOrbit(0f, pitchBeforeLowAngle);
        await WaitSettled();

        // Restore official must undo everything and stay applicable.
        _editor.RequestRestoreOfficial();
        Check(_editor.CanApplyNow && Near(draft.State.Pose.X, 0) && Near(draft.State.Pose.Th, 0),
            "restore official layout");

        // Edit → apply → the rebuilt session engine runs the edited geometry.
        // First drag our robot through the same entity picker: the vertical ray
        // at the start point hits the vehicle proxy (field pose is identity here,
        // so the ground point equals the field-local start).
        var usStartBeforeApply = draft.State.Starts[RoleNames.Us];
        _editor.SelectAtGround(usStartBeforeApply.X, usStartBeforeApply.Y);
        Check(_editor.SelectedLabel == "我方小车", "start-point pick selects our robot (entity-first)");
        _editor.NudgeSelectedBy(0.1, 0.0);
        var usStartEdited = draft.State.Starts[RoleNames.Us];
        Check(Near(usStartEdited.X, usStartBeforeApply.X + 0.1) && Near(usStartEdited.Y, usStartBeforeApply.Y)
            && Near(usStartEdited.Th, usStartBeforeApply.Th), "nudge on robot moves only its start (heading kept)");
        _editor.SelectAtGround(1.9, 1.9);
        _editor.NudgeSelectedBy(0.0, 0.3);
        InjectAction("editor_rotate_cw");
        await WaitFrames(2);
        _editor.RequestApply();
        await WaitFrames(2);
        Check(!_editor.Active, "apply closes edit mode");
        var applied = _session.Engine.Scenario.Field.Pose!;
        Check(Near(applied.X, 0) && Near(applied.Y, 0.3) && Near(applied.Th, Deg(5)),
            "applied scenario carries edited pose");
        var appliedStart = _session.Engine.Scenario.Field.Starts[RoleNames.Us];
        Check(Near(appliedStart.X, usStartEdited.X) && Near(appliedStart.Y, usStartEdited.Y)
            && Near(appliedStart.Th, usStartEdited.Th), "applied scenario carries the edited start");
        var (usX, usY) = new Sim.Core.FieldTransform(applied.X, applied.Y, applied.Th)
            .LocalToWorldPoint(usStartEdited.X, usStartEdited.Y);
        Check(Near(_session.Engine.Us.X, usX) && Near(_session.Engine.Us.Y, usY),
            "engine spawn follows the edited start pose");

        // The applied layout must reproduce bit-for-bit through record + verify.
        var scenario = _session.Scenario;
        using var recorder = Sim.Hosting.MatchEngineHost.Create(scenario);
        recorder.Arm();
        var prints = new List<string>();
        while (!recorder.Done)
        {
            var snap = recorder.Tick();
            prints.AddRange(snap.Events?.Select(e => $"{e.Seq}|{e.Tick}|{e.Type}|{e.Cls}|{e.Msg}") ?? []);
        }
        var recorded = new ReplayFile
        {
            Scenario = scenario,
            Header = recorder.BuildReplayHeader(),
            Ticks = recorder.TickIndex,
            FinalScores = recorder.Scores,
            DoneReason = recorder.CommitSnapshot().DoneReason,
            EventFingerprints = prints,
        };
        Check(ParityCheck.Verify(recorded).Pass, "applied layout passes parity-check");

        Check(failures.Count == 0,
            failures.Count == 0 ? "all checks passed" : $"failures: {string.Join(" | ", failures)}");
        Finish();
        return;

        void Finish()
        {
            if (_capturePath.Length == 0)
            {
                _capturePath = Path.GetFullPath("docs/desktop-editsmoke-720.png");
            }
            // 30 帧而非 2 帧: Apply 刚重建了 MatchSession, 等 ResetToLive 后首个
            // tick 提交, HUD 显示真实实况状态而不是应用瞬间的空帧。
            _captureFramesLeft = 30;
            _smokeExit = failures.Count == 0 ? 0 : 1;
        }

        async Task WaitFrames(int n)
        {
            for (var i = 0; i < n; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }

        // 阻尼收敛等待: 屏幕点选/反投影断言必须等机位稳定 (SetOverviewOrbit 后
        // 相机按 lerp 滑向目标), 与 --camera-smoke 同一模式。
        async Task WaitSettled(int maxFrames = 240)
        {
            for (var i = 0; i < maxFrames; i++)
            {
                var prev = _camera.Position;
                await WaitFrames(1);
                if (_camera.Position.DistanceTo(prev) < 1e-4f)
                {
                    return;
                }
            }
        }

        // 注入拖动的效果以草稿状态到位为准 (无头输入缓冲冲洗有 1-2 帧抖动,
        // 固定等帧数会偶发竞态; 上限 60 帧)。
        async Task WaitUntil(Func<bool> applied, int maxFrames = 60)
        {
            for (var i = 0; i < maxFrames && !applied(); i++)
            {
                await WaitFrames(1);
            }
        }

        static void InjectAction(string action)
        {
            Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
            Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        }
    }

    private void LoadDesktopSettings()
    {
        var path = ProjectSettings.GlobalizePath($"user://{SettingsStore.DefaultFileName}");
        _settingsStore = new SettingsStore(path, GD.PrintErr);
        _settings = _settingsStore.Load();
        RebuildVisionFactory();
        ApplyDisplaySettings(_settings);
        GD.Print($"[settings] 已加载 {path}: {DisplaySettingsLine(_settings)}");
    }

    private void ApplyDesktopSettings(DesktopSettings settings)
    {
        // 批3 R3.1: 比较逻辑抽到 DesktopSettingsDiff (纯逻辑, Sim.Tests 回归), Vehicle
        // (含传感器覆盖) 已计入 —— 只改小车参数同样触发自动重开当前对局。
        var matchChanged = !DesktopSettingsDiff.MatchRelevantEqual(_settings, settings);
        _settings = settings;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception error)
        {
            GD.PrintErr($"[settings] 配置保存失败: {error.Message}");
        }

        ApplyDisplaySettings(settings);
        RebuildVisionFactory();
        // 场景文件选择先落模板 (ApplyMatchScenarioSelection 内部只换模板/挂待生效),
        // 再按新模板装配控制器与后续重置。
        var scenarioChanged = ApplyMatchScenarioSelection(settings);
        _controllerAssignment = RebuildControllerWiring(BuildLiveScenarioFromTemplate(), probeExternal: true);
        PublishControllerWiring();
        if (matchChanged || scenarioChanged)
        {
            // 2026-10-04 用户拍板"保存后自动重置生效": 实况中(含待命/进行中/已结束)
            // 保存即重建会话, 省一次手动 F5。两个安全例外维持"下一场生效": 回放中
            // (重置会踢出回放)与布局编辑中(重置会丢弃未应用草稿)。
            // 批3 R3.1: 小车设置在 matchChanged 内, 日志不再出现"只改小车却谎报显示设置"。
            ReloadSessionForScenarioTemplate(
                scenarioChanged && !matchChanged
                    ? "[settings] 比赛/场景设置已应用: 已按所选场景重建当前对局"
                    : "[settings] 比赛相关设置已应用并重置: 已重建当前对局",
                "[settings] 设置已保存，将在下一场或 F5 重置后生效");
        }
        else
        {
            // 对局相关字段无变化 (只改显示项, 或连显示项都没改的空应用): 显示设置即时
            // 应用, 不重置对局。日志文案沿用既有口径 (只改小车参数的谎报已由 R3.1 修掉)。
            GD.Print("[settings] 显示设置已应用");
        }
    }

    /// <summary>
    /// 运行时会话模板已换后的统一生效入口 (批1 导入 / 批2 场景选择与换种子重开共用):
    /// 实况且非布局编辑中立即重建会话; 回放/编辑中只挂待生效 —— 不把用户踢出回放、
    /// 不丢未应用草稿 (与 ApplyDesktopSettings 自动重置同一安全例外, 规则维护在一处)。
    /// 返回 true = 已重建会话。
    /// </summary>
    private bool ReloadSessionForScenarioTemplate(string reloadMessage, string pendingMessage)
    {
        if (_session.Mode == SessionMode.Live && !_editor.Active)
        {
            ResetLiveSession(reloadMessage);
            return true;
        }
        _pendingMatchSettings = true;
        GD.Print(pendingMessage);
        return false;
    }

    /// <summary>
    /// "比赛/场景"页的场景文件选择 (批2 R2.1)。非空且与已生效值不同 → 解析 (走
    /// <see cref="ResolveUserPath"/> 语义) + Validate 通过才换模板; 空 → 回到启动场景
    /// (--scenario-path / 官方布局)。文件缺失/无效 → 中文诊断 + 保留当前场景, 不改模板。
    /// 返回 true = 模板已换, 调用方按"下一场或 F5"同规则触发重置/挂起 (回放与布局编辑
    /// 中不重建会话 —— 与批1 导入入口 TryImportScenario 同一安全例外)。
    /// </summary>
    private bool ApplyMatchScenarioSelection(DesktopSettings settings)
    {
        var raw = settings.MatchOverrides?.ScenarioPath ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (_appliedMatchScenarioPath.Length == 0)
            {
                return false;
            }
            _startupScenarioTemplate ??= LoadBaseScenarioTemplate();
            _scenarioTemplate = _startupScenarioTemplate;
            ScenarioPath = _startupScenarioPath;
            _appliedMatchScenarioPath = "";
            GD.Print($"[settings] 比赛/场景: 已回到启动场景 ({(_startupScenarioPath.Length == 0 ? "官方布局" : _startupScenarioPath)})");
            return true;
        }

        var resolved = ResolveUserPath(raw);
        if (!File.Exists(resolved))
        {
            GD.PrintErr($"[settings] 比赛/场景: 场景文件不存在, 保留当前场景: {raw} (解析为 {resolved})");
            _hud?.ShowNotice($"场景文件不存在，已保留当前场景：{raw}", ok: false);
            return false;
        }
        if (string.Equals(resolved, _appliedMatchScenarioPath, StringComparison.Ordinal))
        {
            return false;
        }
        Scenario scenario;
        try
        {
            scenario = ProtocolJson.Deserialize<Scenario>(System.IO.File.ReadAllText(resolved));
        }
        catch (Exception error)
        {
            GD.PrintErr($"[settings] 比赛/场景: 场景文件载入失败, 保留当前场景: {resolved}: {error.Message}");
            _hud?.ShowNotice($"场景文件载入失败，已保留当前场景：{error.Message}", ok: false);
            return false;
        }
        var errors = scenario.Validate().ToArray();
        if (errors.Length > 0)
        {
            GD.PrintErr($"[settings] 比赛/场景: 场景文件无效, 保留当前场景: {resolved}: {string.Join(" | ", errors)}");
            _hud?.ShowNotice("场景文件无效，已保留当前场景", ok: false);
            return false;
        }
        _scenarioTemplate = scenario;
        ScenarioPath = resolved;
        _appliedMatchScenarioPath = resolved;
        GD.Print($"[settings] 比赛/场景: 已切换场景模板 {resolved}");
        return true;
    }

    /// <summary>
    /// "换种子重开" (批2 R2.1): 等价"写 seed + F5 重置"。种子写进比赛覆盖
    /// (<see cref="MatchOverrides.Seed"/>, 随设置持久化), 实况且非布局编辑中立即重建;
    /// 回放/编辑中只挂待生效 (不把用户踢出回放)。
    /// </summary>
    private void RestartWithSeed(int seed)
    {
        var match = _settings.MatchOverrides ?? new MatchOverrides();
        _settings = _settings with { MatchOverrides = match with { Seed = seed } };
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception error)
        {
            GD.PrintErr($"[settings] 配置保存失败: {error.Message}");
        }
        ReloadSessionForScenarioTemplate(
            $"[settings] 已按新种子重开: seed={seed}",
            $"[settings] 新种子 {seed} 已就绪，将在下一场或 F5 重置后生效");
        _hud?.ShowNotice($"已按 seed {seed} 重开", ok: true);
    }

    /// <summary>
    /// 按当前设置装配视觉源工厂: 默认 classifyRate 不注入(逐位不变)。证据包/CSV 预检
    /// 失败时高声报错(控制台 + HUD)并回退默认源 —— 桌面必须始终能开赛, 但绝不静默换源。
    /// </summary>
    private void RebuildVisionFactory()
    {
        try
        {
            _visionFactory = _settings.CreateVisionFactory();
            var source = _settings.Vision?.Source ?? VisionSources.ClassifyRate;
            GD.Print(source == VisionSources.ClassifyRate
                ? "[vision] 视觉源: 默认 classifyRate 桩 (不注入 adapter)"
                : $"[vision] 视觉源: {source}");
        }
        catch (Exception error)
        {
            // 视觉源是外部文件/进程边界: 坏路径、哈希不一致、方言不符都在此收敛为
            // "本场用默认源 + 显式告警", 与 DesktopLiveDriver 的 fault 处理同一取向。
            _visionFactory = null;
            GD.PrintErr($"[vision] 视觉源装配失败，本场回退默认 classifyRate: {error.Message}");
            _hud?.ShowNotice($"视觉源装配失败，已回退默认源 · {error.Message}", ok: false);
        }
    }

    /// <summary>
    /// 控制器来源装配（无引擎决策/解析在 ControllerWiring，纯逻辑可单测）:
    /// 我方 external = SCORE_BLOCK 展演，只在 mujoco 场景启用；应用设置时按 liveProcess
    /// 视觉源先例预检一次（启动→握手→立刻释放，坏命令当场响亮报错）。拒绝/回退只影响
    /// 本场，设置本身照旧保存 —— 换回 mujoco 场景或修好命令后重新应用即恢复。
    /// </summary>
    private ControllerAssignment RebuildControllerWiring(Scenario scenario, bool probeExternal)
    {
        var us = _settings.UsController ?? new ControllerProfile();
        if (!ControllerWiring.IsRunnableExternal(us))
        {
            _usPreflight = null;
        }
        else if (probeExternal && ControllerWiring.ScenarioSupportsExhibition(scenario))
        {
            _usPreflight = ControllerPreflight.Run(us);
        }
        return ControllerWiring.Resolve(scenario, us, _settings.ThemController,
            ControllerWiring.IsRunnableExternal(us) ? _usPreflight : null);
    }

    /// <summary>装配结果送控制台与 HUD：拒绝/预检告警红色响亮、正常绿色。</summary>
    private void PublishControllerWiring()
    {
        var assignment = _controllerAssignment;
        var us = ControllerWiring.DescribeSource(assignment.Us);
        if (assignment.Notice?.IsRejection == true)
        {
            us += "（外部控制器被拒绝）";
        }
        else if (assignment.Exhibition)
        {
            us += "（SCORE_BLOCK 展演）";
        }
        var them = ControllerWiring.DescribeSource(assignment.Them);
        _hud?.UpdateControllerSources(us, them, assignment.Notice?.IsRejection == true);
        if (assignment.Notice is { } notice)
        {
            if (notice.IsRejection)
            {
                GD.PrintErr($"[controller] {notice.Message}");
            }
            else
            {
                GD.Print($"[controller] {notice.Message}");
            }
            _hud?.ShowNotice(notice.Message, ok: !notice.IsRejection);
            return;
        }
        GD.Print($"[controller] 我方 {us}, 对手 {them}"
            + (assignment.Exhibition ? " —— Arm 后预推进到 SCORE_BLOCK 再交接" : ""));
    }

    private void ApplyDisplaySettings(DesktopSettings settings)
    {
        var window = settings.Window ?? new WindowSettings();
        DisplayServer.WindowSetSize(new Vector2I(window.Width, window.Height));
        if (window.Mode == DisplayModes.Fullscreen)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        }
        else
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        }
        _hud?.SetUiScale(settings.UiScale);
        _settingsPanel?.SetUiScale(settings.UiScale);
    }

    private void OpenSettings()
    {
        if (_settingsPanel is null || _settingsPanel.IsOpen)
        {
            return;
        }
        _settingsPanel.Open(_settings, _pendingMatchSettings, _robotModels, ScenarioHasLayoutVersion());
    }

    /// <summary>
    /// 当前场景模板是否带 layoutVersion (布局编辑器/布局文件产物) —— 批3 R3.3:
    /// 能量块页"自定义能量块布局"开启前要据此警告"将覆盖布局编辑器的摆位"。
    /// </summary>
    private bool ScenarioHasLayoutVersion()
        => _scenarioTemplate is { LayoutVersion: ProtocolVersion.ArenaLayoutV1 };

    private Scenario BuildScenario()
    {
        // 启动模板 = 无桌面覆盖的原始场景值 (指向"跟随启动场景"档); 场景文件覆盖
        // (设置里的"比赛/场景"页) 只在模板选择这一层生效, 之后才叠加桌面覆盖链。
        _startupScenarioTemplate ??= LoadBaseScenarioTemplate();
        var template = LoadMatchOverrideScenarioTemplate() ?? _startupScenarioTemplate;
        _scenarioTemplate = template;
        return ApplyDesktopSettings(template);
    }

    /// <summary>启动场景 (--scenario-path 文件或官方布局), 不含设置里的场景覆盖。</summary>
    private Scenario LoadBaseScenarioTemplate()
        => string.IsNullOrEmpty(ScenarioPath)
            ? new Scenario { Seed = Seed, Blocks = OfficialLayout.Blocks }
            : ProtocolJson.Deserialize<Scenario>(System.IO.File.ReadAllText(ScenarioPath));

    /// <summary>
    /// 设置里"比赛/场景"页选的场景文件 → 启动模板。空 = 跟随启动场景 (返回 null);
    /// 文件缺失/解析失败/校验不过 → 响亮诊断 + 本次运行回退启动场景, 不崩启动。
    /// </summary>
    private Scenario? LoadMatchOverrideScenarioTemplate()
    {
        if (_matchOverrideScenarioFailed)
        {
            return null;
        }
        var raw = _settings.MatchOverrides?.ScenarioPath;
        if (string.IsNullOrWhiteSpace(raw))
        {
            _appliedMatchScenarioPath = "";
            return null;
        }
        var resolved = ResolveUserPath(raw);
        if (!File.Exists(resolved))
        {
            _matchOverrideScenarioFailed = true;
            GD.PrintErr($"[scenario] 设置中的场景文件不存在: {raw} (解析为 {resolved}) —— 回退启动场景");
            _hud?.ShowNotice($"设置中的场景文件不存在，已回退启动场景：{raw}", ok: false);
            return null;
        }
        Scenario scenario;
        try
        {
            scenario = ProtocolJson.Deserialize<Scenario>(System.IO.File.ReadAllText(resolved));
        }
        catch (Exception error)
        {
            _matchOverrideScenarioFailed = true;
            GD.PrintErr($"[scenario] 设置中的场景文件读取失败: {resolved}: {error.Message} —— 回退启动场景");
            _hud?.ShowNotice($"设置中的场景文件读取失败，已回退启动场景：{error.Message}", ok: false);
            return null;
        }
        var errors = scenario.Validate().ToArray();
        if (errors.Length > 0)
        {
            _matchOverrideScenarioFailed = true;
            GD.PrintErr($"[scenario] 设置中的场景文件无效: {resolved}: {string.Join(" | ", errors)} —— 回退启动场景");
            _hud?.ShowNotice("设置中的场景文件无效，已回退启动场景", ok: false);
            return null;
        }
        ScenarioPath = resolved;
        _appliedMatchScenarioPath = resolved;
        GD.Print($"[settings] 比赛/场景: 启动加载场景覆盖 {resolved}");
        return scenario;
    }

    /// <summary>
    /// 显式桌面覆盖层 (比赛/场景 → 参数 → 小车 → 控制器选择): 仅显式设置生效,
    /// 默认档不改场景/车辆字段。比赛/场景覆盖最先 —— 物理后端/比赛时长/种子先落进
    /// 场景, 之后 ApplyVehicleOverrides 才能按覆盖后的 backend=v2 决定是否生效。
    /// 控制器选择最后叠加 —— MBri 档把 vehicles[].controller
    /// 写成 "mbri", builtin/external 保持场景原值 (external 走进程桥, 不占字段)。
    /// </summary>
    private Scenario ApplyDesktopSettings(Scenario template)
        => _settings.ApplyControllerSelection(
            _settings.ApplyVehicleOverrides(
            _settings.ApplyBlocks(
            _settings.ApplySimulationParameters(
            _settings.ApplyMatchOverrides(template)))));

    // 响亮回退(同视觉源预检先例): 场景文件读不到时给指路报错并回退官方布局,
    // 不留一个没建起场景的空窗口。
    private Scenario BuildScenarioWithFallback()
    {
        try
        {
            return BuildScenario();
        }
        catch (Exception e)
        {
            GD.PushError($"[scenario] 场景加载失败 ({(ScenarioPath.Length == 0 ? "官方布局" : ScenarioPath)}): {e.Message} —— 回退官方布局");
            // 归因: BuildScenario 先加载启动模板, 只有它成功 (非 null) 后才会走设置里的
            // 场景覆盖 — 此时抛错只可能来自覆盖路径, 本次运行内不再重试; 启动模板自身
            // 失败则清掉启动档 (跟随档也指向官方布局), 让重试仍能尝试设置里的覆盖。
            if (_startupScenarioTemplate is null)
            {
                _startupScenarioPath = "";
            }
            else if (!string.IsNullOrWhiteSpace(_settings.MatchOverrides?.ScenarioPath))
            {
                _matchOverrideScenarioFailed = true;
            }
            ScenarioPath = "";
            return BuildScenario();
        }
    }

    // 命令行相对路径解析: `--path godot` 会把进程 CWD 带进 godot/ 子目录, 用户在
    // 仓库根敲的 `--scenario-path scenarios/x.json` 曾被解析成 godot/scenarios/...
    // 而启动失败(2026-09-30 目检发现)。输入路径按 "CWD → res:// 父目录(仓库根)"
    // 顺序做存在性锚定; 两处都不存在(输出类或新建文件)时保持 CWD 解析的现状
    // 语义, 由后续 IO 用完整路径报错。
    private static string ResolveUserPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || Path.IsPathRooted(raw))
        {
            return raw;
        }
        var cwdCandidate = Path.GetFullPath(raw);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        var repoCandidate = Path.GetFullPath(Path.Combine(repoRoot, raw));
        return File.Exists(repoCandidate) ? repoCandidate : cwdCandidate;
    }

    private Scenario BuildLiveScenarioFromTemplate()
    {
        var template = _scenarioTemplate ?? _session.Engine.Scenario;
        return ApplyDesktopSettings(template);
    }

    /// <summary>
    /// Swaps in a fresh session for <paramref name="scenario"/> and releases the
    /// previous engine. Every session replacement goes through here so a leaked
    /// MuJoCo model/data pair (native handles, no finalizer) is impossible.
    /// </summary>
    private void ReplaceSession(Scenario scenario)
    {
        var previous = _session;
        // legacy L1/L2/L3 开关 (批2 高级/开发者折叠区): 默认全开 = 现行为; mujoco 不消费。
        _session = new MatchSession(scenario, _visionFactory, _settings.CreateContactResolveOptions());
        previous?.Dispose();
    }

    private void ResetLiveSession(string message)
    {
        StopLiveDriver();
        var scenario = BuildLiveScenarioFromTemplate();
        ReplaceSession(scenario);
        _pendingMatchSettings = false;
        ApplyScenarioToShell(scenario);
        StartLiveDriverIfConfigured(scenario);
        GD.Print(message);
    }

    private void StartLiveDriverIfConfigured(Scenario scenario)
    {
        if (_session.Mode != SessionMode.Live)
        {
            return;
        }
        // 用本场真实场景重算装配（纯决策 + 上次应用时的预检结论）: 场景不符/预检失败
        // 即回退内置 FSM, 不启动 driver（响亮说明在 PublishControllerWiring, 只在装配
        // 变化时打印一次, 避免每场重复刷屏）。
        var assignment = RebuildControllerWiring(scenario, probeExternal: false);
        if (assignment != _controllerAssignment)
        {
            _controllerAssignment = assignment;
            PublishControllerWiring();
        }
        if (!assignment.Us.IsExternal && !assignment.Them.IsExternal)
        {
            return;
        }
        StopLiveDriver();
        _driverSnapshot = null;
        _liveDriver = new DesktopLiveDriver(scenario, assignment.Us, assignment.Them,
            _visionFactory, assignment.Exhibition, _settings.CreateContactResolveOptions());
        _liveDriver.Start();
        GD.Print("[controller] 已启动桌面后台 driver；实况渲染线程不等待外部策略");
    }

    private void StopLiveDriver()
    {
        if (_liveDriver is null)
        {
            return;
        }
        _liveDriver.Dispose();
        _liveDriver = null;
        _driverSnapshot = null;
    }

    private void ArmLive()
    {
        if (_settingsPanel.PreflightInProgress)
        {
            GD.Print("[arm] 控制器预检进行中, 完成后再发令");
            return;
        }
        if (_liveDriver is not null)
        {
            _liveDriver.RequestArm();
            return;
        }
        _session.Engine.Arm();
    }

    private MatchControlPhase LivePhase
        => _liveDriver?.Status.Phase ?? _session.Engine.Phase;

    private bool LivePaused
        => _liveDriver?.Status.Paused ?? _session.Engine.Paused;

    private static string DisplaySettingsLine(DesktopSettings settings)
    {
        var window = settings.Window ?? new WindowSettings();
        return $"{window.Width}x{window.Height} {window.Mode}, uiScale={settings.UiScale:0.##}, "
            + $"overrides={settings.SimulationParameters?.Count ?? 0}";
    }

    /// <summary>场地几何/位姿变化时刷新静态展示与相机取景 (初始加载、回放、编辑 Apply 共用)。</summary>
    private void ApplyScenarioToShell(Scenario scenario)
    {
        _visualizer.Configure(scenario);
        var model = new FieldModel(scenario.Field);
        var (cx, cy) = model.CenterWorld;
        _camera.ConfigureArena(new Vec3(cx, 0, cy), scenario.Field.FieldSize);
        ApplyRobotModels();
        // 场景重建 = 新比赛/新回放: 事件栏缓冲清空。
        _eventBuffer.Clear();
        _lastEventTick = -1;
        UpdateWindowTitle();
    }

    /// <summary>
    /// 窗口标题带 seed/模式/场景 (多开时分辨窗口); pid 保证同名场景也可区分。
    /// </summary>
    private void UpdateWindowTitle()
    {
        var detail = _session.Mode == SessionMode.Replay ? "回放" : "实况";
        GetWindow().Title =
            $"WushuRingSim · seed{_session.Engine.Scenario.Seed} · {detail}"
            + $" · {_session.Engine.Scenario.Id} · #{System.Environment.ProcessId}";
    }

    private void ApplyRobotModels()
    {
        if (_robotModels is null)
        {
            return;
        }
        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            var config = _robotModels.TryGetValue(role, out var c) ? c : null;
            var error = RobotModelLoader.Apply(_visualizer.RobotRoot(role), config);
            if (error is not null)
            {
                GD.PrintErr($"[models] {role}: {error} (已回退 primitive)");
            }
            else if (config is { IsEmpty: false })
            {
                GD.Print($"[models] {role}: 已应用外观模型 {config.Path}");
            }
        }
    }

    /// <summary>
    /// 设置面板外观区的落盘 + 立即重挂: 写回读入时解析的同一文件 (未显式指定时默认
    /// res://robot-models.json), 原子替换; 之后 ApplyRobotModels 让新绑定即时可见。
    /// 路径留空的 role 不产生条目 —— 重挂时自动回退 primitive 分件。
    /// </summary>
    private void SaveRobotModels(IReadOnlyDictionary<string, RobotModelConfig> models)
    {
        _robotModelsPath ??= "res://robot-models.json";
        _robotModels = new Dictionary<string, RobotModelConfig>(models, StringComparer.Ordinal);
        try
        {
            var global = _robotModelsPath.StartsWith("res://", StringComparison.Ordinal)
                ? ProjectSettings.GlobalizePath(_robotModelsPath)
                : _robotModelsPath;
            var directory = System.IO.Path.GetDirectoryName(global);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            var temporaryPath = global + ".tmp";
            System.IO.File.WriteAllText(temporaryPath, ProtocolJson.Serialize(_robotModels));
            System.IO.File.Move(temporaryPath, global, overwrite: true);
            ApplyRobotModels();
            GD.Print($"[models] 外观偏好已保存: {_robotModelsPath}");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[models] 外观偏好保存失败 {_robotModelsPath}: {e.Message}");
            _hud?.ShowNotice($"外观模型保存失败: {e.Message}", ok: false);
        }
    }

    // ---------- 配置包导出/导入 (批1 复现基座) ----------

    /// <summary>导入落点 user://imported/ —— 用户文件不写仓库目录 (res://robot-models.json 是既有先例)。</summary>
    private const string ImportedDirectoryRelative = "user://imported";
    private const string ImportedTrainConfigName = "train-config.json";

    /// <summary>
    /// 导出配置包 (R1.2): 主设置 + 外观模型 + 当前场景文件原文 + 可选训练配置。
    /// 面板只用文件对话框选落点, 内容收集与落盘都在壳层 (与设置落盘同一条链)。
    /// </summary>
    public void ExportSettingsBundle(string path, string? trainConfigPath = null)
    {
        path = ResolveUserPath(path);
        try
        {
            var trainConfig = string.IsNullOrWhiteSpace(trainConfigPath)
                ? (System.Text.Json.JsonElement?)null
                : SettingsBundleStore.ReadTrainConfigObject(ResolveUserPath(trainConfigPath));
            var bundle = SettingsBundle.Create(_settings, _robotModels, CaptureScenarioFile(), trainConfig);
            new SettingsBundleStore(path).Save(bundle);
            var summary = $"配置包已导出: {path}"
                + $" (外观模型 {(bundle.RobotModels?.Count ?? 0)} 条, 场景 {bundle.ScenarioFile?.FileName ?? "无"},"
                + $" 训练配置 {(bundle.TrainConfig is null ? "无" : "已附带")})";
            GD.Print($"[bundle] {summary}");
            _hud?.ShowNotice(summary, ok: true);
        }
        catch (Exception error)
        {
            GD.PrintErr($"[bundle] 配置包导出失败: {error.Message}");
            _hud?.ShowNotice($"配置包导出失败: {error.Message}", ok: false);
        }
    }

    /// <summary>
    /// 当前场景的内嵌来源: --scenario-path 的磁盘原文 (逐字内嵌, 不重新序列化);
    /// 布局编辑器应用过的 arena-layout-v1 模板; 官方内置布局 → null (bundle 可空字段)。
    /// </summary>
    private SettingsBundleFile? CaptureScenarioFile()
    {
        if (!string.IsNullOrEmpty(ScenarioPath) && System.IO.File.Exists(ScenarioPath))
        {
            var content = System.IO.File.ReadAllText(ScenarioPath);
            return new SettingsBundleFile
            {
                FileName = System.IO.Path.GetFileName(ScenarioPath),
                Content = content,
                IsLayout = IsLayoutFileContent(content),
            };
        }
        if (_scenarioTemplate is { LayoutVersion: ProtocolVersion.ArenaLayoutV1 } template)
        {
            return new SettingsBundleFile
            {
                FileName = "arena-layout.json",
                Content = ProtocolJson.Serialize(template),
                IsLayout = true,
            };
        }
        return null;
    }

    /// <summary>场景原文是否布局产物 (仅用于 bundle 元数据; 识别失败按普通场景导出)。</summary>
    private static bool IsLayoutFileContent(string content)
    {
        try
        {
            return ProtocolJson.Deserialize<Scenario>(content).LayoutVersion == ProtocolVersion.ArenaLayoutV1;
        }
        catch (Exception error)
        {
            GD.PrintErr($"[bundle] 场景文件识别失败（按普通场景导出）: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// 导入配置包 (R1.3): 读文件 → 版本校验 (≠1 → 中文原因拒绝) → 结构校验 →
    /// 确认弹窗列出覆盖范围 → 四类内容落地, 完成弹窗列各落点。
    /// </summary>
    public void ImportSettingsBundle(string path)
    {
        path = ResolveUserPath(path);
        GD.Print($"[bundle] 导入尝试: {path}");
        SettingsBundle bundle;
        try
        {
            bundle = new SettingsBundleStore(path).Load();
        }
        catch (Exception error)
        {
            ReportBundleImportFailure($"配置包读取失败: {error.Message}");
            return;
        }
        if (SettingsBundle.RejectUnsupportedVersion(bundle.BundleSchemaVersion) is { } versionError)
        {
            // R1.3: 版本不匹配明确拒绝, 不做自动迁移。
            ReportBundleImportFailure(versionError);
            return;
        }
        var errors = bundle.Validate().ToArray();
        if (errors.Length > 0)
        {
            // 诊断: 结构校验失败 = 读到的 JSON 与预期不符。打出实读文件的路径/大小/首段,
            // 让"导出的文件自己导不回去"这类问题可以从日志直接定位, 不靠猜。
            try
            {
                var content = File.ReadAllText(path);
                var head = content.Length <= 160 ? content : content[..160];
                GD.PrintErr($"[bundle] 导入诊断 {path} (size={content.Length}) head={head}");
            }
            catch (Exception readError)
            {
                GD.PrintErr($"[bundle] 导入诊断 {path}: 二次读取失败 {readError.Message}");
            }
            ReportBundleImportFailure("配置包内容无效" + System.Environment.NewLine
                + string.Join(System.Environment.NewLine, errors.Select(error => "· " + error))
                + System.Environment.NewLine + $"（文件: {path}）");
            return;
        }

        var lines = new List<string> { "导入将覆盖以下内容:" };
        lines.Add("· 主设置（窗口 / 仿真参数 / 控制器 / 小车 / 视觉 / 能量块）");
        if (bundle.RobotModels is not null)
        {
            lines.Add($"· 外观模型 {bundle.RobotModels.Count} 条 → {_robotModelsPath ?? "res://robot-models.json"}");
        }
        if (bundle.ScenarioFile is { } file)
        {
            lines.Add($"· 场景文件 {file.FileName}{(file.IsLayout ? "（布局产物）" : "")} → user://imported/");
        }
        if (bundle.TrainConfig is not null)
        {
            lines.Add($"· 训练配置 → user://imported/{ImportedTrainConfigName}");
        }
        lines.Add("主设置保存后自动重开当前对局；回放/布局编辑中则下一场或 F5 生效。");

        var confirm = new ConfirmationDialog
        {
            Title = "导入配置包",
            DialogText = string.Join("\n", lines),
            OkButtonText = "导入",
            CancelButtonText = "取消",
        };
        confirm.Confirmed += () =>
        {
            confirm.QueueFree();
            try
            {
                ApplyImportedBundle(bundle);
            }
            catch (Exception error)
            {
                // 落盘/重载是文件系统边界 (权限、磁盘满、目标被目录占用): 失败必须
                // 响亮报出, 不把异常抛回 Godot 信号回调。
                ReportBundleImportFailure($"配置包应用失败: {error.Message}");
            }
        };
        confirm.Canceled += () => confirm.QueueFree();
        AddChild(confirm);
        confirm.PopupCentered(new Vector2I(600, 300));
    }

    private void ApplyImportedBundle(SettingsBundle bundle)
    {
        var notes = new List<string>();
        // 导入场景的本地落点 (仅载入成功时非空): 主设置里的场景覆盖路径要改指到这里。
        var importedScenarioTarget = "";

        // 场景先落盘并作为模板挂上; ApplyDesktopSettings(DesktopSettings) 随后的自动重置
        // 会从新模板重建, 于是无论比赛设置是否变化最终会话都包含新场景。
        if (bundle.ScenarioFile is { } file)
        {
            var target = ImportedScenarioPath(file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WriteFileAtomic(target, file.Content);
            // 批2 相互作用保护: 导入的设置若没带场景覆盖路径, 清掉"已生效覆盖路径"记忆,
            // 否则同一流程里的 ApplyDesktopSettings 会按"回到启动场景"把刚导入的场景换回去。
            if (string.IsNullOrWhiteSpace(bundle.Settings?.MatchOverrides?.ScenarioPath))
            {
                _appliedMatchScenarioPath = "";
            }
            var imported = TryImportScenario(target);
            if (imported)
            {
                importedScenarioTarget = target;
            }
            notes.Add(imported
                ? $"场景 → {target}（已重载；回放/布局编辑中为下一场或 F5 生效）"
                : $"场景 → {target}（载入失败，已保留当前场景）");
        }

        // 主设置走既有应用链: 保存 + 显示/视觉/控制器 + 比赛设置自动重置/挂起。
        if (bundle.Settings is { } settings)
        {
            // 批2 相互作用: 设置里的场景覆盖路径是导出机的路径引用, 跨机器可能解析到同名
            // 不同内容的文件 (bundle 原文才是导出机的当前场景 —— 设计 §1a "内嵌内容而非
            // 路径引用")。有内嵌场景时改指本地落点并记为已生效, 防止随后的
            // ApplyDesktopSettings 按旧路径再换一次模板、把刚导入的场景换掉。
            if (importedScenarioTarget.Length > 0
                && settings.MatchOverrides is { } match
                && !string.IsNullOrWhiteSpace(match.ScenarioPath))
            {
                settings = settings with
                {
                    MatchOverrides = match with { ScenarioPath = importedScenarioTarget },
                };
                _appliedMatchScenarioPath = importedScenarioTarget;
            }
            ApplyDesktopSettings(settings);
            notes.Add($"主设置 → {ProjectSettings.GlobalizePath($"user://{SettingsStore.DefaultFileName}")}");
        }

        // 外观模型复用 SaveRobotModels (写读入时解析的同一文件, 默认 res://robot-models.json, 已 gitignore) + 立即重挂。
        if (bundle.RobotModels is { } models)
        {
            SaveRobotModels(models);
            notes.Add($"外观模型 → {_robotModelsPath ?? "res://robot-models.json"}");
        }

        // 训练配置只是落盘给 train.py --config 用, 桌面不解析其字段 (schema 在 train_config.py)。
        if (bundle.TrainConfig is { } trainConfig)
        {
            var target = Path.Combine(ProjectSettings.GlobalizePath(ImportedDirectoryRelative), ImportedTrainConfigName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WriteFileAtomic(target, ProtocolJson.Serialize(trainConfig));
            notes.Add($"训练配置 → {target}");
        }

        // 面板仍开着 (导入从面板触发): 从导入后的设置重载草稿控件, 否则控件停留在
        // 导入前的值, 随后的"应用设置"会把旧值写回、静默回退本次导入。
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.Open(_settings, _pendingMatchSettings, _robotModels, ScenarioHasLayoutVersion());
        }

        var message = "配置包导入完成:\n" + string.Join("\n", notes.Select(note => "· " + note));
        GD.Print($"[bundle] {message.Replace("\n", " | ")}");
        _hud?.ShowNotice("配置包已导入", ok: true);
        var done = new AcceptDialog { Title = "导入配置包", DialogText = message };
        done.Confirmed += () => done.QueueFree();
        done.Canceled += () => done.QueueFree();
        AddChild(done);
        done.PopupCentered(new Vector2I(640, 320));
    }

    /// <summary>
    /// 运行时场景重载 (批1 导入; 批2"比赛/场景"页选择场景共用入口, 参考 ApplyLayoutScenario):
    /// 解析 + 场景校验通过才写入模板; 生效规则走统一入口
    /// <see cref="ReloadSessionForScenarioTemplate"/> (实况且非布局编辑中立即重建, 否则
    /// 挂为待生效, 不把用户踢出回放)。
    /// </summary>
    private bool TryImportScenario(string path)
    {
        Scenario scenario;
        try
        {
            scenario = ProtocolJson.Deserialize<Scenario>(System.IO.File.ReadAllText(path));
            var errors = scenario.Validate().ToArray();
            if (errors.Length > 0)
            {
                GD.PrintErr($"[bundle] 导入场景无效 ({path}): {string.Join(" | ", errors)}");
                return false;
            }
        }
        catch (Exception error)
        {
            GD.PrintErr($"[bundle] 导入场景载入失败 ({path}): {error.Message}");
            return false;
        }

        ScenarioPath = path;
        _scenarioTemplate = scenario;
        ReloadSessionForScenarioTemplate(
            $"[bundle] 已按导入场景重载: {path}",
            $"[bundle] 导入场景已就绪，将在下一场或 F5 重置后生效: {path}");
        return true;
    }

    private static string ImportedScenarioPath(string fileName)
        => Path.Combine(ProjectSettings.GlobalizePath(ImportedDirectoryRelative), Path.GetFileName(fileName));

    /// <summary>导入内容原子落盘 (.tmp + File.Move, 与 SettingsStore/SettingsBundleStore 同模式)。</summary>
    private static void WriteFileAtomic(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            System.IO.File.WriteAllText(temporaryPath, content);
            System.IO.File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(temporaryPath))
            {
                System.IO.File.Delete(temporaryPath);
            }
        }
    }

    private void ReportBundleImportFailure(string message)
    {
        // 模态设置页可能挡住 HUD 通知, 失败原因同时进控制台 + 独立弹窗, 绝不静默。
        GD.PrintErr($"[bundle] {message}");
        _hud?.ShowNotice(message, ok: false);
        var dialog = new AcceptDialog { Title = "导入配置包失败", DialogText = message };
        dialog.Confirmed += () => dialog.QueueFree();
        dialog.Canceled += () => dialog.QueueFree();
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(560, 240));
    }

    /// <summary>本地外观偏好 (渲染层, 永不进入 Scenario/回放): --robot-models 参数或 res://robot-models.json。</summary>
    private void LoadRobotModelPreferences(string[] userArgs)
    {
        var index = Array.IndexOf(userArgs, "--robot-models");
        var path = index >= 0 && index + 1 < userArgs.Length ? userArgs[index + 1] : null;
        if (path is null && Godot.FileAccess.FileExists("res://robot-models.json"))
        {
            path = "res://robot-models.json";
        }
        if (path is null)
        {
            return;
        }
        _robotModelsPath = path;
        try
        {
            var text = path.StartsWith("res://", StringComparison.Ordinal)
                ? Godot.FileAccess.GetFileAsString(path)
                : System.IO.File.ReadAllText(path);
            _robotModels = ProtocolJson.Deserialize<Dictionary<string, RobotModelConfig>>(text);
            GD.Print($"[models] 已加载外观偏好: {path}");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[models] 外观偏好读取失败 {path}: {e.Message}");
        }
    }

    private RenderFrame Project(Snapshot snapshot)
        => SnapshotView.From(snapshot, _session.Engine.Scenario.Field.PlatformHeight);

    private static void SetupDefaultFont()
    {
        try
        {
            ThemeDB.FallbackFont = new SystemFont
            {
                FontNames = new string[] { "Microsoft YaHei", "Noto Sans CJK SC", "Segoe UI" },
            };
        }
        catch (Exception e)
        {
            GD.Print($"[hud] 默认字体设置失败(回退内置): {e.Message}");
        }
    }

    private void BuildFileDialog()
    {
        _fileDialog = new FileDialog
        {
            Title = "打开 CLI 生成的回放文件",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.json ; 回放文件 (ReplayFile)" },
        };
        AddChild(_fileDialog);
        _fileDialog.FileSelected += LoadReplay;
    }

    public override void _Process(double delta)
    {
        if (_session is null)
        {
            return;
        }
        TickVisualFrameStats(delta);
        HandleCommands();

        if (_settingsSmokeFramesLeft > 0)
        {
            _settingsSmokeFramesLeft--;
            if (_settingsSmokeFramesLeft == 0)
            {
                GD.Print("[settings-smoke] UI 构建冒烟通过");
                GetTree().Quit(_smokeExit);
                return;
            }
        }

        if (_editor.Active)
        {
            // 编辑模式: 只展示草稿预览帧, 不推进任何仿真时钟。
            Present(_editor.PreviewFrame
                ?? SnapshotView.From(_session.Engine.CommitSnapshot(), _session.Engine.Scenario.Field.PlatformHeight));
        }
        else if (_session.Mode == SessionMode.Live)
        {
            if (_liveDriver is not null)
            {
                if (_liveDriver.TryTakeLatest(out var driverSnapshot))
                {
                    _driverSnapshot = driverSnapshot;
                }
                Present(_driverSnapshot is { } latestDriver
                    ? Project(latestDriver)
                    : EmptyFrame());
            }
            else if (_session.StepLive(delta, out var snapshot))
            {
                Present(snapshot is not null ? Project(snapshot) : EmptyFrame());
            }
            else
            {
                Present(_session.LatestSnapshot is { } snap ? Project(snap) : EmptyFrame());
            }
        }
        else
        {
            if (_session.ReplayPlaying)
            {
                if (_session.AdvanceReplayPlayback(delta))
                {
                    _replayAlphaAccumulator = 0;   // 刚跨过 tick: 对齐新快照
                }
                if (_session.ReplayAtEnd)
                {
                    _session.ReplayPlaying = false;
                    _replayAlphaAccumulator = 0;
                }
            }
            if (!_session.ReplayPlaying && _session.ReplayCache.Count > 0)
            {
                _replayAlphaAccumulator = Math.Min(
                    1.0, _replayAlphaAccumulator + ReplayAlphaSettlePerSecond * delta);
            }
            Present(_session.ReplayFrame(_replayAlphaAccumulator));
        }

        TickCapture();
    }

    private void TickVisualFrameStats(double delta)
    {
        if (_visualFrameStatsLeft < 0)
        {
            return;
        }
        var milliseconds = delta * 1000.0;
        _visualFrameStatsCount++;
        _visualFrameStatsSumMs += milliseconds;
        _visualFrameStatsMinMs = Math.Min(_visualFrameStatsMinMs, milliseconds);
        _visualFrameStatsMaxMs = Math.Max(_visualFrameStatsMaxMs, milliseconds);
        _visualFrameStatsLeft--;
        if (_visualFrameStatsLeft == 0)
        {
            GD.Print($"[visual-qa] frame stats: count={_visualFrameStatsCount} "
                + $"avg={_visualFrameStatsSumMs / _visualFrameStatsCount:0.###}ms "
                + $"min={_visualFrameStatsMinMs:0.###}ms max={_visualFrameStatsMaxMs:0.###}ms");
        }
    }

    // ---------- visual QA capture (--capture <png>) ----------

    private void TickCapture()
    {
        if (_captureFramesLeft < 0 || _capturePath.Length == 0)
        {
            return;
        }
        _captureFramesLeft--;
        if (_captureFramesLeft > 0)
        {
            return;
        }
        // 无头 dummy 渲染器没有真实视口纹理: 冒烟结果照常上报, 截图跳过,
        // 退出码不受影响 (真实渲染证据由 --rendering-method gl_compatibility 运行产出)。
        if (DisplayServer.GetName() == "headless")
        {
            GD.Print($"[capture] headless dummy renderer: 截图跳过 ({_capturePath}), smoke 退出码 {_smokeExit}");
            GetTree().Quit(_smokeExit);
            return;
        }
        try
        {
            var img = GetViewport().GetTexture().GetImage();
            var dir = Path.GetDirectoryName(_capturePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var saveError = img.SavePng(_capturePath);
            if (saveError != Error.Ok)
            {
                throw new IOException($"SavePng returned {saveError}");
            }
            _captureStats = DumpPixelStats(img);
            GD.Print($"[capture] saved {_capturePath} {img.GetWidth()}x{img.GetHeight()}");
            GD.Print($"[capture] stats: {_captureStats}");
            GetTree().Quit(_smokeExit);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[capture] 失败: {e.Message}");
            GetTree().Quit(1);
        }
    }

    /// <summary>Counts pixels near known scene colors; proves each visual layer rendered.</summary>
    private static string DumpPixelStats(Image img)
    {
        var buckets = new Dictionary<string, int>
        {
            ["us"] = 0, ["them"] = 0, ["buff"] = 0, ["debuff"] = 0,
            ["platform"] = 0, ["floor"] = 0, ["model"] = 0,
        };
        var w = img.GetWidth();
        var h = img.GetHeight();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = img.GetPixel(x, y);
                if (c.R > 0.6f && c.B > 0.6f && c.G < 0.6f)
                {
                    buckets["model"]++; // 品红测试模型 (robot-cube.gltf); 灯光/tonemap 会把绿色抬到 ~0.55
                }
                // Official energy marks are deliberately classified before the
                // team colors: the debuff's red X is close to the red robot under
                // the broad presentation-color tolerance used by this QA bucket.
                else if (IsOfficialBuffPixel(c))
                {
                    buckets["buff"]++;
                }
                else if (IsOfficialDebuffPixel(c))
                {
                    buckets["debuff"]++;
                }
                else if (Close(c, UsColor) || Close(c, ThemColor))
                {
                    buckets[Close(c, UsColor) ? "us" : "them"]++;
                }
                else if (c.R > 0.7f && c.G > 0.7f && c.B > 0.7f && Mathf.Abs(c.R - c.G) < 0.05f)
                {
                    buckets["platform"]++;
                }
                else if (c.R < 0.25f && c.G < 0.27f && c.B < 0.32f)
                {
                    buckets["floor"]++;
                }
            }
        }
        return string.Join(" ", buckets.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static bool Close(Color a, Color b)
    {
        const float tol = 0.35f;
        return Mathf.Abs(a.R - b.R) < tol && Mathf.Abs(a.G - b.G) < tol && Mathf.Abs(a.B - b.B) < tol;
    }

    private static bool IsOfficialBuffPixel(Color c)
        => c.R > 0.65f && c.G > 0.65f && c.B < 0.25f;

    private static bool IsOfficialDebuffPixel(Color c)
        => c.R > 0.65f && c.G < 0.18f && c.B < 0.18f;

    // us/them colors plus representative official energy-mark colors used by
    // the capture bucket check (visual QA evidence, not rule logic).
    private static readonly Color UsColor = new(0.28f, 0.48f, 0.95f);
    private static readonly Color ThemColor = new(0.92f, 0.30f, 0.28f);

    private void Present(RenderFrame frame)
    {
        // 布局编辑器拥有鼠标时 (选择/拖动), 相机指针处理必须让位。
        _camera.PointerInputEnabled = !_editor.Active;
        _visualizer.ShowFrame(frame);
        _camera.SetFocus(frame);
        AccumulateEvents(frame, _session.Mode);
        var hud = frame.Hud with { RecentEvents = _eventBuffer.ToArray() };
        _hud.UpdateFrame(frame with { Hud = hud }, _session.Mode,
            _session.Mode == SessionMode.Replay ? _session.ReplayTickForIndex(_session.ReplayIndex) : 0,
            _session.ReplayCache.Count,
            _session.ReplayPlaying,
            _camera.Mode,
            _camera.Mode switch
            {
                CameraMode.Overview => _camera.OverviewYaw,
                CameraMode.Follow => _camera.FollowYaw,
                _ => _camera.TopYaw,
            });
        _hud.UpdateEditor(_editor.Active, _editor.SelectedLabel, _editor.InspectorLine,
            _editor.StatusLine, _editor.CanApplyNow);
        _hud.UpdateControllerStatus(_liveDriver?.Status);
    }

    /// <summary>
    /// Appends the snapshot's incremental events into the persistent feed
    /// buffer (dedup by tick: the same snapshot is presented many frames).
    /// Mode switches clear the buffer (live ↔ replay are different matches).
    /// </summary>
    private void AccumulateEvents(RenderFrame frame, SessionMode mode)
    {
        if (mode != _lastEventMode)
        {
            _eventBuffer.Clear();
            _lastEventTick = -1;
            _lastEventMode = mode;
        }
        if (frame.Hud.Tick == _lastEventTick)
        {
            return;
        }
        _lastEventTick = frame.Hud.Tick;
        _eventBuffer.AddRange(frame.Hud.RecentEvents);
        if (_eventBuffer.Count > 8)
        {
            _eventBuffer.RemoveRange(0, _eventBuffer.Count - 8);
        }
    }

    private static RenderFrame EmptyFrame() => new()
    {
        Us = new RobotVisual { Role = RoleNames.Us },
        Them = new RobotVisual { Role = RoleNames.Them },
    };

    private void HandleCommands()
    {
        if (Input.IsActionJustPressed("settings_toggle"))
        {
            OpenSettings();
            return;
        }
        if (_settingsPanel is not null && _settingsPanel.IsOpen)
        {
            return; // 设置模态层拥有键盘/鼠标输入，比赛状态不被快捷键误触。
        }
        if (Input.IsActionJustPressed("editor_toggle"))
        {
            TryToggleEditor();
            return;
        }
        if (Input.IsActionJustPressed("camera_cycle"))
        {
            _camera.CycleMode();
        }
        if (_editor.Active)
        {
            return; // 编辑模式屏蔽比赛/回放控制, 防止边跑边改
        }

        if (_session.Mode == SessionMode.Replay)
        {
            HandleReplayCommands();
            return;
        }
        HandleLiveCommands();
    }

    private void TryToggleEditor()
    {
        if (_editor.Active)
        {
            _editor.Close();
            GD.Print("[editor] 退出布局编辑 (未应用的改动不生效)");
            return;
        }
        // 门禁判定在 EditorGate (纯函数, Sim.Tests 回归): Prep 与 Ready 都算「尚未发令」。
        // 原先只认 Prep —— 内核在发令准备满 60 s 后自动转 Ready, 界面仍显示「发令准备」,
        // 编辑入口却静默失效 (2026-10-06 修复)。
        if (EditorGate.RejectReason(_session.Mode, _liveDriver is not null, _session.Engine.Phase) is { } reason)
        {
            RejectEditor(reason);
            return;
        }
        _editor.Enter(_session.ScenarioWithResolvedBlocks());
        GD.Print("[editor] 进入布局编辑: 点击选择, 拖动移动, [ ] 旋转, S 吸附, B 添加能量块, K 切换增益/减益, Del 删除块, Ctrl+Z/Y 撤销/重做, Enter 应用, E 退出");
    }

    /// <summary>
    /// Logs a rejected layout-editor entry and mirrors the reason onto the HUD.
    /// A windowed run has no visible console, so a log-only reason reads to the
    /// user as "the E key does nothing".
    /// </summary>
    private void RejectEditor(string reason)
    {
        GD.Print($"[editor] {reason}");
        _hud.ShowNotice(reason);
    }

    private void ApplyLayoutScenario(Scenario scenario)
    {
        StopLiveDriver();
        // Layout edits change geometry/starts/blocks, while desktop parameter
        // overrides remain a separate pending layer. Keep scenario-authored
        // parameters as the template so selecting "自动" can really remove a
        // desktop override on the next reset.
        var templateParameters = _scenarioTemplate?.Parameters is null
            ? null
            : new Dictionary<string, double>(_scenarioTemplate.Parameters);
        _scenarioTemplate = scenario with { Parameters = templateParameters };
        // 编辑器产物不再等于启动时 --scenario-path 指向的文件: 清掉旧来源, 否则
        // F5/配置包导出会继续引用已过期的场景文件 (批1 导出按"当前场景"取内容)。
        ScenarioPath = "";
        var applied = ApplyDesktopSettings(_scenarioTemplate);
        ReplaceSession(applied);
        _pendingMatchSettings = false;
        ApplyScenarioToShell(applied);
        StartLiveDriverIfConfigured(applied);
        _editor.Close();
        GD.Print($"[editor] 布局已应用: pose=({applied.Field.Pose?.X ?? 0:0.00},{applied.Field.Pose?.Y ?? 0:0.00},{applied.Field.Pose?.Th ?? 0:0.00}rad)"
            + $" 能量块已冻结为固定坐标 (seed={applied.Seed})");
    }

    private void RestoreShellScenario()
    {
        ApplyScenarioToShell(_session.Engine.Scenario);
    }

    private void HandleLiveCommands()
    {
        var engine = _session.Engine;
        if (Input.IsActionJustPressed("ui_accept"))
        {
            if (LivePhase is MatchControlPhase.Prep or MatchControlPhase.Ready)
            {
                ArmLive();
            }
        }
        if (Input.IsActionJustPressed("pause_toggle"))
        {
            if (_liveDriver is not null)
            {
                if (LivePaused)
                {
                    _liveDriver.RequestResume();
                }
                else
                {
                    _liveDriver.RequestPause();
                }
            }
            else
            {
                if (engine.Paused)
                {
                    engine.Resume();
                }
                else
                {
                    engine.Pause("桌面端手动暂停");
                }
            }
        }
        if (Input.IsActionJustPressed("restart_us"))
        {
            TryRestartRobot(RoleNames.Us);
        }
        if (Input.IsActionJustPressed("restart_them"))
        {
            TryRestartRobot(RoleNames.Them);
        }
        if (Input.IsActionJustPressed("reset_match"))
        {
            ResetLiveSession("[shell] 已重置为同 seed 新比赛，应用当前设置");
        }
        if (Input.IsActionJustPressed("open_replay"))
        {
            _fileDialog.Popup();
        }
    }

    /// <summary>
    /// Referee R/T: real restart of one robot (back to start pose, transients
    /// cleaned, opponent +3 per the 2026 restart rule). Only legal while the
    /// match is live; the engine owns the rule, the shell only routes the
    /// command and reports the result.
    /// </summary>
    private void TryRestartRobot(string role)
    {
        if (_liveDriver is not null)
        {
            if (LivePhase is not (MatchControlPhase.Running or MatchControlPhase.Paused))
            {
                GD.Print("[referee] 真实重启仅在比赛进行中 (RUNNING/PAUSED) 可用: 先发令再使用");
                return;
            }
            _liveDriver.RequestRestart(role);
            GD.Print($"[referee] 已向后台 driver 投递 {(role == RoleNames.Us ? "我方" : "对手")} 重启");
            return;
        }
        var engine = _session.Engine;
        if (engine.Phase is not (MatchControlPhase.Running or MatchControlPhase.Paused))
        {
            GD.Print("[referee] 真实重启仅在比赛进行中 (RUNNING/PAUSED) 可用: 先发令再使用");
            return;
        }
        if (engine.RestartRobot(role))
        {
            GD.Print($"[referee] 已重启 {(role == RoleNames.Us ? "我方" : "对手")}: 回到出发点, 对方 +3");
        }
        else
        {
            GD.Print("[referee] 重启被拒绝 (当前阶段不允许)");
        }
        // HUD/画面随下一帧提交的快照刷新; 场景保持不变。
    }

    private void HandleReplayCommands()
    {
        if (Input.IsActionJustPressed("replay_toggle"))
        {
            _session.ReplayPlaying = !_session.ReplayPlaying;
        }
        if (Input.IsActionJustPressed("replay_step_back"))
        {
            _session.ReplayPlaying = false;
            _session.ReplayStep(-1);
            _session.ResetReplayClock();
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("replay_step_fwd"))
        {
            _session.ReplayPlaying = false;
            _session.ReplayStep(+1);
            _session.ResetReplayClock();
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("replay_seek_start"))
        {
            _session.ReplayPlaying = false;
            _session.ReplaySeekTick(1);
            _session.ResetReplayClock();
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("replay_seek_end"))
        {
            _session.ReplayPlaying = false;
            _session.ReplaySeekTick(_session.ReplayCache.Count);
            _session.ResetReplayClock();
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("reset_match"))
        {
            ResetLiveSession("[shell] 已重置回实况模式，应用当前设置");
        }
    }

    private void LoadReplay(string path)
    {
        try
        {
            StopLiveDriver();
            var file = ProtocolJson.Deserialize<ReplayFile>(System.IO.File.ReadAllText(path));
            _session.LoadReplay(file);
            _scenarioTemplate = file.Scenario;
            // 回放文件内嵌完整场景: 展示几何跟随它, 保证与录制端同一场地。
            ApplyScenarioToShell(file.Scenario);
            _replayAlphaAccumulator = 0;
            GD.Print($"[replay] 已加载 {path}: {file.Ticks} ticks, {file.EventFingerprints.Count} 事件"
                + $" (得分 {file.FinalScores.Us:0.#}:{file.FinalScores.Them:0.#})");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[replay] 加载失败 {path}: {e.Message}");
        }
    }

    public override void _ExitTree()
    {
        StopLiveDriver();
        _session?.Dispose();
    }

    // ---------- headless parity check ----------

    private bool TryRunParityCheck()
    {
        var args = OS.GetCmdlineUserArgs();
        var index = Array.IndexOf(args, "--parity-check");
        if (index < 0 || index + 1 >= args.Length)
        {
            return false;
        }
        var path = ResolveUserPath(args[index + 1]);
        try
        {
            var file = ProtocolJson.Deserialize<ReplayFile>(System.IO.File.ReadAllText(path));
            var report = ParityCheck.Verify(file);
            GD.Print($"parity-check {path}: scores {report.Scores.Us:0.#}:{report.Scores.Them:0.#}"
                + $" (expected {file.FinalScores.Us:0.#}:{file.FinalScores.Them:0.#})"
                + $" ticks {report.Ticks}/{file.Ticks} done={report.DoneReason ?? "(none)"}"
                + $" events {report.EventCount}/{file.EventFingerprints.Count}");
            if (report.Pass)
            {
                GD.Print("PASS: Godot shell reproduces the CLI-recorded match (score, done reason, final tick, event fingerprints).");
                GetTree().Quit(0);
            }
            else
            {
                GD.PrintErr($"FAIL: {report.Error ?? report.FirstDivergence}");
                GetTree().Quit(1);
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"parity-check FAIL: {e.Message}");
            GetTree().Quit(2);
        }
        return true;
    }
}
