namespace Sim.Core;

/// <summary>
/// Simulation parameters, ported verbatim from the legacy CORE
/// <c>params</c> object (including the Chinese comment semantics).
/// Scenario <c>parameters</c> override individual entries by name.
/// </summary>
public sealed class SimParameters
{
    public double EdgeThreshold { get; set; } = 400;      // 灰度边缘阈值(SEARCH 扫描避边)
    public double FallThreshold { get; set; } = 150;      // 掉台判定(灰度; 登台 climbed 信号用)
    public double OnStageThreshold { get; set; } = 500;   // 登台判定(灰度; 仅显示)
    public double GrayNoise { get; set; } = 30;           // 灰度噪声 ±
    public double IrNoise { get; set; } = 0.02;           // 红外噪声 ±
    public double IrTrigger { get; set; } = 0.35;         // 红外触发阈值
    public double MountSpeed { get; set; } = 780;         // 倒车冲台速度(代码单位)
    public double ClassifyRate { get; set; } = 100;       // 视觉识别成功率 %
    public double RecoverLimit { get; set; } = 3;         // 恢复次数上限
    public double StallTime { get; set; } = 0.4;          // 堵转判定持续时长 (s)
    public double StallSpeed { get; set; } = 0.03;        // 堵转线速度阈值 (m/s)
    public double StallRelease { get; set; } = 0.06;      // 解除堵转线速度 (m/s)
    public double StallDisplacement { get; set; } = 0.006;// 无进展位移阈值 (m/窗口)
    public double CmdLatencyFrames { get; set; } = 0;     // 指令延迟队列长度(帧)
    public double IrHystBand { get; set; } = 0.10;        // 数字红外施密特迟滞带宽
    public double GraySpotRadius { get; set; } = 0.025;   // 灰度近地光斑采样半径 (m)
    public double BlockStickSpeed { get; set; } = 0.02;   // 能量块静摩擦"粘住"阈值 (m/s)
    public double BlockMuK { get; set; } = 0.5;           // 能量块库仑动摩擦系数
    public double? CollisionRestitution { get; set; }     // null=速度相关恢复公式(确定性回归验证)

    /// <summary>Legacy parameter names accepted from scenario JSON, mapped to properties.</summary>
    public static SimParameters FromDictionary(IReadOnlyDictionary<string, double>? source)
    {
        var parameters = new SimParameters();
        if (source is null)
        {
            return parameters;
        }

        foreach (var (name, value) in source)
        {
            switch (name)
            {
                case "EDGE_THRESHOLD": parameters.EdgeThreshold = value; break;
                case "FALL_THRESHOLD": parameters.FallThreshold = value; break;
                case "ON_STAGE_THRESHOLD": parameters.OnStageThreshold = value; break;
                case "grayNoise": parameters.GrayNoise = value; break;
                case "irNoise": parameters.IrNoise = value; break;
                case "IR_TRIGGER": parameters.IrTrigger = value; break;
                case "MOUNT_SPEED": parameters.MountSpeed = value; break;
                case "classifyRate": parameters.ClassifyRate = value; break;
                case "RECOVER_LIMIT": parameters.RecoverLimit = value; break;
                case "STALL_TIME": parameters.StallTime = value; break;
                case "STALL_SPEED": parameters.StallSpeed = value; break;
                case "STALL_RELEASE": parameters.StallRelease = value; break;
                case "STALL_DISPLACEMENT": parameters.StallDisplacement = value; break;
                case "cmdLatencyFrames": parameters.CmdLatencyFrames = value; break;
                case "IR_HYST_BAND": parameters.IrHystBand = value; break;
                case "graySpotRadius": parameters.GraySpotRadius = value; break;
                case "BLOCK_STICK_SPEED": parameters.BlockStickSpeed = value; break;
                case "BLOCK_MU_K": parameters.BlockMuK = value; break;
                case "COLLISION_RESTITUTION": parameters.CollisionRestitution = value; break;
                default:
                    throw new ArgumentException(
                        $"Unknown simulation parameter '{name}'. Known parameters are the legacy CORE params (see SimParameters).",
                        nameof(source));
            }
        }
        return parameters;
    }
}
