using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sim.Core;
using Sim.Protocol;

namespace Sim.Mujoco;

/// <summary>Versioned, deterministic contact model. All coordinates are SI, Z up.</summary>
internal static class MujocoModel
{
    internal const string Version = PhysicsSpec.MujocoModelV1;

    // ---- 物理时基(单一真值) ----
    // 2026-09-30 时基回归修复: 09-29 的 QACC 数值稳定修复只把 MJCF 的 option
    // timestep 字面量改成 0.002, C# 侧仍是 SubstepsPerTick(10)×SubstepSeconds
    // (0.005) —— 每裁判 tick 只 mj_step 10 次、只积分 0.02 s, 物理时间流速变成
    // 比赛钟的 0.4×(慢动作), FSM 时限/轮速/翻覆行为全被拉长, contactTime 网格
    // 也与真实推进量脱节。现在只有 MjcTimestep 一个真值: MJCF timestep、子步
    // 时长、每 tick 步数全部由它推导, 任何一处改动都必须先改它。
    /// <summary>MuJoCo 物理子步长(s): 唯一真值(MJCF <c>option timestep</c> 同源)。</summary>
    internal const double MjcTimestep = 0.002;
    /// <summary>子步时长 = <see cref="MjcTimestep"/>(语义别名, 供接触时刻上报使用)。</summary>
    internal const double SubstepSeconds = MjcTimestep;
    /// <summary>后端强制的裁判 tick 时长(s): <see cref="MujocoPhysicsBackend.Step"/>
    /// 只接受恰好该值的 dt, 场景 tickSeconds 也必须等于它。</summary>
    internal const double TickSeconds = 0.05;
    /// <summary>每裁判 tick 的子步数 = <see cref="TickSeconds"/> / <see cref="MjcTimestep"/>
    /// (= 25)。不变量 <c>SubstepsPerTick × MjcTimestep == TickSeconds</c> 在 IEEE 下
    /// 精确成立(0.05/0.002 的商与 25×0.002 都恰为该 double), 由 MujocoTimebaseTests 钉住。</summary>
    internal const int SubstepsPerTick = (int)(TickSeconds / MjcTimestep);

    internal const double WheelRadius = 0.065;

    // ---- 2342 减速电机真值(博创尚和; 12V 额定、空载 8100 rpm、减速后 120 rpm、
    // 输出扭矩 1.72 N·m) ----
    // 这是"真值→仿真量"的推导, 不是工程限幅拟合: 输出轴空载角速度
    // ω_noload = 120/60 × 2π = 4π rad/s, 输出轴堵转扭矩 τ_stall = 1.72 N·m。
    // **数学等价推导**: MuJoCo <velocity> 执行器的广义力 = kv×(ctrl − qvel) 且被
    // forcerange 双侧截断。取 ctrl = 该轮目标角速度、kv = τ_stall/ω_noload、
    // forcerange = ±τ_stall, 则目标 = ω_noload 时
    //   τ(ω) = τ_stall/ω_noload × (ω_noload − ω) = τ_stall × (1 − ω/ω_noload)
    // 恰为直流电机线性转速-扭矩曲线: ω=0(堵转)时扭矩 = τ_stall, ω=ω_noload(空载)
    // 时扭矩 = 0, 中间线性; 曲线外的 |τ|>τ_stall 由 forcerange 截断(与堵转保护一致)。
    // 再配合 SetControls 的 duty 口径(ctrl = ±duty×ω_noload, duty = clamp(轮面线
    // 速度/MaxSpeed, −1, 1)), 等价于按占空比缩放电池电压: 起步扭矩 = duty×τ_stall、
    // 空载转速 = duty×ω_noload —— 2342 真车正是开环 PWM 调速, 无量程静差。
    // 旧工程值(kv=0.25/±3.0 N·m/±80 rad/s, 80 rad/s ≈ 2.6 m/s 远超真车 0.408 m/s)
    // 已删除, 避免出现第二份"电机真值"。
    /// <summary>2342 输出轴堵转扭矩 τ_stall(N·m): MJCF <c>forcerange</c> 取 ±此值。</summary>
    internal const double MjcStallTorque = 1.72;
    /// <summary>2342 输出轴空载角速度 ω_noload(rad/s) = 120 rpm = 4π: <c>ctrlrange</c> 取 ±此值。</summary>
    internal const double MjcNoLoadSpeed = 12.566370614359172;
    /// <summary>速度伺服增益 kv = τ_stall/ω_noload ≈ 0.136873(N·m·s/rad), 由真值推导;
    /// 该取值使执行器扭矩-转速曲线精确等价直流电机线性曲线(见上方推导)。</summary>
    internal const double MjcServoKv = MjcStallTorque / MjcNoLoadSpeed;

