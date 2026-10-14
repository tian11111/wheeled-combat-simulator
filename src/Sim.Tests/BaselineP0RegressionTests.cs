using Sim.Core;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 10-14-baseline-p0-fix 的两个 P0 回归钉子。
///
/// <para>P0-1 台沿抖动死循环: 掉台判据由 footprint 四角改为"车体中心越界"。
/// 旧判据把贴沿原地旋转时角点的短暂外扫当成掉台, 实测 seed1@120s 每 0.7-1.0s
/// 产出一对 Mount+Drop、对手 93 分里 90 分 (96.8%) 来自我方掉台送分。
/// 新判据下 seed1@120s 计分掉台 0 次、不存在同 tick 的 Mount+Drop 对。</para>
///
/// <para>P0-2 ATTACK 无超时死锁: 连续进攻超过 AttackTimeoutSeconds(5s) 转 SEARCH。
/// 修复前 seed2@300s 在 t=35.2s 之后不再产出任何事件 (静默 264.8s)、比分冻结 10:4。</para>
/// </summary>
public sealed class BaselineP0RegressionTests
{
    // ---------- helpers ----------

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    /// <summary>官方场景 scenarios/wushu-ring-2026.json, 覆写 seed 与比赛时长。</summary>
    private static Scenario OfficialScenario(long seed, double duration)
    {
        var path = FindRepoFile("scenarios/wushu-ring-2026.json");
        var scenario = ProtocolJson.Deserialize<Scenario>(File.ReadAllText(path));
        return scenario with
        {
            Seed = seed,
            Field = scenario.Field with { MatchDuration = duration },
        };
    }

    /// <summary>
    /// 整场跑完并累积每个 tick 的事件。<see cref="MatchEngine.Events"/> 只保留最近
    /// 500 条, 因此必须逐 tick 收集 <see cref="Snapshot.Events"/> 才能拿到全量事件流。
    /// </summary>
    private static (List<Event> Events, MatchEngine Engine) RunMatch(Scenario scenario, int maxTicks = 20_000)
    {
        var engine = new MatchEngine(scenario);
        engine.Arm();
        var all = new List<Event>();
        for (var i = 0; i < maxTicks && !engine.Done; i++)
        {
            all.AddRange(engine.Tick().Events ?? []);
        }
        Assert.True(engine.Done, $"比赛未在 {maxTicks} tick 内结束");
        return (all, engine);
    }

    /// <summary>计分掉台次数 (带 points 的 Drop 事件; 一次掉台只计一次)。</summary>
    private static int ScoredDrops(List<Event> events, string role)
        => events.Count(e => e.Type == EventKind.Drop
            && e.Role == role
            && e.Data is not null
            && e.Data.ContainsKey("points"));

    /// <summary>真实掉台次数 (FSM 的 "[fsm] 掉台! → RECOVER" 宣告, 每次掉台一条)。</summary>
    private static int RealDrops(List<Event> events, string role)
        => events.Count(e => e.Type == EventKind.Drop
            && e.Role == role
            && (e.Data is null || !e.Data.ContainsKey("points")));

    /// <summary>摆位姿并清零速度 (外部动作路径下 FSM 不介入, 位姿可精确构造)。</summary>
    private static void Park(RobotRuntime r, double x, double y, double th)
    {
        r.X = x;
        r.Y = y;
        r.Th = th;
        r.V = 0;
        r.W = 0;
        r.Vx = 0;
        r.Vy = 0;
    }

    // ---------- P0-1: 掉台判据 ----------

    [Fact]
    public void DropCriterion_UsesBodyCenter_NotFootprintCorners()
    {
        // 官方台面世界坐标 [0.7, 3.1]^2 (中心 (1.9,1.9), 半宽 1.2)。
        // 车体中心 (3.0, 1.9)、车头朝 +x 时前角 x = 3.0 + FrontExtent(0.22) = 3.22 > 3.1 悬出台沿,
        // 而中心 3.0 仍在台内 —— 这正是"四角判据判掉台、中心判据不判"的构造。
        var engine = new MatchEngine(OfficialScenario(seed: 1, duration: 120));
        engine.Tick(RobotAction.Zero); // PREP → RUNNING (两车进入 Manual, 不跑 FSM)
        Assert.True(engine.Us.Fsm.Armed);

        Park(engine.Us, 3.0, 1.9, th: 0.0);
        engine.Tick(RobotAction.Zero); // 记录 WasOn = true
        Park(engine.Us, 3.0, 1.9, th: 0.0);
        var hang = engine.Tick(RobotAction.Zero).Events ?? [];

        Assert.True(engine.Field.OnPlatform(engine.Us.X, engine.Us.Y)); // 车体中心在台
        Assert.False(engine.PhysicsBackend.OnStage(engine.Us));          // 四角判据: 不在台
        Assert.DoesNotContain(hang, e => e.Type == EventKind.Drop);
        Assert.DoesNotContain(hang, e => e.Type == EventKind.SimultaneousDrop);

        // 车体中心也离开台面 → 记一次掉台; 对方在台上时按规则给对方 +1。
        Park(engine.Them, 1.9, 1.9, th: 0.0);
        engine.Tick(RobotAction.Zero); // 对方 WasOn = true
        var before = engine.Scores.Them;

        Park(engine.Us, 3.2, 1.9, th: 0.0);
        var off = engine.Tick(RobotAction.Zero).Events ?? [];

        Assert.Contains(off, e => e.Type == EventKind.Drop && e.Role == RoleNames.Us);
        Assert.Equal(before + 1, engine.Scores.Them);
    }

