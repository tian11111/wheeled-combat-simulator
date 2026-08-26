using System.Text.Json;
using Sim.Protocol;

namespace Sim.Tests;

/// <summary>Representative DTO instances used across the test suite.</summary>
internal static class Samples
{
    internal static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static Observation Observation() => new()
    {
        RequestId = 42,
        Tick = 246,
        T = 12.3,
        Role = RoleNames.Us,
        Timer = 107.7,
        Scores = new Scores { Us = 3, Them = 0 },
        Robot = new RobotView
        {
            X = 1.9, Y = 1.9, Th = 0.5, V = 0.9, W = 0.1,
            OnPlatform = true, Hang = false,
            State = "SEARCH", Action = "旋转扫描",
            Vehicle = new VehicleProfile { Id = "wheeledCombat", Sensors = SensorProfiles.WheeledCombat11 },
        },
        Sensors = new LegacySensors
        {
            GrayFront = 940, GrayRear = 920, GrayLeft = 300, GrayRight = 310,
            ShovelUnderLeft = 1, ShovelUnderRight = 1,
            ShovelFrontLeft = 0.9, ShovelFrontRight = 0.8,
            DiagLeftFront = 0.1, DiagRightFront = 0.72, DiagLeftRear = 0.0, DiagRightRear = 0.3,
            Front = 0.98, Rear = 0.0,
        },
        RawSensors = new Dictionary<string, double>
        {
            ["gray_front"] = 940, ["gray_rear"] = 920, ["gray_left"] = 300, ["gray_right"] = 310,
            ["diag_left_front"] = 0.1, ["diag_left_rear"] = 0.0,
            ["diag_right_front"] = 0.72, ["diag_right_rear"] = 0.3,
            ["shovel_under_left"] = 1, ["shovel_under_right"] = 1, ["shovel_front"] = 0.9,
        },
        SensorLayout = SensorProfiles.WheeledCombat11,
        Perception = new Perception
        {
            FieldGray = new FieldGrayInfo { Id = "hand_drawn", Mode = "hand_drawn", Interpolation = "bilinear" },
            Vision = new VisionInfo
            {
                Mode = "default",
                ClassifyRate = 0.5,
                ErrorCount = 0,
                External = Json("""{"roles":{"us":{"frameId":null,"detection":null}}}"""),
            },
        },
        Opponent = new OpponentView { X = 2.6, Y = 2.0, Th = -2.2, OnPlatform = true, State = "SCORE_BLOCK" },
        Objects = new ObjectSet
        {
            Buffs =
            [
                new EnergyBlockView { X = 1.4, Y = 1.3, OnPlatform = true, Out = false, LastTouch = RoleNames.Us },
                new EnergyBlockView { X = 1.2, Y = 2.5, OnPlatform = true },
            ],
            Debuff = new EnergyBlockView { X = 2.2, Y = 2.5, OnPlatform = false, Out = true, LastTouch = RoleNames.Them },
        },
    };

    internal static Event Event(long seq = 7) => new()
    {
        Seq = seq,
        Tick = 140,
        T = 7.0,
        Type = EventKind.BlockScore,
        Role = RoleNames.Us,
        Cls = RoleNames.Us,
        Msg = "[US] 推下增益块 +3",
        Data = new Dictionary<string, JsonElement>
        {
            ["blockId"] = Json("1"),
            ["points"] = Json("3"),
            ["position"] = Json("""{"x":1.4,"y":1.3}"""),
        },
    };

