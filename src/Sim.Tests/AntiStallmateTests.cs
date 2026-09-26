using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 反僵局铲刃微调 (docs/PORTING_NOTES.md 有意偏差条目): 同型机器人正面全速对推时
/// 铲刃静差恒为 0, 遗留楔入阈值 |aBlade−bBlade|>0.004 永不触发 → 无限顶牛。
/// 修改后双方有效铲刃高度叠加种子派生初相的慢速正弦项, 楔入周期性发生。
/// </summary>
public class AntiStallmateTests
{
    private const int TicksPerSecond = 20; // tickSeconds = 0.05 s

    /// <summary>
    /// 官方布局场景, 双车同型, 置于中线 y=1.9 相距 2 m 相向 (场局部, 身份布局):
    /// us (0.9, 1.9) 朝 +x, them (2.9, 1.9) 朝 −x。
    /// </summary>
    private static Scenario HeadOnScenario(long seed = 42, double? amp = null)
    {
        var scenario = new Scenario
        {
            Seed = seed,
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
        return WithAmp(scenario, amp);
    }

    /// <summary>非正面场景: us 倒车 (尾部) 撞 them 正面, facing=0 永不入楔入分支。</summary>
    private static Scenario RearPushScenario(long seed = 42, double? amp = null)
    {
        var scenario = new Scenario
        {
            Seed = seed,
            Blocks = OfficialLayout.Blocks,
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 0.9, Y = 1.9, Th = 0 },
                    [RoleNames.Them] = new() { X = 2.0, Y = 1.9, Th = 0 },
                },
            },
        };
        return WithAmp(scenario, amp);
    }

    private static Scenario WithAmp(Scenario scenario, double? amp) => amp is null
        ? scenario
        : scenario with { Parameters = new Dictionary<string, double> { ["antiStallBladeAmp"] = amp.Value } };

    private static RobotAction FullSpeed() => new() { V = VehicleProfile.Default.MaxSpeed, W = 0 };

    /// <summary>相对位置 (us.X − them.X); 顶牛锁死时其净变化≈0, 破局后被楔方被推退。</summary>
    private static double RelativeSeparation(MatchEngine engine) => Math.Abs(engine.Us.X - engine.Them.X);

    /// <summary>首帧接触 (物理层报告的本 tick 接触) 的 tick 序号, 未接触返回 null。</summary>
    private static int? FirstContactTick(MatchEngine engine, int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            engine.Tick(FullSpeed(), FullSpeed());
            if (engine.Us.WedgedFront || engine.Them.WedgedFront
                || Math.Abs(RelativeSeparation(engine) - 2 * VehicleProfile.Default.CollisionRadius) < 0.02)
            {
                return i;
            }
        }
        return null;
    }

    private static double ExpectedPhase(long seed, string role)
        => (uint)DeterministicRandom.HashString32($"anti-stall:{seed}:{role}") / 4294967296.0 * (2 * Math.PI);

    // ---------- 破局 ----------

    [Fact]
    public void HeadOnPush_WithOscillation_BreaksStalemate()
    {
        var engine = new MatchEngine(HeadOnScenario(seed: 42));
        var contactTick = FirstContactTick(engine, maxTicks: 4 * TicksPerSecond);
        Assert.True(contactTick is not null, "robots must meet within 4 s of full-speed approach");
        (double usX, double themX) atContact = (engine.Us.X, engine.Them.X);

        // 接触后推到 10 s: 必须出现 WedgedFront, 且被楔方被推离僵持位置 0.05 m 以上
        // (接触保持贴合, 破局表现为双车随拍频来回迁移而非原地锁死)。
        var maxExcursion = 0.0;
        int? wedgedTick = null;
        for (var i = contactTick.Value + 1; i <= 10 * TicksPerSecond; i++)
        {
            engine.Tick(FullSpeed(), FullSpeed());
            wedgedTick ??= engine.Us.WedgedFront || engine.Them.WedgedFront ? i : null;
            maxExcursion = Math.Max(maxExcursion, Math.Max(
                Math.Abs(engine.Us.X - atContact.usX), Math.Abs(engine.Them.X - atContact.themX)));
            if (engine.Us.Fsm.SimT >= 10.0 - 1e-9)
            {
                break;
            }
        }
        Assert.True(wedgedTick is not null, "WedgedFront must occur within 10 s (locked before the change)");
        Assert.True(maxExcursion > 0.05,
            $"stalemate position must be left by >0.05 m within 10 s, got {maxExcursion:R} m");
    }

    [Fact]
    public void HeadOnPush_OscillationOff_StaysLocked()
    {
        // amp=0 是逃生舱: 逐位恢复修改前行为 → 60 s 永不楔入, 僵持位置不动。
        var engine = new MatchEngine(HeadOnScenario(seed: 42, amp: 0));
        Assert.Equal(0, engine.Physics.AntiStallBladeAmp);

        var contactTick = FirstContactTick(engine, maxTicks: 4 * TicksPerSecond);
        Assert.True(contactTick is not null);
        (double usX, double themX) atContact = (engine.Us.X, engine.Them.X);

        for (var i = 0; i < 60 * TicksPerSecond; i++)
        {
            engine.Tick(FullSpeed(), FullSpeed());
            Assert.False(engine.Us.WedgedFront);
            Assert.False(engine.Them.WedgedFront);
        }
        Assert.True(Math.Abs(engine.Us.X - atContact.usX) < 0.01 && Math.Abs(engine.Them.X - atContact.themX) < 0.01,
            $"locked pair must hold the deadlock position for 60 s, " +
            $"us drifted {Math.Abs(engine.Us.X - atContact.usX):R} m, them drifted {Math.Abs(engine.Them.X - atContact.themX):R} m");
    }

    // ---------- 楔入方向 ----------

    [Fact]
    public void WedgeDirection_FollowsInstantaneousBladeHeight()
    {
        foreach (var seed in new long[] { 42, 21, 7, 5 })
        {
            var engine = new MatchEngine(HeadOnScenario(seed: seed));
            // 初相派生契约: (seed, role) 经 hashString32 → [0, 2π), 不消费比赛随机流。
            Assert.Equal(ExpectedPhase(seed, RoleNames.Us), engine.Physics.AntiStallPhaseUs);
            Assert.Equal(ExpectedPhase(seed, RoleNames.Them), engine.Physics.AntiStallPhaseThem);

            var physics = engine.Physics;
            var sawWedge = false;
            for (var i = 0; i < 20 * TicksPerSecond; i++)
            {
                engine.Tick(FullSpeed(), FullSpeed());
                if (!(engine.Us.WedgedFront || engine.Them.WedgedFront))
                {
                    continue;
                }
                sawWedge = true;
                var t = engine.Us.Fsm.SimT; // 与楔入判定同源的时间 (physics 内取 a.Fsm.SimT)
                var aBlade = engine.Us.ZG + engine.Us.Vehicle.ShovelHeight
                    + physics.AntiStallBladeAmp * Math.Sin(2 * Math.PI * t / physics.AntiStallBladePeriodUs + physics.AntiStallPhaseUs);
                var bBlade = engine.Them.ZG + engine.Them.Vehicle.ShovelHeight
                    + physics.AntiStallBladeAmp * Math.Sin(2 * Math.PI * t / physics.AntiStallBladePeriodThem + physics.AntiStallPhaseThem);
                Assert.True(Math.Abs(aBlade - bBlade) > 0.004, $"seed {seed} tick {i}: wedge must sit beyond the 0.004 threshold");
                if (engine.Us.WedgedFront)
                {
                    Assert.True(aBlade > bBlade, $"seed {seed} tick {i}: us wedged but them has the higher effective blade");
                }
                if (engine.Them.WedgedFront)
                {
                    Assert.True(bBlade > aBlade, $"seed {seed} tick {i}: them wedged but us has the higher effective blade");
                }
            }
            Assert.True(sawWedge, $"seed {seed}: head-on push must wedge at least once within 20 s");
        }
    }

    // ---------- 越界守卫 ----------

    [Fact]
    public void NonFrontalContact_OscillationNeverLeaks_BitwiseIdenticalToDisabled()
    {
        // 尾部对撞 (facing=0) 不进楔入分支: 默认参数与 amp=0 必须逐位一致。
        var enabled = new MatchEngine(RearPushScenario(seed: 42));
        var disabled = new MatchEngine(RearPushScenario(seed: 42, amp: 0));
        Assert.True(enabled.Physics.AntiStallBladeAmp > 0);

        for (var i = 0; i < 10 * TicksPerSecond; i++)
        {
            var a = enabled.Tick(FullSpeed(), FullSpeed());
            var b = disabled.Tick(FullSpeed(), FullSpeed());
            Assert.Equal(ProtocolJson.Serialize(b), ProtocolJson.Serialize(a));
        }
    }

    // ---------- 确定性 ----------

    [Fact]
    public void Determinism_SameSeed_BitIdentical()
    {
        var a = new MatchEngine(HeadOnScenario(seed: 42));
        var b = new MatchEngine(HeadOnScenario(seed: 42));
        for (var i = 0; i < 20 * TicksPerSecond; i++)
        {
            var sa = a.Tick(FullSpeed(), FullSpeed());
            var sb = b.Tick(FullSpeed(), FullSpeed());
            if (i % 100 == 99)
            {
                Assert.Equal(ProtocolJson.Serialize(sb), ProtocolJson.Serialize(sa));
            }
            if (a.Us.WedgedFront || a.Them.WedgedFront)
            {
                Assert.True(b.Us.WedgedFront || b.Them.WedgedFront, "identical engines must wedge on the same tick");
            }
        }
        Assert.Equal(ProtocolJson.Serialize(a.CommitSnapshot()), ProtocolJson.Serialize(b.CommitSnapshot()));
        Assert.Equal(
            a.Events.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.T:R}|{e.Kind}|{e.Cls}|{e.Msg}"),
            b.Events.Events.Select(e => $"{e.Seq}|{e.Tick}|{e.T:R}|{e.Kind}|{e.Cls}|{e.Msg}"));
    }

    // ---------- 参数解析 ----------

    [Fact]
    public void AntiStallParameters_ParseAndResolveDefaults()
    {
        var parsed = SimParameters.FromDictionary(new Dictionary<string, double>
        {
            ["antiStallBladeAmp"] = 0,
            ["antiStallBladePeriodUs"] = 3.3,
            ["antiStallBladePeriodThem"] = 4.1,
        });
        Assert.Equal(0, parsed.AntiStallBladeAmp);
        Assert.Equal(3.3, parsed.AntiStallBladePeriodUs);
        Assert.Equal(4.1, parsed.AntiStallBladePeriodThem);

        // 缺省 (null) → Physics 内解析默认值; 振幅 0 = 关闭, 周期非正值回落默认。
        var defaults = new MatchEngine(HeadOnScenario(seed: 42)).Physics;
        Assert.Equal(0.006, defaults.AntiStallBladeAmp, precision: 12);
        Assert.Equal(2.1, defaults.AntiStallBladePeriodUs, precision: 12);
        Assert.Equal(2.7, defaults.AntiStallBladePeriodThem, precision: 12);

        var zero = new MatchEngine(HeadOnScenario(seed: 42, amp: 0)).Physics;
        Assert.Equal(0, zero.AntiStallBladeAmp);
        Assert.Equal(2.1, zero.AntiStallBladePeriodUs, precision: 12);
        Assert.Equal(2.7, zero.AntiStallBladePeriodThem, precision: 12);

        var custom = SimParameters.FromDictionary(new Dictionary<string, double>
        {
            ["antiStallBladePeriodUs"] = 0,
        });
        Assert.Equal(0, custom.AntiStallBladePeriodUs); // 原样存 0, 默认值在 PhysicsWorld 解析
        Assert.Throws<ArgumentException>(
            () => SimParameters.FromDictionary(new Dictionary<string, double> { ["antiStallBladeNope"] = 1 }));
    }
}
