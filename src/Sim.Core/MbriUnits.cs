namespace Sim.Core;

/// <summary>
/// MBri 真车单位换算层（批1，纯函数，零 IO/零时钟/零随机）。
///
/// 移植依据（真车源码只读参考 D:/project/robocup/2026/MBri，不入库）：
/// - 轮速锚点：config.py:145 <c>PATROL_RECOVER_STEP_CM = 21.5  # 400×0.6s 实测退离距离</c>
///   ⇒ 400 单位 ≈ 0.215 m / 0.6 s = 0.3583 m/s ⇒ k = 0.000896 m/s/unit（规划定稿值）。
/// - 差速→v/w：规划公式 v=(l+r)/2·k，w=(r−l)/(2·TrackWidth)·k（design.md 移植映射表）。
///   分母取 2·TrackWidth 是对真车原地转向标定表（config.py:121-138
///   MOTOR_TURN_CALIBRATION：135°→(625,1.0s)、90°→(600,0.65s)）的经验对齐：
///   ±625 差速代入得 w=2.445 rad/s，135° 耗时 0.96s ≈ 表值 1.0s；理想差速
///  动力学公式 w=(r−l)·k/TrackWidth 会快一倍（0.48s），与实测明显不符。
/// - TrackWidth：真车实测 0.229 m（本仓 MujocoModel.cs:96-97 轮心实测
///   y=+0.11635/−0.11265；scenarios/wushu-ring-2026-mujoco-v2.json trackWidth=0.229）。
///   旧桥猜测 0.18（真车仓 sim_bridge.py:30 注明"车端 config 无此值"）与实测差
///   21.4% &lt; 30% 停止阈值（implement.md Stop Conditions），按实测值执行并披露。
/// - 时长→tick：真车 wall-clock 时长 ÷ TickSeconds(0.05) 四舍五入取整
///   （design.md "时长→tick 数（÷0.05，四舍五入）"）；START_REVERSE 1.8s → 36 tick。
/// </summary>
public static class MbriUnits
{
    /// <summary>轮速单位 → m/s 比例系数（锚点：400×0.6s 实测 21.5 cm）。</summary>
    public const double WheelSpeedK = 0.000896;

    /// <summary>真车实测轮距（m）。来源见类型注释；旧桥猜测 0.18 已复核弃用。</summary>
    public const double TrackWidthMeters = 0.229;

    /// <summary>真车 wall-clock 时长换算用的默认裁判 tick（s），与场景 field.tickSeconds 一致。</summary>
    public const double DefaultTickSeconds = 0.05;

    /// <summary>车端轮速命令（0-1023 带符号）→ 线速度 m/s。纯函数。</summary>
    public static double WheelToMs(double unit) => unit * WheelSpeedK;

    /// <summary>
    /// 左右轮速命令（车端单位，可带符号）→ 差速驱动 v/w（m/s, rad/s）。
    /// 规划定稿公式：v=(l+r)/2·k，w=(r−l)/(2·trackWidth)·k。纯函数。
    /// </summary>
    public static (double V, double W) DifferentialToVW(double left, double right, double trackWidth)
    {
        if (!(trackWidth > 0) || !double.IsFinite(trackWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(trackWidth), "trackWidth must be a positive finite number.");
        }
        var v = (left + right) / 2.0 * WheelSpeedK;
        var w = (right - left) / (2.0 * trackWidth) * WheelSpeedK;
        return (v, w);
    }

    /// <summary>左右轮速命令 → v/w，使用真车实测轮距 <see cref="TrackWidthMeters"/>。</summary>
    public static (double V, double W) DifferentialToVW(double left, double right)
        => DifferentialToVW(left, right, TrackWidthMeters);

    /// <summary>
    /// 真车 wall-clock 时长（s）→ tick 数（÷tickSeconds，四舍五入AwayFromZero）。
    /// 负时长按 0 处理（真车语义中不出现负时长，防御性归零）。纯函数。
    /// </summary>
    public static long SecondsToTicks(double seconds, double tickSeconds = DefaultTickSeconds)
    {
        if (!(tickSeconds > 0) || !double.IsFinite(tickSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), "tickSeconds must be a positive finite number.");
        }
        if (!(seconds > 0) || !double.IsFinite(seconds))
        {
            return 0;
        }
        return (long)Math.Round(seconds / tickSeconds, MidpointRounding.AwayFromZero);
    }
}
