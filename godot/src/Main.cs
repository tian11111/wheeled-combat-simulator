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
    private Dictionary<string, RobotModelConfig>? _robotModels;
    private double _replayAlphaAccumulator;
    private int _captureFramesLeft = -1;
    private string _capturePath = "";
    private string _captureStats = "";
    private int _smokeExit;
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
        BuildFileDialog();

        var userArgs = OS.GetCmdlineUserArgs();
        var spIndex = Array.IndexOf(userArgs, "--scenario-path");
        if (spIndex >= 0 && spIndex + 1 < userArgs.Length)
        {
            ScenarioPath = Path.GetFullPath(userArgs[spIndex + 1]);
        }

        var scenario = BuildScenario();
        _session = new MatchSession(scenario);
        ApplyScenarioToShell(scenario);

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

        if (string.IsNullOrEmpty(autoReplay) && Array.IndexOf(userArgs, "--auto-arm") >= 0)
        {
            _session.Engine.Arm();
            GD.Print("[shell] --auto-arm: 已发令进入 RUNNING");
        }

        var captureIndex = Array.IndexOf(userArgs, "--capture");
        if (captureIndex >= 0 && captureIndex + 1 < userArgs.Length)
        {
            _capturePath = Path.GetFullPath(userArgs[captureIndex + 1]);
            if (Array.IndexOf(userArgs, "--edit-smoke") >= 0 || Array.IndexOf(userArgs, "--camera-smoke") >= 0)
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
        var recorder = new Sim.Core.MatchEngine(scenario);
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

    private Scenario BuildScenario()
        => string.IsNullOrEmpty(ScenarioPath)
            ? new Scenario { Seed = Seed, Blocks = OfficialLayout.Blocks }
            : ProtocolJson.Deserialize<Scenario>(System.IO.File.ReadAllText(ScenarioPath));

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
        HandleCommands();

        if (_editor.Active)
        {
            // 编辑模式: 只展示草稿预览帧, 不推进任何仿真时钟。
            Present(_editor.PreviewFrame
                ?? SnapshotView.From(_session.Engine.CommitSnapshot(), _session.Engine.Scenario.Field.PlatformHeight));
        }
        else if (_session.Mode == SessionMode.Live)
        {
            if (_session.StepLive(delta, out var snapshot))
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
            if (_session.ReplayPlaying && !_session.ReplayAtEnd)
            {
                _session.ReplayStep(+1);
                _replayAlphaAccumulator = 0;
            }
            else if (_session.ReplayPlaying)
            {
                _session.ReplayPlaying = false;
                _replayAlphaAccumulator = 0;
            }
            if (!_session.ReplayPlaying && _session.ReplayCache.Count > 0)
            {
                _replayAlphaAccumulator = Math.Min(1.0, _replayAlphaAccumulator + 0.02);
            }
            Present(_session.ReplayFrame(_replayAlphaAccumulator));
        }

        TickCapture();
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
        if (_session.Mode == SessionMode.Replay)
        {
            GD.Print("[editor] 回放模式不能编辑布局: 按 F5 回到实况");
            return;
        }
        var engine = _session.Engine;
        // 门禁只看阶段: 实况 Prep = 比赛尚未发令。TickIndex 在 Prep 空转时也以
        // 20 Hz 递增, 用它做门禁会让人工永远进不了编辑器 (0.05 s 后即 >0)。
        if (engine.Phase != MatchControlPhase.Prep)
        {
            GD.Print(engine.Phase == MatchControlPhase.Finished
                ? "[editor] 比赛已结束: 按 F5 重置为同 seed 新比赛后再编辑布局"
                : "[editor] 比赛已进行中: 按 F5 重置为同 seed 新比赛后再编辑布局");
            return;
        }
        _editor.Enter(_session.ScenarioWithResolvedBlocks());
        GD.Print("[editor] 进入布局编辑: 点击选择, 拖动移动, [ ] 旋转, S 吸附, Ctrl+Z/Y 撤销/重做, Enter 应用, E 退出");
    }

    private void ApplyLayoutScenario(Scenario scenario)
    {
        _session = new MatchSession(scenario);
        ApplyScenarioToShell(scenario);
        _editor.Close();
        GD.Print($"[editor] 布局已应用: pose=({scenario.Field.Pose?.X ?? 0:0.00},{scenario.Field.Pose?.Y ?? 0:0.00},{scenario.Field.Pose?.Th ?? 0:0.00}rad)"
            + $" 能量块已冻结为固定坐标 (seed={scenario.Seed})");
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
            if (engine.Phase is MatchControlPhase.Prep or MatchControlPhase.Ready)
            {
                engine.Arm();
            }
        }
        if (Input.IsActionJustPressed("pause_toggle"))
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
            _session.ResetToLive();
            // 场景/可视化根/相机按重置后的引擎场景重建 (回放 F5 后同源)。
            ApplyScenarioToShell(_session.Engine.Scenario);
            GD.Print("[shell] 已重置为同 seed 新比赛");
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
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("replay_step_fwd"))
        {
            _session.ReplayPlaying = false;
            _session.ReplayStep(+1);
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("replay_seek_start"))
        {
            _session.ReplayPlaying = false;
            _session.ReplaySeekTick(1);
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("replay_seek_end"))
        {
            _session.ReplayPlaying = false;
            _session.ReplaySeekTick(_session.ReplayCache.Count);
            _replayAlphaAccumulator = 0;
        }
        if (Input.IsActionJustPressed("reset_match"))
        {
            _session.ResetToLive();
            // 回放 → 实况: 可视化根与相机按新引擎的场景 (回放内嵌场景) 重建,
            // 保证模拟状态、展示几何与镜头三者同步。
            ApplyScenarioToShell(_session.Engine.Scenario);
            GD.Print("[shell] 已重置回实况模式");
        }
    }

    private void LoadReplay(string path)
    {
        try
        {
            var file = ProtocolJson.Deserialize<ReplayFile>(System.IO.File.ReadAllText(path));
            _session.LoadReplay(file);
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

    // ---------- headless parity check ----------

    private bool TryRunParityCheck()
    {
        var args = OS.GetCmdlineUserArgs();
        var index = Array.IndexOf(args, "--parity-check");
        if (index < 0 || index + 1 >= args.Length)
        {
            return false;
        }
        var path = Path.GetFullPath(args[index + 1]);
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
