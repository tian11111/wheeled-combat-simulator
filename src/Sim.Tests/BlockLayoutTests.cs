using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// v2 真车场景能量块 seed 随机布局（BlockSpec null 坐标 = 裁判确定性放置，
/// 见 BlockSpec 文档）：同 seed 同布局、随机性可探、禁区约束满足；
/// legacy/v1 与显式坐标场景冻结位不变。放置实现在 MatchEngine.RespawnBlock
/// （台面内缩 0.35 m、避两车 0.8 m、避中央 0.6 m 区、块间 0.5 m）。
/// </summary>
public class BlockLayoutTests
{
    private static Scenario RandomLayoutScenario(long seed) => new()
    {
        Seed = seed,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
        Blocks =
        [
            new BlockSpec { Kind = BlockKind.Buff },
            new BlockSpec { Kind = BlockKind.Buff },
            new BlockSpec { Kind = BlockKind.Debuff },
        ],
    };

    [Fact]
    public void SameSeed_ReproducesIdenticalLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var a = MatchEngineHost.Create(RandomLayoutScenario(42));
        using var b = MatchEngineHost.Create(RandomLayoutScenario(42));
        Assert.Equal(3, a.Blocks.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(a.Blocks[i].Kind, b.Blocks[i].Kind);
            Assert.Equal(a.Blocks[i].X, b.Blocks[i].X, 12);
            Assert.Equal(a.Blocks[i].Y, b.Blocks[i].Y, 12);
        }
    }

    [Fact]
    public void SameSeed_TrajectoriesMatchThroughEarlyTicks()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var a = MatchEngineHost.Create(RandomLayoutScenario(42));
        using var b = MatchEngineHost.Create(RandomLayoutScenario(42));
        a.Arm();
        b.Arm();
        for (var i = 0; i < 60 && !a.Done; i++)
        {
            a.Tick();
            b.Tick();
            Assert.Equal(a.Us.X, b.Us.X, 9);
            Assert.Equal(a.Them.X, b.Them.X, 9);
        }
    }

    [Fact]
    public void DifferentSeeds_ExploreDifferentLayouts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        static (double X, double Y)[] Layout(long seed)
        {
            using var e = MatchEngineHost.Create(RandomLayoutScenario(seed));
            return e.Blocks.Select(b => (b.X, b.Y)).ToArray();
        }

        var seeds = new long[] { 42, 43, 44, 45 };
        var layouts = seeds.Select(Layout).ToArray();
        var differentPairs = 0;
        for (var i = 0; i < layouts.Length; i++)
        {
            for (var j = i + 1; j < layouts.Length; j++)
            {
                var differs = layouts[i].Zip(layouts[j])
                    .Any(p => Math.Abs(p.First.X - p.Second.X) > 1e-9 || Math.Abs(p.First.Y - p.Second.Y) > 1e-9);
                if (differs)
                {
                    differentPairs++;
                }
            }
        }

        // 6 对里至少 3 对不同: 三块各自独立抽点后整组重合的概率可忽略,
        // 该断言只拦截"布局实际没随 seed 变"的回归。
        Assert.True(differentPairs >= 3, $"6 组 seed 对里只有 {differentPairs} 对布局不同, 随机性可疑");
    }

    [Fact]
    public void SeededPlacement_SatisfiesKeepoutConstraints()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var seed in new long[] { 1, 42, 777 })
        {
            using var e = MatchEngineHost.Create(RandomLayoutScenario(seed));
            var f = e.Field;
            var span = 2 * f.Half - 0.7;
            var locals = e.Blocks.Select(b => f.Transform.WorldToLocalPoint(b.X, b.Y)).ToArray();

            foreach (var (lx, ly) in locals)
            {
                Assert.True(f.OnPlatformLocal(lx, ly), $"seed {seed}: 块不在台面 ({lx:0.000},{ly:0.000})");
                Assert.InRange(lx, f.El + 0.35 - 1e-9, f.El + 0.35 + span + 1e-9);
                Assert.InRange(ly, f.El + 0.35 - 1e-9, f.El + 0.35 + span + 1e-9);
                Assert.False(lx > 1.6 && lx < 2.2 && ly > 1.6 && ly < 2.2, $"seed {seed}: 块落在中央禁区 ({lx:0.000},{ly:0.000})");
            }

            for (var i = 0; i < locals.Length; i++)
            {
                for (var j = i + 1; j < locals.Length; j++)
                {
                    var gap = Js.Hypot(locals[i].X - locals[j].X, locals[i].Y - locals[j].Y);
                    Assert.True(gap > 0.5, $"seed {seed}: 块 {i}/{j} 间距 {gap:0.000} 不足 0.5");
                }
            }

            var (ux, uy) = f.Transform.WorldToLocalPoint(e.Us.X, e.Us.Y);
            var (tx, ty) = f.Transform.WorldToLocalPoint(e.Them.X, e.Them.Y);
            foreach (var (lx, ly) in locals)
            {
                var distUs = Js.Hypot(ux - lx, uy - ly);
                var distThem = Js.Hypot(tx - lx, ty - ly);
                Assert.True(Math.Min(distUs, distThem) > 0.8,
                    $"seed {seed}: 块 ({lx:0.000},{ly:0.000}) 距车 {Math.Min(distUs, distThem):0.000} 不足 0.8");
            }
        }
    }

    [Fact]
    public void FrozenScenarios_KeepOfficialCoordinates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var legacy = new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks };
        using var e = MatchEngineHost.Create(legacy);
        var official = OfficialLayout.Blocks;
        Assert.Equal(3, e.Blocks.Count);
        for (var i = 0; i < 3; i++)
        {
            var (wx, wy) = e.Field.Transform.LocalToWorldPoint(official[i].X!.Value, official[i].Y!.Value);
            Assert.Equal(wx, e.Blocks[i].X, 12);
            Assert.Equal(wy, e.Blocks[i].Y, 12);
        }
    }
}
