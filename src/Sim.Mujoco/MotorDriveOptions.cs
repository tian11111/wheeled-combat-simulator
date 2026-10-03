namespace Sim.Mujoco;

/// <summary>
/// 电机驱动选项(电池压降参数化接口)。**默认禁用** ——
/// <see cref="Enabled"/> = false 时 <see cref="VoltageScale"/> 恒返回 1,
/// 驱动路径与无电池模型逐位一致(验收: 默认禁用下生成的 MJCF 与常量路径同哈希)。
///
/// 启用纪律(prd R2.5): 需要真车实测的电池电压-电流曲线(空载电压 V0、内阻 R、
/// 堵转电流 I_stall), 三个参数全部实测标定后才允许置 Enabled = true ——
/// **无实测数据不得发明压降数值**。本类型刻意不进场景 JSON/协议(不新增场景字段、
/// 不改协议): 只作为 Sim.Mujoco 内部构造器注入点, 未标定物理量不暴露成公开契约。
/// </summary>
internal readonly record struct MotorDriveOptions
{
    /// <summary>是否启用电池压降折算。默认 false(未标定)。</summary>
    public bool Enabled { get; init; }

    /// <summary>电池内阻 R(Ω)。默认 0 = 未标定(禁用)。</summary>
    public double BatteryInternalResistanceOhm { get; init; }

    /// <summary>电池空载电压 V0(V)。默认 0 = 未标定(禁用)。注意 12V 是 2342 的
    /// 额定电机电压, 不是实测的电池无载电压, 不得直接填这里。</summary>
    public double BatteryNoLoadVoltageVolts { get; init; }

    /// <summary>堵转电流 I_stall(A; 2342 台架实测 1.4)。默认 0 = 未标定(禁用)。</summary>
    public double StallCurrentAmps { get; init; }

    /// <summary>
    /// 端电压折扣 = (V0 − I·R)/V0, I = |duty|×I_stall(电流∝电磁扭矩的一阶线性
    /// 近似: ω=0 时起步电流 = duty×I_stall)。任一参数未标定(非正)或未启用时恒
    /// 返回 1 —— 默认路径与无电池模型逐位一致。返回值由 <c>SetControls</c> 乘在
    /// ctrl 上: 空载转速与堵转扭矩都正比端电压, 故 ctrl 折降等价于电压折降。
    /// </summary>
    internal double VoltageScale(double duty)
    {
        if (!Enabled || !(BatteryInternalResistanceOhm > 0) || !(BatteryNoLoadVoltageVolts > 0)
            || !(StallCurrentAmps > 0))
        {
            return 1.0;
        }
        var current = Math.Abs(duty) * StallCurrentAmps;
        var voltage = BatteryNoLoadVoltageVolts - current * BatteryInternalResistanceOhm;
        return Math.Clamp(voltage / BatteryNoLoadVoltageVolts, 0.0, 1.0);
    }
}

/// <summary>
/// 驱动轮-地接触参数(MuJoCo 域标定轮的注入点)。**默认(未设置)取标定值** ——
/// <see cref="Condim"/> = 0 视为未设置, 取 2026-10-02 域标定轮定值的每模型摩擦
/// (v1 slide 5.0 / v2 slide 6.0, spin 0.002 / roll 0.002, condim 3);
/// 显式注入(Condim != 0)时全三元组按注入值生效, 供探针/试验扫描。
///
/// 取值锚点(须披露): **工程初值, 无真车锚点** —— 真车胎纹各向异性(顺滚动易/横向难)
/// 无法用 MuJoCo 各向同性接触摩擦表达, 滑移转向的最优摩擦点是实测扫描峰:
/// v1 在 f4–f6 平台(峰 f5, 稳态偏航 2.54 rad/s)、v2 在 f6–f9 平台(峰 f6, 1.17 rad/s),
/// 两侧再抬高(≥f10)或降低(≤f4)都显著变差; f7–f8 处 v1 的偶发低谷为粘滑极限环
/// 模式切换, 非单调区。摩擦改变会改变 v1/v2 模型哈希(旧 MuJoCo 回放/checkpoint 失配,
/// 用户已拍板接受)。
///
/// 注意 C# 的 <c>default</c> 对 struct 是全零、不执行属性初始值设定项, 故用
/// "0=未设置"而不是属性默认值。
/// </summary>
internal readonly record struct WheelContactOptions
{
    /// <summary>接触维度: 0=未设置(取标定默认 3), 3=切向滑动, 4=+扭转, 6=+扭转+滚动。</summary>
    public int Condim { get; init; }

    /// <summary>滑动摩擦系数(未设置取每模型标定值 v1 5.0 / v2 6.0)。</summary>
    public double Slide { get; init; }

    /// <summary>扭转摩擦(未设置取 0.02; condim≥4 才有效)。</summary>
    public double Spin { get; init; }

    /// <summary>滚动摩擦(未设置取 0.002; condim=6 才有效)。</summary>
    public double Roll { get; init; }

    /// <summary>
    /// 轮 geom 接触 solref timeconst(s): **0=未设置(取现行历史字面量 0.02, MJCF
    /// 逐字节不变), &gt;0=按注入值写 <c>solref="{N(t)} 1"</c>**(solref 第二参数
    /// damping ratio 恒 1, 与现行字面量一致)。
    /// 2026-10-03 M1 v2 轮软接触注入点: 轮 geom 的 0.02 是整车 19–35mm 级穿透的
    /// 共同根因(轮-块/轮-车/轮-地接触按 solmix 平均取 0.02 与 0.008 的折中, 软于
    /// 其余全部 geom)。注入锚点 = 模型 default class 既有标定值 0.008
    /// (<see cref="MujocoModel.Header"/> 的 <c>&lt;default&gt;</c>, 全部非轮 geom
    /// 的实际生效值), 稳定域 timeconst ≥ 2×timestep(0.002×2,
    /// <see cref="MujocoModel.MjcTimestep"/>)。
    /// **探针实测(tmp/mjpenprobe full+extra, 须回填的推断目标 ≤10mm 如实记录)**:
    /// 0.008 —— v2 FSM 陷地 -34.1→-19.0mm/台沿 -29.2→-2.8mm, 出生踢跳 -8.1→-2.8mm,
    /// 但平台对撞 车-车**反而变深**(0.5/1.0 boost 达 -32.5/-40.6mm, 基线 -20.4mm);
    /// 0.005 —— FSM 整场全部轮类残余 ≤6.4mm、围栏/台沿 ≤7.2mm、150 种子出生零穿透,
    /// 双车对挤最坏 us-块 -27.1mm、对撞 车-车 -27.3mm 仍 >10mm(工程最坏用例未达
    /// 推断目标)。默认(0.02)本轮不动; 翻默认须先重跑 10-02 域标定并拍板。
    /// **与摩擦三元组相互独立**: 只注入本字段时 Condim=0 ⇒ 摩擦仍取每模型标定默认,
    /// 单变量对照成立。显式注入会改变 v1/v2 模型哈希 ⇒ 该注入态下旧 MuJoCo
    /// 回放/checkpoint 失配; 未注入(0)时哈希逐位不变。
    /// </summary>
    public double SolRefTimeconst { get; init; }

    /// <summary>解析为实际生效值: Condim=0(未设置) ⇒ 取每模型标定默认。</summary>
    internal (int Condim, double Slide, double Spin, double Roll) Resolved(bool isV2)
        => Condim == 0
            ? (3, isV2 ? 6.0 : 5.0, 0.02, 0.002)
            : (Condim, Slide, Spin, Roll);
}
