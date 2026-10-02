using Sim.Cli;
using Sim.Core;
using Sim.GodotShell;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 批3 接线单测：场景 <c>vehicles[].controller</c> 选择 (协议加法) → MatchEngine
/// mbri 派发；桌面控制器档位 (内置 FSM / 内置 MBri / 外部命令) 与场景写入；
/// CLI <c>--stats</c> 在台迁移统计。默认 builtin 路径逐位不变由"省略字段 ==
/// 显式 builtin"的引擎级指纹相等钉住。
/// </summary>
[Collection("cli-console")]
public sealed class MbriSelectionTests
{
    private static Scenario LegacyScenario() => new()
    {
        Seed = 42,
        Field = new FieldParams { MatchDuration = 20 },
        Blocks = OfficialLayout.Blocks,
    };

    private static Scenario WithControllers(Scenario scenario, string? us, string? them)
    {
        var vehicles = new Dictionary<string, VehicleProfile>(scenario.Vehicles, StringComparer.Ordinal);
        if (us is not null)
        {
            vehicles[RoleNames.Us] = vehicles[RoleNames.Us] with { Controller = us };
        }
        if (them is not null)
        {
            vehicles[RoleNames.Them] = vehicles[RoleNames.Them] with { Controller = them };
        }
        return scenario with { Vehicles = vehicles };
    }

    /// <summary>跑满一场 (至 Done 或 tick 上限) 的事件指纹 + 终局比分。</summary>
    private static (List<string> Fingerprints, Scores Scores) RunFingerprints(Scenario scenario, int maxTicks = 2400)
    {
        using var engine = MatchEngineHost.Create(scenario);
        engine.Arm();
        var fingerprints = new List<string>();
        for (var i = 0; i < maxTicks && !engine.Done; i++)
        {
            var snapshot = engine.Tick();
            if (snapshot.Events is { Count: > 0 })
            {
                foreach (var evt in snapshot.Events)
                {
                    fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                }
            }
        }
        return (fingerprints, engine.Scores);
    }

    // ---------- 协议字段 ----------

    [Fact]
    public void ControllerField_DefaultsToNull_AndIsOmittedFromTheWire()
    {
        Assert.Null(new VehicleProfile().Controller);
        var json = ProtocolJson.Serialize(new Scenario { Seed = 1 });
        Assert.DoesNotContain("controller", json, StringComparison.Ordinal);

        var scenario = WithControllers(LegacyScenario(), VehicleControllers.Mbri, null);
        var round = ProtocolJson.Deserialize<Scenario>(ProtocolJson.Serialize(scenario));
        Assert.Equal(VehicleControllers.Mbri, round.Vehicles[RoleNames.Us].Controller);
        Assert.Null(round.Vehicles[RoleNames.Them].Controller);
        Assert.Empty(round.Validate());
    }

    [Fact]
    public void ControllerField_RejectsUnknownValues()
    {
        var scenario = WithControllers(LegacyScenario(), "external", null);
        Assert.Contains(scenario.Validate(), error => error.Contains("controller", StringComparison.Ordinal));
    }

    // ---------- 默认路径逐位不变 ----------

    [Fact]
    public void ExplicitBuiltin_IsBitIdentical_ToOmittedController()
    {
        var omitted = RunFingerprints(LegacyScenario());
        var explicitBuiltin = RunFingerprints(WithControllers(LegacyScenario(),
            VehicleControllers.BuiltIn, VehicleControllers.BuiltIn));

        Assert.Equal(omitted.Scores.Us, explicitBuiltin.Scores.Us);
        Assert.Equal(omitted.Scores.Them, explicitBuiltin.Scores.Them);
        Assert.Equal(omitted.Fingerprints, explicitBuiltin.Fingerprints);
    }

    // ---------- mbri 派发 ----------

    [Fact]
    public void MbriSelected_StartsWithExclusiveReverseMount_WhileBuiltinSideKeepsItsOwnFsm()
    {
        using var engine = MatchEngineHost.Create(
            WithControllers(LegacyScenario(), VehicleControllers.Mbri, null));
        engine.Arm();
        var snapshot = engine.Tick();

        // 我方 (mbri): 首 tick 即 START_REVERSE 独占后退 (1000×k, 见 MbriFsmTests)。
        Assert.Equal("开局后退上台", engine.Us.Fsm.Action);
        Assert.Equal(FsmState.MountRing, engine.Us.Fsm.State);
        Assert.Equal(-1000 * 0.000896, engine.Us.V, 9);
        Assert.Contains(snapshot.Events!, evt => evt.Msg!.Contains("[mbri] START_REVERSE", StringComparison.Ordinal));

        // 对手 (builtin): 同样 MountRing, 但走既有姿态确认路径, 无 mbri 事件。
        Assert.Equal(FsmState.MountRing, engine.Them.Fsm.State);
        Assert.DoesNotContain(snapshot.Events!,
            evt => evt.Role == RoleNames.Them && evt.Msg!.Contains("[mbri]", StringComparison.Ordinal));
    }

