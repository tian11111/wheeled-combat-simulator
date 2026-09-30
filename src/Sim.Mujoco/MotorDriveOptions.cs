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
