// 观察相机: 概览 / 跟随 / 俯视 三模式, C 键切换。跟随模式有阻尼平滑;
// 相机只读取渲染帧的建议焦点, 从不写回 Sim.Core (design: 只读观察者)。
//
// 指针交互 (仅布局编辑器未激活时):
//   左键拖动 — 转动视角: 概览/跟随绕焦点环绕 (偏航 + 俯仰限幅), 俯视绕视线轴自旋;
//   右键拖动 — 画面平移 (抓取语义: 把指针射线投到地面 y=0, 被抓住的地面点跟随光标;
//   跟随模式焦点由渲染帧驱动, 不支持平移);
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

    // 概览: 地面焦点 + 环绕角(偏航/俯仰) + 限幅距离; 俯视: 地面焦点 + 限幅高度 + 自旋角;
    // 跟随: 帧驱动焦点 + 环绕角 + 限幅跟拍倍率。默认角 = DefaultOverview*Ratio 取景比例。
    private float _overviewYaw;
    private float _overviewPitch = DefaultOverviewPitch;
    // 兜底初值镜像官方默认场地 3.8m × 默认概览比例 (Main 启动会用 Scenario 重新取景)。
    private float _baseOverviewDistance = 4.05f;
    private float _overviewDistance = 4.05f;
    private Vector3 _overviewFocus = new(1.9f, 0f, 1.9f);
    private float _baseTopHeight = 9.5f;
    private float _topHeight = 9.5f;
    private float _topYaw;
    private Vector3 _topFocus = new(1.9f, 0f, 1.9f);
    private float _followYaw;
    private float _followPitch = DefaultFollowPitch;
    private float _followZoom = 1f;
    // ConfigureArena/首个渲染帧前的跟随焦点兜底 (镜像场地中心), 首帧立即覆盖。
    private Vector3 _followFocus = new(1.9f, 0f, 1.9f);

    // 默认环绕角: 概览方向 = DefaultOverviewHeight/BackRatio (仰角 50.36°)、跟随偏移
    // (0,3.4,3.8) 的仰角。概览比例按 PRD R1 取景目标反推: 场地包围盒在 16:9 视口
    // (720p/1080p, vfov 75°) 中约占宽 52% / 高 54%, 四边留出安全边距 (原比例
    // (0,1.95,1.6) 仅约 17%/21%, 场地缩在中央)。
    private const float DefaultOverviewHeightRatio = 0.82f;
    private const float DefaultOverviewBackRatio = 0.68f;
    private const float DefaultOverviewPitch = 50.36f;
    private const float DefaultFollowPitch = 41.82f;
    // 环绕角限幅: 俯仰不能贴地也不能越过正俯视 (俯视模式俯仰固定 -90°)。
    private const float MinPitch = 10f;
    private const float MaxPitch = 85f;
    // 拖动灵敏度 (度/像素): 左键转动视角。
    private const float YawPerPx = 0.3f;
    private const float PitchPerPx = 0.25f;

    // 阻尼运动目标 (俯视位姿固定, 不走阻尼)。
    private Vector3 _positionGoal;
    private Vector3 _lookGoal;
    private Vector3 _lookCurrent = new(1.9f, 0f, 1.9f);
    private float _damp = 6.0f;

    private bool _rotating;
    private Vector2 _rotateAnchor;
    private bool _panning;
    private Vector2 _panWorldAnchor; // 抓取的地面点 (世界 XZ)

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

    /// <summary>Orbit/spin angles (degrees), for deterministic input evidence.</summary>
    public float OverviewYaw => _overviewYaw;
    public float OverviewPitch => _overviewPitch;
    public float TopYaw => _topYaw;
    public float FollowYaw => _followYaw;
    public float FollowPitch => _followPitch;

    /// <summary>Orbit direction unit vector from yaw/pitch (yaw 0 = +Z, pitch up from ground).</summary>
    private Vector3 OrbitDir(float yawDeg, float pitchDeg)
    {
        var yaw = Mathf.DegToRad(yawDeg);
        var pitch = Mathf.DegToRad(pitchDeg);
        return new Vector3(
            Mathf.Sin(yaw) * Mathf.Cos(pitch),
            Mathf.Sin(pitch),
            Mathf.Cos(yaw) * Mathf.Cos(pitch));
    }

    private float FollowBaseLen => _followOffset.Length();

    public override void _Ready()
    {
        ResetGoals();
        _lookCurrent = _lookGoal;
        Position = _positionGoal;
        LookAt(_lookCurrent, Vector3.Up);
    }

    /// <summary>Frames the overview/top shots around the (possibly moved) arena; rotations reset.</summary>
    public void ConfigureArena(Vec3 center, double fieldSize)
    {
        _center = new Vector3((float)center.X, 0f, (float)center.Z);
        _fieldSize = (float)fieldSize;
        // 概览取景比例 (高 0.82 : 后 0.68, 相对场地边长) — 见 DefaultOverview*Ratio 注释。
        var dir = new Vector3(0f, _fieldSize * DefaultOverviewHeightRatio, _fieldSize * DefaultOverviewBackRatio);
        _baseOverviewDistance = dir.Length();
        _baseTopHeight = _fieldSize * 2.5f;
        _overviewFocus = _center;
        _overviewDistance = _baseOverviewDistance;
        _overviewYaw = 0f;
        _overviewPitch = DefaultOverviewPitch;
        _topFocus = _center;
        _topHeight = _baseTopHeight;
        _topYaw = 0f;
        _followYaw = 0f;
        _followPitch = DefaultFollowPitch;
        _followZoom = 1f;
        _followFocus = _center;
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
        // Godot 默认旋转序 YXZ: Ry(topYaw)·Rx(-90) — 仍正俯视, 图像绕视线轴自旋。
        Position = TopPosition;
        RotationDegrees = new Vector3(-90f, _topYaw, 0f);
    }

    private void ResetGoals()
    {
        switch (_mode)
        {
            case CameraMode.Overview:
                _positionGoal = _overviewFocus + OrbitDir(_overviewYaw, _overviewPitch) * _overviewDistance;
                _lookGoal = _overviewFocus;
                break;
            case CameraMode.Follow:
                _positionGoal = _followFocus
                    + OrbitDir(_followYaw, _followPitch) * (FollowBaseLen * _followZoom);
                _lookGoal = _followFocus;
                break;
        }
    }

    public void SetFocus(RenderFrame frame)
    {
        // 跟随模式: 自动以双方中点为焦点 (缩放/环绕只改机位, 不与 SetFocus 抢焦点)。
        // 概览/俯视: 使用可平移的地面焦点, 不跟随机器人中点。
        if (_mode != CameraMode.Follow)
        {
            return;
        }
        _followFocus = new Vector3((float)frame.CameraFocus.X, 0.05f, (float)frame.CameraFocus.Z);
        ResetGoals();
    }

    public override void _Process(double delta)
    {
        // 拖动中编辑器接管指针时, 释放被限定的鼠标 (否则光标一直锁在窗口)。
        if (!PointerInputEnabled && Input.MouseMode == Input.MouseModeEnum.Confined)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        if (_mode == CameraMode.Top)
        {
            ApplyTopPose(); // 位姿由焦点/高度/自旋直接决定 (支持拖动与缩放)
            return;
        }
        var t = Mathf.Clamp((float)delta * _damp, 0f, 1f);
        Position = Position.Lerp(_positionGoal, t);
        _lookCurrent = _lookCurrent.Lerp(_lookGoal, t);
        LookAt(_lookCurrent, Vector3.Up);
    }

    /// <summary>
    /// 拖动期间把光标限定在窗口内 (防止拖出窗口丢事件); 无头模式无 effects。
    /// </summary>
    private void ConfineMouse()
    {
        if (DisplayServer.GetName() != "headless")
        {
            Input.MouseMode = Input.MouseModeEnum.Confined;
        }
    }

    /// <summary>旋转与平移都结束后恢复自由光标。</summary>
    private void ReleaseMouseIfIdle()
    {
        if (!_rotating && !_panning && Input.MouseMode == Input.MouseModeEnum.Confined)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    // ---------- pointer input (rotate / pan / zoom) ----------

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
                if (mb.Pressed && !_rotating)
                {
                    _rotating = true;
                    _rotateAnchor = mb.Position;
                    ConfineMouse();
                    GetViewport().SetInputAsHandled();
                }
                else if (!mb.Pressed && _rotating)
                {
                    _rotating = false;
                    ReleaseMouseIfIdle();
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed && !_panning)
                {
                    if (GroundPoint(mb.Position) is { } hit)
                    {
                        _panning = true;
                        _panWorldAnchor = new Vector2(hit.X, hit.Z);
                    }
                    ConfineMouse();
                    GetViewport().SetInputAsHandled();
                }
                else if (!mb.Pressed && _panning)
                {
                    _panning = false;
                    ReleaseMouseIfIdle();
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
        else if (@event is InputEventMouseMotion motion)
        {
            if (_rotating)
            {
                var screenDelta = motion.Position - _rotateAnchor;
                _rotateAnchor = motion.Position;
                RotateView(screenDelta);
                GetViewport().SetInputAsHandled();
            }
            else if (_panning)
            {
                // 抓取语义: 被抓住的地面点跟随光标 (世界锚点逐步跟随, 焦点反向平移)。
                if (GroundPoint(motion.Position) is { } now)
                {
                    var current = new Vector2(now.X, now.Z);
                    PanBy(new Vector3(current.X - _panWorldAnchor.X, 0f, current.Y - _panWorldAnchor.Y));
                    _panWorldAnchor = current;
                }
                GetViewport().SetInputAsHandled();
            }
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
    /// 左键转动视角: 偏航跟随光标横向 (机位绕焦点向右环绕), 俯仰跟随光标纵向
    /// (上拖升高机位、更俯视)。俯视模式俯仰固定 -90°, 纵向拖动只自旋 (横向)。
    /// </summary>
    private void RotateView(Vector2 screenDelta)
    {
        switch (_mode)
        {
            case CameraMode.Overview:
                _overviewYaw = Mathf.Wrap(_overviewYaw + screenDelta.X * YawPerPx, -180f, 180f);
                _overviewPitch = Mathf.Clamp(_overviewPitch - screenDelta.Y * PitchPerPx, MinPitch, MaxPitch);
                ResetGoals();
                break;
            case CameraMode.Follow:
                _followYaw = Mathf.Wrap(_followYaw + screenDelta.X * YawPerPx, -180f, 180f);
                _followPitch = Mathf.Clamp(_followPitch - screenDelta.Y * PitchPerPx, MinPitch, MaxPitch);
                ResetGoals();
                break;
            case CameraMode.Top:
                _topYaw = Mathf.Wrap(_topYaw + screenDelta.X * YawPerPx, -180f, 180f);
                break;
        }
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
        // 跟随模式焦点由渲染帧驱动, 不支持平移。
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
