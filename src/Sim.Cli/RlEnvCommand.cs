using System.Text.Json;
using Sim.Core;
using Sim.Hosting;
using Sim.Mujoco;
using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// 训练专用持久环境(JSONL stdin/stdout): reset/step/close。
/// 训练会话持有按内容哈希复用的 mjModel(编译一次); 每集新建 MatchEngine 运行时
/// 与独立 mjData; Arm 后双方内置 FSM 预推进到我方首次 SCORE_BLOCK, 锁定目标块;
/// 仅策略 step 向我方传动作, 对手始终内置 FSM。普通比赛路径与协议零改动。
/// </summary>
public static class RlEnvCommand
{
    private const double TargetReward = 1.0;
    private const double NotOursPenalty = -0.5;
    private const double OurDropPenalty = -1.0;
    private const double StepCost = -0.0001;
    private const double EdgeShapingScale = 0.1;
    private const long MaxPolicyTicks = 2400;
    private const int BaseObservationSize = 9;
    private const int ObservationSize = 11;

    public static int Run(string[] args)
    {
        var scenarioPath = "scenarios/wushu-ring-2026-mujoco.json";
        var duration = 120.0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--scenario" && i + 1 < args.Length) scenarioPath = args[i + 1];
            if (args[i] == "--duration" && i + 1 < args.Length) duration = double.Parse(args[i + 1]);
        }

