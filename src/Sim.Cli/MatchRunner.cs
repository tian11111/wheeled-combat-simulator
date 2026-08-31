using Sim.Core;
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

    internal sealed record MatchRunResult(
        long Seed, long Ticks, Scores Scores, Scores Penalties,
        string? DoneReason, long UsFaults, long ThemFaults,
        List<string> EventFingerprints, ReplayHeader Header);

    internal sealed record Options
    {
        public string? ControllerUs { get; init; }

        public string? ControllerThem { get; init; }

        public double TimeoutMs { get; init; } = 100;

        public bool Events { get; init; }
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
        var engine = new MatchEngine(scenario);
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
            var fingerprints = new List<string>();
            var snapshots = new List<Snapshot>();
            while (!engine.Done && snapshots.Count < MaxTicks)
            {
                RobotAction? usAction = null;
                RobotAction? themAction = null;
                if (usBridge is not null && !engine.Done)
                {
                    usAction = usBridge.Decide(engine.BuildObservation(engine.Us));
                }
                if (themBridge is not null && !engine.Done)
                {
                    themAction = themBridge.Decide(engine.BuildObservation(engine.Them));
                }
                var snapshot = engine.Tick(usAction, themAction);
                snapshots.Add(snapshot);
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
                engine.BuildReplayHeader());
        }
        finally
        {
            usBridge?.Dispose();
            themBridge?.Dispose();
        }
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
