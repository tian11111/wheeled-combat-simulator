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
    private static readonly Vector3 OverviewPos = new(1.9f, 7.4f, 8.0f);
    private static readonly Vector3 OverviewTarget = new(1.9f, 0.05f, 1.9f);
    private static readonly Vector3 TopPos = new(1.9f, 9.5f, 1.9f);

    private CameraMode _mode = CameraMode.Overview;
    private Vector3 _targetPos = OverviewPos;
    private Vector3 _focus = OverviewTarget;
    private float _damp = 6.0f;

    public CameraMode Mode => _mode;

    public override void _Ready()
    {
        Position = OverviewPos;
        LookAt(OverviewTarget, Vector3.Up);
        _targetPos = OverviewPos;
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
            Position = TopPos;
            RotationDegrees = new Vector3(90, 0, 0);
            _targetPos = TopPos;
        }
    }

    public void SetFocus(RenderFrame frame)
    {
        if (_mode == CameraMode.Overview)
        {
            _focus = OverviewTarget;
            _targetPos = OverviewPos;
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