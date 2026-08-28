// 观察相机: 概览 / 跟随 / 俯视 三模式, C 键切换。跟随模式有阻尼平滑;
// 相机只读取渲染帧的建议焦点, 从不写回 Sim.Core (design: 只读观察者)。
//
// 指针交互 (仅布局编辑器未激活时):
//   左键拖动 — 抓取语义: 把指针射线投到地面 (y=0), 被抓住的地面点跟随光标
//   (焦点按世界位移的反方向平移);
//   滚轮 — 缩放, 距离/高度按基准取景倍率限幅 (概览/俯视), 跟随模式缩放跟拍距离。
// 被相机消费的事件一律标记为已处理, 不再下传; 布局编辑器激活时由 Main 关闭
// PointerInputEnabled, 编辑器的选择/拖拽行为不受影响。

using Godot;

namespace Sim.GodotShell;

public enum CameraMode
{
    Overview,
    Follow,
    Top,
}

public partial class MatchCamera : Camera3D
{
    // ConfigureArena 之前的兜底取景 (镜像官方默认场景的场地中心/边长);
    // Main 启动时会用 Scenario 立即重新取景, 这里不是第二份几何真值。
    private Vector3 _center = new(1.9f, 0f, 1.9f);
    private float _fieldSize = 3.8f;

    private readonly Vector3 _followOffset = new(0f, 3.4f, 3.8f);

    private CameraMode _mode = CameraMode.Overview;

    // 概览: 地面焦点 + 方向 + 限幅距离; 俯视: 地面焦点 + 限幅高度。
    private Vector3 _overviewDir = new(0f, 0.7725f, 0.6338f); // (0,1.95,1.6) 归一化
    private float _baseOverviewDistance = 9.59f;
    private float _overviewDistance = 9.59f;
    private Vector3 _overviewFocus = new(1.9f, 0f, 1.9f);
    private float _baseTopHeight = 9.5f;
    private float _topHeight = 9.5f;
    private Vector3 _topFocus = new(1.9f, 0f, 1.9f);
    private float _followZoom = 1f;

    // 阻尼运动目标 (俯视位姿固定, 不走阻尼)。
    private Vector3 _positionGoal;
    private Vector3 _lookGoal;
    private Vector3 _lookCurrent = new(1.9f, 0f, 1.9f);
    private float _damp = 6.0f;

    private bool _dragging;
    private Vector2 _dragAnchor;

    public CameraMode Mode => _mode;

    /// <summary>
    /// Ownership hook: when false (layout editor owns the mouse) the camera
    /// ignores all pointer input. Main mirrors <c>LayoutEditor.Active</c> here.
    /// </summary>
    public bool PointerInputEnabled { get; set; } = true;

    /// <summary>Current ground-plane focus (world), for deterministic input evidence.</summary>
    public Vector3 FocusPoint => _mode switch
    {
        CameraMode.Overview => _overviewFocus,
        CameraMode.Top => _topFocus,
        _ => _followFocus,
    };

    /// <summary>Overview view distance (clamped), for deterministic input evidence.</summary>
    public float OverviewDistance => _overviewDistance;

    /// <summary>Top-view height (clamped), for deterministic input evidence.</summary>
    public float TopHeight => _topHeight;

    /// <summary>Follow-mode zoom multiplier (clamped), for deterministic input evidence.</summary>
    public float FollowZoom => _followZoom;

    private Vector3 _followFocus;

    public override void _Ready()
    {
        ResetGoals();
        _lookCurrent = _lookGoal;
        Position = _positionGoal;
        LookAt(_lookCurrent, Vector3.Up);
    }

    /// <summary>Frames the overview/top shots around the (possibly moved) arena.</summary>
    public void ConfigureArena(Vec3 center, double fieldSize)
    {
        _center = new Vector3((float)center.X, 0f, (float)center.Z);
        _fieldSize = (float)fieldSize;
        // 原有概览取景比例 (高 1.95 : 后 1.6, 相对场地边长) 与俯视高度 2.5。
        var dir = new Vector3(0f, _fieldSize * 1.95f, _fieldSize * 1.6f);
        _baseOverviewDistance = dir.Length();
        _overviewDir = dir / _baseOverviewDistance;
        _baseTopHeight = _fieldSize * 2.5f;
        _overviewFocus = _center;
        _overviewDistance = _baseOverviewDistance;
        _topFocus = _center;
        _topHeight = _baseTopHeight;
        _followZoom = 1f;
        if (_mode == CameraMode.Top)
        {
            ApplyTopPose();
        }
        else
        {
            ResetGoals();
        }
    }

    public void CycleMode()
    {
        _mode = _mode switch
        {
            CameraMode.Overview => CameraMode.Follow,
            CameraMode.Follow => CameraMode.Top,
            _ => CameraMode.Overview,
        };
        if (_mode == CameraMode.Top)
        {
            // 正俯视时 LookAt 的上向量会退化, 直接设置姿态: 绕 X 轴 -90°
            // (相机看向 -Y, 屏幕上方对应世界 -Z, 与概览视角同向)。
            ApplyTopPose();
        }
        else
        {
            ResetGoals(); // 阻尼从当前机位滑向新模式目标
        }
    }

