using Godot;
using System.Globalization;
using System.Text;
using Sim.Protocol;
using Sim.Core;

namespace Sim.GodotShell;

/// <summary>Independent viewer of recorded real-world data; never drives the match.</summary>
public partial class RealMatchReviewPanel : Control
{
    private PanelContainer _dialog = null!;
    private Label _status = null!, _step = null!, _stats = null!, _referenceDetail = null!;
    private TextEdit _details = null!, _log = null!;
    private HSlider _timeline = null!;
    private Button _play = null!;
    private LineEdit _name = null!, _start = null!, _end = null!, _x = null!, _y = null!, _heading = null!;
    private CheckBox _stationary = null!;
    private ReviewChart _chart = null!;
    private ReviewMap _map = null!;
    private ArenaVisualizer _arena = null!;
    private Scenario _scenario = null!;
    private Label _poseStatus = null!;
    private FileDialog _picker = null!;
    private Action<string>? _selected;
    private ReviewRecording? _recording;
    private ReviewSeries? _series;
    private readonly List<ReviewReference> _references = [];
    private readonly List<int> _steps = [];
    private IReadOnlyList<ReviewPose> _poses = [];
    private int _index;
    private bool _sync, _playing;
    private double _elapsed;
    public bool IsOpen => Visible;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        Build();
        SyncRect();
    }

    public void SetUiScale(double value)
    {
        var scale = Mathf.Clamp((float)value, 0.8f, 1.4f);
        Scale = new Vector2(scale, scale);
        if (IsInsideTree()) SyncRect();
    }

    public void Open(Scenario scenario, ArenaVisualizer arena)
    {
        _scenario = scenario;
        _arena = arena;
        var field = scenario.Field;
        _map.FieldSize = field.FieldSize;
        _map.PlatformMinX = field.Platform.MinX;
        _map.PlatformMinY = field.Platform.MinY;
        _map.PlatformMaxX = field.Platform.MaxX;
        _map.PlatformMaxY = field.Platform.MaxY;
        UpdatePoses();
        _map.QueueRedraw();
        Visible = true;
        SyncRect();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        SyncRect();
        if (!_playing || _recording is null) return;
        _elapsed += delta;
        while (_playing && _index < _recording.Events.Count - 1)
        {
            var delay = _recording.Events[_index + 1].Time - _recording.Events[_index].Time;
            if (_elapsed < delay) break;
            _elapsed -= delay;
            SetStep(_index + 1);
        }
        if (_index >= _recording.Events.Count - 1) SetPlaying(false);
    }

    public override void _Input(InputEvent e)
    {
        if (Visible && e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && !_picker.Visible)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    public event Action? Closed;
    private void Close() { SetPlaying(false); Visible = false; Closed?.Invoke(); }

    private void SyncRect()
    {
        var size = GetViewport().GetVisibleRect().Size;
        AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
        Position = Vector2.Zero;
        Size = size;
        PivotOffset = Vector2.Zero;
        var w = Math.Min(420, size.X * .42f / Scale.X);
        var h = size.Y / Scale.Y - 20;
        _dialog.Position = new Vector2(size.X / Scale.X - w - 10, 10);
        _dialog.Size = new Vector2(w, h);
    }

    private void Build()
    {
        _dialog = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(.055f, .085f, .14f, .98f), BorderColor = new Color(.3f, .52f, .82f), ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10 };
        style.SetBorderWidthAll(1);
        _dialog.AddThemeStyleboxOverride("panel", style);
        AddChild(_dialog);
        var outer = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        _dialog.AddChild(outer);
        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        outer.AddChild(root);
        var header = Row(root);
        var title = Label(header, "实车复盘", 19);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Button(header, "关闭", Close);
        Label(root, "比赛已挂起；主界面比分为原比赛数据。", 12).AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status = Label(root, "请选择记录或采样文件。", 12);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var tabs = new TabContainer { CustomMinimumSize = new Vector2(0, 360), SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(tabs);
        var record = Page(tabs, "记录复盘");
        var row = Row(record);
        Button(row, "导入记录…", () => Pick("导入实车记录", false, ["*.csv,*.json ; 实车记录"], LoadRecording));
        Button(row, "打开独立日志…", () => Pick("打开无时间戳日志", false, ["*.log ; 日志"], LoadLog));
        row = Row(record);
        Button(row, "上一条", () => { SetPlaying(false); StepDecision(-1); });
        _play = Button(row, "播放", () => SetPlaying(!_playing));
        Button(row, "下一条", () => { SetPlaying(false); StepDecision(1); });
        _step = Label(record, "未载入记录", 12);
        _timeline = new HSlider { MinValue = 0, MaxValue = 1, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(100, 24) };
        _timeline.ValueChanged += value => { if (!_sync) { SetPlaying(false); SetStep((int)value); } };
        record.AddChild(_timeline);
        _poseStatus = Label(record, "未记录位姿", 12);
        _poseStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Label(record, "记录详情：▶ 为当前事件", 12);
        _details = Text(record, 180);
        Label(record, "独立日志：无可靠时间戳，不与记录同步", 12);
        _log = Text(record, 120);

        var sample = Page(tabs, "采样与参考点");
        row = Row(sample);
        Button(row, "导入采样 CSV…", () => Pick("导入采样 CSV", false, ["*.csv ; 采样 CSV"], LoadSeries));
        Button(row, "保存参考点…", () => Pick("保存参考点", true, ["*.json ; 参考点 JSON"], SaveReferences));
        row = Row(sample);
        Button(row, "打开参考点…", () => Pick("打开参考点", false, ["*.json ; 参考点 JSON"], LoadReferences));
        Label(sample, "按原列名展示；两版左右通道映射待核实。曲线横轴为相对时间，按行降采样，缺失处断开。", 12).AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _chart = new ReviewChart { CustomMinimumSize = new Vector2(300, 180), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sample.AddChild(_chart);
        _stats = Label(sample, "未载入采样", 12);
        _stats.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row = Row(sample);
        _start = Field(row, "起始索引", "0"); _end = Field(row, "结束索引", "0");
        row = Row(sample);
        _stationary = new CheckBox { Text = "确认区间静止" }; row.AddChild(_stationary);
        row = Row(sample);
        _name = Field(row, "点名", ""); _x = Field(row, "X (m)", "");
        row = Row(sample);
        _y = Field(row, "Y (m)", ""); _heading = Field(row, "航向 (rad)", "");
        row = Row(sample);
        Button(row, "建立参考点", AddReference);
        Label(sample, "场地示意：原点在左下角；浅色边框为台沿，圆点为人工标注。无小车位置或轨迹。", 12).AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _map = new ReviewMap { CustomMinimumSize = new Vector2(300, 280), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _map.Selected += ShowReference;
        sample.AddChild(_map);
        _referenceDetail = Label(sample, "尚未选择参考点", 12);
        _referenceDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _picker = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true };
        AddChild(_picker);
        _picker.FileSelected += path => _selected?.Invoke(path);
    }

    private static VBoxContainer Page(TabContainer tabs, string title)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        tabs.AddChild(scroll);
        tabs.SetTabTitle(tabs.GetTabCount() - 1, title);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(box);
        return box;
    }
    private static HBoxContainer Row(Container parent) { var row = new HBoxContainer(); parent.AddChild(row); return row; }
    private static Label Label(Container parent, string text, int size) { var l = new Label { Text = text, ClipText = false }; l.AddThemeFontSizeOverride("font_size", size); parent.AddChild(l); return l; }
    private static Button Button(Container parent, string text, Action action) { var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 32) }; b.Pressed += action; parent.AddChild(b); return b; }
    private static TextEdit Text(Container parent, float height)
    {
        var t = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(300, height), SizeFlagsHorizontal = SizeFlags.ExpandFill, WrapMode = TextEdit.LineWrappingMode.Boundary };
        t.AddThemeColorOverride("font_readonly_color", new Color(.85f, .90f, .96f));
        parent.AddChild(t);
        return t;
    }
    private static LineEdit Field(Container parent, string label, string value) { Label(parent, label, 12); var field = new LineEdit { Text = value, CustomMinimumSize = new Vector2(70, 30), SizeFlagsHorizontal = SizeFlags.ExpandFill }; parent.AddChild(field); return field; }
    private void Pick(string title, bool save, string[] filters, Action<string> action)
    {
        _selected = action;
        _picker.FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile;
        _picker.Title = title;
        _picker.Filters = filters;
        _picker.PopupCentered(new Vector2I(800, 550));
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { _status.Text = "操作失败：" + error.Message; GD.PrintErr("[real-review] " + error); }
    }
    private void LoadRecording(string path) => Run(() =>
    {
        var recording = RealMatchReviewData.LoadRecording(path);
        var poses = RealMatchReviewData.ExtractPoses(recording);
        _recording = recording;
        _poses = poses;
        _steps.Clear();
        for (var i = 0; i < recording.Events.Count; i++) if (recording.Events[i].Type == "decision") _steps.Add(i);
        if (_steps.Count == 0) for (var i = 0; i < recording.Events.Count; i++) _steps.Add(i);
        _timeline.MaxValue = Math.Max(1, recording.Events.Count - 1);
        SetPlaying(false); SetStep(0);
        _log.Text = "无已打开的日志。";
        var logPath = Path.ChangeExtension(path, ".log");
        if (File.Exists(logPath)) _log.Text = File.ReadAllText(logPath);
        _status.Text = $"已载入 {path} · {recording.Format} · {_steps.Count} 步；同名日志独立显示";
    });
    private void LoadLog(string path) => Run(() => { _log.Text = File.ReadAllText(path); _status.Text = "已打开独立日志：" + path; });
    private void SetPlaying(bool value) { _playing = value && _recording is not null && _index < _recording.Events.Count - 1; _elapsed = 0; if (_play is not null) _play.Text = _playing ? "暂停" : "播放"; }
    private void StepDecision(int direction)
    {
        if (_recording is null) return;
        var target = direction < 0 ? _steps.LastOrDefault(i => i < _index, 0)
            : _steps.FirstOrDefault(i => i > _index, _recording.Events.Count - 1);
        SetStep(target);
    }
    private void SetStep(int index)
    {
        if (_recording is null || _steps.Count == 0) return;
        _index = Math.Clamp(index, 0, _recording.Events.Count - 1);
        _sync = true; _timeline.Value = _index; _sync = false;
        var start = _steps.LastOrDefault(i => i <= _index, 0);
        var end = _steps.FirstOrDefault(i => i > _index, _recording.Events.Count);
        _step.Text = $"事件 {_index + 1} / {_recording.Events.Count} · {_recording.Events[_index].Time:0.###} s";
        var b = new StringBuilder();
        if (_recording.Format == "patrol-csv") b.AppendLine("FSM：未记录   视觉：未记录   左右电机：未记录");
        for (var i = start; i < end; i++)
        {
            var ev = _recording.Events[i];
            var marker = i == _index ? "▶ " : i > _index ? "  [后续] " : "  ";
            b.AppendLine($"{marker}[{i}] {ev.Time:0.###} s · {ev.Type}");
            foreach (var item in ev.Values) b.AppendLine($"  {item.Key}: {item.Value ?? "未记录"}");
        }
        b.AppendLine("FSM / 视觉 / 左右电机：未提供的字段为未记录。");
        _details.Text = b.ToString();
        UpdatePoses();
    }
    private void UpdatePoses()
    {
        var us = RealMatchReviewData.PoseAtOrBefore(_poses, "us", _recording is null ? -1 : _index);
        var them = RealMatchReviewData.PoseAtOrBefore(_poses, "them", _recording is null ? -1 : _index);
        ShowPose("us", us);
        ShowPose("them", them);
        static string Describe(string role, ReviewPose? pose) => pose is null ? role + "：未记录位姿"
            : $"{role}：记录于 {pose.Time:0.###} s · source: {pose.Source}";
        _poseStatus.Text = Describe("我方", us) + "\n" + Describe("对手", them)
            + "\n擂台为当前场景示意；最近记录无插值或外推。\n高度为显示投影，不是实测。";
    }
    public void PresentPoses() { if (Visible) UpdatePoses(); }

    public Vector3? RecordedFocus
    {
        get
        {
            var us = _arena.RobotRoot("us")!;
            var them = _arena.RobotRoot("them")!;
            if (us.Visible && them.Visible) return (us.Position + them.Position) / 2;
            if (us.Visible) return us.Position;
            if (them.Visible) return them.Position;
            return null;
        }
    }

    private void ShowPose(string role, ReviewPose? pose)
    {
        var root = _arena.RobotRoot(role)!;
        root.Visible = pose is not null;
        if (root.GetNodeOrNull<Node3D>("Ring") is { } ring) ring.Visible = false;
        if (pose is null) return;
        var field = _scenario.Field;
        var transform = new FieldTransform(field.Pose?.X ?? 0, field.Pose?.Y ?? 0, field.Pose?.Th ?? 0);
        var (x, y) = transform.LocalToWorldPoint(pose.X, pose.Y);
        var p = field.Platform;
        var height = pose.X >= p.MinX && pose.X <= p.MaxX && pose.Y >= p.MinY && pose.Y <= p.MaxY ? field.PlatformHeight + .001 : 0;
        root.Position = new Vector3((float)x, (float)height, (float)y);
        root.Rotation = new Vector3(0, (float)(Math.PI / 2 - transform.LocalToWorldHeading(pose.Heading)), 0);
    }
    private void LoadSeries(string path) => Run(() =>
    {
        _series = RealMatchReviewData.LoadSeries(path);
        _chart.Series = _series; _chart.QueueRedraw();
        _start.Text = "0"; _end.Text = (_series.Rows.Count - 1).ToString(CultureInfo.InvariantCulture); _stationary.ButtonPressed = false;
        var lines = new List<string> { $"行索引：0–{_series.Rows.Count - 1}（从 0 开始）", "数值列  count / min / max / mean" };
        var times = _series.Rows.Select(r => r.Time).ToArray();
        lines.Add($"t: {times.Length} / {times.Min():0.###} / {times.Max():0.###} / {times.Average():0.###}");
        foreach (var channel in _series.Channels)
        {
            var values = _series.Rows.Select(r => r.Values.TryGetValue(channel, out var v) ? v : null).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            lines.Add(values.Length == 0 ? $"{channel}: 0 / 未记录" : $"{channel}: {values.Length} / {values.Min():0.###} / {values.Max():0.###} / {values.Average():0.###}");
        }
        _stats.Text = string.Join("\n", lines); _status.Text = "已载入采样：" + path;
    });
    private void AddReference() => Run(() =>
    {
        if (_series is null) throw new InvalidOperationException("请先导入采样文件。");
        if (!_stationary.ButtonPressed) throw new InvalidOperationException("请先确认所选区间静止。");
        var culture = CultureInfo.InvariantCulture;
        var x = double.Parse(_x.Text, culture);
        var y = double.Parse(_y.Text, culture);
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > _map.FieldSize || y > _map.FieldSize)
            throw new ArgumentOutOfRangeException(nameof(x), $"参考点须在场地范围 0–{_map.FieldSize:0.###} m 内。");
        var reference = RealMatchReviewData.MakeReference(_series, _name.Text, int.Parse(_start.Text, culture), int.Parse(_end.Text, culture), x, y, double.Parse(_heading.Text, culture));
        _references.Add(reference); _map.References = _references; _map.QueueRedraw(); _stationary.ButtonPressed = false;
        ShowReference(reference); _status.Text = "参考点已建立；请另存为独立文件。";
    });
    private void SaveReferences(string path) => Run(() => { RealMatchReviewData.SaveReferences(path, _references); _status.Text = "参考点已保存：" + path; });
    private void LoadReferences(string path) => Run(() =>
    {
        var loaded = RealMatchReviewData.LoadReferences(path);
        _references.Clear(); _references.AddRange(loaded); _map.References = _references; _map.QueueRedraw();
        _referenceDetail.Text = "点击参考点查看详情";
        var outside = _references.Count(r => r.X < 0 || r.Y < 0 || r.X > _map.FieldSize || r.Y > _map.FieldSize);
        _status.Text = "已打开参考点：" + path + (outside > 0 ? $"；{outside} 个点超出当前场地范围，未绘制" : "");
    });
    private void ShowReference(ReviewReference r) => _referenceDetail.Text = $"{r.Name} · {r.Source} · 行 {r.StartIndex}–{r.EndIndex} · X={r.X:0.###} Y={r.Y:0.###} m · 航向={r.Heading:0.###} rad\n" + string.Join("\n", r.Stats.Select(s => s.Value.Count == 0 ? $"{s.Key}: 未记录" : $"{s.Key}: {s.Value.Count} / {s.Value.Min:0.###} / {s.Value.Max:0.###} / {s.Value.Mean:0.###}"));
}

