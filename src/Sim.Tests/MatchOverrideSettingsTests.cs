using Sim.Core;
using Sim.GodotShell;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>
/// 批2 回归 (R2.1-R2.3): "比赛/场景"覆盖与"高级/开发者"legacy 接触开关。
/// 兼容性红线 = 新增字段缺省时老配置/旧 bundle 行为逐位不变: 缺省档不写场景字段、
/// 接触开关等价 <c>new ContactResolveOptions()</c>、设置序列化不产生新键。
/// 另钉住覆盖语义、CLI(--seed/--duration)等价与"换种子重开"的同 seed 轨迹一致。
/// </summary>
public class MatchOverrideSettingsTests
{
    /// <summary>官方 2026 布局 + 冻结块坐标 (与 MatchEngineTests.FixedScenario 同源)。</summary>
    private static Scenario OfficialScene(long seed = 42) => new()
    {
        Seed = seed,
        Blocks = OfficialLayout.Blocks,
    };

    private static DesktopSettings WithMatch(MatchOverrides? match)
        => DesktopSettings.Default with { MatchOverrides = match };

    // ---------- 默认值钉死 (兼容性红线) ----------

    [Fact]
    public void DefaultSettings_FollowScenario_AndKeepContactsFullyOpen()
    {
        var settings = DesktopSettings.Default;
        Assert.Null(settings.MatchOverrides);
        Assert.Null(settings.DevContact);
        Assert.True(settings.IsValid);

        // 缺省 = 不覆盖: 原样返回同一场景 (引用相等, 逐位不变的最强断言)。
        var scenario = OfficialScene();
        Assert.Same(scenario, settings.ApplyMatchOverrides(scenario));

        var contact = settings.CreateContactResolveOptions();
        var baseline = new ContactResolveOptions();
        Assert.Equal(baseline.RobotPairObbSeparation, contact.RobotPairObbSeparation);
        Assert.Equal(baseline.RobotBlockObbSeparation, contact.RobotBlockObbSeparation);
        Assert.Equal(baseline.BlockStageWall, contact.BlockStageWall);
    }

    [Fact]
    public void LegacySettingsJson_WithoutNewFields_LoadsFollowDefaults_AndStaysOutOfTheFile()
    {
        // 批2 之前写出的老配置文件: 只有既有字段, 无 matchOverrides/devContact。
        const string legacyJson = """
        {"schemaVersion":1,"window":{"width":1280,"height":720,"mode":"windowed"},"uiScale":1.0,
        "simulationParameters":{"EDGE_THRESHOLD":400},"vehicle":{"mass":3.5,"motorRpm":120,
        "motorTorque":1.72,"wheelRadius":0.0325,"sensorDisabled":[],"sensorOffsets":{}},
        "vision":{"source":"classifyRate","evidencePath":"","csvPath":"","processCommand":"","maxAgeMs":500},
        "usController":{"mode":"builtin","command":"","timeoutMs":100},
        "themController":{"mode":"builtin","command":"","timeoutMs":100}}
        """;
        var settings = ProtocolJson.Deserialize<DesktopSettings>(legacyJson);

        Assert.True(settings.IsValid);
        Assert.Null(settings.MatchOverrides);
        Assert.Null(settings.DevContact);
        var scenario = OfficialScene();
        Assert.Same(scenario, settings.ApplyMatchOverrides(scenario));
        Assert.True(settings.CreateContactResolveOptions().RobotPairObbSeparation);

        // 保存老配置 (未碰新控件) 不产生新键: 文件层面也逐位不变。
        var saved = ProtocolJson.Serialize(settings);
        Assert.DoesNotContain("matchOverrides", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("devContact", saved, StringComparison.Ordinal);
    }

    // ---------- backendOverride 覆盖语义 ----------

    [Fact]
    public void ApplyMatchOverrides_WritesBackendAndModelVersion_OnlyWhenOverridden()
    {
        var legacy = new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks };
        var mujocoV2 = legacy with
        {
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV2 },
        };
        var mujocoV1 = legacy with
        {
            Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        };

        // 跟随档 (null / "follow") 不改任何字段: 三种场景都原样返回。
        foreach (var follow in new[] { (string?)null, "follow", "" })
        {
            var settings = WithMatch(new MatchOverrides { PhysicsBackendOverride = follow });
            Assert.Same(legacy, settings.ApplyMatchOverrides(legacy));
            Assert.Same(mujocoV2, settings.ApplyMatchOverrides(mujocoV2));
        }

