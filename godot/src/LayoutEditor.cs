// 布局编辑层 (Godot 侧交互): 选择/拖动/旋转/吸附/撤销/重做/恢复官方/打开/
// 另存为/应用。所有编辑只写入纯 C# 的 LayoutDraft, 不触碰当前 MatchEngine;
// 应用 (Apply) 时由 Main 重建会话。预览帧通过临时 MatchEngine 快照投影获得,
// 与比赛模式共用同一 SnapshotView 管线, 不存在第二套几何真值。

using Godot;
using Sim.Core;
using Sim.Protocol;

namespace Sim.GodotShell;

public partial class LayoutEditor : Node3D
{
    private enum Selection
    {
        None,
        Field,
        ZoneUs,
        ZoneThem,
        Block,
        RobotUs,
        RobotThem,
    }

    /// <summary>One analytic entity hit: which render proxy the camera ray
    /// touched, how far along the ray (visual nearest wins), and the identifying
    /// block index or role. Pure input geometry — never a physics body.</summary>
    private sealed record EntityHit(Selection Kind, int BlockIndex, string Role, float Distance);

    private const float NudgeStep = 0.05f; // 键盘微调步长 (m, 场局部)
    // 输入命中代理的容差 (纯输入层, 不进入任何仿真/渲染几何):
    private const float PickMargin = 0.02f;      // 代理边界外扩, 便于点中视觉边缘
    private const float RobotPickHeight = 0.3f;  // 车辆代理高度: 高于 primitive 车体与常见 glTF 外观, 低视角也能点中
    private const float VerticalPickHeight = 10f; // 地面点程序化选中的垂直射线起点
    private const float SelectMargin = 0.06f;    // 选中高亮块超出对象的边距 (与块/区高亮一致)

    private Camera3D _camera = null!;
    private ArenaVisualizer _visualizer = null!;
    private LayoutDraft? _draft;
    private Selection _selected = Selection.None;
    private int _selectedBlock = -1;
    private bool _snap = true;
    private bool _dragging;
    private Vector2 _dragPrevWorld;
    private RenderFrame? _previewFrame;
    private string _message = "";
    private MeshInstance3D _highlight = null!;
    private FileDialog _saveDialog = null!;
    private FileDialog _openDialog = null!;
    private Scenario _lastValidPreviewScenario = null!;

    public bool Active { get; private set; }

    /// <summary>Current draft (null when not editing). Exposed for the automated edit-smoke run.</summary>
    public LayoutDraft? Draft => _draft;

    /// <summary>Selects the object at a simulation-world ground point (programmatic entry to the pick path).
    /// Routes through the same entity-first picker as the mouse: a vertical ray at the point hits entity
    /// proxies (blocks/vehicles), then the ground fallback (zone/field) applies.</summary>
    public void SelectAtGround(double worldX, double worldZ)
    {
        var origin = new Vector3((float)worldX, VerticalPickHeight, (float)worldZ);
        PickFromRay(origin, new Vector3(0, -1, 0));
    }

    /// <summary>Screen-space pick entry for the automated edit-smoke: drives the
    /// exact same entity-first picker the mouse uses (camera ray through the point).</summary>
    public void SelectAtScreen(Vector2 screenPos) => PickAtScreen(screenPos);

    /// <summary>Applies the selected object's move semantics by a delta in the selection's move frame
    /// (world axes for the whole field, field-local axes for zones/blocks). Programmatic drag evidence.</summary>
    public void NudgeSelectedBy(double dx, double dy) => NudgeSelected(dx, dy);

    /// <summary>Raised after the draft changes (preview/invalid state/HUD refresh).</summary>
    public event Action? PreviewChanged;

    /// <summary>Raised when the user applies the draft; Main rebuilds the session.</summary>
    public event Action<Scenario>? Applied;

    /// <summary>Raised on exit without apply; Main restores the live scenario.</summary>
    public event Action? Closed;