public partial class ReviewChart : Control
{
    public ReviewSeries? Series { get; set; }
    private static readonly Color[] Colors = [new(0.4f, 0.7f, 1f), new(1f, .7f, .3f), new(.4f, .9f, .65f), new(1f, .45f, .5f)];
    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(.025f, .04f, .07f));
        if (Series is null || Series.Rows.Count == 0) return;
        var all = Series.Rows.SelectMany(r => r.Values.Values).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (all.Length == 0) return;
        var min = all.Min(); var span = Math.Max(1e-12, all.Max() - min);
        var startTime = Series.Rows[0].Time;
        var duration = Series.Rows[^1].Time - startTime;
        for (var c = 0; c < Series.Channels.Count; c++)
        {
            var channel = Series.Channels[c]; var color = Colors[c % Colors.Length];
            DrawString(ThemeDB.FallbackFont, new Vector2(8 + c * 95, 16), channel, HorizontalAlignment.Left, -1, 12, color);
            Vector2? previous = null;
            var stride = Math.Max(1, (int)Math.Ceiling(Series.Rows.Count / Math.Max(1.0, Size.X)));
            for (var i = 0; i < Series.Rows.Count; i++)
            {
                if (!Series.Rows[i].Values.TryGetValue(channel, out var value) || value is null) { previous = null; continue; }
                if (i % stride != 0 && i != Series.Rows.Count - 1) continue;
                var timeFraction = duration > 0 ? (Series.Rows[i].Time - startTime) / duration : 0.5;
                var point = new Vector2((float)timeFraction * (Size.X - 10) + 5, Size.Y - 5 - (float)((value.Value - min) / span) * (Size.Y - 30));
                if (previous is { } p) DrawLine(p, point, color, 1.5f);
                previous = point;
            }
        }
    }
}

