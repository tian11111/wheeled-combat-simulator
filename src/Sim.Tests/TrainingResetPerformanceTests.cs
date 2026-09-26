using System.Diagnostics;
using System.Text.Json;
using Sim.Core;
using Sim.Hosting;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 09-25 RL 试点性能门槛(AC2): 训练 factory 的热 reset(模型复用, 无 MJCF 编译)
/// p95 必须不高于冷建模(完整 MJCF 编译) p95 的 50%。
/// </summary>
public class TrainingResetPerformanceTests
{
    private static Scenario MujocoScenario(int seed) => new()
    {
        Seed = seed,
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

    private static double P95(List<double> samples)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        var idx = Math.Max(0, (int)Math.Ceiling(0.95 * sorted.Count) - 1);
        return sorted[idx];
    }

    private static double P50(List<double> samples)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        return sorted[(sorted.Count - 1) / 2];
    }

    [Fact]
    public void ChangedModelContentCompilesAnotherModel()
    {
        if (!OperatingSystem.IsWindows()) return;
        var factory = new MujocoTrainingPhysicsBackendFactory();
        try
        {
            var original = MujocoScenario(42);
            using (var engine = MatchEngineHost.Create(original, null, factory))
            {
                Assert.Equal(0, engine.TickIndex);
            }
            using (var engine = MatchEngineHost.Create(MujocoScenario(43), null, factory))
            {
                Assert.Equal(0, engine.TickIndex);
            }
            Assert.Equal(1, factory.CompileCount); // seed is runtime state, not model identity

            var changed = original with
            {
                Field = original.Field with { PlatformHeight = original.Field.PlatformHeight + 0.001 },
            };
            using (var engine = MatchEngineHost.Create(changed, null, factory))
            {
                Assert.Equal(0, engine.TickIndex);
            }
            Assert.Equal(2, factory.CompileCount);
        }
        finally
        {
            factory.ReleaseAll();
        }
    }

    [Fact]
    public void HotResetP95_IsAtMostHalfOfColdCompileP95()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int coldRuns = 20;
        const int hotRuns = 100;
        var factory = new MujocoTrainingPhysicsBackendFactory();
        var cold = new List<double>();
        var hot = new List<double>();

        // 冷建模: 每次清空缓存 → 完整 MJCF 编译 + 引擎创建 + 释放。
        for (var i = 0; i < coldRuns; i++)
        {
            factory.ReleaseAll();
            var sw = Stopwatch.StartNew();
            using var engine = MatchEngineHost.Create(MujocoScenario(i + 1), null, factory);
            engine.Tick();
            sw.Stop();
            cold.Add(sw.Elapsed.TotalMilliseconds);
        }
        Assert.Equal(coldRuns, factory.CompileCount);

        // 热 reset: 模型已缓存 → 只建引擎运行时与 mjData。
        for (var i = 0; i < hotRuns; i++)
        {
            var sw = Stopwatch.StartNew();
            using var engine = MatchEngineHost.Create(MujocoScenario(1), null, factory);
            engine.Tick();
            sw.Stop();
            hot.Add(sw.Elapsed.TotalMilliseconds);
        }

        Assert.Equal(coldRuns, factory.CompileCount);

        factory.ReleaseAll();
        var coldP95 = P95(cold);
        var hotP95 = P95(hot);
        var outputPath = Environment.GetEnvironmentVariable("ROBOT_SIM_RL_PERF_OUTPUT");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                os = Environment.OSVersion.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                scenario = "official-layout MuJoCo, 0.05 s tick",
                method = "cold clears model cache; hot reuses model; both create engine/mjData and tick once",
                compileCount = factory.CompileCount,
                coldMs = cold,
                hotMs = hot,
                coldP50Ms = P50(cold), coldP95Ms = coldP95,
                hotP50Ms = P50(hot), hotP95Ms = hotP95,
                ratio = hotP95 / coldP95,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(hotP95 * 2.0 <= coldP95,
            $"hot reset p95 {hotP95:0.00} ms must be <= 50% of cold compile p95 {coldP95:0.00} ms " +
            $"(cold median {P95(cold):0.00}); raw cold=[{string.Join(',', cold.Take(5))}..] hot=[{string.Join(',', hot.Take(5))}..]");
    }
}
