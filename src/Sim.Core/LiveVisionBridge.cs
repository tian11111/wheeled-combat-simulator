namespace Sim.Core;

/// <summary>
/// Live vision bridge: serves the FSM the detection stream a real camera service
/// delivers, advanced by simulation time. This is the "cache" half of the
/// <see cref="IVisionAdapter"/> contract (external YOLO bridges run outside the
/// core and only refresh a cache): <see cref="Classify"/> lazily pumps its
/// <see cref="IVisionStreamSource"/> up to the current SimT and then serves the
/// newest frame in the maxAge window with the exact camera-cache rule, reason
/// codes and ledger shape of <see cref="VisionReplayAdapter"/> — both call the
/// same <see cref="VisionFrameSelector"/>, so equivalence is structural.
///
/// Time base: the source session's first delivered frame IS SimT 0, exactly like
/// the replay package's first frame; frame ages are computed from SimT and the
/// service-reported age is never trusted. A source must therefore release frames
/// on the same timeline it reports them on (CSV: arrival = (ts − first frame)/1000;
/// 1x real inference: wall clock), otherwise ages carry a constant offset.
///
/// Discipline (shared with the replay adapter, load-bearing for replay identity):
/// the bridge never reads <see cref="VisionContext.Target"/> (the simulator's
/// world truth) and never draws <see cref="VisionContext.Random"/> (the shared
/// Mulberry32 stream must not shift between the default and live paths). Missing,
/// stale or faulted frames return <c>unknown</c> with an explicit reason — never a
/// silent random fallback.
/// </summary>
public sealed class LiveVisionBridge : IVisionAdapter
{
    /// <summary>Adapter id (replay header vision mode and detection source).</summary>
    public const string ModeName = "liveBridge";

    /// <summary>Default staleness window (ms) of the live bridge (the `vision live --max-age-ms` default).</summary>
    public const double DefaultMaxAgeMs = 500;

    private IVisionStreamSource? _source;
    private readonly double _maxAgeMs;
    private readonly List<VisionReplayFrame> _frames = [];
    private readonly Dictionary<string, VisionReplayConsumeRecord> _lastByRole = new();
    private readonly List<VisionReplayConsumeRecord> _consumes = [];
    private int _pulled;
    private double? _sessionStartMs;

