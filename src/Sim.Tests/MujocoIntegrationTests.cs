using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Tests;

public class MujocoIntegrationTests
{
    private static Scenario Scenario(long seed = 42) => new()
    {
        Seed = seed,
        Physics = new PhysicsSpec { Backend = PhysicsSpec.Mujoco, ModelVersion = PhysicsSpec.MujocoModelV1 },
        Blocks = OfficialLayout.Blocks,
        Field = FieldParams.Default with
        {
            Starts = new Dictionary<string, Pose2>
            {
                [RoleNames.Us] = new() { X = 0.95, Y = 0.3, Th = 0 },
                [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI },
            },
        },
    };

    [Fact]
    public void NativeMode_StraightAndTurnProduceFiniteThreeDimensionalState()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = MatchEngineHost.Create(Scenario());
        var initial = engine.CommitSnapshot();
        var drive = new RobotAction { V = 0.5 };
        Snapshot current = initial;
        for (var i = 0; i < 20; i++)
            current = engine.Tick(drive, RobotAction.Zero);

        var start = initial.Robots[RoleNames.Us];
        var end = current.Robots[RoleNames.Us];
        Assert.True(end.X > start.X + 0.01,
            $"straight drive did not advance: {start.X} -> {end.X}, final yaw={end.Th}, centre Z={current.PhysicsPoses?.Robots[RoleNames.Us].Z}");
        Assert.NotNull(current.PhysicsPoses);
        Assert.Equal(2, current.PhysicsPoses!.Buffs.Count);
        Assert.NotNull(current.PhysicsPoses.Debuff);
        Assert.Empty(current.Validate());