    // 速度档与时限语义核对(2026-09-30 批 2, 只读结论 —— FSM/场景取值不动):
    // - v2 真车场景(maxSpeed=0.408): 执行器真极速 = ω_noload×r =
    //   12.566370614359172×0.0325 = 0.408407 m/s ≈ 场景声明极速 ⇒
    //   Fsm.TimeScale(1.5/MaxSpeed) 的时限缩放与实际行驶速度同源, 仍自洽;
    //   FSM 的 0.9/1.0/1.25 m/s 档(≥ MaxSpeed)在 duty 口径下截到 duty=1,
    //   等价真车开环全速 —— 这是去掉旧 80 rad/s 余量假象后的预期语义。
    // - v1 历史场景(maxSpeed=1.5, 轮径 0.065): 真极速被 ω_noload 压到 0.817 m/s,
    //   而 TimeScale 仍按 1.5 算(缩放恒 1.0)⇒ 登台/恢复时限相对偏紧; 披露不改
    //   (v1 mujoco 场景此后定位"工程验证", 见 spec 教训)。
    // 2026-09-25 登台修复: 原车体 spawn 高度使底盘下缘恰在 ground+0.06 = 台面高度,
    // 后轮爬上台沿后平底腹部立刻搁在台沿上(几何卡死)。实现方式: 轮轴在体坐标系内
    // 下移 0.02(-0.04 → -0.06)、车体 spawn 同步抬高 0.02 —— 轮子仍精确接地, 底盘
    // 下缘抬到 ground+0.08(高于台沿 2 cm), 无落地冲击。(geom pos 偏移方案实测会
    // 冻结整车, 已弃用。)
    internal const double ChassisClearanceLift = 0.02;