    /// <param name="source">Frame source; pumped lazily on every classify.</param>
    /// <param name="maxAgeMs">Fixed staleness window; older frames return unknown("stale").</param>
    public LiveVisionBridge(IVisionStreamSource source, double maxAgeMs = DefaultMaxAgeMs)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(maxAgeMs) || maxAgeMs <= 0)
        {
            throw new ArgumentException("live vision bridge requires a positive finite maxAgeMs.", nameof(maxAgeMs));
        }
        _source = source;
        _maxAgeMs = maxAgeMs;
    }

    public string Id => ModeName;

    public double MaxAgeMs => _maxAgeMs;

    /// <summary>Consumption ledger in call order (deterministic).</summary>
    public IReadOnlyList<VisionReplayConsumeRecord> Consumes => _consumes;

    /// <summary>Last classify result per role (the external-vision registry snapshot).</summary>
    public IReadOnlyDictionary<string, VisionReplayConsumeRecord> LastByRole => _lastByRole;

    /// <summary>
    /// Frames delivered by the source so far, ascending by (TimestampMs,
    /// Sequence) — the sidecar working set of the streamed session.
    /// </summary>
    public IReadOnlyList<VisionReplayFrame> ReleasedFrames => _frames;

    /// <summary>Re-received frames (same timestamp + sequence) ignored after the first delivery.</summary>
    public int DuplicateFrames { get; private set; }

    /// <summary>
    /// Re-received frames whose CONTENT differs from the first delivery (status,
    /// error, selection or any detection row). The stream path keeps the first
    /// delivery and counts (never kills the engine); the import path hard-rejects
    /// the same input, so a non-zero count here is a real protocol violation by
    /// the source and the report surfaces it loudly.
    /// </summary>
    public int ConflictingDuplicates { get; private set; }

    public VisionDetection Classify(VisionContext context)
    {
        // 纪律(与 VisionReplayAdapter 同一条契约)：绝不读 context.Target（模拟
        // 世界真值）制造"正确答案"，绝不调 context.Random（Mulberry32 共享流
        // 不得位移）。
        Pump(context.T);

        // SimT 0 = 会话首帧：锚点取首个交付帧的 epoch ms，与回放适配器同一时基。
        var tMs = (_sessionStartMs ?? 0) + context.T * 1000.0;
        var (record, detection) = VisionFrameSelector.Select(
            _frames, tMs, _maxAgeMs, context.Role, context.T, ModeName);
        _lastByRole[context.Role] = record;
        _consumes.Add(record);
        return detection ?? new VisionDetection
        {
            Label = "unknown",
            Confidence = 0,
            Source = record.Reason ?? ModeName,
        };
    }

    /// <summary>
    /// 释放底层流源 (桌面每场替换适配器时调用): CSV 源无资源、进程源结束子进程。
    /// 释放后的桥不得再参与 classify (Pump 会抛 ObjectDisposedException)。
    /// </summary>
    public void DisposeSource()
    {
        var source = Interlocked.Exchange(ref _source, null);
        (source as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Lazy pump: ask the source for everything that has arrived by
    /// <paramref name="simT"/> and merge it into the working set. The source never
    /// blocks, so this cannot stall the engine.
    /// </summary>
    private void Pump(double simT)
    {
        var source = _source ?? throw new ObjectDisposedException(nameof(LiveVisionBridge));
        source.PumpUntil(simT);
        var released = source.Released;
        for (var i = _pulled; i < released.Count; i++)
        {
            Merge(released[i]);
        }
        _pulled = released.Count;
        // 锚点只在首个交付帧处锁定一次：会话中途不漂移（源按升序交付时它正是
        // 源会话的首帧，与回放包的 _frames[0] 同一帧）。
        if (_sessionStartMs is null && _frames.Count > 0)
        {
            _sessionStartMs = _frames[0].TimestampMs;
        }
    }

    /// <summary>
    /// Inserts one delivered frame keeping the working set sorted by
    /// (TimestampMs, Sequence) — the order <see cref="VisionReplayAdapter"/> sorts
    /// its package into, so the shared binary search sees the same sequence. A
    /// re-received frame (same pair) keeps the FIRST delivery, mirroring the import
    /// collapse rule (later duplicates are protocol violations; they must not
    /// rewrite a frame the FSM has already been served). A re-received frame whose
    /// content differs from the first delivery additionally counts into
    /// <see cref="ConflictingDuplicates"/> — the import path rejects that input
    /// outright; the stream path degrades to counting so a bad source stays
    /// visible without killing the session.
    /// </summary>
    private void Merge(VisionReplayFrame frame)
    {
        var index = LowerBound(frame.TimestampMs, frame.Sequence);
        if (index < _frames.Count
            && _frames[index].TimestampMs == frame.TimestampMs
            && _frames[index].Sequence == frame.Sequence)
        {
            DuplicateFrames++;
            if (!SameContent(_frames[index], frame))
            {
                ConflictingDuplicates++;
            }
            return;
        }
        _frames.Insert(index, frame);
    }

    /// <summary>
    /// Content comparison for re-received frames: the same field set the import
    /// path's collapse rule validates (status/error/selection + every detection
    /// row). Arrival-side bookkeeping is intentionally NOT compared — a
    /// re-received row legitimately carries a different receive age.
    /// </summary>
    private static bool SameContent(VisionReplayFrame first, VisionReplayFrame later)
        => first.Status == later.Status
            && first.Error == later.Error
            && first.SelectedTargetIndex == later.SelectedTargetIndex
            && first.Detections.Count == later.Detections.Count
            && first.Detections.Zip(later.Detections, (a, b) =>
                a.Label == b.Label && a.Confidence == b.Confidence && a.OffsetX == b.OffsetX)
                .All(equal => equal);

    /// <summary>First index with (TimestampMs, Sequence) &gt;= the given key.</summary>
    private int LowerBound(double timestampMs, long sequence)
    {
        var low = 0;
        var high = _frames.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var frame = _frames[mid];
            if (frame.TimestampMs < timestampMs
                || (frame.TimestampMs == timestampMs && frame.Sequence < sequence))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }
}
