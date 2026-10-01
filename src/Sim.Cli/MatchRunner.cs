using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// Shared single-match execution path used by <c>match</c>, <c>replay-record</c>
/// and <c>batch</c>: one tick loop, one controller bridge lifecycle, one set of
/// action validations. Hosts no global state — every call builds its own
/// engine, bridges and event lists, so concurrent calls (batch workers) are
/// isolated by construction and parallelism changes wall-clock only.
/// </summary>
internal static class MatchRunner
{
    private const int MaxTicks = 10_000;

    /// <summary>
    /// SCORE_BLOCK 展演摘要（仅 <see cref="Options.StartAtScoreBlock"/> 为 true 时非
    /// null；默认关时 null ⇒ 既有摘要形状不变）。展演恒为非门禁证据。
    /// </summary>
    internal sealed record ExhibitionSummary(
        bool Handoff, string? Reason, long EntryTick, long TargetIndex, string? TargetName)
    {
        /// <summary>R6: 展演不得标为门禁证据，也不得晋升 fidelity/盲集索引。</summary>
        public bool GateEvidenceEligible => false;
    }

    internal sealed record MatchRunResult(
        long Seed, long Ticks, Scores Scores, Scores Penalties,
        string? DoneReason, long UsFaults, long ThemFaults,
        List<string> EventFingerprints, ReplayHeader Header)
    {
        public ExhibitionSummary? Exhibition { get; init; }
    }

    internal sealed record Options
    {
        public string? ControllerUs { get; init; }

        public string? ControllerThem { get; init; }

        public double TimeoutMs { get; init; } = 100;

        public bool Events { get; init; }

        /// <summary>
        /// 展演模式: Arm 后先双方内置 FSM 预推进到我方 SCORE_BLOCK（共享缝
        /// <see cref="ScoreBlockExhibition"/>），交接后我方交给外部策略、对手不变。
        /// 默认 false = 逐 tick 语义与既有 match/replay-record/batch 完全一致。
        /// </summary>
        public bool StartAtScoreBlock { get; init; }
    }

    /// <summary>An external controller process could not be started.</summary>
    internal sealed class ControllerStartException(string message) : Exception(message);