        var toV2 = WithMatch(new MatchOverrides { PhysicsBackendOverride = MatchBackendOverrides.MujocoV2 });
        var upgraded = toV2.ApplyMatchOverrides(legacy);
        Assert.Equal(PhysicsSpec.Mujoco, upgraded.Physics?.Backend);
        Assert.Equal(PhysicsSpec.MujocoModelV2, upgraded.Physics?.ModelVersion);
        Assert.Empty(upgraded.Validate());

        var toV1 = WithMatch(new MatchOverrides { PhysicsBackendOverride = MatchBackendOverrides.MujocoV1 });
        Assert.Equal(PhysicsSpec.MujocoModelV1, toV1.ApplyMatchOverrides(mujocoV2).Physics?.ModelVersion);

        var toLegacy = WithMatch(new MatchOverrides { PhysicsBackendOverride = MatchBackendOverrides.Legacy });
        var downgraded = toLegacy.ApplyMatchOverrides(mujocoV2);
        Assert.Equal(PhysicsSpec.Legacy, downgraded.Physics?.Backend);
        Assert.Null(downgraded.Physics?.ModelVersion);
        Assert.Empty(downgraded.Validate());
    }

    [Fact]
    public void ApplyMatchOverrides_DurationAndSeed_FollowWhenNull()
    {
        var scenario = OfficialScene(42);
        Assert.Same(scenario, WithMatch(new MatchOverrides()).ApplyMatchOverrides(scenario));

        var overridden = WithMatch(new MatchOverrides { MatchDuration = 90, Seed = 4096 })
            .ApplyMatchOverrides(scenario);
        Assert.Equal(90, overridden.Field.MatchDuration);
        Assert.Equal(4096, overridden.Seed);
        Assert.Equal(scenario.Field.FieldSize, overridden.Field.FieldSize);
        Assert.Equal(scenario.Blocks.Count, overridden.Blocks.Count);
        Assert.Empty(overridden.Validate());

        // seed=0 是合法覆盖值 (不能被 ?? / ==null 判成"跟随")。
        Assert.Equal(0, WithMatch(new MatchOverrides { Seed = 0 }).ApplyMatchOverrides(scenario).Seed);
    }

    [Fact]
    public void Validate_RejectsBadMatchOverrides_AndAcceptsExplicitOnes()
    {
        string[] Errors(DesktopSettings settings) => settings.Validate().ToArray();

        Assert.Contains(Errors(WithMatch(new MatchOverrides { PhysicsBackendOverride = "mujoco" })),
            error => error.Contains("physicsBackendOverride"));
        Assert.Contains(Errors(WithMatch(new MatchOverrides { MatchDuration = 0 })),
            error => error.Contains("matchDuration"));
        Assert.Contains(Errors(WithMatch(new MatchOverrides { MatchDuration = double.NaN })),
            error => error.Contains("matchDuration"));
        Assert.Contains(Errors(WithMatch(new MatchOverrides { Seed = -1 })),
            error => error.Contains("matchOverrides.seed"));
        Assert.Contains(Errors(WithMatch(new MatchOverrides { Seed = 4097 })),
            error => error.Contains("matchOverrides.seed"));

        Assert.Empty(Errors(WithMatch(new MatchOverrides
        {
            PhysicsBackendOverride = MatchBackendOverrides.MujocoV2,
            ScenarioPath = "scenarios/wushu-ring-2026-mujoco-v2.json",
            MatchDuration = 120,
            Seed = 4096,
        })));
        Assert.Empty(Errors(DesktopSettings.Default with
        {
            DevContact = new DevContact { L1VehicleVehicleObb = false, L3BlockWallBlock = false },
        }));
    }

    [Fact]
    public void CreateContactResolveOptions_MapsTheThreeDevSwitches()
    {
        var settings = DesktopSettings.Default with
        {
            DevContact = new DevContact
            {
                L1VehicleVehicleObb = false,
                L2VehicleBlockObb = true,
                L3BlockWallBlock = false,
            },
        };
        var options = settings.CreateContactResolveOptions();
        Assert.False(options.RobotPairObbSeparation);
        Assert.True(options.RobotBlockObbSeparation);
        Assert.False(options.BlockStageWall);
    }

    // ---------- 生效链路: 同 seed 同布局/轨迹一致 (R2.1 验收) ----------

    [Fact]
    public void MatchOverride_DefaultContactOptions_LeavesLegacyStreamBitIdentical()
    {
        // 注入默认档 (null 与"全开"两种走法) 的 legacy 轨迹必须逐位一致: 新链路
        // 在未改动高级区时不许改变任何对局结果。
        var scenario = OfficialScene();
        using var plain = new MatchSession(scenario);
        using var injected = new MatchSession(scenario, null, DesktopSettings.Default.CreateContactResolveOptions());
        Assert.Equal(RunStream(plain), RunStream(injected));
    }

    [Fact]
    public void SeedOverride_ReproducesCliSeedTrajectory_AndIsStableOnReopen()
    {
        // 设置页覆盖 seed=123/duration=90 后的场景 == Sim.Cli `--seed 123 --duration 90`
        // 的变换 (ProgramOptions.BuildScenario / RlEnvCommand.EpisodeScenario 同式)。
        var applied = WithMatch(new MatchOverrides { Seed = 123, MatchDuration = 90 })
            .ApplyMatchOverrides(OfficialScene());
        var cli = OfficialScene() with
        {
            Seed = 123,
            Field = OfficialScene().Field with { MatchDuration = 90 },
        };
        Assert.Equal(ProtocolJson.Serialize(cli), ProtocolJson.Serialize(applied));

        using var fromSettings = new MatchSession(applied);
        using var fromCli = new MatchSession(cli);
        var settingsStream = RunStream(fromSettings);
        Assert.Equal(RunStream(fromCli), settingsStream);

        // "换种子重开"/F5 语义 = 用同一场景重建会话: 轨迹必须一致 (同 seed 同布局)。
        using var reopened = new MatchSession(applied);
        Assert.Equal(settingsStream, RunStream(reopened));

        // 不同 seed 必须分叉 (防止覆盖被静默忽略)。
        var other = WithMatch(new MatchOverrides { Seed = 7, MatchDuration = 90 })
            .ApplyMatchOverrides(OfficialScene());
        using var otherSession = new MatchSession(other);
        Assert.NotEqual(settingsStream, RunStream(otherSession));
    }

    [Fact]
    public void BackendOverride_ReallySwitchesTheConstructedBackend_BothDirections()
    {
        // spike 2026-10-06 的可执行证据: 会话构造 (MatchSession ctor → MatchEngineHost.Create)
        // 每次按 scenario.physics 重新分派 backend —— 同一场景值改覆盖后重建即热切,
        // 不存在"启动期一次性选定"。
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var legacy = OfficialScene();

        // 覆盖 → mujoco v2: 真实构造 MuJoCo 原生后端并可推进 (不只是分派判据)。
        var upgraded = WithMatch(new MatchOverrides { PhysicsBackendOverride = MatchBackendOverrides.MujocoV2 })
            .ApplyMatchOverrides(legacy);
        Assert.True(MatchEngineHost.UsesMujocoFactory(upgraded));
        using (var session = new MatchSession(upgraded))
        {
            Assert.Equal(PhysicsSpec.Mujoco, session.Engine.PhysicsBackend.BackendId);
            session.Engine.Tick();
        }

        // 覆盖 → legacy: 从同一场景再重建, 回到 legacy 求解器 (双向热切)。
        var downgraded = WithMatch(new MatchOverrides { PhysicsBackendOverride = MatchBackendOverrides.Legacy })
            .ApplyMatchOverrides(upgraded);
        Assert.False(MatchEngineHost.UsesMujocoFactory(downgraded));
        using (var session = new MatchSession(downgraded))
        {
            Assert.Equal(PhysicsSpec.Legacy, session.Engine.PhysicsBackend.BackendId);
            session.Engine.Tick();
        }
    }

    // ---------- helpers ----------

    /// <summary>实况会话推进 120 tick, 每 20 tick 与末帧序列化快照 (逐位比较用)。</summary>
    private static List<string> RunStream(MatchSession session)
    {
        var engine = session.Engine;
        engine.Arm();
        var stream = new List<string>();
        for (var tick = 1; tick <= 120 && !engine.Done; tick++)
        {
            engine.Tick();
            if (tick % 20 == 0)
            {
                stream.Add(ProtocolJson.Serialize(engine.CommitSnapshot()));
            }
        }
        stream.Add(ProtocolJson.Serialize(engine.CommitSnapshot()));
        return stream;
    }
}
