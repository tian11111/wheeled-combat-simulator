using System.Reflection;
using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

// 2026-09-28 倾覆门控: MuJoCo 下车会底朝天, 轮子朝天无法驱动, 而 2D 语义的
// FSM/RECOVER 都不知道这件事 —— 持续倾覆应进入 INCAPACITATED 停车等待裁判重启;
// legacy 没有 roll/pitch 自由度, 该状态必须永不出现(行为逐位不变)。
public class IncapacitatedTests
{
    // 2026-09-30 时基修正(每 tick 实际积分 0.02 s → 0.05 s, 物理时间流速 0.4× → 1×)
    // 后 seed 19 不再翻覆: 同一场景 1..40 扫描实测其 maxRoll 仅 11.4°, 从未越 60°
    // 倾角门 —— 慢动作时基下每个裁判 tick 只推进 0.02 s, 接触/驱动力序列被 2.5×
    // 拉长, 翻覆轨迹本就不可信。用同一场景与同一判据重扫 1..40 (tmp/timescan)
    // 选定 seed 13: 269 tick 翻覆, 276 tick 宣告 INCAPACITATED(Δ7 ≤ 30), 事件 2 次。
    // 2026-09-30 批 2 电机真值标定(工程执行器 kv=0.25/±3.0 N·m/±80 rad/s →
    // 2342 真值 kv=0.136873/±1.72 N·m/±12.566 rad/s + duty 口径)后 seed 13 不再翻覆:
    // v1 轮径 0.065 下驱动极速被 ω_noload 压到 12.566×0.065 = 0.817 m/s(旧 ctrlrange
    // ±80 rad/s 下 FSM 1.5 m/s 档位可全速达成), 轮端扭矩上限同时 −43%(3.0 → 1.72 N·m)
    // —— 冲台/撞击掀翻所需的驱动能量整体下降。同场景同判据重扫 1..170(timescan,
    // 2400 tick)实测: 1..50 最大 roll 仅 19.7° 无翻覆; 选定 seed 155: 803 tick 翻覆
    // (maxRoll 180°), 809 tick 宣告 INCAPACITATED(Δ6 ≤ 30), 事件 2 次。
    private static Scenario MujocoV1Scenario() => new()
    {
        Seed = 155,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        Blocks = OfficialLayout.Blocks,
    };

    [Fact]
    public void UprightProjection_MatchesLevelSideAndInvertedPoses()
    {
        _ = typeof(MatchEngineHost); // 触发 Sim.Mujoco 程序集加载
        var type = Type.GetType("Sim.Mujoco.MujocoPhysicsBackend, Sim.Mujoco")!;
        var uprightOf = type.GetMethod("UprightOf", BindingFlags.NonPublic | BindingFlags.Static)!;
        var isFlippedAt = type.GetMethod("IsFlippedAt", BindingFlags.NonPublic | BindingFlags.Static)!;

        double Upright(double qx, double qy, double qw)
            => (double)uprightOf.Invoke(null, [new PhysicsPose3 { Qw = qw, Qx = qx, Qy = qy }])!;
        bool Flipped(double upright) => (bool)isFlippedAt.Invoke(null, [upright])!;

        Assert.Equal(1.0, Upright(0, 0, 1), 12);                        // 直立
        Assert.Equal(0.0, Upright(Math.Sqrt(0.5), 0, Math.Sqrt(0.5)), 12); // 侧躺 90°
        Assert.Equal(-1.0, Upright(1, 0, 0), 12);                       // 底朝天 180°

        Assert.False(Flipped(Upright(0, 0, 1)));
        Assert.True(Flipped(Upright(Math.Sqrt(0.5), 0, Math.Sqrt(0.5))), "90° 侧躺也应判为失去行动能力");
        Assert.True(Flipped(Upright(1, 0, 0)));
        Assert.False(Flipped(0.5), "阈值边界: 恰好 0.5 不算倾覆");
        Assert.True(Flipped(0.4999));
    }

    [Fact]
    public void MujocoMatch_FlippedRobotStopsInIncapacitatedState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var engine = MatchEngineHost.Create(MujocoV1Scenario());
        engine.Arm();
        long flippedAtTick = -1;
        long declaredTick = -1;
        for (var i = 0; i < 1400 && !engine.Done; i++)
        {
            engine.Tick();
            var us = engine.Us;
            if (flippedAtTick < 0 && Math.Abs(us.Roll) > 60 * Math.PI / 180)
            {
                flippedAtTick = engine.TickIndex;
            }
            if (us.Fsm.State == FsmState.Incapacitated)
            {
                declaredTick = declaredTick < 0 ? engine.TickIndex : declaredTick;
                Assert.Equal(0.0, us.V);
                Assert.Equal(0.0, us.W);
            }
        }

        Assert.True(flippedAtTick > 0, "seed 155 in MuJoCo v1 geometry is expected to flip; trajectory changed, review needed");
        Assert.True(declaredTick > 0, "a flipped robot must enter INCAPACITATED");
        Assert.True(declaredTick - flippedAtTick <= 30,
            $"flip at tick {flippedAtTick} -> INCAPACITATED at tick {declaredTick} should be within 30 ticks");
        var events = engine.Events.Events.Count(e => e.Kind == EventKind.Incapacitated);
        Assert.True(events >= 1 && events <= 2, $"INCAPACITATED should be announced on entry (got {events})");
    }

    [Fact]
    public void LegacyMatch_NeverEntersIncapacitated()
    {
        var scenario = new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks };
        using var engine = MatchEngineHost.Create(scenario);
        engine.Arm();
        for (var i = 0; i < 2400 && !engine.Done; i++)
        {
            engine.Tick();
            Assert.NotEqual(FsmState.Incapacitated, engine.Us.Fsm.State);
            Assert.NotEqual(FsmState.Incapacitated, engine.Them.Fsm.State);
        }

        Assert.DoesNotContain(engine.Events.Events, e => e.Kind == EventKind.Incapacitated);
    }
}
