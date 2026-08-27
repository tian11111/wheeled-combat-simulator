// 桌面壳入口: 组装 MatchSession / ArenaVisualizer / HudPanel / MatchCamera,
// 把裁判指令 (发令/暂停/重启/重置/回放导航) 路由到 Sim.Core, 渲染层只消费
// SnapshotView 投影, 不复刻任何规则。
//
// 操作 (另见 HUD 右上角帮助):
//   Enter 发令 · P 暂停/继续 · R 我方重启 · T 对手重启 (+4)
//   F5 重置同 seed 比赛 · C 切换镜头 · L 打开回放文件
//   回放模式: 空格 播放/暂停 · ←/→ 单步 · Home/End 到首/末帧 · 拖动时间轴跳转
//
// 无头模式: `godot --headless --path godot -- --parity-check <replay.json>`
// 使用与 Sim.Cli replay-check 相同的语义比对最终比分/结束原因/末帧/事件指纹。

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
            _captureFramesLeft = 30;
            GD.Print($"[capture] 30 帧后保存视口到 {_capturePath}");
        }

        LoadRobotModelPreferences(userArgs);
        ApplyRobotModels();

        if (Array.IndexOf(userArgs, "--edit-smoke") >= 0)
        {
            _ = RunEditSmokeAsync();
            return;
        }

        GD.Print($"[shell] core={MatchEngine.CoreVersion} seed={scenario.Seed}"
            + $" tick={scenario.Field.TickSeconds}s duration={scenario.Field.MatchDuration}s"
            + $" mode={_session.Mode}");
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
            _captureFramesLeft = 2;
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
            engine.RestartPenalty(RoleNames.Us, "restart");
        }
        if (Input.IsActionJustPressed("restart_them"))
        {
            engine.RestartPenalty(RoleNames.Them, "restart");
        }
        if (Input.IsActionJustPressed("reset_match"))
        {
            _session.ResetToLive();
            GD.Print("[shell] 已重置为同 seed 新比赛");
        }
        if (Input.IsActionJustPressed("open_replay"))
        {
            _fileDialog.Popup();
        }
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