    // ---- v2 真车几何(实测常量, 见 tools/mesh/README.md 与 tools/mesh/extract_vehicle_mesh.py) ----
    // v2 的体坐标系原点 = 轮轴平面(模型原点), 车体网格直接以自身坐标挂在 body 原点;
    // v1 的"原点在车体中心 + 轮轴下移 0.04 + 抬升 0.02"只在 v1 分支保留。
    /// <summary>胎皮外径实测 0.064899–0.064990(四轮), 取名义半径 0.0325 m。</summary>
    internal const double WheelRadiusV2 = 0.0325;
    /// <summary>胎宽实测 0.0290, 半宽 0.0145。</summary>
    internal const double WheelHalfWidthV2 = 0.0145;
    internal const double WheelMassV2 = 0.03;
    internal const double ShovelMassV2 = 0.02;
    /// <summary>
    /// 车底重物质量(kg, 电池/电机/主控): mesh 默认惯性把质心放在几何中部(偏高),
    /// 真车重物贴底盘。2026-09-29: 整车质量此前沿用 1kg 旧默认(漏算电池/电机/
    /// 主控 → 车过轻被顶飞); 真车整车 3.5kg(v2 场景 `vehicles.*.mass`, 09-29
    /// 电机参数轮生效), 其中 0.9kg 作为底部配重从 chassis mesh 质量中扣除
    /// (总质量不变), 质心拉到轮轴上方 ~9mm。
    /// </summary>
    internal const double ChassisBallastV2 = 0.9;
    /// <summary>v1 的轮/铲质量(历史字面量, 与 v2 数值相同但语义独立, 不得随之改动)。</summary>
    internal const double WheelMassV1 = 0.03;
    /// <summary>轮心(车体局部系, 实测): 前 x=+0.0730 / 后 x=−0.0770(轴距 0.150)。</summary>
    internal const double WheelFrontX = 0.0730;
    internal const double WheelRearX = -0.0770;
    /// <summary>轮心(车体局部系, 实测): 左 y=+0.11635 / 右 y=−0.11265(轮距 0.229)。</summary>
    internal const double WheelLeftY = 0.11635;
    internal const double WheelRightY = -0.11265;
    /// <summary>轮心高度: 与体坐标系原点同面(实测 −0.00025, 取 0 使 spawn 高度 = 半径)。</summary>
    internal const double WheelLocalZ = 0.0;
    /// <summary>后铲网格最低边的实测高度(=−0.01245), 用于自检。</summary>
    internal const double ShovelLowestZ = -0.01245;
    /// <summary>底板/上板合并凸体的实测最低点(=−0.00825)。</summary>
    internal const double ChassisLowestZ = -0.00825;

    /// <summary>v2 场景(PhysicsSpec.MujocoModelV2)走真车几何分支。</summary>
    internal static bool IsV2(PhysicsBackendContext context)
        => context.Scenario.Physics?.ModelVersion == PhysicsSpec.MujocoModelV2;

    /// <summary>驱动轮半径(执行器把 m/s 指令换算成 rad/s)。</summary>
    internal static double RadiusFor(PhysicsBackendContext context) => IsV2(context) ? WheelRadiusV2 : WheelRadius;

    /// <summary>spawn 时体坐标系原点相对轮轴平面的高度(轮心 z 偏移)。</summary>
    internal static double SpawnAxleOffset(PhysicsBackendContext context)
        => IsV2(context) ? WheelLocalZ : 0.04 + ChassisClearanceLift;

    /// <summary>
    /// 裁判 ResetRobot 时体坐标系原点相对轮轴平面的高度。v1 的 reset 高度与 spawn
    /// 高度刻意不同(历史行为, 不得改动); v2 直接把轮心放在ground + r。
    /// </summary>
    internal static double ResetAxleOffset(PhysicsBackendContext context)
        => IsV2(context) ? WheelLocalZ : 0.04;

    /// <summary>
    /// 生成 MJCF。返回值带资产字节, 供 <see cref="MujocoNative.CreateModel(string, IReadOnlyList{MujocoMeshAsset}?)"/>
    /// 挂进内存 VFS; v1 场景无资产(v1 生成路径与 v1 哈希保持逐字节不变)。
    /// </summary>
    internal static (string Xml, IReadOnlyList<MujocoMeshAsset> Assets, string Sha256) Generate(PhysicsBackendContext context)
        => Generate(context, IsV2(context) ? MujocoMeshAssets.Load() : Array.Empty<MujocoMeshAsset>());