public partial class ReviewMap : Control
{
    public double FieldSize { get; set; } = 3.8;
    public double PlatformMinX { get; set; }
    public double PlatformMinY { get; set; }
    public double PlatformMaxX { get; set; }
    public double PlatformMaxY { get; set; }
    public IReadOnlyList<ReviewReference> References { get; set; } = [];
    public event Action<ReviewReference>? Selected;
    private Rect2 FieldRect => new((Size.X - Math.Min(Size.X, Size.Y) + 20) / 2, 10, Math.Min(Size.X, Size.Y) - 20, Math.Min(Size.X, Size.Y) - 20);
    private Vector2 PositionOf(double x, double y) { var rect = FieldRect; return new Vector2(rect.Position.X + (float)(x / FieldSize) * rect.Size.X, rect.End.Y - (float)(y / FieldSize) * rect.Size.Y); }
    private Vector2 PositionOf(ReviewReference r) => PositionOf(r.X, r.Y);
    public override void _Draw()
    {
        var rect = FieldRect; DrawRect(rect, new Color(.11f, .15f, .20f)); DrawRect(rect, new Color(.4f, .6f, .8f), false, 2);
        var platformTopLeft = PositionOf(PlatformMinX, PlatformMaxY);
        var platformBottomRight = PositionOf(PlatformMaxX, PlatformMinY);
        var platform = new Rect2(platformTopLeft, platformBottomRight - platformTopLeft);
        DrawRect(platform, new Color(.19f, .23f, .28f));
        DrawRect(platform, new Color(.7f, .8f, .9f), false, 2);
        DrawString(ThemeDB.FallbackFont, rect.Position + new Vector2(4, 16), "+Y ↑   +X → (场局部米)", HorizontalAlignment.Left, -1, 12, Colors.White);
        foreach (var r in References.Where(IsInsideField)) { var p = PositionOf(r); DrawCircle(p, 5, new Color(1f, .75f, .3f)); DrawString(ThemeDB.FallbackFont, p + new Vector2(7, -4), r.Name, HorizontalAlignment.Left, -1, 12, Colors.White); }
    }
    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse) return;
        var nearest = References.Where(IsInsideField).Select(r => (Reference: r, Distance: PositionOf(r).DistanceTo(mouse.Position))).OrderBy(r => r.Distance).FirstOrDefault();
        if (nearest.Reference is not null && nearest.Distance <= 14) { Selected?.Invoke(nearest.Reference); AcceptEvent(); }
    }
    private bool IsInsideField(ReviewReference r) => r.X >= 0 && r.Y >= 0 && r.X <= FieldSize && r.Y <= FieldSize;
}
