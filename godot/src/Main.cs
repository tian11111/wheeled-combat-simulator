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
    private FileDialog _fileDialog = null!;
    private double _replayAlphaAccumulator;
    private int _captureFramesLeft = -1;
    private string _capturePath = "";
    private string _captureStats = "";

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

        var scenario = BuildScenario();
        _session = new MatchSession(scenario);

        _hud.ConfigureTimeline(tick => _session.ReplaySeekTick(tick));

        var userArgs = OS.GetCmdlineUserArgs();
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

        GD.Print($"[shell] core={MatchEngine.CoreVersion} seed={scenario.Seed}"
            + $" tick={scenario.Field.TickSeconds}s duration={scenario.Field.MatchDuration}s"
            + $" mode={_session.Mode}");
    }

    private Scenario BuildScenario()
        => string.IsNullOrEmpty(ScenarioPath)
            ? new Scenario { Seed = Seed, Blocks = OfficialLayout.Blocks }
            : ProtocolJson.Deserialize<Scenario>(System.IO.File.ReadAllText(ScenarioPath));

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

        if (_session.Mode == SessionMode.Live)
        {
            if (_session.StepLive(delta, out var snapshot))
            {
                Present(snapshot is not null ? SnapshotView.From(snapshot) : EmptyFrame());
            }
            else
            {
                Present(_session.LatestSnapshot is { } snap ? SnapshotView.From(snap) : EmptyFrame());
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
            GetTree().Quit(0);
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
            ["platform"] = 0, ["floor"] = 0,
        };
        var w = img.GetWidth();
        var h = img.GetHeight();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = img.GetPixel(x, y);
                if (Close(c, UsColor) || Close(c, ThemColor))
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
    }

    private static RenderFrame EmptyFrame() => new()
    {
        Us = new RobotVisual { Role = RoleNames.Us },
        Them = new RobotVisual { Role = RoleNames.Them },
    };

    private void HandleCommands()
    {
        if (Input.IsActionJustPressed("camera_cycle"))
        {
            _camera.CycleMode();
        }

        if (_session.Mode == SessionMode.Replay)
        {
            HandleReplayCommands();
            return;
        }
        HandleLiveCommands();
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