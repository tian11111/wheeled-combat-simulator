// 桌面壳入口: 组装 MatchSession / ArenaVisualizer / HudPanel / MatchCamera,
// 把裁判指令 (发令/暂停/重启/重置/回放导航) 路由到 Sim.Core, 渲染层只消费
// SnapshotView 投影, 不复刻任何规则。
//
// 操作 (另见 HUD 右上角帮助):
//   Enter 发令 · P 暂停/继续 · R 我方重启 · T 对手重启 (真实重启, 对手 +4)
//   F5 重置同 seed 比赛 (回放模式回到实况并同步场景/相机) · C 切换镜头 · L 打开回放文件
//   镜头: 非编辑模式左键拖动平移 (概览/俯视, 抓取语义), 滚轮缩放 (限幅)
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
                _captureFramesLeft = 30;
                GD.Print($"[capture] 30 帧后保存视口到 {_capturePath}");
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

    /// <summary>
    /// Deterministic camera input evidence through the real input pipeline:
    /// Overview framing, wheel zoom (×1.1 steps + clamp), Top orientation
    /// (-90° pitch, full-field coverage), ground-plane grab-drag pan in
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
        var viewportSize = GetViewport().GetVisibleRect().Size;
        var screenCenter = viewportSize / 2f;

        // Overview: 焦点在场地中心, 机位 = 焦点 + (0,1.95,1.6) 归一化 × 距离。
        Check(_camera.Mode == CameraMode.Overview, "starts in Overview");
        await WaitFrames(3);
        Check(Near(_camera.FocusPoint.X, center.X) && Near(_camera.FocusPoint.Z, center.Z),
            "overview focus = arena center");
        var expectedPos = center + new Vector3(0, 1.95f, 1.6f).Normalized() * _camera.OverviewDistance;
        Check(_camera.Position.DistanceTo(expectedPos) < 0.05f,
            "overview position = focus + framing direction * distance");

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

        // Top: 绕 X 轴 -90° 正俯视, 高度覆盖完整场地, 焦点在场地中心。
        await InjectActionUntil("camera_cycle", () => _camera.Mode == CameraMode.Top);
        Check(_camera.Mode == CameraMode.Top, "cycles to Top");
        Check(Near(_camera.RotationDegrees.X, -90f), "top pitches -90° (straight down)");
        var halfTan = Math.Tan(_camera.Fov * Math.PI / 360.0);
        Check(_camera.TopHeight * halfTan >= fieldSize * 0.7071 - 0.01,
            $"top covers full field (height {_camera.TopHeight:0.00} m)");
        Check(Near(_camera.FocusPoint.X, center.X) && Near(_camera.FocusPoint.Z, center.Z),
            "top focus = arena center");

        // Top 拖动: 抓取语义 — 焦点按地面射线位移的反方向平移。拖动用小像素步长:
        // 无头 dummy 视口只有 64×64, 大位移会撞上焦点限幅, 破坏等值断言。
        const int dragPx = 8;
        await WaitSettled();
        var topS1 = screenCenter;
        var topS2 = screenCenter + new Vector2(dragPx, 0);
        var tg1 = GroundPointAt(_camera, topS1);
        var tg2 = GroundPointAt(_camera, topS2);
        Check(tg1 is not null && tg2 is not null, "top rays hit the ground plane");
        if (tg1 is { } g1 && tg2 is { } g2)
        {
            InjectDrag(topS1, topS2);
            await WaitFrames(2);
            Check(Near(_camera.FocusPoint.X, center.X - (g2.X - g1.X))
                && Near(_camera.FocusPoint.Z, center.Z - (g2.Z - g1.Z)),
                "top drag pans focus opposite the ground delta (grab semantics)");
            // 拖回原位。
            InjectDrag(topS2, topS1);
            await WaitFrames(2);
            Check(Near(_camera.FocusPoint.X, center.X) && Near(_camera.FocusPoint.Z, center.Z),
                "top drag back restores the center focus");
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

        // Overview 拖动: 同样的抓取语义 (世界跟随光标)。等待阻尼收敛后再取地面射线,
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
            InjectDrag(ovS1, ovS2);
            await WaitFrames(2);
            var focusNow = _camera.FocusPoint;
            Check(Near(focusNow.X, center.X - (og2v.X - og1v.X))
                && Near(focusNow.Z, center.Z - (og2v.Z - og1v.Z)),
                "overview drag pans focus with grab semantics");
        }

        // 编辑器拥有鼠标时相机让位: 拖动不再移动相机焦点。先等机位收敛, 再 F5 复位
        // (实况新比赛, TickIndex=0 才允许进入编辑模式 — 复位后同步进入, 不留 tick 窗口),
        // 然后注入拖动验证相机不消费指针。
        await WaitSettled();
        _session.ResetToLive();
        ApplyScenarioToShell(_session.Engine.Scenario);
        TryToggleEditor();
        Check(_editor.Active, "editor active (owns the pointer)");
        var focusBeforeEditorDrag = _camera.FocusPoint;
        InjectDrag(screenCenter, screenCenter + new Vector2(dragPx, dragPx / 2));
        await WaitFrames(2);
        Check(Near(_camera.FocusPoint.X, focusBeforeEditorDrag.X)
            && Near(_camera.FocusPoint.Z, focusBeforeEditorDrag.Z),
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

        static void InjectWheel(int steps)
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

        static void InjectDrag(Vector2 from, Vector2 to)
        {
            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
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
                ButtonIndex = MouseButton.Left,
                Pressed = false,
                Position = to,
                GlobalPosition = to,
            });
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
        var pose = draft.State.Pose;
        var t = new Sim.Core.FieldTransform(pose.X, pose.Y, pose.Th);
        var zone = draft.State.StartZones[RoleNames.Us];
        var (zwx, zwy) = t.LocalToWorldPoint((zone.MinX + zone.MaxX) / 2, (zone.MinY + zone.MaxY) / 2);
        _editor.SelectAtGround(zwx, zwy);
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

        // Restore official must undo everything and stay applicable.
        _editor.RequestRestoreOfficial();
        Check(_editor.CanApplyNow && Near(draft.State.Pose.X, 0) && Near(draft.State.Pose.Th, 0),
            "restore official layout");

        // Edit → apply → the rebuilt session engine runs the edited geometry.
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
        var (usX, usY) = new Sim.Core.FieldTransform(applied.X, applied.Y, applied.Th)
            .LocalToWorldPoint(0.95, 0.3);
        Check(Near(_session.Engine.Us.X, usX) && Near(_session.Engine.Us.Y, usY),
            "engine spawn follows applied pose");

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
                if (c.R > 0.6f && c.B > 0.6f && c.G < 0.45f)
                {
                    buckets["model"]++; // 品红测试模型 (robot-cube.gltf)
                }
                else if (Close(c, UsColor) || Close(c, ThemColor))
                {
                    buckets[Close(c, UsColor) ? "us" : "them"]++;
                }
                else if (Close(c, BuffColor) || Close(c, DebuffColor))
                {
                    buckets[Close(c, BuffColor) ? "buff" : "debuff"]++;
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

    // us/them/buff/debuff colors duplicated for the capture check; keep in sync
    // with ArenaVisualizer (visual QA evidence, not rule logic).
    private static readonly Color UsColor = new(0.28f, 0.48f, 0.95f);
    private static readonly Color ThemColor = new(0.92f, 0.30f, 0.28f);
    private static readonly Color BuffColor = new(0.24f, 0.82f, 0.72f);
    private static readonly Color DebuffColor = new(0.91f, 0.55f, 0.22f);

    private void Present(RenderFrame frame)
    {
        // 布局编辑器拥有鼠标时 (选择/拖动), 相机指针处理必须让位。
        _camera.PointerInputEnabled = !_editor.Active;
        _visualizer.ShowFrame(frame);
        _camera.SetFocus(frame);
        _hud.UpdateFrame(frame, _session.Mode,
            _session.Mode == SessionMode.Replay ? _session.ReplayTickForIndex(_session.ReplayIndex) : 0,
            _session.ReplayCache.Count,
            _session.ReplayPlaying,
            _camera.Mode);
        _hud.UpdateEditor(_editor.Active, _editor.SelectedLabel, _editor.InspectorLine,
            _editor.StatusLine, _editor.CanApplyNow);
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
        if (engine.TickIndex > 0 || engine.Phase != MatchControlPhase.Prep)
        {
            GD.Print("[editor] 比赛已进行中: 按 F5 重置为同 seed 新比赛后再编辑布局");
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
    /// cleaned, opponent +4). Only legal while the match is live; the engine
    /// owns the rule, the shell only routes the command and reports the result.
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
            GD.Print($"[referee] 已重启 {(role == RoleNames.Us ? "我方" : "对手")}: 回到出发点, 对方 +4");
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