        var beforeTurn = end.Th;
        var turn = new RobotAction { W = 1.0 };
        for (var i = 0; i < 10; i++)
            current = engine.Tick(turn, RobotAction.Zero);
        Assert.True(Math.Abs(current.Robots[RoleNames.Us].Th - beforeTurn) > 0.02,
            $"turn did not rotate: {beforeTurn} -> {current.Robots[RoleNames.Us].Th}");
    }

    [Fact]
    public void NativeMode_RealRestartResetsOnlyTargetAndPreservesClock()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = MatchEngineHost.Create(Scenario());
        for (var i = 0; i < 12; i++)
            engine.Tick(new RobotAction { V = 0.5 }, RobotAction.Zero);
        var before = engine.CommitSnapshot();

        Assert.True(engine.RestartRobot(RoleNames.Us));
        var after = engine.CommitSnapshot();
        Assert.Equal(0.95, after.Robots[RoleNames.Us].X, 6);
        Assert.Equal(before.Robots[RoleNames.Them].X, after.Robots[RoleNames.Them].X, 9);
        Assert.Equal(before.Timer, after.Timer, 9);
        Assert.Equal(before.Scores.Them + 3, after.Scores.Them, 9);
        Assert.Equal(before.PhysicsPoses!.Buffs[0], after.PhysicsPoses!.Buffs[0]);
    }

    [Fact]
    public void NativeMode_RobotCanPushABlockThroughContact()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scenario = Scenario() with
        {
            Blocks =
            [
                OfficialLayout.Blocks[0] with { X = 2.0, Y = 1.35 },
                OfficialLayout.Blocks[1],
                OfficialLayout.Blocks[2],
            ],
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.3, Y = 1.35, Th = 0 },
                    [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI },
                },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        var start = engine.CommitSnapshot().PhysicsPoses!.Buffs[0].X;
        Snapshot current = engine.CommitSnapshot();
        for (var i = 0; i < 80; i++)
            current = engine.Tick(new RobotAction { V = 0.6 }, RobotAction.Zero);

        var moved = current.PhysicsPoses!.Buffs[0].X;
        Assert.True(moved > start + 0.05,
            $"block did not move through contact: {start} -> {moved}; robot={current.Robots[RoleNames.Us].X},{current.Robots[RoleNames.Us].Y}; blockZ={current.PhysicsPoses.Buffs[0].Z}");
        Assert.Empty(current.Validate());
    }

    [Fact]
    public void NativeMode_MountsTheSixCentimetreStageContinuously()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scenario = Scenario() with
        {
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.9, Y = 0.3, Th = -Math.PI / 2 },
                    [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI },
                },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        var previous = engine.CommitSnapshot().PhysicsPoses!.Robots[RoleNames.Us];
        var highest = previous.Z;
        var largestJump = 0.0;
        var largestJumpTick = -1;
        PhysicsPose3? jumpFrom = null;
        PhysicsPose3? jumpTo = null;
        var fullMountTicks = 0;
        Snapshot current = engine.CommitSnapshot();
        for (var i = 0; i < 120; i++)
        {
            current = engine.Tick(new RobotAction { V = -0.6 }, RobotAction.Zero);
            var pose = current.PhysicsPoses!.Robots[RoleNames.Us];
            highest = Math.Max(highest, pose.Z);
            if (current.Robots[RoleNames.Us].OnPlatform) fullMountTicks++;
            var jump = Math.Sqrt(Math.Pow(pose.X - previous.X, 2) + Math.Pow(pose.Y - previous.Y, 2) + Math.Pow(pose.Z - previous.Z, 2));
            if (jump > largestJump)
            {
                largestJump = jump;
                largestJumpTick = i;
                jumpFrom = previous;
                jumpTo = pose;
            }
            previous = pose;
        }
        Assert.True(highest > 0.12, $"stage was not mounted: highest centre Z={highest}; final Y={previous.Y}");
        // 2026-09-25 登台修复(力上限 3.0 N·m/轮 + 底盘离地 0.08 m)后, 直接驱动必须能
        // 四角全上台(OnStage=FullOn), 不再允许"后轮卡沿"的半登台状态。
        Assert.True(fullMountTicks > 10,
            $"car never had all four corners on the stage: full-mount ticks={fullMountTicks}; final y={previous.Y:0.###} z={previous.Z:0.###}");
        Assert.True(largestJump < 0.15,
            $"physical pose jumped {largestJump} metres in tick {largestJumpTick}: {jumpFrom} -> {jumpTo}; highestZ={highest}");
    }

    [Fact]
    public void NativeMode_FsmMountsFromOfficialSpawn()
    {
        if (!OperatingSystem.IsWindows()) return;
        // 2026-09-25 登台修复验收: 内置 FSM 从官方出生点 (0.95, 0.3) 发令后,
        // 必须在比赛时间内完成倒车登台(四角全上台)并进入 SEARCH。
        var scenario = Scenario() with
        {
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    // 官方出生位姿(Scenario 默认): 尾朝 y=0.7 台沿, 倒车登台。
                    [RoleNames.Us] = new() { X = 0.95, Y = 0.3, Th = -Math.PI / 2 },
                    [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI / 2 },
                },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        engine.Arm();
        int? firstFullOnTick = null;
        int? firstSearchTick = null;
        Snapshot current = engine.CommitSnapshot();
        for (var i = 0; i < 600 && firstSearchTick is null; i++)
        {
            current = engine.Tick();
            var robot = current.Robots[RoleNames.Us];
            if (robot.OnPlatform)
            {
                firstFullOnTick ??= i;
            }
            if (string.Equals(robot.State, "SEARCH", StringComparison.Ordinal))
            {
                firstSearchTick ??= i;
            }
            Assert.Empty(current.Validate());
        }
        Assert.True(firstFullOnTick is not null,
            $"FSM never got all four corners on the stage; final state={current.Robots[RoleNames.Us].State}");
        Assert.True(firstSearchTick is not null,
            $"FSM did not enter SEARCH after mounting; final state={current.Robots[RoleNames.Us].State}, y={current.Robots[RoleNames.Us].Y:0.###}");
        // 登台须由直接倒车路径一次达成: 不得出现 倒车超时/换面重试/正冲备选。
        var mountEvents = engine.Events.Events
            .Where(e => e.Kind == EventKind.Mount && !e.Neutral && e.Robot.IsUs)
            .ToList();
        Assert.NotEmpty(mountEvents);
        var detour = engine.Events.Events
            .Where(e => e.Msg is not null && (e.Msg.Contains("倒车超时") || e.Msg.Contains("换面重试") || e.Msg.Contains("正冲备选")))
            .ToList();
        Assert.Empty(detour);
    }

    [Fact]
    public void NativeMode_SensorChannelsTrackAnalyticPlaneProjectionWhileBodyTilts()
    {
        if (!OperatingSystem.IsWindows()) return;
        // R3 设计边界: 新模式传感器仍是解析平面投影 —— 台沿/台面判定消费
        // (x, y, th) 平面点与场几何, 不消费 MuJoCo 三维碰撞几何。零噪声下
        // ir_ground 通道必须逐 tick 精确等于"传感器点在台面矩形内"的三元值,
        // 即使车体已被物理抬升/倾斜。若未来把传感器升级为三维感知, 本测试
        // 失败即提示必须同步修订契约文档与保真度声明。
        var scenario = Scenario() with
        {
            Parameters = new Dictionary<string, double> { ["irNoise"] = 0.0 },
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.9, Y = 0.3, Th = -Math.PI / 2 },
                    [RoleNames.Them] = new() { X = 2.85, Y = 3.5, Th = Math.PI },
                },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        var groundChannels = engine.CommitSnapshot().SensorLayout![RoleNames.Us].Channels
            .Where(c => c.Type == SensorType.IrGround && c.Mode == "ground")
            .ToDictionary(c => c.Id, c => c);
        Assert.Equal(2, groundChannels.Count); // legacy14: uL/uR
        var field = engine.Field;
        var maxCentreZ = 0.0;
        var maxTilt = 0.0;
        Snapshot current = engine.CommitSnapshot();
        var previousRobot = current.Robots[RoleNames.Us];
        for (var i = 0; i < 140; i++)
        {
            current = engine.Tick(new RobotAction { V = -0.6 }, RobotAction.Zero);
            var pose = current.PhysicsPoses!.Robots[RoleNames.Us];
            maxCentreZ = Math.Max(maxCentreZ, pose.Z);
            // body +Z 与世界 +Z 的夹角: cos(tilt) = 1 - 2(qx² + qy²)
            maxTilt = Math.Max(maxTilt,
                Math.Acos(Math.Clamp(1.0 - 2.0 * (pose.Qx * pose.Qx + pose.Qy * pose.Qy), -1.0, 1.0)));
            // 传感器在物理步进前采样(读取上一提交帧) —— 期望值必须用上一帧平面位姿,
            // 否则车辆快速越过台沿边界时一帧位移就会造成假性偏差。
            var robot = previousRobot;
            var c = Math.Cos(robot.Th);
            var s = Math.Sin(robot.Th);
            foreach (var (id, ch) in groundChannels)
            {
                var px = robot.X + c * ch.Forward - s * ch.Lateral;
                var py = robot.Y + s * ch.Forward + c * ch.Lateral;
                var expected = field.OnPlatform(px, py) ? 1.0 : 0.0;
                Assert.True(current.RawSensors![RoleNames.Us][id] == expected,
                    $"tick {i}: {id} read {current.RawSensors[RoleNames.Us][id]}, analytic plane projection says {expected} " +
                    $"(pose x={robot.X:0.###} y={robot.Y:0.###} th={robot.Th:0.###}, centreZ={pose.Z:0.###})");
            }
            previousRobot = current.Robots[RoleNames.Us];
        }
        Assert.True(maxCentreZ > 0.12, $"body was not raised onto the stage edge: highest centre Z={maxCentreZ}");
        Assert.True(maxTilt > 0.05,
            $"no three-dimensional tilt observed (max body-Z tilt={maxTilt:0.####} rad); the 3D-pose premise of this boundary test did not occur");
        Assert.True(maxTilt > 0.02,
            $"no three-dimensional tilt observed (max body-Z tilt={maxTilt:0.####} rad); the 3D-pose premise of this boundary test did not occur");
    }


    [Fact]
    public void NativeMode_HeadOnRobotsCollideWithoutPassingThrough()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scenario = Scenario() with
        {
            Blocks =
            [
                OfficialLayout.Blocks[0] with { X = 1.1, Y = 2.8 },
                OfficialLayout.Blocks[1] with { X = 1.9, Y = 2.8 },
                OfficialLayout.Blocks[2] with { X = 2.7, Y = 2.8 },
            ],
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 1.2, Y = 1.9, Th = 0 },
                    [RoleNames.Them] = new() { X = 2.6, Y = 1.9, Th = Math.PI },
                },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        var closest = double.PositiveInfinity;
        Snapshot current = engine.CommitSnapshot();
        for (var i = 0; i < 160; i++)
        {
            current = engine.Tick(new RobotAction { V = 0.6 }, new RobotAction { V = 0.6 });
            var us = current.Robots[RoleNames.Us];
            var them = current.Robots[RoleNames.Them];
            closest = Math.Min(closest, them.X - us.X);
            Assert.Empty(current.Validate());
        }
        Assert.True(closest < 0.45, $"robots never approached contact: closest X gap={closest}");
        Assert.True(closest > 0.15, $"robots passed through one another: closest X gap={closest}");
    }

    [Fact]
    public void NativeMode_PushesBlockOffStage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scenario = Scenario() with
        {
            Blocks =
            [
                OfficialLayout.Blocks[0] with { X = 2.85, Y = 1.9 },
                OfficialLayout.Blocks[1] with { X = 1.1, Y = 2.8 },
                OfficialLayout.Blocks[2] with { X = 1.9, Y = 2.8 },
            ],
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 2.2, Y = 1.9, Th = 0 },
                    [RoleNames.Them] = new() { X = 1.9, Y = 1.1, Th = 0 },
                },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        var initialBlockZ = engine.CommitSnapshot().PhysicsPoses!.Buffs[0].Z;
        var lowestBlockZ = initialBlockZ;
        var farthestBlockX = double.NegativeInfinity;
        Snapshot current = engine.CommitSnapshot();
        for (var i = 0; i < 160; i++)
        {
            current = engine.Tick(new RobotAction { V = 0.6 }, RobotAction.Zero);
            var block = current.PhysicsPoses!.Buffs[0];
            lowestBlockZ = Math.Min(lowestBlockZ, block.Z);
            farthestBlockX = Math.Max(farthestBlockX, block.X);
            Assert.Empty(current.Validate());
        }
        Assert.True(farthestBlockX > scenario.Field.Platform.MaxX,
            $"block was not pushed past stage edge: farthest X={farthestBlockX}");
        Assert.True(lowestBlockZ < initialBlockZ - 0.03,
            $"block did not physically fall from stage: {initialBlockZ} -> {lowestBlockZ}");
    }

    [Fact]
    public void NativeMode_RobotDescendsFromStageWithoutPositionJump()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scenario = Scenario() with
        {
            Field = FieldParams.Default with
            {
                Starts = new Dictionary<string, Pose2>
                {
                    [RoleNames.Us] = new() { X = 2.7, Y = 1.9, Th = 0 },
                    [RoleNames.Them] = new() { X = 1.1, Y = 1.1, Th = 0 },
                },
            },
        };
        using var engine = MatchEngineHost.Create(scenario);
        var previous = engine.CommitSnapshot().PhysicsPoses!.Robots[RoleNames.Us];
        var lowest = previous.Z;
        var farthestX = previous.X;
        var largestJump = 0.0;
        for (var i = 0; i < 160; i++)
        {
            var current = engine.Tick(new RobotAction { V = 0.6 }, RobotAction.Zero);
            var pose = current.PhysicsPoses!.Robots[RoleNames.Us];
            lowest = Math.Min(lowest, pose.Z);
            farthestX = Math.Max(farthestX, pose.X);
            largestJump = Math.Max(largestJump, Math.Sqrt(
                Math.Pow(pose.X - previous.X, 2) + Math.Pow(pose.Y - previous.Y, 2) + Math.Pow(pose.Z - previous.Z, 2)));
            previous = pose;
            Assert.Empty(current.Validate());
        }
        Assert.True(farthestX > scenario.Field.Platform.MaxX, $"robot never left stage: farthest X={farthestX}");
        // 2026-09-25 底盘离地抬高后, 地面静止中心 Z 从 0.105 变为 0.125。
        Assert.True(lowest < 0.135, $"robot did not physically descend: lowest centre Z={lowest}");
        Assert.True(largestJump < 0.15, $"robot position jumped during drop: {largestJump}");
    }
}