    /// <summary>
    /// 显式资产版本(测试注入用): 资产内容参与 <paramref name="context"/> 对应模型的哈希,
    /// 改一个字节则 ModelSha256 必变。非 v2 场景忽略传入资产。
    /// </summary>
    internal static (string Xml, IReadOnlyList<MujocoMeshAsset> Assets, string Sha256) Generate(
        PhysicsBackendContext context, IReadOnlyList<MujocoMeshAsset> assets,
        WheelContactOptions wheelContact = default)
    {
        var field = context.Scenario.Field;
        var isV2 = IsV2(context);
        var used = isV2 ? assets : Array.Empty<MujocoMeshAsset>();
        if (isV2)
        {
            foreach (var name in MujocoMeshAssets.Required)
            {
                if (!used.Any(a => a.Name == name))
                {
                    throw new ArgumentException($"MuJoCo model v2 requires mesh asset '{name}'.", nameof(assets));
                }
            }
        }
        var sb = new StringBuilder(8192);
        Header(sb, isV2);
        if (isV2)
        {
            sb.Append("<asset>");
            foreach (var mesh in Meshes)
            {
                sb.Append("<mesh name=\"").Append(mesh.Geom).Append("\" file=\"").Append(mesh.File)
                    .Append("\" maxhullvert=\"256\"/>");
            }
            sb.Append("</asset>");
        }
        sb.Append("<worldbody>");
        Arena(sb, field, context.Field);
        if (isV2)
        {
            RobotV2(sb, context.Us, context.Field, wheelContact);
            RobotV2(sb, context.Them, context.Field, wheelContact);
        }
        else
        {
            Robot(sb, context.Us, context.Field, wheelContact);
            Robot(sb, context.Them, context.Field, wheelContact);
        }
        Blocks(sb, context);
        sb.Append("</worldbody>");
        Actuators(sb);
        sb.Append("</mujoco>");
        var xml = sb.ToString();
        return (xml, used, Hash(xml, used));
    }

    /// <summary>资产在 MJCF 里的 mesh 名与逻辑文件名(v2)。</summary>
    internal static readonly (string Geom, string File)[] Meshes =
    [
        ("chassis", MujocoMeshAssets.Chassis),
        ("rear_shovel", MujocoMeshAssets.RearShovel),
    ];

    private static void Header(StringBuilder sb, bool isV2)
    {
        // timestep 必须与 C# 子步循环同源(MjcTimestep): 09-29 只改这里的字面量
        // 曾造成 XML 0.002 / C# 0.005×10 的二次失配(每 tick 只积分 0.02 s)。
        // N(0.002) 的序列化字节未变; 2026-09-30 批2 执行器真值标定后 v1/v2 模型
        // 哈希已随之变更(守卫 V1ModelSha256 同步), 不再与 2dd61d9 时代相同。
        sb.Append("<mujoco model=\"").Append(isV2 ? PhysicsSpec.MujocoModelV2 : PhysicsSpec.MujocoModelV1)
            .Append("\"><compiler angle=\"radian\"/><option timestep=\"").Append(N(MjcTimestep))
            .Append("\" gravity=\"0 0 -9.81\" integrator=\"implicitfast\"/>")
            .Append("<size njmax=\"2000\" nconmax=\"500\"/><default><geom friction=\"0.85 0.01 0.002\" solref=\"0.008 1\" solimp=\"0.95 0.99 0.001\"/></default>");
    }