    /// <summary>
    /// Runs one full match headlessly. External roles each get a fresh
    /// <see cref="PythonBridge"/> (one controller process per match — never
    /// shared); both bridges are disposed on every path (completed, controller
    /// start failure, exception) via the try/finally below.
    /// </summary>
    internal static MatchRunResult Run(Scenario scenario, Options options)
    {
        using var engine = MatchEngineHost.Create(scenario);
        PythonBridge? usBridge = null;
        PythonBridge? themBridge = null;
        try
        {
            if (options.ControllerUs is { } usCommand)
            {
                usBridge = StartBridge(usCommand, options.TimeoutMs);
            }
            if (options.ControllerThem is { } themCommand)
            {
                themBridge = StartBridge(themCommand, options.TimeoutMs);
            }

            engine.Arm();

            // SCORE_BLOCK 展演: 我方先由内置 FSM 预推进到入口(对手始终内置 FSM),
            // 交接后每 tick 把 11 维 rlObservation 注入宿主 obs 再交给外部策略。
            var handoff = default(ScoreBlockExhibition.PrerollResult?);
            var handoffSnapshot = default(Snapshot?);
            if (options.StartAtScoreBlock)
            {
                var preroll = ScoreBlockExhibition.ArmAndPreroll(engine);
                if (!preroll.HasTarget)
                {
                    // 沿用 rl-env 口径: 不静默跑整场, 也不调用策略。
                    return new MatchRunResult(
                        scenario.Seed,
                        engine.TickIndex,
                        engine.Scores,
                        engine.RestartPenalties,
                        ScoreBlockExhibition.NoScoreBlockReason,
                        usBridge?.Faults ?? 0,
                        themBridge?.Faults ?? 0,
                        [],
                        engine.BuildReplayHeader())
                    {
                        Exhibition = new ExhibitionSummary(false, preroll.Reason, preroll.EntryTick, -1, null),
                    };
                }
                handoff = preroll;
                handoffSnapshot = preroll.EntrySnapshot;
                if (options.Events)
                {
                    Console.WriteLine($"[handoff] tick={preroll.EntryTick} target={engine.Blocks[preroll.TargetIndex].Name}"
                        + $" state=SCORE_BLOCK prerollTicks={preroll.PrerollTicks}");
                }
            }

            var fingerprints = new List<string>();
            var snapshots = new List<Snapshot>();
            while (!engine.Done && snapshots.Count < MaxTicks)
            {
                RobotAction? usAction = null;
                RobotAction? themAction = null;
                if (usBridge is not null && !engine.Done)
                {
                    usAction = handoff is { } handedOff && handoffSnapshot is { } lastSnapshot
                        ? usBridge.Decide(BuildExhibitionObservation(engine, handedOff, lastSnapshot))
                        : usBridge.Decide(engine.BuildObservation(engine.Us));
                }
                if (themBridge is not null && !engine.Done)
                {
                    themAction = themBridge.Decide(engine.BuildObservation(engine.Them));
                }
                var snapshot = engine.Tick(usAction, themAction);
                snapshots.Add(snapshot);
                handoffSnapshot = snapshot;
                if (snapshot.Events is { Count: > 0 })
                {
                    foreach (var evt in snapshot.Events)
                    {
                        fingerprints.Add($"{evt.Seq}|{evt.Tick}|{evt.Type}|{evt.Cls}|{evt.Msg}");
                        if (options.Events)
                        {
                            Console.WriteLine($"[{evt.Seq,4}] t={evt.T,7:0.00} {evt.Type,-16} {evt.Msg}");
                        }
                    }
                }
            }

            return new MatchRunResult(
                scenario.Seed,
                snapshots.Count,
                engine.Scores,
                engine.RestartPenalties,
                engine.Done ? snapshots[^1].DoneReason : "(未结束)",
                usBridge?.Faults ?? 0,
                themBridge?.Faults ?? 0,
                fingerprints,
                engine.BuildReplayHeader())
            {
                Exhibition = handoff is { } completed
                    ? new ExhibitionSummary(true, null, completed.EntryTick, completed.TargetIndex,
                        engine.Blocks[completed.TargetIndex].Name)
                    : null,
            };
        }
        finally
        {
            usBridge?.Dispose();
            themBridge?.Dispose();
        }
    }

    /// <summary>
    /// 展演交接后的我方 obs: 宿主 <see cref="MatchEngine.BuildObservation"/> + 加性
    /// <c>rlObservation</c>（唯一投影来自 <see cref="ScoreBlockExhibition.BuildObservation"/>，
    /// 参数口径与 rl-env 一致: 车体状态取当前引擎, 在台/剩余时间取最近提交帧快照）。
    /// 交接前不填充该字段（预检/残帧由适配器按零动作应答）。
    /// </summary>
    private static Observation BuildExhibitionObservation(MatchEngine engine,
        ScoreBlockExhibition.PrerollResult handoff, Snapshot lastSnapshot)
    {
        var observation = engine.BuildObservation(engine.Us);
        var field = engine.Scenario.Field;
        return observation with
        {
            RlObservation = ScoreBlockExhibition.BuildObservation(engine, handoff.TargetIndex,
                field.Platform, field.MatchDuration,
                lastSnapshot.Robots[RoleNames.Us].OnPlatform, lastSnapshot.Timer),
        };
    }

    private static PythonBridge StartBridge(string command, double timeoutMs)
    {
        try
        {
            return PythonBridge.Start(command, timeoutMs);
        }
        catch (Exception ex)
        {
            // Preserve the legacy top-level message text (e.g. "failed to start
            // controller process: <cmd>" or the Win32 file-not-found message)
            // while marking the failure category for batch rows.
            throw new ControllerStartException(ex.Message);
        }
    }
}