    internal static Snapshot Snapshot() => new()
    {
        Tick = 246,
        T = 12.3,
        Timer = 107.7,
        Phase = MatchPhase.Run,
        Paused = false,
        Done = false,
        Scores = new Scores { Us = 3, Them = 0 },
        RestartPenalties = new Scores { Us = 0, Them = 1 },
        Robots = new Dictionary<string, RobotState>
        {
            [RoleNames.Us] = new RobotState
            {
                X = 1.9, Y = 1.9, Th = 0.5, V = 0.9, W = 0.1,
                Vx = 0.79, Vy = 0.43, Speed = 0.9, Omega = 0.1,
                Pitch = 0.02, Roll = -0.01, ZG = 0.06,
                IsStalled = false, WedgedFront = false, FrontLoad = 1,
                OnPlatform = true, Hang = false, State = "SEARCH", Action = "旋转扫描",
                Armed = true, Manual = false, Timer = 0,
                Vehicle = new VehicleProfile { Id = "wheeledCombat", Sensors = SensorProfiles.WheeledCombat11 },
            },
            [RoleNames.Them] = new RobotState
            {
                X = 2.6, Y = 2.0, Th = -2.2,
                OnPlatform = true, State = "SCORE_BLOCK", Armed = true,
                Vehicle = VehicleProfile.Default,
            },
        },
        Sensors = new Dictionary<string, LegacySensors>
        {
            [RoleNames.Us] = new LegacySensors { GrayFront = 940, Front = 0.98, Rear = 0 },
        },
        RawSensors = new Dictionary<string, Dictionary<string, double>>
        {
            [RoleNames.Us] = new Dictionary<string, double> { ["gray_front"] = 940, ["shovel_front"] = 0.9 },
            [RoleNames.Them] = new Dictionary<string, double> { ["gF"] = 950 },
        },
        SensorLayout = new Dictionary<string, SensorProfile>
        {
            [RoleNames.Us] = SensorProfiles.WheeledCombat11,
            [RoleNames.Them] = SensorProfiles.Legacy14,
        },
        Perception = new Perception
        {
            FieldGray = new FieldGrayInfo { Id = "hand_drawn", Mode = "hand_drawn" },
            Vision = new VisionInfo { Mode = "default", ClassifyRate = 0.5 },
        },
        Objects = new ObjectSet
        {
            Buffs = [new EnergyBlockView { X = 1.4, Y = 1.3, OnPlatform = true }],
            Debuff = new EnergyBlockView { X = 2.2, Y = 2.5, OnPlatform = true },
        },
        Events = [Event(seq: 6), Event(seq: 7)],
        Reward = new Scores { Us = 3, Them = 0 },
    };

    internal static Scenario Scenario() => new()
    {
        Id = "wushu-ring-2026",
        Name = "standard duel",
        Seed = 42,
        Field = FieldParams.Default,
        Vehicles = new Dictionary<string, VehicleProfile>
        {
            [RoleNames.Us] = new() { Id = "wheeledCombat", Sensors = SensorProfiles.WheeledCombat11 },
            [RoleNames.Them] = VehicleProfile.Default,
        },
        Blocks =
        [
            new BlockSpec { Kind = BlockKind.Buff, X = 1.4, Y = 1.3 },
            new BlockSpec { Kind = BlockKind.Buff },                 // seeded referee placement
            new BlockSpec { Kind = BlockKind.Debuff, X = 2.2, Y = 2.5, Radius = 0.075 },
        ],
        Parameters = new Dictionary<string, double>
        {
            ["grayNoise"] = 0.05,
            ["irNoise"] = 0.02,
            ["classifyRate"] = 0.5,
            ["FALL_THRESHOLD"] = 300,
        },
    };

    internal static ReplayHeader ReplayHeader() => new()
    {
        RulesetId = "wushu-ring-2026",
        Seed = 42,
        CoreVersion = "sim-core-0.1.0",
        VisionMode = "random_stub",
        Parameters = new Dictionary<string, double> { ["grayNoise"] = 0.05 },
        Vehicles = new Dictionary<string, VehicleProfile>
        {
            [RoleNames.Us] = new() { Id = "wheeledCombat", Sensors = SensorProfiles.WheeledCombat11 },
            [RoleNames.Them] = VehicleProfile.Default,
        },
        FieldGray = new FieldGrayRef { Id = "hand_drawn", Hash = "e3b0c44298fc1c149afbf4c8996fb924", Mode = "hand_drawn" },
        Ticks =
        [
            new ReplayTick
            {
                Tick = 0,
                T = 0,
                Actions = new Dictionary<string, RobotAction>
                {
                    [RoleNames.Us] = new() { V = 1.2, W = 0, RequestId = "1" },
                    [RoleNames.Them] = new() { V = -0.5, W = 0.25 },
                },
            },
            new ReplayTick
            {
                Tick = 1,
                T = 0.05,
                Actions = new Dictionary<string, RobotAction>
                {
                    [RoleNames.Us] = new() { V = 0, W = 0, RequestId = "2" },
                    [RoleNames.Them] = new() { V = -0.5, W = 0.25 },
                },
                Commands = ["pause"],
            },
        ],
        CreatedAt = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero),
    };
}
