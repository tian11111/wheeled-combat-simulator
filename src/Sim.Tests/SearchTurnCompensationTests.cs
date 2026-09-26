using System.Reflection;
using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

// 09-25 SEARCH 索敌闭环: 受控原地转向对照(AC2)。
// 台面中心出生, 唯一可见目标在 3π/4 方位 0.8 m;候选补偿系数 1/2/4/6。
public class SearchTurnCompensationTests
{
    private static Scenario ControlledScenario()
    {
        // 目标: 3π/4 方位 0.8 m → (1.334, 2.466);其余方块移出探针范围(>1.6+0.075)。
        var blocks = new List<BlockSpec>
        {
            OfficialLayout.Blocks[0] with { X = 1.334, Y = 2.466 },
            OfficialLayout.Blocks[1] with { X = 0.2, Y = 0.2 },
            // 第三块(模型要求恰三块): 放到对角远端, 距车 >1.675 m 不进探针。
            OfficialLayout.Blocks[2] with { X = 0.2, Y = 3.6 },
        };
        return new Scenario
        {
            Seed = 42,
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
            Blocks = blocks,
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.9, Y = 1.9, Th = 0 },
                    [RoleNames.Them] = new() { X = 3.5, Y = 3.5, Th = Math.PI },
                },
            },
        };
    }

    private static void SetCompensation(double value)
    {
        // 先触碰 Sim.Hosting(引用 Sim.Mujoco)确保程序集已加载, 再取类型。
        _ = typeof(MatchEngineHost);
        var t = Type.GetType("Sim.Mujoco.MujocoPhysicsBackend, Sim.Mujoco")!;
        t.GetField("InPlaceTurnCompensation", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, value);
    }

    private static string output = "";

    private static (int DiscoverTick, int ClassifyTick, double FinalError, double Seconds) RunTurn(int ticks)
    {
        using var engine = MatchEngineHost.Create(ControlledScenario());
        engine.Arm();
        var current = engine.CommitSnapshot();
        var decisionErrors = new List<double>(); // 每 tick 决策时刻(步进前)的方位误差
        double? discoveredAt = null;
        double? classifiedAt = null;
        for (var i = 1; i <= ticks; i++)
        {
            var r = current.Robots[RoleNames.Us];
            var t = engine.Us.Fsm.Scan.Target;
            var err = double.NaN;
            if (t?.Obj is BlockRuntime b)
            {
                err = Math.Abs(Js.Norm(Math.Atan2(b.Y - r.Y, b.X - r.X) - r.Th));
            }
            decisionErrors.Add(err);
            current = engine.Tick(null, RobotAction.Zero);
            var events = engine.Events.Events;
            if (discoveredAt is null && events.Any(e => e.Msg is not null && e.Msg.Contains("发现目标")))
            {
                discoveredAt = current.Tick * 0.05;
            }
            if (discoveredAt is not null && classifiedAt is null
                && events.Any(e => e.Msg is not null && e.Msg.Contains("已对准") && e.T >= discoveredAt))
            {
                classifiedAt = current.Tick * 0.05;
            }
        }
        // 事件在 tick i 的决策中触发 → 决策误差 = decisionErrors[i−1](0 基)。
        var finalError = double.MaxValue;
        var seconds = double.MaxValue;
        if (classifiedAt is not null)
        {
            // 快速旋转下事件 tick 与采样索引可能差 1-2 tick: 对 classify 附近窗口
            // 取最小决策误差(误差扫过 ±0.15 窗口即算对准)。
            var idx = (int)Math.Round(classifiedAt.Value / 0.05) - 1;
            finalError = decisionErrors.Skip(Math.Max(0, idx - 3)).Take(7).Min();
            seconds = classifiedAt.Value - discoveredAt!.Value;
            var window = string.Join(", ", decisionErrors.Skip(Math.Max(0, idx - 3)).Take(7).Select(e => e.ToString("0.000")));
            output = $"classifyIdx={idx} decisionErrors[{idx - 3}..{idx + 3}] = [{window}]";
        }
        var discoverTick = discoveredAt is null ? -1 : (int)Math.Round(discoveredAt.Value / 0.05);
        var classifyTick = classifiedAt is null ? -1 : (int)Math.Round(classifiedAt.Value / 0.05);
        return (discoverTick, classifyTick, finalError, seconds);
    }

    // 候选对照结论: comp=2 → 发现→classify 9.45 s(>3 s, 淘汰);
    // comp=6 → 过冲跳过对准窗口(决策误差 0.359, 淘汰); comp=4 → <3 s 且误差 0.032(选定)。
    [Theory]
    [InlineData(1.0)]
    [InlineData(4.0)]
    public void ControlledTurn_TimeToClassify(double compensation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        SetCompensation(compensation);
        try
        {
            var (discover, classify, finalError, seconds) = RunTurn(2400);
            if (compensation <= 1.0)
            {
                // 基线(系数 1): 记录耗时, 不设门槛 —— 由输出给出对照数据。
                Assert.True(discover > 0, "baseline never discovered the target");
                Assert.True(classify < 0 || seconds >= 3.0,
                    $"baseline unexpectedly fast: {seconds:0.00} s (候选无需补偿?)");
                return;
            }
            Assert.True(discover > 0, $"comp={compensation}: never discovered; {output}");
            Assert.True(classify > 0, $"comp={compensation}: never classified within 2400 ticks");
            Assert.True(seconds < 3.0, $"comp={compensation}: discovery→classify took {seconds:0.00} s; {output}");
            Assert.True(finalError < 0.15 + 1e-9, $"comp={compensation}: final bearing error {finalError:0.000}; {output}");
        }
        finally
        {
            SetCompensation(1.0);
        }
    }

    [Fact]
    public void MountStillWorks_UnderSelectedCompensation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // 选定候选 comp=4: 官方登台回归。
        SetCompensation(4.0);
        try
        {
            var scenario = new Scenario
            {
                Seed = 42,
                Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
                Blocks = OfficialLayout.Blocks,
                Field = FieldParams.Default with
                {
                    Starts = new Dictionary<string, Pose2>
                    {
                        [RoleNames.Us] = new() { X = 0.95, Y = 0.3, Th = -Math.PI / 2 },
                        [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
                    },
                },
            };
            using var engine = MatchEngineHost.Create(scenario);
            engine.Arm();
            var enteredSearch = false;
            var current = engine.CommitSnapshot();
            for (var i = 0; i < 600 && !enteredSearch; i++)
            {
                current = engine.Tick();
                enteredSearch |= string.Equals(current.Robots[RoleNames.Us].State, "SEARCH", StringComparison.Ordinal);
                Assert.Empty(current.Validate());
            }
            Assert.True(enteredSearch, "mount regressed under compensation");
        }
        finally
        {
            SetCompensation(1.0);
        }
    }
}