    private static void Arena(StringBuilder sb, FieldParams field, FieldModel model)
    {
        var t = model.Transform;
        // The field is placed once by a rigid parent; entity initial poses are already world poses.
        sb.Append("<body name=\"arena\" pos=\"").Append(N(t.X)).Append(' ').Append(N(t.Y))
            .Append(" 0\" euler=\"0 0 ").Append(N(t.Th)).Append("\">");
        Box(sb, "ground", field.FieldSize / 2, field.FieldSize / 2, -0.025,
            field.FieldSize / 2, field.FieldSize / 2, 0.025, friction: 1.0);
        var p = field.Platform;
        Box(sb, "platform", (p.MinX + p.MaxX) / 2, (p.MinY + p.MaxY) / 2,
            field.PlatformHeight / 2, (p.MaxX - p.MinX) / 2, (p.MaxY - p.MinY) / 2,
            field.PlatformHeight / 2, friction: 1.0);
        // 2026-09-25 登台修复: 台沿 45° 倒角(全高斜坡)。刚体圆柱轮咬不住直角台沿——
        // 低速绕角 pivot 打滑、高速被驱动力矩掀成轮抬抛体(实测 z 最大 +4.4cm 仍落回,
        // 接触对证明爬升瞬间后轮与台面零接触), 与扭矩无关(0.3/2/3/6 N·m 行为一致)。
        // 真实场地边缘同样存在磨损/圆角; 倒角是几何工程近似, 不改变 OnStage 判定
        // (仍为 [MinX,MaxX]×[MinY,MaxY] 矩形), 只让轮子可以滚上/滚下。
        AppendChamfers(sb, p, field.PlatformHeight);
        const double fenceThickness = 0.025;
        var fs = field.FieldSize;
        Box(sb, "fence_s", fs / 2, -fenceThickness / 2, field.FenceHeight / 2,
            fs / 2 + fenceThickness, fenceThickness / 2, field.FenceHeight / 2);
        Box(sb, "fence_n", fs / 2, fs + fenceThickness / 2, field.FenceHeight / 2,
            fs / 2 + fenceThickness, fenceThickness / 2, field.FenceHeight / 2);
        Box(sb, "fence_w", -fenceThickness / 2, fs / 2, field.FenceHeight / 2,
            fenceThickness / 2, fs / 2, field.FenceHeight / 2);
        Box(sb, "fence_e", fs + fenceThickness / 2, fs / 2, field.FenceHeight / 2,
            fenceThickness / 2, fs / 2, field.FenceHeight / 2);
        sb.Append("</body>");
    }

    private static void Blocks(StringBuilder sb, PhysicsBackendContext context)
    {
        var field = context.Scenario.Field;
        for (var i = 0; i < context.Blocks.Count; i++)
        {
            var b = context.Blocks[i];
            var z = context.Field.StageHeightAt(b.X, b.Y) + field.BlockSize / 2;
            sb.Append("<body name=\"block_").Append(i).Append("\" pos=\"")
                .Append(N(b.X)).Append(' ').Append(N(b.Y)).Append(' ').Append(N(z))
                .Append("\"><freejoint name=\"block_joint_").Append(i).Append("\"/>");
            sb.Append("<geom name=\"block_geom_").Append(i).Append("\" type=\"box\" size=\"")
                .Append(N(field.BlockSize / 2)).Append(' ').Append(N(field.BlockSize / 2)).Append(' ')
                .Append(N(field.BlockSize / 2)).Append("\" mass=\"0.3\" friction=\"0.6 0.01 0.002\"/>");
            sb.Append("</body>");
        }
    }

    private static void Actuators(StringBuilder sb)
    {
        sb.Append("<actuator>");
        foreach (var role in new[] { RoleNames.Us, RoleNames.Them })
        {
            foreach (var axle in new[] { "front", "rear" })
            foreach (var side in new[] { "left", "right" })
            {
                sb.Append("<velocity name=\"motor_").Append(role).Append('_').Append(axle).Append('_').Append(side)
                    .Append("\" joint=\"wheel_").Append(role).Append('_').Append(axle).Append('_').Append(side)
                    .Append("\" kv=\"").Append(N(MjcServoKv))
                    .Append("\" ctrllimited=\"true\" ctrlrange=\"-").Append(N(MjcNoLoadSpeed))
                    .Append(' ').Append(N(MjcNoLoadSpeed)).Append("\" forcelimited=\"true\" forcerange=\"-")
                    .Append(N(MjcStallTorque)).Append(' ').Append(N(MjcStallTorque)).Append("\"/>");
            }
        }
        sb.Append("</actuator>");
    }