    private Vector3 TopPosition => _topFocus + new Vector3(0f, _topHeight, 0f);

    private void ApplyTopPose()
    {
        Position = TopPosition;
        RotationDegrees = new Vector3(-90f, 0f, 0f);
    }

    private void ResetGoals()
    {
        switch (_mode)
        {
            case CameraMode.Overview:
                _positionGoal = _overviewFocus + _overviewDir * _overviewDistance;
                _lookGoal = _overviewFocus;
                break;
            case CameraMode.Follow:
                _positionGoal = _followFocus + _followOffset * _followZoom;
                _lookGoal = _followFocus;
                break;
        }
    }

    public void SetFocus(RenderFrame frame)
    {
        // 跟随模式: 自动以双方中点为焦点 (缩放只改跟拍距离, 不与 SetFocus 抢焦点)。
        // 概览/俯视: 使用可拖动的地面焦点, 不跟随机器人中点。
        if (_mode != CameraMode.Follow)
        {
            return;
        }
        _followFocus = new Vector3((float)frame.CameraFocus.X, 0.05f, (float)frame.CameraFocus.Z);
        ResetGoals();
    }

    public override void _Process(double delta)
    {
        if (_mode == CameraMode.Top)
        {
            ApplyTopPose(); // 位姿由焦点/高度直接决定 (支持拖动与缩放)
            return;
        }
        var t = Mathf.Clamp((float)delta * _damp, 0f, 1f);
        Position = Position.Lerp(_positionGoal, t);
        _lookCurrent = _lookCurrent.Lerp(_lookGoal, t);
        LookAt(_lookCurrent, Vector3.Up);
    }

    // ---------- pointer input (pan / zoom) ----------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!PointerInputEnabled)
        {
            return;
        }
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && _mode != CameraMode.Follow)
                {
                    if (GroundPoint(mb.Position) is { } hit)
                    {
                        _dragging = true;
                        _dragAnchor = new Vector2(hit.X, hit.Z);
                        GetViewport().SetInputAsHandled();
                    }
                }
                else if (!mb.Pressed && _dragging)
                {
                    _dragging = false;
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
            {
                ZoomSteps(-1);
                GetViewport().SetInputAsHandled();
            }
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
            {
                ZoomSteps(+1);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            if (GroundPoint(motion.Position) is { } hit)
            {
                var current = new Vector2(hit.X, hit.Z);
                PanBy(new Vector3(current.X - _dragAnchor.X, 0f, current.Y - _dragAnchor.Y));
                _dragAnchor = current;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Ray-casts a screen position onto the ground plane (y=0), world space.</summary>
    private Vector3? GroundPoint(Vector2 screenPos)
    {
        var from = ProjectRayOrigin(screenPos);
        var dir = ProjectRayNormal(screenPos);
        if (Mathf.Abs(dir.Y) < 1e-6f)
        {
            return null;
        }
        var t = -from.Y / dir.Y;
        if (t <= 0f)
        {
            return null;
        }
        return from + dir * t;
    }

    /// <summary>
    /// Translates the mode's ground focus by a world-space delta (grab
    /// semantics: the grabbed ground point follows the cursor, so the focus
    /// moves opposite the cursor's ground delta).
    /// </summary>
    private void PanBy(Vector3 groundDelta)
    {
        if (_mode == CameraMode.Overview)
        {
            _overviewFocus = ClampFocus(_overviewFocus - groundDelta);
            ResetGoals();
        }
        else if (_mode == CameraMode.Top)
        {
            _topFocus = ClampFocus(_topFocus - groundDelta);
        }
        // 跟随模式焦点由渲染帧驱动, 不支持拖动平移。
    }

    /// <summary>Keeps the ground focus within 场地中心 ± 场地边长 (walkway margin included).</summary>
    private Vector3 ClampFocus(Vector3 focus)
    {
        var limit = _fieldSize;
        return new Vector3(
            Mathf.Clamp(focus.X, _center.X - limit, _center.X + limit),
            0f,
            Mathf.Clamp(focus.Z, _center.Z - limit, _center.Z + limit));
    }

    /// <summary>
    /// Wheel zoom: one notch = ×1.1 (steps &gt; 0 zooms out). Distance/height
    /// clamp to 0.3–3× (overview) / 0.5–3× (top) of the base framing, so the
    /// full field stays visible and the camera cannot reach degenerate poses.
    /// </summary>
    private void ZoomSteps(int steps)
    {
        const float factor = 1.1f;
        var scale = (float)Math.Pow(factor, steps);
        switch (_mode)
        {
            case CameraMode.Overview:
                _overviewDistance = Mathf.Clamp(
                    _overviewDistance * scale,
                    _baseOverviewDistance * 0.3f,
                    _baseOverviewDistance * 3f);
                ResetGoals();
                break;
            case CameraMode.Top:
                _topHeight = Mathf.Clamp(
                    _topHeight * scale,
                    _baseTopHeight * 0.5f,
                    _baseTopHeight * 3f);
                break;
            case CameraMode.Follow:
                _followZoom = Mathf.Clamp(_followZoom * scale, 0.5f, 2.5f);
                ResetGoals();
                break;
        }
    }
}