    [Fact]
    public void MbriSelected_IsDeterministicAcrossRuns()
    {
        var scenario = WithControllers(LegacyScenario(), VehicleControllers.Mbri, VehicleControllers.Mbri);
        var first = RunFingerprints(scenario);
        var second = RunFingerprints(scenario);

        Assert.Equal(first.Scores.Us, second.Scores.Us);
        Assert.Equal(first.Scores.Them, second.Scores.Them);
        Assert.Equal(first.Fingerprints, second.Fingerprints);
        Assert.NotEmpty(first.Fingerprints);
    }

    [Fact]
    public void MbriMatch_ProducesMappedSnapshotStates_ForHudAndReferee()
    {
        using var engine = MatchEngineHost.Create(
            WithControllers(LegacyScenario(), VehicleControllers.Mbri, null));
        engine.Arm();
        for (var i = 0; i < 200 && !engine.Done; i++)
        {
            engine.Tick();
        }
        // 快照 State 始终落在既有 FsmState 枚举 (映射保证 HUD/裁判可读)。
        Assert.True(Enum.IsDefined(engine.Us.Fsm.State));
    }

    // ---------- 桌面设置 ----------

    [Fact]
    public void DesktopMbriMode_ValidatesWithoutCommand()
    {
        var settings = DesktopSettings.Default with
        {
            UsController = new ControllerProfile { Mode = ControllerModes.Mbri },
            ThemController = new ControllerProfile { Mode = ControllerModes.Mbri },
        };
        Assert.Empty(settings.Validate());
        Assert.True(settings.UsController.IsMbri);

        var unknown = settings with { UsController = new ControllerProfile { Mode = "mbri-typo" } };
        Assert.Contains(unknown.Validate(), error => error.Contains("usController.mode"));
    }

    [Fact]
    public void ApplyControllerSelection_WritesMbriOnlyWhenSelected_AndNeverMutatesTheCaller()
    {
        var scenario = new Scenario { Seed = 1 };
        // 默认 (builtin) 档: 原样返回同一场景引用 —— 既有身份逐位不变。
        Assert.Same(scenario, DesktopSettings.Default.ApplyControllerSelection(scenario));

        var settings = DesktopSettings.Default with
        {
            UsController = new ControllerProfile { Mode = ControllerModes.Mbri },
        };
        var applied = settings.ApplyControllerSelection(scenario);
        Assert.Equal(VehicleControllers.Mbri, applied.Vehicles[RoleNames.Us].Controller);
        Assert.Null(applied.Vehicles[RoleNames.Them].Controller);
        Assert.Null(scenario.Vehicles[RoleNames.Us].Controller);

        // 对手档位同理。
        var themSettings = DesktopSettings.Default with
        {
            ThemController = new ControllerProfile { Mode = ControllerModes.Mbri },
        };
        var themApplied = themSettings.ApplyControllerSelection(scenario);
        Assert.Equal(VehicleControllers.Mbri, themApplied.Vehicles[RoleNames.Them].Controller);
        Assert.Null(themApplied.Vehicles[RoleNames.Us].Controller);
    }

    [Fact]
    public void ControllerWiring_DescribesMbri_AndKeepsItInTheAssignment()
    {
        var profile = new ControllerProfile { Mode = ControllerModes.Mbri };
        Assert.Equal("内置 MBri", ControllerWiring.DescribeSource(profile));

        var assignment = ControllerWiring.Resolve(LegacyScenario(), profile, new ControllerProfile());
        Assert.Equal(ControllerModes.Mbri, assignment.Us.Mode);
        Assert.False(assignment.Exhibition);
        Assert.Null(assignment.Notice);
    }

    // ---------- CLI --stats ----------

    [Fact]
    public void MatchStats_ReportsPerRoleOnStageTransitions()
    {
        var stdout = Console.Out;
        using var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            var code = Program.Main(["match", "--seed", "42", "--duration", "20", "--stats"]);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var statsLine = sink.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("stats seed=42", StringComparison.Ordinal));
        Assert.NotNull(statsLine);
        // 官方场景 20 s 场 (既有基线 4:8) 中双方各有掉台/上台迁移; --stats 只是
        // 追加一行, 人类摘要 (seed=... ) 原样保留。
        Assert.Contains("us_falls=", statsLine);
        Assert.Contains("them_falls=", statsLine);
        Assert.Contains("us_mounts=", statsLine);
        Assert.Contains("them_mounts=", statsLine);
        Assert.Contains("seed=42 ticks=", sink.ToString());
    }

    [Fact]
    public void MatchMbriScenario_ProducesMbriEventsAndStats()
    {
        var scenarioPath = Path.Combine(Path.GetTempPath(),
            $"mbri-selection-{Guid.NewGuid():N}.json");
        try
        {
            var scenario = WithControllers(LegacyScenario(), VehicleControllers.Mbri, null) with { Seed = 7 };
            File.WriteAllText(scenarioPath, ProtocolJson.Serialize(scenario));

            var stdout = Console.Out;
            using var sink = new StringWriter();
            try
            {
                Console.SetOut(sink);
                Assert.Equal(0, Program.Main(
                    ["match", "--seed", "7", "--duration", "10", "--scenario", scenarioPath, "--events", "--stats"]));
            }
            finally
            {
                Console.SetOut(stdout);
            }
            var output = sink.ToString();
            Assert.Contains("[mbri] START_REVERSE", output, StringComparison.Ordinal);
            Assert.Contains("stats seed=7", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(scenarioPath);
        }
    }
}
