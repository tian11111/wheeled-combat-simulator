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
    // 2026-09-29: timestep 0.002 物理重校后 seed 42 不再翻覆; seed 19 在 724 tick
    // 翻覆并于 5 tick 内进入 INCAPACITATED (tmp/flipscan 扫描 1..40 选定)。
    private static Scenario MujocoV1Scenario() => new()
    {
        Seed = 19,
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

        Assert.True(flippedAtTick > 0, "seed 19 in MuJoCo v1 geometry is expected to flip; trajectory changed, review needed");
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