    public void Bind(Camera3D camera, ArenaVisualizer visualizer)
    {
        _camera = camera;
        _visualizer = visualizer;
        _highlight = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1, 0.01f, 1) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 1f, 0.2f, 0.35f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            Visible = false,
        };
        AddChild(_highlight);

        _saveDialog = new FileDialog
        {
            Title = "保存布局场景",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Filters = new[] { "*.json ; 场景文件 (Scenario)" },
        };
        _openDialog = new FileDialog
        {
            Title = "打开布局场景",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.json ; 场景文件 (Scenario)" },
        };
        _saveDialog.FileSelected += OnSaveSelected;
        _openDialog.FileSelected += OnOpenSelected;
        AddChild(_saveDialog);
        AddChild(_openDialog);
    }

    // ---------- mode lifecycle ----------

    /// <summary>Enters edit mode drafting from a fresh scenario (spawn-resolved).</summary>
    public void Enter(Scenario baseScenario)
    {
        _draft = new LayoutDraft(baseScenario);
        _lastValidPreviewScenario = baseScenario;
        _selected = Selection.Field;
        _selectedBlock = -1;
        _message = "";
        Active = true;
        RefreshPreview();
    }

    public void Close()
    {
        Active = false;
        _dragging = false;
        _draft = null;
        _previewFrame = null;
        _highlight.Visible = false;
        Closed?.Invoke();
    }

    // ---------- preview ----------

    /// <summary>Render frame for the current draft (spawn state), or null.</summary>
    public RenderFrame? PreviewFrame => _previewFrame;

    private void RefreshPreview()
    {
        if (_draft is null)
        {
            return;
        }
        var scenario = _draft.BuildScenario();
        var errors = scenario.Validate().ToList();
        try
        {
            var engine = new MatchEngine(scenario);
            var snap = engine.CommitSnapshot();
            _previewFrame = SnapshotView.From(snap, scenario.Field.PlatformHeight);
            if (errors.Count == 0)
            {
                _lastValidPreviewScenario = scenario;
                _message = "";
            }
            else
            {
                _message = $"布局暂不可保存: {errors[0]}";
            }
            _visualizer.Configure(errors.Count == 0 ? scenario : _lastValidPreviewScenario);
        }
        catch (ArgumentException e)
        {
            _message = $"布局暂不可保存: {e.Message.Split('\n')[0]}";
        }
        UpdateHighlight();
        PreviewChanged?.Invoke();
    }

    private void UpdateHighlight()
    {
        if (_draft is null)
        {
            _highlight.Visible = false;
            return;
        }
        var t = FieldTransform.FromPose(_draft.State.Pose);
        var scenario = _draft.BuildScenario();
        var field = scenario.Field;
        switch (_selected)
        {
            case Selection.Field:
            {
                var (cx, cy) = t.LocalToWorldPoint(field.FieldSize / 2, field.FieldSize / 2);
                _highlight.Visible = true;
                _highlight.Position = new Vector3((float)cx, 0.005f, (float)cy);
                _highlight.Rotation = new Vector3(0, -(float)(field.Pose?.Th ?? 0.0), 0);
                _highlight.Scale = new Vector3((float)field.FieldSize, 1, (float)field.FieldSize);
                break;
            }
            case Selection.ZoneUs or Selection.ZoneThem:
            {
                var role = _selected == Selection.ZoneUs ? RoleNames.Us : RoleNames.Them;
                if (field.StartZones.TryGetValue(role, out var zone) && zone is not null)
                {
                    var (cx, cy) = t.LocalToWorldPoint((zone.MinX + zone.MaxX) / 2, (zone.MinY + zone.MaxY) / 2);
                    _highlight.Visible = true;
                    _highlight.Position = new Vector3((float)cx, 0.01f, (float)cy);
                    _highlight.Rotation = new Vector3(0, -(float)(field.Pose?.Th ?? 0.0), 0);
                    _highlight.Scale = new Vector3(
                        (float)(zone.MaxX - zone.MinX) + 0.06f, 1, (float)(zone.MaxY - zone.MinY) + 0.06f);
                }
                break;
            }
            case Selection.Block when _selectedBlock >= 0 && _selectedBlock < _draft.State.Blocks.Count:
            {
                var block = _draft.State.Blocks[_selectedBlock];
                if (block.X is { } bx && block.Y is { } by)
                {
                    var (cx, cy) = t.LocalToWorldPoint(bx, by);
                    var r = (float)field.BlockSize + SelectMargin;
                    _highlight.Visible = true;
                    _highlight.Position = new Vector3((float)cx, 0.08f, (float)cy);
                    _highlight.Rotation = new Vector3(0, -(float)(field.Pose?.Th ?? 0.0), 0);
                    _highlight.Scale = new Vector3(r, 1, r);
                }
                break;
            }
            case Selection.RobotUs or Selection.RobotThem:
            {
                var role = _selected == Selection.RobotUs ? RoleNames.Us : RoleNames.Them;
                if (_draft.State.Starts.TryGetValue(role, out var start) && start is not null)
                {
                    var (cx, cy) = t.LocalToWorldPoint(start.X, start.Y);
                    // Ring marker at the start location, sized from the role's
                    // collision radius plus the shared selection margin; the
                    // base height follows the preview support plane so a start
                    // on the platform is not drawn under the platform body.
                    var baseHeight = RobotPreviewBaseHeight(role, field.PlatformHeight);
                    var diameter = 2f * ArenaVisualizer.RobotRadiusFor(scenario, role) + SelectMargin;
                    _highlight.Visible = true;
                    _highlight.Position = new Vector3((float)cx, (float)baseHeight + 0.012f, (float)cy);
                    _highlight.Rotation = new Vector3(0, -(float)(field.Pose?.Th ?? 0.0), 0);
                    _highlight.Scale = new Vector3(diameter, 1, diameter);
                }
                break;
            }
            default:
                _highlight.Visible = false;
                break;
        }
    }

    /// <summary>Support-plane height of a role's preview robot (platform top when spawned on it).</summary>
    private double RobotPreviewBaseHeight(string role, double platformHeight)
    {
        if (_previewFrame is null)
        {
            return 0.0;
        }
        var robot = role == RoleNames.Us ? _previewFrame.Us : _previewFrame.Them;
        return robot.OnPlatform ? platformHeight : 0.0;
    }

    // ---------- input ----------

    public override void _Process(double delta)
    {
        if (!Active || _draft is null || _saveDialog.Visible || _openDialog.Visible)
        {
            return;
        }
        if (Input.IsActionJustPressed("editor_undo"))
        {
            _draft.Undo();
            RefreshPreview();
        }
        if (Input.IsActionJustPressed("editor_redo"))
        {
            _draft.Redo();
            RefreshPreview();
        }
        if (Input.IsActionJustPressed("editor_snap_toggle"))
        {
            _snap = !_snap;
            _message = _snap ? "网格吸附: 开 (0.01m / 5°)" : "网格吸附: 关";
            PreviewChanged?.Invoke();
        }
        if (Input.IsActionJustPressed("editor_rotate_ccw"))
        {
            RotateField(-LayoutDraft.RotationSnap);
        }
        if (Input.IsActionJustPressed("editor_rotate_cw"))
        {
            RotateField(LayoutDraft.RotationSnap);
        }
        if (Input.IsActionJustPressed("ui_accept"))
        {
            RequestApply();
        }
        var (nx, ny) = NudgeDelta();
        if (nx != 0 || ny != 0)
        {
            NudgeSelected(nx, ny);
        }
    }

    private void RotateField(double dyaw)
    {
        if (_draft is null)
        {
            return;
        }
        var pose = _draft.State.Pose;
        var th = pose.Th + dyaw;
        if (_snap)
        {
            th = LayoutDraft.SnapRotation(th);
        }
        _draft.SetFieldPose(pose with { Th = th });
        RefreshPreview();
    }

    private (double, double) NudgeDelta()
    {
        double dx = 0, dy = 0;
        if (Input.IsActionJustPressed("ui_left"))
        {
            dx -= NudgeStep;
        }
        if (Input.IsActionJustPressed("ui_right"))
        {
            dx += NudgeStep;
        }
        if (Input.IsActionJustPressed("ui_up"))
        {
            dy += NudgeStep;
        }
        if (Input.IsActionJustPressed("ui_down"))
        {
            dy -= NudgeStep;
        }
        return (dx, dy);
    }

    private void NudgeSelected(double dx, double dy)
    {
        switch (_selected)
        {
            case Selection.Field:
                _draft!.MoveField(dx, dy);
                break;
            case Selection.ZoneUs:
                _draft!.MoveStartZone(RoleNames.Us, dx, dy);
                break;
            case Selection.ZoneThem:
                _draft!.MoveStartZone(RoleNames.Them, dx, dy);
                break;
            case Selection.RobotUs or Selection.RobotThem:
            {
                var role = _selected == Selection.RobotUs ? RoleNames.Us : RoleNames.Them;
                var start = _draft!.State.Starts[role];
                _draft.MoveStart(role, start.X + dx, start.Y + dy);
                break;
            }
            case Selection.Block when _selectedBlock >= 0:
            {
                var block = _draft!.State.Blocks[_selectedBlock];
                _draft.MoveBlock(_selectedBlock, (block.X ?? 0) + dx, (block.Y ?? 0) + dy);
                break;
            }
        }
        RefreshPreview();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active || _draft is null)
        {
            return;
        }
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                // 实体命中优先 (能量块/小车走解析代理, 不靠 y=0 投影猜目标);
                // 地面投影只用于出发区/场地 fallback 与拖动锚点。
                PickAtScreen(mb.GlobalPosition);
                if (GroundPoint(mb.GlobalPosition) is { } hit)
                {
                    _dragging = true;
                    _dragPrevWorld = hit;
                    _draft.BeginGroup();
                }
                GetViewport().SetInputAsHandled();
            }
            else if (_dragging)
            {
                _dragging = false;
                _draft.EndGroup();
                RefreshPreview();
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            GetViewport().SetInputAsHandled();
            var world = GroundPoint(motion.GlobalPosition);
            if (world is not { } point)
            {
                return;
            }
            var raw = point - _dragPrevWorld;
            // Field moves in world axes; zones/blocks in field-local axes so
            // a rotated arena drags along the screen direction. Snapping is
            // applied in the selection's move frame.
            var t = FieldTransform.FromPose(_draft.State.Pose);
            var localOnly = _selected != Selection.Field;
            var (lx, ly) = localOnly ? t.WorldToLocalVector(raw.X, raw.Y) : ((double)raw.X, raw.Y);
            var dx = _snap ? LayoutDraft.SnapTranslation(lx) : lx;
            var dy = _snap ? LayoutDraft.SnapTranslation(ly) : ly;
            if (dx == 0 && dy == 0)
            {
                return;
            }
            // The group keeps the pre-drag state; each motion mutates in place.
            NudgeSelected(dx, dy);
            var (wx, wy) = localOnly ? t.LocalToWorldVector(dx, dy) : (dx, dy);
            _dragPrevWorld = new Vector2(_dragPrevWorld.X + (float)wx, _dragPrevWorld.Y + (float)wy);
        }
    }

    /// <summary>Ray-casts the pointer onto the ground plane (y=0), returning sim (x, y).</summary>
    private Vector2? GroundPoint(Vector2 screenPos)
    {
        var from = _camera.ProjectRayOrigin(screenPos);
        var dir = _camera.ProjectRayNormal(screenPos);
        return GroundPoint(from, dir);
    }

    /// <summary>Intersects an explicit ray with the ground plane (y=0), sim (x, y).</summary>
    private static Vector2? GroundPoint(Vector3 from, Vector3 dir)
    {
        if (Mathf.Abs(dir.Y) < 1e-6)
        {
            return null;
        }
        var t = -from.Y / dir.Y;
        if (t <= 0)
        {
            return null;
        }
        var point = from + dir * t;
        return new Vector2(point.X, point.Z); // 仿真 y 轴 = 世界 z 轴
    }

    /// <summary>
    /// Full pick pipeline for one pointer position: entity proxies first
    /// (energy blocks and both vehicles, nearest ray hit wins, ties prefer a
    /// robot), then the ground fallback (start zone / whole field / clear).
    /// Entity selection never infers the target from the y=0 intersection —
    /// at low camera angles that point can be far behind a raised cube or
    /// vehicle body (PRD R4).
    /// </summary>
    private void PickAtScreen(Vector2 screenPos)
    {
        PickFromRay(_camera.ProjectRayOrigin(screenPos), _camera.ProjectRayNormal(screenPos));
    }

    private void PickFromRay(Vector3 origin, Vector3 dir)
    {
        if (_draft is null)
        {
            return;
        }
        var scenario = _draft.BuildScenario();
        if (TrySelectEntity(origin, dir, scenario))
        {
            RefreshPreview();
            return;
        }
        var world = GroundPoint(origin, dir);
        if (world is { } hit)
        {
            PickGround(hit, scenario.Field);
            return;
        }
        _selected = Selection.None;
        _selectedBlock = -1;
        RefreshPreview();
    }

    /// <summary>Ground fallback: start zones and the whole field. Energy blocks
    /// are entities and are never selected from the ground projection alone.</summary>
    private void PickGround(Vector2 world, FieldParams field)
    {
        var t = FieldTransform.FromPose(_draft!.State.Pose);
        var (lx, ly) = t.WorldToLocalPoint(world.X, world.Y);

        if (TryHitZone(field, RoleNames.Us, lx, ly))
        {
            _selected = Selection.ZoneUs;
            _selectedBlock = -1;
            RefreshPreview();
            return;
        }
        if (TryHitZone(field, RoleNames.Them, lx, ly))
        {
            _selected = Selection.ZoneThem;
            _selectedBlock = -1;
            RefreshPreview();
            return;
        }
        if (lx >= 0 && ly >= 0 && lx <= field.FieldSize && ly <= field.FieldSize)
        {
            _selected = Selection.Field;
            _selectedBlock = -1;
            RefreshPreview();
            return;
        }
        _selected = Selection.None;
        _selectedBlock = -1;
        RefreshPreview();
    }

    /// <summary>Selects the nearest entity proxy under the ray; returns false when
    /// nothing is hit so the ground fallback can run.</summary>
    private bool TrySelectEntity(Vector3 origin, Vector3 dir, Scenario scenario)
    {
        var hits = EntityHits(origin, dir, scenario);
        if (hits.Count == 0)
        {
            return false;
        }
        var best = hits[0];
        foreach (var hit in hits)
        {
            var robotBias = IsRobot(hit.Kind) && !IsRobot(best.Kind)
                && MathF.Abs(hit.Distance - best.Distance) < 1e-4f;
            if (hit.Distance < best.Distance || robotBias)
            {
                best = hit;
            }
        }
        _selected = best.Kind;
        _selectedBlock = best.Kind == Selection.Block ? best.BlockIndex : -1;
        return true;
    }

    private static bool IsRobot(Selection selection)
        => selection is Selection.RobotUs or Selection.RobotThem;

    /// <summary>
    /// Analytic hit proxies under one ray. Blocks use world-aligned boxes at the
    /// draft's fixed (editable) coordinates through the shared
    /// <see cref="FieldTransform"/> — seeded (null) placements are not
    /// draft-editable and stay unselectable, matching the drag mutation.
    /// Vehicles use upright proxies at the preview (spawn) position, sized from
    /// the role's collision radius plus a small input margin and a generous
    /// pick height; the proxy is independent of primitive/glTF rendering and
    /// never becomes a physics body. No rotation math lives here: world
    /// positions come from <see cref="FieldTransform"/>, ray data from the camera.
    /// </summary>
    private List<EntityHit> EntityHits(Vector3 origin, Vector3 dir, Scenario scenario)
    {
        var hits = new List<EntityHit>();
        if (_draft is null)
        {
            return hits;
        }
        var field = scenario.Field;
        var t = FieldTransform.FromPose(_draft.State.Pose);
        var platform = field.Platform;

        for (var i = 0; i < _draft.State.Blocks.Count; i++)
        {
            var block = _draft.State.Blocks[i];
            if (block.X is not { } bx || block.Y is not { } by)
            {
                continue;
            }
            var (wx, wy) = t.LocalToWorldPoint(bx, by);
            var baseHeight = bx >= platform.MinX && bx <= platform.MaxX
                && by >= platform.MinY && by <= platform.MaxY
                ? field.PlatformHeight : 0.0;
            var half = (float)(field.BlockSize / 2) + PickMargin;
            var min = new Vector3((float)wx - half, (float)baseHeight - PickMargin, (float)wy - half);
            var max = new Vector3((float)wx + half, (float)(baseHeight + field.BlockSize) + PickMargin, (float)wy + half);
            if (RayHitsBox(origin, dir, min, max, out var distance))
            {
                hits.Add(new EntityHit(Selection.Block, i, "", distance));
            }
        }

        AddVehicleHit(hits, origin, dir, _previewFrame?.Us, RoleNames.Us, scenario);
        AddVehicleHit(hits, origin, dir, _previewFrame?.Them, RoleNames.Them, scenario);
        return hits;
    }

    private void AddVehicleHit(
        List<EntityHit> hits, Vector3 origin, Vector3 dir, RobotVisual? robot, string role, Scenario scenario)
    {
        if (robot is null)
        {
            return;
        }
        var radius = ArenaVisualizer.RobotRadiusFor(scenario, role) + PickMargin;
        var baseHeight = robot.OnPlatform ? scenario.Field.PlatformHeight : 0.0;
        var min = new Vector3((float)(robot.Position.X - radius), (float)baseHeight, (float)(robot.Position.Z - radius));
        var max = new Vector3(
            (float)(robot.Position.X + radius), (float)(baseHeight + RobotPickHeight), (float)(robot.Position.Z + radius));
        if (RayHitsBox(origin, dir, min, max, out var distance))
        {
            hits.Add(new EntityHit(role == RoleNames.Us ? Selection.RobotUs : Selection.RobotThem, -1, role, distance));
        }
    }

    /// <summary>Slab test of a ray against a world-aligned box; reports the entry
    /// distance along the ray when the box is hit in front of the origin.</summary>
    private static bool RayHitsBox(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float distance)
    {
        distance = 0f;
        var tmin = 1e-4f;
        var tmax = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = origin[axis];
            var d = dir[axis];
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < min[axis] || o > max[axis])
                {
                    return false;
                }
                continue;
            }
            var t1 = (min[axis] - o) / d;
            var t2 = (max[axis] - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax)
            {
                return false;
            }
        }
        distance = tmin;
        return true;
    }

    private static bool TryHitZone(FieldParams field, string role, double lx, double ly)
        => field.StartZones.TryGetValue(role, out var zone) && zone is not null
            && lx >= zone.MinX - 0.02 && lx <= zone.MaxX + 0.02
            && ly >= zone.MinY - 0.02 && ly <= zone.MaxY + 0.02;

    // ---------- HUD callbacks ----------

    public string SelectedLabel => _selected switch
    {
        Selection.Field => "场地整体",
        Selection.ZoneUs => "黄色出发区",
        Selection.ZoneThem => "蓝色出发区",
        Selection.Block => _selectedBlock >= 0 ? $"能量块 #{_selectedBlock + 1}" : "能量块",
        Selection.RobotUs => "我方小车",
        Selection.RobotThem => "对手小车",
        _ => "无 (点击选择)",
    };

    public string StatusLine => _message;

    public string InspectorLine
    {
        get
        {
            if (_draft is null)
            {
                return "";
            }
            var pose = _draft.State.Pose;
            return $"位姿 x={pose.X:+0.00;-0.00;0.00}m y={pose.Y:+0.00;-0.00;0.00}m "
                + $"θ={pose.Th * 180 / Math.PI:0.0}° 吸附={( _snap ? "开" : "关")}";
        }
    }

    public bool CanApplyNow => _draft?.CanApply == true;

    public void RequestSave() => _saveDialog.PopupCentered(new Vector2I(860, 620));

    public void RequestOpen() => _openDialog.PopupCentered(new Vector2I(860, 620));

    public void RequestRestoreOfficial()
    {
        _draft?.RestoreOfficial();
        RefreshPreview();
    }

    public void RequestUndo()
    {
        _draft?.Undo();
        RefreshPreview();
    }

    public void RequestRedo()
    {
        _draft?.Redo();
        RefreshPreview();
    }

    public void RequestApply()
    {
        if (_draft is null || !_draft.CanApply)
        {
            _message = "布局不合法, 无法应用";
            PreviewChanged?.Invoke();
            return;
        }
        Applied?.Invoke(_draft.BuildScenario());
    }

    private void OnSaveSelected(string path)
    {
        if (_draft is null)
        {
            return;
        }
        try
        {
            _draft.SaveTo(path);
            _message = $"已保存: {path}";
        }
        catch (Exception e)
        {
            _message = $"保存失败: {e.Message}";
        }
        PreviewChanged?.Invoke();
    }

    private void OnOpenSelected(string path)
    {
        if (_draft is null)
        {
            return;
        }
        try
        {
            _draft.LoadFrom(LayoutDraft.ReadScenario(path));
            _message = $"已载入布局: {path}";
            RefreshPreview();
        }
        catch (Exception e)
        {
            _message = $"载入失败: {e.Message}";
            PreviewChanged?.Invoke();
        }
    }
}
