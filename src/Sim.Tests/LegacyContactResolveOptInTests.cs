using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// L1/L2/L3 opt-in 接触求解扩展回归测试 (legacy 侧修复, 评审三轮方案)。
/// 每项修复配 "开关开 → 改善断言" 与 "默认 → 现状断言" 两类用例;
/// 默认路径逐位不变另由 replay-check replays/seed-42.json 逐位硬门覆盖。
/// 数值锚点来自 tmp/penctl / penctl2 / penprobe 实测 (本轮留档)。
/// </summary>
public class LegacyContactResolveOptInTests
{
    private const double Edge = 0.7;        // 台沿 (南, 场局部=世界, 身份布局)
    private const double Front = 0.22;      // VehicleProfile.Default.FrontExtent (Profiles.cs:257)
    private const double Side = 0.14;
    private const double BlockHalf = 0.075; // BlockRuntime.R (RuntimeState.cs:177)

    private static readonly FieldModel Field = new(new FieldParams());
    private static readonly SimParameters Params = new();

    private static RobotRuntime MkRobot(string role, double x, double y, double th, bool armed = true) => new()
    {
        Role = role,
        Name = role,
        X = x,
        Y = y,
        Th = th,
        Vehicle = VehicleProfile.Default,
        R = VehicleProfile.Default.CollisionRadius,
        Fsm = new FsmRuntime { Armed = armed },
    };

    private static BlockRuntime MkBlock(double x, double y, bool wasOn = false, bool out_ = false) => new()
    {
        Kind = BlockKind.Buff,
        Name = "增益块",
        X = x,
        Y = y,
        R = BlockHalf,
        WasOn = wasOn,
        Out = out_,
    };

    // ---- penctl 同款对称 SAT (front-front 接触下精确; 后侧接触高估, 只用于其锚定用例) ----

    private static double Hyp(double x, double y) => Math.Sqrt(x * x + y * y);

    private static (double[] Ax, double[] Ay, double[] Ex) ObbAxes(double th)
    {
        var c = Math.Cos(th);
        var s = Math.Sin(th);
        return ([c, -s], [s, c], [Front, Side]);
    }

    private static (double[] Ax, double[] Ay, double[] Ex) AabbAxes() => ([1, 0], [0, 1], [BlockHalf, BlockHalf]);