        var factory = new MujocoTrainingPhysicsBackendFactory();
        MatchEngine? engine = null;
        var state = new EpisodeState();
        try
        {
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out var opElement)
                        || opElement.ValueKind != JsonValueKind.String)
                    {
                        throw new JsonException("request must be an object with a string 'op'.");
                    }
                    var op = opElement.GetString();
                    switch (op)
                    {
                        case "reset":
                            var seed = root.GetProperty("seed").GetInt32();
                            // Opt-in per-episode diagnostic trace. Absent or false keeps the
                            // training/evaluation response shape byte-identical.
                            var trace = root.TryGetProperty("trace", out var traceElement)
                                        && traceElement.ValueKind == JsonValueKind.True;
                            var reset = Reset(factory, scenarioPath, seed, duration, state, ref engine, trace);
                            Emit(new { type = "reset", obs = reset.Obs, info = reset.Info });
                            break;
                        case "step":
                        {
                            if (engine is null)
                            {
                                EmitError("step before reset");
                                continue;
                            }
                            var v = root.GetProperty("v").GetDouble();
                            var w = root.GetProperty("w").GetDouble();
                            var step = Step(engine, state, v, w, duration);
                            Emit(new { type = "step", obs = step.Obs, reward = step.Reward,
                                       terminated = step.Terminated, truncated = step.Truncated, info = step.Info });
                            break;
                        }
                        case "step_fsm":
                        {
                            if (engine is null)
                            {
                                EmitError("step_fsm before reset");
                                continue;
                            }
                            var stepFsm = Step(engine, state, null, null, duration);
                            Emit(new { type = "step", obs = stepFsm.Obs, reward = stepFsm.Reward,
                                       terminated = stepFsm.Terminated, truncated = stepFsm.Truncated, info = stepFsm.Info });
                            break;
                        }
                        case "close":
                            engine?.Dispose();
                            engine = null;
                            factory.ReleaseAll();
                            Emit(new { type = "closed" });
                            return 0;
                        default:
                            EmitError($"unknown op: {op}");
                            break;
                    }
                }
                catch (Exception exc) when (exc is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
                {
                    engine?.Dispose();
                    engine = null;
                    factory.ReleaseAll();
                    EmitError($"bad request: {exc.Message}");
                }
                catch (Exception exc)
                {
                    engine?.Dispose();
                    engine = null;
                    factory.ReleaseAll();
                    EmitError($"request failed: {exc.Message}");
                }
            }
        }
        finally
        {
            // Episode owns mjData; training factory owns shared mjModels.
            engine?.Dispose();
            factory.ReleaseAll();
        }
        return 0;
    }

    private static void Emit(object payload) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions()));

    private static void EmitError(string message) =>
        Emit(new { type = "error", message });

    internal static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class EpisodeState
    {
        public Scenario Scenario = new();
        public bool NoScoreBlock;

        /// <summary>Diagnostic trace opt-in (set per episode by the <c>reset</c> request).</summary>
        public bool Trace;

        public long LastSeq;
        public int Seed;
        public int EntryTick;
        public int TargetIndex = -1;
        public long PolicyTicks;
        public double PrevEdgeDistance = double.NaN;
        public int TargetBlockScores;
        public int TargetBlockOffs;
        public int UsDrops;
        public int UsBlockScoreEvents;
        public int ThemBlockScoreEvents;
        public int UnownedBlockOffs;
        public bool AttributionAmbiguous;
        public double EntryTargetX;
        public double EntryTargetY;
        public long TargetOutcomeTick = -1;
    }

    private static Scenario EpisodeScenario(string basePath, int seed, double duration)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(basePath),
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new InvalidOperationException($"scenario '{basePath}' failed to deserialize");
        return scenario with { Seed = seed, Field = scenario.Field with { MatchDuration = duration } };
    }

    private static (double[] Obs, Dictionary<string, object?> Info) Reset(
        MujocoTrainingPhysicsBackendFactory factory, string scenarioPath, int seed,
        double duration, EpisodeState state, ref MatchEngine? engineRef, bool trace)
    {
        engineRef?.Dispose();
        engineRef = null;
        var scenario = EpisodeScenario(scenarioPath, seed, duration);
        state.Scenario = scenario;
        var engine = MatchEngineHost.Create(scenario, null, factory);
        engineRef = engine;
        engine.Arm();

        state.NoScoreBlock = false;
        state.Trace = trace;
        state.Seed = seed;
        state.PolicyTicks = 0;
        state.LastSeq = LatestEventSequence(engine);
        state.EntryTick = -1;
        state.TargetIndex = -1;
        state.PrevEdgeDistance = double.NaN;
        state.TargetBlockScores = 0;
        state.TargetBlockOffs = 0;
        state.UsDrops = 0;
        state.UsBlockScoreEvents = 0;
        state.ThemBlockScoreEvents = 0;
        state.UnownedBlockOffs = 0;
        state.AttributionAmbiguous = false;
        state.EntryTargetX = double.NaN;
        state.EntryTargetY = double.NaN;
        state.TargetOutcomeTick = -1;

        // 预推进: 双方内置 FSM, 直到我方首次 SCORE_BLOCK 或比赛结束。
        var guard = 0;
        var snap = engine.CommitSnapshot();
        while (!engine.Done && guard < 4800)
        {
            if (engine.Us.Fsm.State == FsmState.ScoreBlock)
            {
                break;
            }
            snap = engine.Tick();
            guard++;
        }

        if (engine.Done || engine.Us.Fsm.State != FsmState.ScoreBlock)
        {
            state.NoScoreBlock = true;
            var info = Info(engine, state, seed, null);
            info["no_score_block"] = true;
            info["pre_roll_ticks"] = guard;
            AttachTrace(info, engine, state);
            return (new double[ObservationSize], info);
        }

        state.EntryTick = (int)engine.TickIndex;
        state.TargetIndex = LockTargetIndex(engine);
        if (state.TargetIndex < 0)
        {
            state.NoScoreBlock = true;
            var noTargetInfo = Info(engine, state, seed, null);
            noTargetInfo["no_score_block"] = true;
            noTargetInfo["reason"] = "score_block_without_valid_buff_target";
            noTargetInfo["pre_roll_ticks"] = guard;
            AttachTrace(noTargetInfo, engine, state);
            return (new double[ObservationSize], noTargetInfo);
        }
        if (state.TargetIndex >= 0)
        {
            state.EntryTargetX = engine.Blocks[state.TargetIndex].X;
            state.EntryTargetY = engine.Blocks[state.TargetIndex].Y;
        }
        // Ignore Arm/mount/search events; strategy metrics start at stage entry.
        state.LastSeq = LatestEventSequence(engine);
        state.PrevEdgeDistance = EdgeDistance(engine, state.TargetIndex);
        var (obs, entryInfo) = BuildObservation(engine, state, seed, snap.Robots[RoleNames.Us].OnPlatform, snap.Timer);
        var infoOut = Info(engine, state, seed, null);
        foreach (var kv in entryInfo) infoOut[kv.Key] = kv.Value;
        AttachTrace(infoOut, engine, state);
        return (obs, infoOut);
    }

    private static (double[] Obs, double Reward, bool Terminated, bool Truncated, Dictionary<string, object?> Info) Step(
        MatchEngine engine, EpisodeState state, double? v, double? w, double duration)
    {
        if (state.NoScoreBlock)
        {
            // 未进入阶段的 seed: 首个 step 不执行动作, 立即零成功终止(保留该 seed 于评测)。
            var zeroInfo = Info(engine, state, state.Seed, null);
            zeroInfo["no_score_block"] = true;
            AttachTrace(zeroInfo, engine, state);
            return (new double[ObservationSize], 0.0, true, false, zeroInfo);
        }

        var edgeBefore = EdgeDistance(engine, state.TargetIndex);
        var wasOut = engine.Blocks.Select(b => b.Out).ToArray();
        state.PolicyTicks++;
        var snapshot = v is null
            ? engine.Tick(null, null)   // 内置 FSM 驱动我方(基线对照口径)
            : engine.Tick(new RobotAction { V = v.Value, W = w ?? 0.0 }, null);
        var events = engine.Events.Events.Where(e => e.Seq > state.LastSeq).ToList();
        state.LastSeq = LatestEventSequence(engine);
        state.UsBlockScoreEvents += events.Count(e => e.Kind == EventKind.BlockScore && !e.Neutral && e.Robot.IsUs);
        state.ThemBlockScoreEvents += events.Count(e => e.Kind == EventKind.BlockScore && !e.Neutral && !e.Robot.IsUs);
        state.UnownedBlockOffs += events.Count(e => e.Kind == EventKind.BlockOff);

        var reward = StepCost;
        var terminated = false;
        var truncated = false;
        var targetName = TargetBlockName(engine, state.TargetIndex);
        var blockStates = Enumerable.Range(0, engine.Blocks.Count)
            .Select(i => (engine.Blocks[i].Name, engine.Blocks[i].Kind, wasOut[i], engine.Blocks[i].Out)).ToArray();
        var eventFacts = events.Select(e => (e.Kind, e.Robot.IsUs, e.Neutral, e.Tick, EventBlockName(e))).ToArray();
        var attribution = ClassifyTargetOutcome(state.TargetIndex, targetName, blockStates, eventFacts, engine.TickIndex);
        var targetScored = attribution.TargetScored;
        var targetLost = attribution.TargetLost;
        var attributionAmbiguous = attribution.Ambiguous;
        if (attributionAmbiguous) state.AttributionAmbiguous = true;
        if (targetScored || targetLost) state.TargetOutcomeTick = engine.TickIndex;
        if (targetScored)
        {
            reward += TargetReward;
            state.TargetBlockScores++;
        }
        else if (targetLost)
        {
            var targetBlockOff = attribution.TargetBlockOff;
            if (targetBlockOff) reward += NotOursPenalty;
            state.TargetBlockOffs += targetBlockOff ? 1 : 0;
        }

        var usDropped = events.Any(e => e.Kind == EventKind.Drop && !e.Neutral && e.Robot.IsUs);
        if (usDropped)
        {
            reward += OurDropPenalty;
            state.UsDrops++;
        }
        terminated = targetScored || targetLost || usDropped;

        var edgeAfter = EdgeDistance(engine, state.TargetIndex);
        if (!double.IsNaN(edgeAfter) && !double.IsNaN(edgeBefore))
        {
            reward += EdgeShapingScale * (edgeBefore - edgeAfter);
        }
        state.PrevEdgeDistance = edgeAfter;

        if (engine.Done) terminated = true;
        if (!terminated && state.PolicyTicks >= MaxPolicyTicks) truncated = true;

        var (obs, info) = BuildObservation(engine, state, state.Seed, snapshot.Robots[RoleNames.Us].OnPlatform, snapshot.Timer);
        info["policy_ticks"] = state.PolicyTicks;
        info["target_scored"] = targetScored;
        info["target_lost_not_ours"] = targetLost;
        info["attribution_ambiguous_this_step"] = attributionAmbiguous;
        info["us_dropped"] = usDropped;
        AttachTrace(info, engine, state, events);
        return (obs, reward, terminated, truncated, info);
    }

    private static int LockTargetIndex(MatchEngine engine)
    {
        var locked = engine.Us.Fsm.ScoreTarget;
        if (locked is not null)
        {
            for (var i = 0; i < engine.Blocks.Count; i++)
            {
                if (ReferenceEquals(engine.Blocks[i], locked))
                {
                    return i;
                }
            }
        }
        // ScoreTarget 为空: 按现有 FSM 规则选第一个有效增益块。
        for (var i = 0; i < engine.Blocks.Count; i++)
        {
            var b = engine.Blocks[i];
            if (b.Kind == BlockKind.Buff && !b.Out && engine.Field.OnPlatform(b.X, b.Y))
            {
                return i;
            }
        }
        return -1;
    }

    private static long LatestEventSequence(MatchEngine engine) =>
        engine.Events.Events.Count == 0 ? 0 : engine.Events.Events[^1].Seq;

    private static string? TargetBlockName(MatchEngine engine, int index) =>
        index >= 0 && index < engine.Blocks.Count ? engine.Blocks[index].Name : null;

    private static string? EventBlockName(CoreEvent e)
    {
        if (e.Data is null) return null;
        var payload = JsonSerializer.SerializeToElement(e.Data, JsonOptions());
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("block", out var block)
            && block.ValueKind == JsonValueKind.String ? block.GetString() : null;
    }

    internal readonly record struct TargetAttribution(bool TargetScored, bool TargetLost, bool Ambiguous, bool TargetBlockOff);

    internal static TargetAttribution ClassifyTargetOutcome(
        int targetIndex,
        string? targetName,
        IReadOnlyList<(string Name, BlockKind Kind, bool WasOut, bool IsOut)> blocks,
        IReadOnlyList<(EventKind Kind, bool IsUs, bool Neutral, long Tick, string? BlockName)> events,
        long currentTick)
    {
        var targetOutTransition = targetIndex >= 0 && targetIndex < blocks.Count
            && !blocks[targetIndex].WasOut && blocks[targetIndex].IsOut;
        if (!targetOutTransition) return default;

        var matchingExits = blocks.Count(b => b.Kind == BlockKind.Buff && b.WasOut == false && b.IsOut
            && string.Equals(b.Name, targetName, StringComparison.Ordinal));
        var matchingScores = events.Count(e => e.Kind == EventKind.BlockScore && e.IsUs && !e.Neutral
            && e.Tick == currentTick && string.Equals(e.BlockName, targetName, StringComparison.Ordinal));
        var matchingOutcomeEvents = events.Count(e => (e.Kind is EventKind.BlockScore or EventKind.BlockOff)
            && e.Tick == currentTick && string.Equals(e.BlockName, targetName, StringComparison.Ordinal));
        var ambiguous = (matchingExits > 1 || matchingScores > 1) && matchingOutcomeEvents > 0;
        var scored = matchingExits == 1 && matchingScores == 1;
        var blockOff = matchingExits == 1 && events.Any(e => e.Kind == EventKind.BlockOff && e.Tick == currentTick
            && string.Equals(e.BlockName, targetName, StringComparison.Ordinal));
        return new TargetAttribution(scored, !scored, ambiguous, blockOff);
    }

    private static double EdgeDistance(MatchEngine engine, int index) =>
        index >= 0 && index < engine.Blocks.Count
            ? engine.Field.DistToNearestEdge(engine.Blocks[index].X, engine.Blocks[index].Y)
            : double.NaN;

    private static (double X, double Y) BlockPos(MatchEngine engine, int index) =>
        index >= 0 && index < engine.Blocks.Count
            ? (engine.Blocks[index].X, engine.Blocks[index].Y)
            : (double.NaN, double.NaN);

    private static (double[] Obs, Dictionary<string, object?> Info) BuildObservation(
        MatchEngine engine, EpisodeState state, int seed, bool usOnPlatform, double timer)
    {
        var field = state.Scenario.Field;
        var side = field.Platform.MaxX - field.Platform.MinX;
        var targetIndex = state.TargetIndex;
        var (bx, by) = targetIndex >= 0 ? BlockPos(engine, targetIndex) : (double.NaN, double.NaN);
        var us = engine.Us;
        var dx = bx - us.X;
        var dy = by - us.Y;
        var cos = Math.Cos(us.Th);
        var sin = Math.Sin(us.Th);
        var relForward = double.IsNaN(dx) ? 0.0 : (cos * dx + sin * dy) / side;
        var relLeft = double.IsNaN(dx) ? 0.0 : (-sin * dx + cos * dy) / side;
        var remaining = field.MatchDuration > 0 ? timer / field.MatchDuration : 0.0;
        var clip = (double v) => clamp(v, -1.0, 1.0);
        var baseObs = new[]
        {
            clip(relForward),
            clip(relLeft),
            clip(bx / side),
            clip(by / side),
            clamp(us.V / (us.Vehicle.MaxSpeed != 0 ? us.Vehicle.MaxSpeed : 1.5), -1.0, 1.0),
            clamp(us.Omega / (us.Vehicle.MaxTurnRate != 0 ? us.Vehicle.MaxTurnRate : 4.0), -1.0, 1.0),
            usOnPlatform ? 1.0 : 0.0,
            targetIndex >= 0 && engine.Field.OnPlatform(engine.Blocks[targetIndex].X, engine.Blocks[targetIndex].Y) ? 1.0 : 0.0,
            clamp(remaining, 0.0, 1.0),
        };
        var obs = AppendOwnPositionObservation(baseObs, us.X, us.Y, field.Platform);
        var info = Info(engine, state, seed, targetIndex);
        return (obs, info);
    }

    internal static double[] AppendOwnPositionObservation(double[] observation, double ownX, double ownY, Region platform)
    {
        if (observation.Length != BaseObservationSize)
        {
            throw new ArgumentException($"expected {BaseObservationSize} base observation values", nameof(observation));
        }

        var halfSide = (platform.MaxX - platform.MinX) / 2.0;
        var centerX = (platform.MinX + platform.MaxX) / 2.0;
        var centerY = (platform.MinY + platform.MaxY) / 2.0;
        var expanded = new double[ObservationSize];
        Array.Copy(observation, expanded, BaseObservationSize);
        expanded[BaseObservationSize] = clamp((ownX - centerX) / halfSide, -1.0, 1.0);
        expanded[BaseObservationSize + 1] = clamp((ownY - centerY) / halfSide, -1.0, 1.0);
        return expanded;
    }

    private static double clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    private static Dictionary<string, object?> Info(
        MatchEngine engine, EpisodeState state, int seed, int? targetIndex)
    {
        var info = new Dictionary<string, object?>
        {
            ["seed"] = seed,
            ["entry_tick"] = state.EntryTick,
            ["policy_ticks"] = state.PolicyTicks,
            ["target_index"] = state.TargetIndex,
            ["us_block_scores"] = state.TargetBlockScores,
            ["us_block_score_events"] = state.UsBlockScoreEvents,
            ["them_block_score_events"] = state.ThemBlockScoreEvents,
            ["target_block_offs"] = state.TargetBlockOffs,
            ["unowned_block_offs"] = state.UnownedBlockOffs,
            ["us_drops"] = state.UsDrops,
            ["attribution_ambiguous"] = state.AttributionAmbiguous,
            ["target_name"] = TargetBlockName(engine, state.TargetIndex) ?? "",
            ["target_entry_x"] = state.TargetIndex >= 0 ? state.EntryTargetX : (double?)null,
            ["target_entry_y"] = state.TargetIndex >= 0 ? state.EntryTargetY : (double?)null,
            ["target_x"] = state.TargetIndex >= 0 ? engine.Blocks[state.TargetIndex].X : (double?)null,
            ["target_y"] = state.TargetIndex >= 0 ? engine.Blocks[state.TargetIndex].Y : (double?)null,
            ["target_out"] = state.TargetIndex >= 0 && engine.Blocks[state.TargetIndex].Out,
            ["target_last_contact_role"] = state.TargetIndex >= 0
                ? engine.Blocks[state.TargetIndex].LastContactRole ?? "" : "",
            ["target_outcome_tick"] = state.TargetOutcomeTick,
            ["phase_entry_tick"] = state.EntryTick,
            ["phase_ticks"] = state.PolicyTicks,
            ["phase"] = state.NoScoreBlock ? "no_score_block" : "score_block",
            ["done_reason"] = engine.Done ? engine.Us.Fsm.DoneReason : "",
            ["faults"] = 0,
            ["score_us"] = engine.Scores.Us,
            ["score_them"] = engine.Scores.Them,
        };
        if (targetIndex is not null)
        {
            var distance = EdgeDistance(engine, targetIndex.Value);
            info["target_edge_distance"] = double.IsFinite(distance) ? distance : (double?)null;
        }
        return info;
    }

    // ---------- opt-in diagnostic trace ----------

    /// <summary>
    /// Adds the per-tick diagnostic trace to <paramref name="info"/> only when the episode
    /// opted in. The trace is a read-only projection of referee-visible state: it consumes
    /// no randomness and mutates nothing, so determinism is unaffected and the default
    /// (non-trace) response shape stays byte-identical.
    /// </summary>
    private static void AttachTrace(Dictionary<string, object?> info, MatchEngine engine,
        EpisodeState state, IReadOnlyList<CoreEvent>? events = null)
    {
        if (!state.Trace) return;
        info["trace"] = BuildTrace(engine, state, events ?? Array.Empty<CoreEvent>());
    }

    private static Dictionary<string, object?> BuildTrace(MatchEngine engine, EpisodeState state,
        IReadOnlyList<CoreEvent> events)
    {
        var blocks = new List<object?>(engine.Blocks.Count);
        foreach (var block in engine.Blocks)
        {
            blocks.Add(BlockTrace(engine, block));
        }
        var eventTraces = new List<object?>(events.Count);
        foreach (var evt in events)
        {
            eventTraces.Add(EventTrace(evt));
        }
        return new Dictionary<string, object?>
        {
            ["tick"] = engine.TickIndex,
            ["policy_ticks"] = state.PolicyTicks,
            ["seed"] = state.Seed,
            ["us"] = RobotTrace(engine, engine.Us),
            ["them"] = RobotTrace(engine, engine.Them),
            ["target_index"] = state.TargetIndex,
            ["blocks"] = blocks,
            ["events"] = eventTraces,
        };
    }

    private static Dictionary<string, object?> RobotTrace(MatchEngine engine, RobotRuntime robot) => new()
    {
        ["x"] = robot.X,
        ["y"] = robot.Y,
        ["th"] = robot.Th,
        // V/W are the kernel-clamped requested commands: for the policy path these are the
        // accepted actions, for the FSM path the FSM request. One unit for both paths.
        ["v"] = robot.V,
        ["w"] = robot.W,
        ["vx"] = robot.Vx,
        ["vy"] = robot.Vy,
        ["on_stage"] = engine.PhysicsBackend.OnStage(robot),
        ["edge_distance"] = engine.Field.DistToNearestEdge(robot.X, robot.Y),
    };

    private static Dictionary<string, object?> BlockTrace(MatchEngine engine, BlockRuntime block)
    {
        var contacts = new List<object?>(block.ContactThisStep.Count);
        foreach (var (role, t) in block.ContactThisStep)
        {
            contacts.Add(new Dictionary<string, object?> { ["r"] = role, ["t"] = t });
        }
        return new Dictionary<string, object?>
        {
            ["name"] = block.Name,
            ["kind"] = block.Kind.ToString(),
            ["x"] = block.X,
            ["y"] = block.Y,
            ["vx"] = block.Vx,
            ["vy"] = block.Vy,
            ["out"] = block.Out,
            ["was_on"] = block.WasOn,
            ["edge_distance"] = engine.Field.DistToNearestEdge(block.X, block.Y),
            ["last_contact_role"] = block.LastContactRole ?? "",
            ["contacts"] = contacts,
        };
    }

    private static Dictionary<string, object?> EventTrace(CoreEvent evt)
    {
        string? block = null;
        string? reason = null;
        if (evt.Data is not null)
        {
            var payload = JsonSerializer.SerializeToElement(evt.Data, JsonOptions());
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("block", out var blockElement)
                    && blockElement.ValueKind == JsonValueKind.String)
                {
                    block = blockElement.GetString();
                }
                if (payload.TryGetProperty("reason", out var reasonElement)
                    && reasonElement.ValueKind == JsonValueKind.String)
                {
                    reason = reasonElement.GetString();
                }
            }
        }
        return new Dictionary<string, object?>
        {
            ["seq"] = evt.Seq,
            ["tick"] = evt.Tick,
            ["kind"] = evt.Kind.ToString(),
            ["role"] = evt.Robot.Role,
            ["is_us"] = evt.Robot.IsUs,
            ["neutral"] = evt.Neutral,
            ["block"] = block ?? "",
            ["reason"] = reason ?? "",
        };
    }
}
