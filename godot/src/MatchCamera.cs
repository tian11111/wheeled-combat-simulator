// 观察相机: 概览 / 跟随 / 俯视 三模式, C 键切换。跟随模式有阻尼平滑;
// 相机只读取渲染帧的建议焦点, 从不写回 Sim.Core (design: 只读观察者)。

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
    // 默认按官方场地 (3.8m, 中心 1.9,1.9) 取景; ConfigureArena 会根据
    // 场地位姿/尺寸重新推导概览与俯视机位。
    private Vector3 _overviewPos = new(1.9f, 7.4f, 7.98f);
    private Vector3 _overviewTarget = new(1.9f, 0.05f, 1.9f);
    private Vector3 _topPos = new(1.9f, 9.5f, 1.9f);

    private CameraMode _mode = CameraMode.Overview;
    private Vector3 _targetPos = new(1.9f, 7.4f, 7.98f);
    private Vector3 _focus = new(1.9f, 0.05f, 1.9f);
    private float _damp = 6.0f;

    public CameraMode Mode => _mode;

    public override void _Ready()
    {
        Position = _overviewPos;
        LookAt(_overviewTarget, Vector3.Up);
        _targetPos = _overviewPos;
    }

    /// <summary>Frames the overview/top shots around the (possibly moved) arena.</summary>
    public void ConfigureArena(Vec3 center, double fieldSize)
    {
        var c = new Vector3((float)center.X, 0f, (float)center.Z);
        _overviewTarget = c + new Vector3(0f, 0.05f, 0f);
        _overviewPos = c + new Vector3(0f, (float)(fieldSize * 1.95), (float)(fieldSize * 1.6));
        _topPos = c + new Vector3(0f, (float)(fieldSize * 2.5), 0f);
        if (_mode == CameraMode.Overview)
        {
            _focus = _overviewTarget;
            _targetPos = _overviewPos;
        }
        else if (_mode == CameraMode.Top)
        {
            Position = _topPos;
            RotationDegrees = new Vector3(90, 0, 0);
            _targetPos = _topPos;
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
            // 正俯视时 LookAt 的上向量会退化, 直接设置姿态。
            Position = _topPos;
            RotationDegrees = new Vector3(90, 0, 0);
            _targetPos = _topPos;
        }
    }

    public void SetFocus(RenderFrame frame)
    {
        if (_mode == CameraMode.Overview)
        {
            _focus = _overviewTarget;
            _targetPos = _overviewPos;
            return;
        }
        _focus = new Vector3((float)frame.CameraFocus.X, 0.05f, (float)frame.CameraFocus.Z);
        if (_mode == CameraMode.Follow)
        {
            _targetPos = _focus + new Vector3(0, 3.4f, 3.8f);
        }
    }

    public override void _Process(double delta)
    {
        if (_mode == CameraMode.Top)
        {
            return; // 姿态已固定
        }
        Position = Position.Lerp(_targetPos, Mathf.Clamp((float)delta * _damp, 0, 1));
        LookAt(_focus, Vector3.Up);
    }
}