    /// <summary>
    /// ModelSha256 = SHA256( xml ‖ 各资产[按名字升序: 名字 ‖ 8 字节小端长度 ‖ 字节] )。
    /// 无资产时退化为 SHA256(xml), v1 的既有哈希因此逐位不变。名字与长度入哈希是为了
    /// 让"换名/换绑定/截断拼接"都会改变身份, 而不只是内容字节本身。
    /// </summary>
    private static string Hash(string xml, IReadOnlyList<MujocoMeshAsset> assets)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        if (assets.Count == 0)
        {
            return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        }
        using var sha = SHA256.Create();
        sha.TransformBlock(payload, 0, payload.Length, null, 0);
        foreach (var asset in assets.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(asset.Name);
            sha.TransformBlock(name, 0, name.Length, null, 0);
            var length = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(length, asset.Bytes.Length);
            sha.TransformBlock(length, 0, length.Length, null, 0);
            sha.TransformBlock(asset.Bytes, 0, asset.Bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void Robot(StringBuilder sb, RobotRuntime r, FieldModel field, WheelContactOptions wheelContact = default)
    {
        var v = r.Vehicle;
        var ground = field.StageHeightAt(r.X, r.Y);
        var z = ground + WheelRadius + 0.04 + ChassisClearanceLift;
        var halfL = Math.Min(v.Length / 2, v.FrontExtent);
        var halfW = Math.Min(v.Width / 2, v.SideExtent);
        sb.Append("<body name=\"robot_").Append(r.Role).Append("\" pos=\"")
            .Append(N(r.X)).Append(' ').Append(N(r.Y)).Append(' ').Append(N(z))
            .Append("\" euler=\"0 0 ").Append(N(r.Th)).Append("\"><freejoint name=\"robot_joint_")
            .Append(r.Role).Append("\"/>");
        sb.Append("<geom name=\"robot_body_").Append(r.Role).Append("\" type=\"box\" size=\"")
            .Append(N(halfL)).Append(' ').Append(N(halfW)).Append(' ').Append(N(v.Height / 2))
            .Append("\" mass=\"").Append(N(v.Mass)).Append("\"/>");
        sb.Append("<geom name=\"robot_shovel_").Append(r.Role).Append("\" type=\"box\" pos=\"")
            .Append(N(v.FrontExtent - v.ShovelLength / 2)).Append(" 0 ")
            .Append(N(-z + ground + v.ShovelHeight + 0.006))
            .Append("\" size=\"").Append(N(v.ShovelLength / 2)).Append(' ')
            .Append(N(v.ShovelWidth / 2)).Append(" 0.006\" mass=\"0.02\"/>");
        WheelBodies(sb, r, WheelRadius, 0.03, WheelMassV1, -0.04 - ChassisClearanceLift,
            v.WheelBase / 2, -v.WheelBase / 2, v.TrackWidth / 2, -v.TrackWidth / 2,
            isV2: false, wheelContact);
        sb.Append("</body>");
    }

    /// <summary>
    /// v2 真车几何: 车体 = chassis + rear_shovel 两个 mesh geom(保留两者之间的凹角,
    /// 不做整车单一凸包), 四个驱动轮用实测半径/半宽/轮心位置。
    /// </summary>
    private static void RobotV2(StringBuilder sb, RobotRuntime r, FieldModel field, WheelContactOptions wheelContact = default)
    {
        var v = r.Vehicle;
        var ground = field.StageHeightAt(r.X, r.Y);
        var z = ground + WheelRadiusV2 + WheelLocalZ;
        var chassisMass = v.Mass - 4 * WheelMassV2 - ShovelMassV2;
        if (!(chassisMass > 0))
        {
            throw new ArgumentException(
                $"MuJoCo model v2 needs vehicle mass > {4 * WheelMassV2 + ShovelMassV2} kg (wheels + rear shovel), got {v.Mass}.",
                nameof(r));
        }
        sb.Append("<body name=\"robot_").Append(r.Role).Append("\" pos=\"")
            .Append(N(r.X)).Append(' ').Append(N(r.Y)).Append(' ').Append(N(z))
            .Append("\" euler=\"0 0 ").Append(N(r.Th)).Append("\"><freejoint name=\"robot_joint_")
            .Append(r.Role).Append("\"/>");
        // 配重按可分配车体质量封顶: 轻车(默认 1kg)下 0.9 会超过 chassis 质量;
        // 真车 3.5kg 时取满 (chassisMass=2.36)。见 ChassisBallastV2 注释。
        var ballast = Math.Min(ChassisBallastV2, chassisMass * 0.7);
        sb.Append("<geom name=\"robot_chassis_").Append(r.Role).Append("\" type=\"mesh\" mesh=\"")
            .Append(Meshes[0].Geom).Append("\" mass=\"").Append(N(chassisMass - ballast)).Append("\"/>");
        // 真车配重(无碰撞、组 3): 贴车底内侧, 把整车质心拉低; 组 3 使传感器
        // raycast(mj_ray 组掩码 0-2)完全不可见。
        sb.Append("<geom name=\"robot_ballast_").Append(r.Role)
            .Append("\" type=\"box\" pos=\"0 0 -0.0008\" size=\"0.06 0.08 0.0015\" mass=\"")
            .Append(N(ballast)).Append("\" group=\"3\" contype=\"0\" conaffinity=\"0\"/>");
        sb.Append("<geom name=\"robot_shovel_").Append(r.Role).Append("\" type=\"mesh\" mesh=\"")
            .Append(Meshes[1].Geom).Append("\" mass=\"").Append(N(ShovelMassV2)).Append("\"/>");
        // 轮-地摩擦取每模型标定默认(v1 5.0 / v2 6.0, 工程初值, 见 WheelContactOptions
        // 注释的扫描证据); 摩擦降档(1.0)单变量试验曾把翻覆数从 84 抬到 129 —— 打滑
        // →冲坡-滑落循环, 方向相反的证据一致: 高摩擦稳爬坡。参数化保留供后续调参。
        WheelBodies(sb, r, WheelRadiusV2, WheelHalfWidthV2, WheelMassV2, WheelLocalZ,
            WheelFrontX, WheelRearX, WheelLeftY, WheelRightY,
            isV2: true, wheelContact);
        sb.Append("</body>");
    }

    /// <summary>
    /// 轮 geom 接触 solref timeconst(s) 的现行历史字面量(0.02)。v1/v2 共用
    /// (2026-10-03 读码裁决: 轮 solref 只此一处, 无"v1 用默认 0.008"的分支)。
    /// 未注入(<see cref="WheelContactOptions.SolRefTimeconst"/>=0)时轮 geom
    /// 逐字节写回本值 —— 默认路径 MJCF 不变, 模型哈希不变。
    /// </summary>
    internal const double WheelSolrefTimeconstLiteral = 0.02;

    private static void WheelBodies(StringBuilder sb, RobotRuntime r, double radius, double halfWidth,
        double mass, double localZ, double frontX, double rearX, double leftY, double rightY,
        bool isV2, WheelContactOptions? wheelContact = null)
    {
        foreach (var (axle, x) in new[] { ("front", frontX), ("rear", rearX) })
        foreach (var (side, y) in new[] { ("left", leftY), ("right", rightY) })
        {
            var name = $"{r.Role}_{axle}_{side}";
            sb.Append("<body name=\"wheel_body_").Append(name).Append("\" pos=\"")
                .Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(localZ)).Append("\">");
            sb.Append("<joint name=\"wheel_").Append(name)
                .Append("\" type=\"hinge\" axis=\"0 1 0\" damping=\"0.02\"/>");
            sb.Append("<geom name=\"wheel_geom_").Append(name)
                .Append("\" type=\"cylinder\" euler=\"1.5707963267948966 0 0\" size=\"")
                .Append(N(radius)).Append(' ').Append(N(halfWidth))
                .Append("\" mass=\"").Append(N(mass));
            var (condim, slide, spin, roll) = (wheelContact ?? default).Resolved(isV2);
            if (condim != 3)
            {
                sb.Append("\" condim=\"").Append(condim);
            }
            // solref 注入点(2026-10-03 M1): 未设置(0) ⇒ 历史字面量 "0.02 1"
            // (逐字节不变); 设置 ⇒ "{N(t)} 1"(damping ratio 恒 1, 取值域
            // timeconst ≥ 2×MjcTimestep 见 WheelContactOptions.SolRefTimeconst)。
            var solrefTimeconst = (wheelContact ?? default).SolRefTimeconst;
            sb.Append("\" friction=\"").Append(N(slide)).Append(' ').Append(N(spin))
                .Append(' ').Append(N(roll)).Append("\" solref=\"");
            if (solrefTimeconst > 0)
            {
                sb.Append(N(solrefTimeconst)).Append(" 1\"/>");
            }
            else
            {
                sb.Append(N(WheelSolrefTimeconstLiteral)).Append(" 1\"/>");
            }
            sb.Append("</body>");
        }
    }

    private static void AppendChamfers(StringBuilder sb, Region p, double height)
    {
        // 每条边一块 α=20° 缓坡薄板: 顶面从 地面外沿(台沿外侧 run) 到 台面内沿, 全高。
        // 45° 实测仍不够(沿坡重力分量 ≈ 车重×0.71, 单轮摩擦不足), 20° 时沿坡分量
        // 仅 ≈ 车重×0.34, 摩擦上限 ≈ 车重×1.5 —— 稳定滚上。倒角外扩 run=height/tan(α)。
        const double halfThick = 0.01;
        const double angle = 0.3490658503988659; // 20°
        var sinA = Math.Sin(angle);
        var cosA = Math.Cos(angle);
        var run = height / Math.Tan(angle);
        var mid = halfThick * sinA;
        var slopeHalf = height / (2 * sinA);
        var cx = (p.MinX + p.MaxX) / 2;
        var cy = (p.MinY + p.MaxY) / 2;
        var alongX = (p.MaxX - p.MinX) / 2 + halfThick;
        var alongY = (p.MaxY - p.MinY) / 2 + halfThick;
        // south (y = MinY, 外向 −y): 顶面 (cx, MinY−run, 0) → (cx, MinY, height)
        Geom(sb, "chamfer_s", cx, p.MinY - run / 2 + mid, height / 2 - halfThick * cosA,
            alongX, slopeHalf, halfThick, N(angle) + " 0 0");
        Geom(sb, "chamfer_n", cx, p.MaxY + run / 2 - mid, height / 2 - halfThick * cosA,
            alongX, slopeHalf, halfThick, N(-angle) + " 0 0");
        Geom(sb, "chamfer_w", p.MinX - run / 2 + mid, cy, height / 2 - halfThick * cosA,
            slopeHalf, alongY, halfThick, "0 " + N(-angle) + " 0");
        Geom(sb, "chamfer_e", p.MaxX + run / 2 - mid, cy, height / 2 - halfThick * cosA,
            slopeHalf, alongY, halfThick, "0 " + N(angle) + " 0");
    }

    private static void Geom(StringBuilder sb, string name, double x, double y, double z,
        double hx, double hy, double hz, string euler)
    {
        sb.Append("<geom name=\"").Append(name).Append("\" type=\"box\" pos=\"")
            .Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(z))
            .Append("\" euler=\"").Append(euler).Append("\" size=\"")
            .Append(N(hx)).Append(' ').Append(N(hy)).Append(' ').Append(N(hz))
            .Append("\" friction=\"1.0 0.01 0.002\"/>");
    }

    private static void Box(StringBuilder sb, string name, double x, double y, double z,
        double hx, double hy, double hz, double friction = 0.8)
    {
        sb.Append("<geom name=\"").Append(name).Append("\" type=\"box\" pos=\"")
            .Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(z)).Append("\" size=\"")
            .Append(N(hx)).Append(' ').Append(N(hy)).Append(' ').Append(N(hz))
            .Append("\" friction=\"").Append(N(friction)).Append(" 0.01 0.002\"/>");
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