    private static double SatPen(double ax, double ay, (double[] Ax, double[] Ay, double[] Ex) a,
        double bx, double by, (double[] Ax, double[] Ay, double[] Ex) b)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var best = double.NegativeInfinity;
        for (var k = 0; k < 2; k++)
        {
            foreach (var (nx, ny) in new[] { (a.Ax[k], a.Ay[k]), (b.Ax[k], b.Ay[k]) })
            {
                var extA = a.Ex[0] * Math.Abs(a.Ax[0] * nx + a.Ay[0] * ny) + a.Ex[1] * Math.Abs(a.Ax[1] * nx + a.Ay[1] * ny);
                var extB = b.Ex[0] * Math.Abs(b.Ax[0] * nx + b.Ay[0] * ny) + b.Ex[1] * Math.Abs(b.Ax[1] * nx + b.Ay[1] * ny);
                best = Math.Max(best, Math.Abs(dx * nx + dy * ny) - extA - extB);
            }
        }
        return -best;
    }

    private static double RobotRobotPen(RobotRuntime a, RobotRuntime b)
    {
        var A = ObbAxes(a.Th);
        var B = ObbAxes(b.Th);
        return SatPen(a.X, a.Y, A, b.X, b.Y, B);
    }

    private static double RobotBlockPen(RobotRuntime r, BlockRuntime o)
        => SatPen(r.X, r.Y, ObbAxes(r.Th), o.X, o.Y, AabbAxes());

    // ================= L1: 车车 OBB 稳态分离 =================

    /// <summary>E1 (开): 顶牛稳态互穿 0.12 → OBB 分离至 slop, 车心距 = 双前铲面距 0.44−slop。</summary>
    [Fact]
    public void RobotPairObb_On_SteadyHeadOnShove_SeparatesToSlop()
    {
        var us = MkRobot(RoleNames.Us, 1.5, 1.9, 0);
        var them = MkRobot(RoleNames.Them, 2.0, 1.9, Math.PI);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime>(), new EventBus(),
            contactOptions: new ContactResolveOptions { RobotPairObbSeparation = true });
        for (var t = 0; t < 60; t++)
        {
            us.V = 1.5;
            them.V = 1.5;
            world.Step(0.05);
            Assert.True(RobotRobotPen(us, them) <= 0.002,
                $"tick {t}: rr pen {RobotRobotPen(us, them):F4} must be ≤ slop+noise after OBB separation");
        }
        var d = Hyp(us.X - them.X, us.Y - them.Y);
        Assert.True(d >= 0.43, $"steady d_center {d:F4} must reach the front-face distance (≥0.43), got {d:F4}");
    }

    /// <summary>E1 (默认/现状): 顶牛稳态保持 d=2R、OBB 互穿 0.12 (逐位现状)。</summary>
    [Fact]
    public void RobotPairObb_Off_Default_HeadOnShove_KeepsLegacyInterpenetration()
    {
        var us = MkRobot(RoleNames.Us, 1.5, 1.9, 0);
        var them = MkRobot(RoleNames.Them, 2.0, 1.9, Math.PI);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime>(), new EventBus());
        for (var t = 0; t < 60; t++)
        {
            us.V = 1.5;
            them.V = 1.5;
            world.Step(0.05);
        }
        var d = Hyp(us.X - them.X, us.Y - them.Y);
        var pen = RobotRobotPen(us, them);
        Assert.Equal(2 * 0.16, d, precision: 3);       // 实测 d=0.3200 = 2R
        Assert.True(pen >= 0.10, $"legacy steady rr pen {pen:F4} must stay at the documented ~0.12 interpenetration");
    }

    /// <summary>
    /// E7 (开, 评审要求): 静止车 (未武装 — 无自身运动积分, 位移全部来自 solver) 置于台沿外
    /// 0.05m, 被对车 OBB 推离 → 车心必须仍在台外且被钳回 Boundary−support−0.002,
    /// 不发生无门槛位移上台。注: 武装静止车会被圆盘冲量赋予内向速度并经既有登台门
    /// (vn&gt;0.3, 正对) 合法登台 — 归因门按设计放行, 不属本修复目标 (tmp/e7dbg 实测)。
    /// </summary>
    [Fact]
    public void RobotPairObb_On_StationaryRobotShoveAcrossStageEdge_ClampedBackOutside()
    {
        var them = MkRobot(RoleNames.Them, 1.9, Edge - 0.05, Math.PI / 2, armed: false);
        var us = MkRobot(RoleNames.Us, 1.9, 0.30, Math.PI / 2);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime>(), new EventBus(),
            contactOptions: new ContactResolveOptions { RobotPairObbSeparation = true });
        var clampTarget = Edge - (Front + 0.002); // 0.478: 与 StageWall 同公式 (support+2mm)
        var sawClampTarget = false;
        for (var t = 0; t < 40; t++)
        {
            us.V = 1.5;
            them.V = 0;
            world.Step(0.05);
            Assert.False(Field.OnPlatform(them.X, them.Y),
                $"tick {t}: stationary robot must never be displaced onto the stage (y={them.Y:F4})");
            if (them.Y <= clampTarget + 1e-9)
            {
                sawClampTarget = true;
            }
        }
        Assert.True(sawClampTarget, $"clamp must pull the robot back to Boundary−support−0.002 ({clampTarget:F3})");
    }

    /// <summary>E7 (默认/现状): 同场景无开关 → 静止车被 solver 位移无门槛推上台 (现状复现)。</summary>
    [Fact]
    public void RobotPairObb_Off_Default_StationaryRobotShovedOntoStage()
    {
        var them = MkRobot(RoleNames.Them, 1.9, Edge - 0.05, Math.PI / 2, armed: false);
        var us = MkRobot(RoleNames.Us, 1.9, 0.30, Math.PI / 2);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime>(), new EventBus());
        for (var t = 0; t < 40; t++)
        {
            us.V = 1.5;
            them.V = 0;
            world.Step(0.05);
        }
        Assert.True(Field.OnPlatform(them.X, them.Y),
            $"status quo: the stationary robot must be shoved onto the stage un-gated (y={them.Y:F4})");
    }

    // ================= L2: 车块 OBB 分离 + 推块速度镜像 =================

    /// <summary>
    /// E2 (开): 推块逼近带 (d∈(0.235,0.295)) OBB 分离后互穿 ≤0.03, 且块仍被推到东围栏。
    /// 已知残余 (如实): 块钉死在围栏后每 tick 都有圆盘接触, 互斥门按设计让位圆盘路径,
    /// 该阶段 pen 回落至圆盘几何决定的 ~0.06 (与默认同值) — 断言只覆盖逼近相与围栏到达。
    /// </summary>
    [Fact]
    public void RobotBlockObb_On_PushPhase_SeparatedAndBlockStillReachesFence()
    {
        var us = MkRobot(RoleNames.Us, 1.5, 1.9, 0);
        var them = MkRobot(RoleNames.Them, 0.3, 0.3, Math.PI);
        var block = MkBlock(2.0, 1.9);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus(),
            contactOptions: new ContactResolveOptions { RobotBlockObbSeparation = true });
        var approachMaxPen = 0.0;
        var reachedFence = false;
        for (var t = 0; t < 60; t++)
        {
            us.V = 1.5;
            them.V = 0;
            world.Step(0.05);
            var pen = RobotBlockPen(us, block);
            if (!reachedFence)
            {
                approachMaxPen = Math.Max(approachMaxPen, pen);
            }
            if (block.X >= 3.8 - 0.12 - 1e-3)
            {
                reachedFence = true;
            }
        }
        Assert.True(reachedFence, "the block must still be pushed to the east fence (no push regression)");
        Assert.True(approachMaxPen <= 0.03,
            $"approach-phase rb pen {approachMaxPen:F4} must collapse from the legacy 0.0595 to ≤0.03");
    }

    /// <summary>E3 (开): 原地旋转铲角扫块 0.1005 互穿 → 每步 OBB 分离至 slop。</summary>
    [Fact]
    public void RobotBlockObb_On_PivotCornerSweep_NoDeepPenetration()
    {
        var us = MkRobot(RoleNames.Us, 1.5, 1.9, 0);
        var them = MkRobot(RoleNames.Them, 0.3, 0.3, Math.PI);
        var block = MkBlock(1.5 + 0.24, 1.9);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus(),
            contactOptions: new ContactResolveOptions { RobotBlockObbSeparation = true });
        var maxPen = 0.0;
        for (var t = 0; t < 80; t++)
        {
            us.V = 0.2;
            us.W = 4.0;
            them.V = 0;
            world.Step(0.05);
            maxPen = Math.Max(maxPen, RobotBlockPen(us, block));
        }
        Assert.True(maxPen <= 0.02, $"pivot-sweep rb pen {maxPen:F4} must collapse from the legacy 0.1005");
    }

    /// <summary>E5 (开): 高速冲块 — 扫掠圆盘先接触, 互斥门让位 → 车不过块 (passed=False 保持)。</summary>
    [Fact]
    public void RobotBlockObb_On_ChargeIntoBlock_SweptStillStopsRobot()
    {
        var us = MkRobot(RoleNames.Us, 1.0, 1.9, 0);
        var them = MkRobot(RoleNames.Them, 0.3, 0.3, Math.PI);
        var block = MkBlock(1.9, 1.9);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus(),
            contactOptions: new ContactResolveOptions { RobotBlockObbSeparation = true });
        var passed = false;
        var maxPen = 0.0;
        for (var t = 0; t < 30; t++)
        {
            var bx0 = block.X;
            us.V = 1.5;
            them.V = 0;
            world.Step(0.05);
            maxPen = Math.Max(maxPen, RobotBlockPen(us, block));
            if (us.X > bx0 + 0.1)
            {
                passed = true;
            }
        }
        Assert.False(passed, "the robot must not pass through the block (swept stop preserved)");
        Assert.True(maxPen <= 0.03, $"charge rb pen {maxPen:F4} must collapse from the legacy 0.0600");
    }

    /// <summary>E2/E3 (默认/现状): 推块逼近相与铲角扫块的 0.06-0.10 互穿保持 (逐位现状)。</summary>
    [Fact]
    public void RobotBlockObb_Off_Default_ShovelPenetrationStatusQuo()
    {
        // E2: 直推
        var us = MkRobot(RoleNames.Us, 1.5, 1.9, 0);
        var them = MkRobot(RoleNames.Them, 0.3, 0.3, Math.PI);
        var block = MkBlock(2.0, 1.9);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus());
        var e2Max = 0.0;
        for (var t = 0; t < 60; t++)
        {
            us.V = 1.5;
            them.V = 0;
            world.Step(0.05);
            e2Max = Math.Max(e2Max, RobotBlockPen(us, block));
        }
        Assert.True(e2Max >= 0.05, $"legacy direct-push rb pen {e2Max:F4} must stay at the documented ~0.0595");

        // E3: 铲角扫块
        var us3 = MkRobot(RoleNames.Us, 1.5, 1.9, 0);
        var them3 = MkRobot(RoleNames.Them, 0.3, 0.3, Math.PI);
        var block3 = MkBlock(1.5 + 0.24, 1.9);
        var world3 = new PhysicsWorld(Field, Params, us3, them3, new List<BlockRuntime> { block3 }, new EventBus());
        var e3Max = 0.0;
        for (var t = 0; t < 80; t++)
        {
            us3.V = 0.2;
            us3.W = 4.0;
            them3.V = 0;
            world3.Step(0.05);
            e3Max = Math.Max(e3Max, RobotBlockPen(us3, block3));
        }
        Assert.True(e3Max >= 0.09, $"legacy pivot-sweep rb pen {e3Max:F4} must stay at the documented ~0.1005");
    }

    // ================= L3: 块-台壁阻挡 =================

    /// <summary>E6 (开): 高速发射块穿台沿 → 钳回台沿外面接触 (距沿 R), 不再登台。</summary>
    [Fact]
    public void BlockStageWall_On_HighSpeedCross_ClampedToFaceContact()
    {
        var us = MkRobot(RoleNames.Us, 1.9, 0.35, Math.PI / 2);
        var them = MkRobot(RoleNames.Them, 0.2, 3.6, -Math.PI / 2);
        var block = MkBlock(1.9, 0.62, wasOn: false, out_: true);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus(),
            contactOptions: new ContactResolveOptions { BlockStageWall = true });
        for (var t = 0; t < 40; t++)
        {
            us.V = 1.5;
            them.V = 0;
            world.Step(0.05);
            Assert.False(Field.OnPlatform(block.X, block.Y),
                $"tick {t}: the block must not cross the stage wall (y={block.Y:F4})");
        }
        Assert.Equal(Edge - BlockHalf, block.Y, precision: 9); // 0.625 面接触
    }

    /// <summary>E6 (默认/现状): 同场景无开关 → 块一 tick 越沿登台 (现状复现)。</summary>
    [Fact]
    public void BlockStageWall_Off_Default_HighSpeedCross_CrossesOntoStage()
    {
        var us = MkRobot(RoleNames.Us, 1.9, 0.35, Math.PI / 2);
        var them = MkRobot(RoleNames.Them, 0.2, 3.6, -Math.PI / 2);
        var block = MkBlock(1.9, 0.62, wasOn: false, out_: true);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus());
        for (var t = 0; t < 40; t++)
        {
            us.V = 1.5;
            them.V = 0;
            world.Step(0.05);
        }
        Assert.True(Field.OnPlatform(block.X, block.Y),
            $"status quo: the block must cross onto the stage un-blocked (y={block.Y:F4})");
        Assert.True(block.Y > 1.0, $"status quo: the block must travel deep into the platform (y={block.Y:F4})");
    }

    /// <summary>E6c(a) (开): 块贴台沿外侧平行滑行 → 维持在面接触线上 (封 74mm 压入), 切向滑行保留。</summary>
    [Fact]
    public void BlockStageWall_On_ParallelSlide_MaintainedAtFaceContact()
    {
        var us = MkRobot(RoleNames.Us, 0.2, 3.6, 0);
        var them = MkRobot(RoleNames.Them, 3.6, 0.2, Math.PI);
        var block = MkBlock(1.0, 0.699, wasOn: false, out_: true);
        block.Vx = 0.5; // 平行台沿 (向东), 74mm 压入带内
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus(),
            contactOptions: new ContactResolveOptions { BlockStageWall = true });
        world.Step(0.05);
        Assert.Equal(Edge - BlockHalf, block.Y, precision: 9);
        Assert.True(block.X > 1.0, $"tangential slide must be preserved (x={block.X:F4})");
        for (var t = 0; t < 9; t++)
        {
            world.Step(0.05);
            Assert.Equal(Edge - BlockHalf, block.Y, precision: 9);
        }
    }

    /// <summary>E6c(b) (开): 出台块带外向速度入带 → 不受钳位, 继续外移 (合法出台出口回归网)。</summary>
    [Fact]
    public void BlockStageWall_On_OutwardMovingBlockInBand_NotClamped()
    {
        var us = MkRobot(RoleNames.Us, 1.9, 1.05, -Math.PI / 2);
        var them = MkRobot(RoleNames.Them, 0.2, 3.6, -Math.PI / 2);
        var block = MkBlock(1.9, 0.9, wasOn: true);
        var world = new PhysicsWorld(Field, Params, us, them, new List<BlockRuntime> { block }, new EventBus(),
            contactOptions: new ContactResolveOptions { BlockStageWall = true });
        for (var t = 0; t < 12; t++)
        {
            us.V = 1.0; // 外向速度须大于每 tick 库仑摩擦 0.245 才能带外向速度入带 (tmp/penctl2 E6c(b))
            them.V = 0;
            world.Step(0.05);
        }
        Assert.True(block.Y < Edge - BlockHalf - 0.02,
            $"a block exiting with outward velocity must not be clamped back to the face-contact line (y={block.Y:F4})");
    }

    /// <summary>E6b (开, 引擎级): 台上块被推下台 → 自由出台并照常触发得分事件 (+3)。</summary>
    [Fact]
    public void BlockStageWall_On_OnStageBlock_PushOffScoresFreely()
    {
        var scenario = new Scenario
        {
            Seed = 42,
            Blocks = [new BlockSpec { Kind = BlockKind.Buff, X = 1.9, Y = 0.9 }],
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.9, Y = 1.2, Th = -Math.PI / 2 },
                    [RoleNames.Them] = new() { X = 0.3, Y = 3.5, Th = Math.PI },
                },
            },
        };
        using var engine = new MatchEngine(scenario, null, null,
            new ContactResolveOptions { BlockStageWall = true });
        var push = new RobotAction { V = 1.5, W = 0 };
        var scored = false;
        for (var t = 0; t < 10 * 20 && !scored; t++)
        {
            engine.Tick(push, null);
            scored = engine.Events.Events.Any(e => e.Kind == EventKind.BlockScore);
        }
        Assert.True(scored, "pushing the on-stage block off must still fire the buff score event");
        Assert.Equal(3, engine.Scores.Us);
        // 得分后再走 10 tick: 块要么带外向速度越带继续外移, 要么按声明语义定身在
        // 0.625 面接触 — 两者都必须保持 OffPlatform, 绝不回台。
        for (var t = 0; t < 10; t++)
        {
            engine.Tick(push, null);
        }
        var block = engine.Blocks[0];
        Assert.False(Field.OnPlatform(block.X, block.Y), "the scored block must stay off the platform");
        Assert.True(block.Y <= Edge - BlockHalf + 1e-9,
            $"the scored block must be beyond or at the face-contact line, never back inside (y={block.Y:F4})");
    }

    // ================= 默认开关组合逐位守卫 =================

    /// <summary>显式全关的 ContactResolveOptions 与 null (默认) 引擎逐位一致 (记录默认值守卫)。</summary>
    [Fact]
    public void AllOffContactOptions_EngineBitwiseIdenticalToNull()
    {
        var scenarioA = new Scenario
        {
            Seed = 42,
            Blocks = OfficialLayout.Blocks,
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 0.9, Y = 1.9, Th = 0 },
                    [RoleNames.Them] = new() { X = 2.9, Y = 1.9, Th = Math.PI },
                },
            },
        };
        var scenarioB = scenarioA with { };
        using var nullOpts = new MatchEngine(scenarioA);
        using var allOff = new MatchEngine(scenarioB, null, null, new ContactResolveOptions());
        for (var i = 0; i < 200; i++)
        {
            var a = nullOpts.Tick();
            var b = allOff.Tick();
            Assert.Equal(ProtocolJson.Serialize(b), ProtocolJson.Serialize(a));
        }
    }
}
