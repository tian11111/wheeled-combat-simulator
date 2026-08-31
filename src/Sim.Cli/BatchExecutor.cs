using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// Bounded worker pool for the <c>batch</c> command. Runs at most
/// <see cref="_parallelism"/> jobs concurrently (each job owns its scenario
/// copy, MatchEngine and controller bridges), writes results into a
/// preallocated slot array indexed by input position, and never lets a job
/// exception drop an input index: a throwing or missing worker result becomes
/// a "batch_scheduler" failed row. Parallelism changes wall-clock only —
/// every job executes the same single-threaded MatchRunner path.
///
/// The <paramref name="worker"/> delegate doubles as the test seam: tests can
/// pass a fake worker (e.g. barrier-synchronized) to prove real overlap
/// without spawning matches.
/// </summary>
internal sealed class BatchExecutor
{
    private readonly int _parallelism;

    public BatchExecutor(int parallelism)
    {
        if (parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(parallelism), "parallelism must be >= 1");
        }
        _parallelism = parallelism;
    }

    /// <summary>
    /// Runs <paramref name="worker"/> once per input index and returns one row
    /// per index in input order. The worker must not throw (batch projects job
    /// exceptions into failed rows itself); any exception is contained here as
    /// a scheduler failure so no slot stays empty and the pool cannot deadlock.
    /// </summary>
    public BatchMatchResult[] Execute(IReadOnlyList<long> seeds, Func<int, BatchMatchResult> worker)
    {
        var slots = new BatchMatchResult[seeds.Count];
        Parallel.For(0, seeds.Count, new ParallelOptions { MaxDegreeOfParallelism = _parallelism }, index =>
        {
            try
            {
                slots[index] = worker(index);
            }
            catch (Exception ex)
            {
                slots[index] = SchedulerFailure(seeds, index, ex.Message);
            }
        });

        for (var index = 0; index < slots.Length; index++)
        {
            slots[index] ??= SchedulerFailure(seeds, index, "worker produced no result for this input index");
        }
        return slots;
    }

    private static BatchMatchResult SchedulerFailure(IReadOnlyList<long> seeds, int index, string message)
        => new()
        {
            InputIndex = index,
            Seed = seeds[index],
            Status = BatchMatchResult.StatusFailed,
            Faults = new BatchFaults(),
            Failure = new BatchFailure
            {
                Kind = "batch_scheduler",
                Message = message,
            },
        };
}