    [Fact]
    public void OfficialSeed1_HasNoSameTickMountDropPair_AndNoSelfDropPoints()
    {
        var (events, _) = RunMatch(OfficialScenario(seed: 1, duration: 120));

        // R1-2: 不存在同一 tick 的 Mount+Drop 对 (旧行为: 每 0.7-1.0s 成对出现)。
        var mountTicks = events.Where(e => e.Type == EventKind.Mount).Select(e => e.Tick).ToHashSet();
        var dropTicks = events
            .Where(e => e.Type is EventKind.Drop or EventKind.SimultaneousDrop)
            .Select(e => e.Tick)
            .ToHashSet();
        Assert.Empty(mountTicks.Intersect(dropTicks));

        // R1-1: 送分来自掉台 → 我方计分掉台 0 次 (旧行为 90 分)。
        Assert.Equal(0, ScoredDrops(events, RoleNames.Us));

        // R1-3: 我方真实掉台保持个位数 (旧行为 105 次抖动掉台)。
        Assert.True(RealDrops(events, RoleNames.Us) <= 5,
            $"我方真实掉台 {RealDrops(events, RoleNames.Us)} 次, 期望个位数");

        // 登台后短窗不掉: 首次上台后 2s (40 tick) 内不得再掉台 (旧行为上台即掉)。
        var firstMount = events.First(e => e.Type == EventKind.Mount && e.Role == RoleNames.Us);
        Assert.DoesNotContain(events, e => e.Type == EventKind.Drop
            && e.Role == RoleNames.Us
            && e.Tick > firstMount.Tick
            && e.Tick <= firstMount.Tick + 40);
    }

    // ---------- P0-2: ATTACK 超时兜底 ----------

    [Fact]
    public void OfficialSeed2_AttackStalemateIsCappedByTimeout()
    {
        var (events, _) = RunMatch(OfficialScenario(seed: 2, duration: 300));

        // 超时兜底生效: 出现"连续进攻 … 未分出胜负 → SEARCH(超时兜底)"宣告。
        Assert.Contains(events, e => e.Msg?.Contains("连续进攻") == true
            && e.Msg.Contains("超时兜底"));

        // 僵持不再死锁: 无 >60s 静默段 (旧行为 t=35.2s 后静默 264.8s)。
        var substantive = events
            .Where(e => e.Type is not (EventKind.End or EventKind.Timeout))
            .Where(e => e.Msg?.Contains("FINISHED") != true && e.Msg?.Contains("比赛时间到") != true)
            .ToList();
        Assert.NotEmpty(substantive);

        var ts = substantive.Select(e => e.T).Distinct().OrderBy(t => t).ToList();
        var maxGap = ts.Zip(ts.Skip(1), (a, b) => b - a).DefaultIfEmpty(0.0).Max();
        Assert.True(maxGap <= 60.0, $"最大静默段 {maxGap:F1}s, 超过 60s");

        // R2-2: 最后 60s 仍有实质事件。
        Assert.Contains(substantive, e => e.T >= 300.0 - 60.0);
    }

    [Theory]
    [InlineData(5L)]
    [InlineData(6L)]
    public void OfficialStalemateSeeds_KeepProducingEventsAtTheEnd(long seed)
    {
        var (events, _) = RunMatch(OfficialScenario(seed, duration: 300));

        var substantive = events
            .Where(e => e.Type is not (EventKind.End or EventKind.Timeout))
            .Where(e => e.Msg?.Contains("FINISHED") != true && e.Msg?.Contains("比赛时间到") != true)
            .ToList();
        var ts = substantive.Select(e => e.T).Distinct().OrderBy(t => t).ToList();
        var maxGap = ts.Zip(ts.Skip(1), (a, b) => b - a).DefaultIfEmpty(0.0).Max();

        Assert.True(maxGap <= 60.0, $"seed {seed} 最大静默段 {maxGap:F1}s, 超过 60s");
        Assert.True(ts.Count(t => t >= 300.0 - 60.0) >= 10,
            $"seed {seed} 最后 60s 只有 {ts.Count(t => t >= 300.0 - 60.0)} 个实质事件时刻");
    }
}
