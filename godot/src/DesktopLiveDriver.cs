// Real-time desktop orchestration boundary. The simulation engine stays on one
// worker thread; Godot only posts referee commands and consumes immutable
// snapshots/status values from this class.

using System.Collections.Concurrent;
using System.Diagnostics;
using Sim.Controller;
using Sim.Core;
using Sim.Hosting;
using Sim.Protocol;

namespace Sim.GodotShell;

public sealed record DesktopControllerStatus(
    string Mode,
    bool Configured,
    bool Running,
    long Faults,
    string LastFault);

public sealed record DesktopLiveStatus(
    long Tick,
    MatchControlPhase Phase,
    bool Paused,
    bool Done,
    Scores Scores,
    Scores Penalties,
    DesktopControllerStatus UsController,
    DesktopControllerStatus ThemController,
    string? DriverFault = null,
    string? Handoff = null,
    string? HandoffReason = null);

/// <summary>
/// Non-blocking live runner for the desktop shell. It is deliberately separate
/// from Sim.Core: process I/O, threads, pacing and queue ownership do not leak
/// into deterministic simulation code.
/// </summary>
public sealed class DesktopLiveDriver : IDisposable
{
    private const int SnapshotCapacity = 8;

    private readonly Scenario _scenario;
    private readonly ControllerProfile _usProfile;
    private readonly ControllerProfile _themProfile;
    private readonly Func<IVisionAdapter?>? _visionFactory;
    // legacy L1/L2/L3 接触开关 (批2): driver 自建引擎必须与桌面 MatchSession 同参,
    // 否则外部控制器实况与渲染线程物理不一致。null = 默认全开 = 现行为。
    private readonly ContactResolveOptions? _contactOptions;
    private readonly bool _exhibitionRequested;
    private readonly BlockingCollection<DriverCommand> _commands = new(128);
    private readonly ConcurrentQueue<Snapshot> _snapshots = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _lifecycleGate = new();
    private Thread? _worker;
    private MatchEngine? _engine;
    private LiveVisionBridge? _liveVision;
    private ExternalControllerBridge? _usBridge;
    private ExternalControllerBridge? _themBridge;
    private string? _usStartupFault;
    private string? _themStartupFault;
    private string? _driverFault;
    private DesktopLiveStatus _status;
    private int _snapshotCount;
    private int _stopRequested;
    private int _disposed;

    // SCORE_BLOCK 展演交接状态（只在 worker 线程读写）: Arm 后先双方内置 FSM 预推进，
    // 交接前我方 Tick(null)，交接后逐 tick 注入 11 维 rlObservation 交给外部策略。
    private bool _exhibitionMode;
    private bool _exhibitionActive;
    private bool _handoffPending;
    private bool _handoffDone;
    private string? _handoffReason;
    private int _prerollTicks;
    private ScoreBlockExhibition.PrerollResult _handoff;
    private Snapshot? _handoffSnapshot;

    /// <param name="visionFactory">
    /// 每场一次的视觉源工厂(null = 默认 classifyRate 桩): driver 自建引擎, 必须与
    /// 桌面 MatchSession 用同一份工厂, 否则外部控制器实况与渲染线程看到的视觉源不同。
    /// </param>
    /// <param name="exhibitionUs">
    /// true = 我方外部控制器按 SCORE_BLOCK 展演交接运行（与 CLI
    /// <c>--start-at score_block</c> 同一口径：Arm 后 ArmAndPreroll → 交接 → 每 tick
    /// 注入 <c>rlObservation</c>）。只在我方为 external 且场景是 mujoco 线时生效；
    /// legacy 场景直接拒绝并给出原因（见 <see cref="ControllerWiring"/>）。
    /// 默认 false = 逐 tick 语义与既有桌面外部控制器完全一致。
    /// </param>
    /// <param name="contactOptions">
    /// legacy L1/L2/L3 接触求解开关 (桌面"高级/开发者"折叠区); null = 默认全开 =
    /// 现行为。mujoco 场景不消费。
    /// </param>
    public DesktopLiveDriver(Scenario scenario, ControllerProfile? usProfile, ControllerProfile? themProfile,
        Func<IVisionAdapter?>? visionFactory = null, bool exhibitionUs = false,
        ContactResolveOptions? contactOptions = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _scenario = scenario;
        _visionFactory = visionFactory;
        _contactOptions = contactOptions;
        _usProfile = usProfile ?? new ControllerProfile();
        _themProfile = themProfile ?? new ControllerProfile();
        _exhibitionRequested = exhibitionUs;
        _status = new DesktopLiveStatus(
            0,
            MatchControlPhase.Prep,
            false,
            false,
            new Scores(),
            new Scores(),
            InitialControllerStatus(_usProfile),
            InitialControllerStatus(_themProfile));
    }

    public Scenario Scenario => _scenario;

    public DesktopLiveStatus Status => Volatile.Read(ref _status);

    public bool IsRunning => _worker?.IsAlive == true && Volatile.Read(ref _stopRequested) == 0;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_worker is not null)
            {
                return;
            }
            _worker = new Thread(Run)
            {
                IsBackground = true,
                Name = "desktop-live-driver",
            };
            _worker.Start();
        }
    }

    public bool TryTakeLatest(out Snapshot snapshot)
    {
        Snapshot? latest = null;
        while (_snapshots.TryDequeue(out var next))
        {
            Interlocked.Decrement(ref _snapshotCount);
            latest = next;
        }
        if (latest is null)
        {
            snapshot = null!;
            return false;
        }
        snapshot = latest;
        return true;
    }

    public bool RequestArm() => Post(new DriverCommand(DriverCommandKind.Arm));

    public bool RequestPause() => Post(new DriverCommand(DriverCommandKind.Pause));

    public bool RequestResume() => Post(new DriverCommand(DriverCommandKind.Resume));

    public bool RequestRestart(string role) => Post(new DriverCommand(DriverCommandKind.Restart, role));

    public bool RequestStop() => Post(new DriverCommand(DriverCommandKind.Stop));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _stopRequested, 1);
        TryAddStopCommand();
        _wake.Set();
        var worker = _worker;
        if (worker is not null && worker != Thread.CurrentThread)
        {
            // Bridge.Decide is bounded by the configured controller timeout
            // (at most 5 seconds), so shutdown remains finite and deterministic
            // from the shell's point of view.
            worker.Join(TimeSpan.FromSeconds(6));
        }
        _commands.Dispose();
        _wake.Dispose();
    }

    /// <summary>当前场的 live 桥记录 (进程源换场必须释放; 与 MatchSession 同模式)。</summary>
    private IVisionAdapter? CreateVision()
    {
        if (_visionFactory?.Invoke() is not { } adapter)
        {
            return null;
        }
        if (adapter is LiveVisionBridge bridge)
        {
            _liveVision = bridge;
        }
        return adapter;
    }

    private void Run()
    {
        try
        {
            _engine = MatchEngineHost.Create(_scenario, CreateVision(), _contactOptions);
            // legacy 场景拒绝展演外部控制器：不启动我方子进程，状态里给出原因
            // （不静默用内置 FSM 假装展演成功）。壳层已按同一决策回退，这里是最后防线。
            var rejected = _exhibitionRequested && _usProfile.IsExternal
                && !ControllerWiring.ScenarioSupportsExhibition(_scenario);
            if (rejected)
            {
                _handoffReason = ControllerWiring.RequiresMujocoScenario;
            }
            _exhibitionMode = _exhibitionRequested && _usProfile.IsExternal;
            _exhibitionActive = _exhibitionMode && !rejected;
            _usBridge = rejected ? null : StartBridge(_usProfile, RoleNames.Us);
            _themBridge = StartBridge(_themProfile, RoleNames.Them);
            PublishStatus();

            var tickSeconds = _scenario.Field.TickSeconds;
            var stopwatch = Stopwatch.StartNew();
            var previous = stopwatch.Elapsed.TotalSeconds;
            var accumulator = 0.0;
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                DrainCommands();
                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    break;
                }

                var now = stopwatch.Elapsed.TotalSeconds;
                accumulator = Math.Min(0.25, accumulator + Math.Max(0, now - previous));
                previous = now;
                var stepped = false;
                while (accumulator >= tickSeconds && Volatile.Read(ref _stopRequested) == 0)
                {
                    StepOne();
                    accumulator -= tickSeconds;
                    stepped = true;
                    if (_engine.Done)
                    {
                        Interlocked.Exchange(ref _stopRequested, 1);
                        break;
                    }
                }
                if (!stepped)
                {
                    var waitMs = (int)Math.Clamp((tickSeconds - accumulator) * 1000, 1, 8);
                    _wake.Wait(waitMs);
                    _wake.Reset();
                }
            }
        }
        catch (Exception error)
        {
            _driverFault = error.Message;
            PublishStatus();
        }
        finally
        {
            _liveVision?.DisposeSource();
            _liveVision = null;
            _usBridge?.Dispose();
            _themBridge?.Dispose();
            _usBridge = null;
            _themBridge = null;
            _engine?.Dispose();
            _engine = null;
        }
    }

    private void DrainCommands()
    {
        if (_engine is null)
        {
            return;
        }
        while (_commands.TryTake(out var command))
        {
            switch (command.Kind)
            {
                case DriverCommandKind.Arm:
                    _engine.Arm();
                    if (_exhibitionActive && !_handoffDone && _handoffReason is null)
                    {
                        // 展演交接从下一个 tick 开始预推进（预推进前我方必须 Tick(null)）。
                        _handoffPending = true;
                    }
                    break;
                case DriverCommandKind.Pause:
                    _engine.Pause("桌面端手动暂停");
                    break;
                case DriverCommandKind.Resume:
                    _engine.Resume();
                    break;
                case DriverCommandKind.Restart when command.Role is not null:
                    _engine.RestartRobot(command.Role);
                    break;
                case DriverCommandKind.Stop:
                    Interlocked.Exchange(ref _stopRequested, 1);
                    break;
            }
            PublishStatus();
        }
    }

    private void StepOne()
    {
        if (_engine is null)
        {
            return;
        }

        if (_handoffPending)
        {
            TryCompleteHandoffAtEntry();
        }

        RobotAction? usAction = null;
        RobotAction? themAction = null;
        if (_engine.Phase == MatchControlPhase.Running)
        {
            // 展演模式(含被拒绝/未交接)一律走 DecideExhibitionUs: 未交接时返回 null,
            // 我方保持内置 FSM —— 绝不用零动作把角色静默切进 Manual。
            usAction = _exhibitionMode
                ? DecideExhibitionUs()
                : Decide(_usProfile, _usBridge, _engine, _engine.Us);
            themAction = Decide(_themProfile, _themBridge, _engine, _engine.Them);
        }
        var snapshot = _engine.Tick(usAction, themAction);
        if (_handoffDone)
        {
            _handoffSnapshot = snapshot;
        }
        PublishSnapshot(snapshot);
        PublishStatus();
    }

    /// <summary>
    /// Arm 之后的可见预推进：不静默快进，而是每个引擎 tick 前探测一次入场条件
    /// （与 CLI 的 <c>--start-at score_block</c> / <see cref="ScoreBlockExhibition.ArmAndPreroll"/>
    /// 同一判定）。未入场时本 tick 仍由内置 FSM 驱动并照常发布快照 —— 上台阶段对用户
    /// 可见；到达 SCORE_BLOCK 即锁定目标完成交接，之后才注入 11 维观测。4800 tick
    /// 上限内未入场则不交接、释放我方子进程，由内置 FSM 继续跑完整场（不静默假装
    /// 展演成功）。
    /// </summary>
    private void TryCompleteHandoffAtEntry()
    {
        var engine = _engine;
        if (engine is null || engine.Done)
        {
            _handoffPending = false;
            if (engine is not null)
            {
                AbortHandoff(ScoreBlockExhibition.NoScoreBlockReason);
            }
            return;
        }
        if (_usBridge is not { IsRunning: true })
        {
            // 启动失败/进程早退：这是唯一能真正回退内置 FSM 的窗口（尚未进入 Manual）。
            _handoffPending = false;
            AbortHandoff(_usStartupFault ?? "外部控制器进程在交接前退出");
            return;
        }
        if (!ScoreBlockExhibition.ScoreBlockEntryReached(engine))
        {
            _prerollTicks++;
            if (_prerollTicks >= ScoreBlockExhibition.PrerollMaxTicks)
            {
                _handoffPending = false;
                AbortHandoff(ScoreBlockExhibition.NoScoreBlockReason);
            }
            return; // 本 tick 仍由内置 FSM 驱动并发布快照（可见上台阶段）
        }
        _handoffPending = false;
        var entryTick = (int)engine.TickIndex;
        var targetIndex = ScoreBlockExhibition.LockTargetIndex(engine);
        if (targetIndex < 0)
        {
            AbortHandoff(ScoreBlockExhibition.NoValidTargetReason);
            return;
        }
        if (_usBridge is not { IsRunning: true })
        {
            AbortHandoff("外部控制器进程在预推进期间退出");
            return;
        }
        _handoff = new ScoreBlockExhibition.PrerollResult(false, null, entryTick, targetIndex, engine.CommitSnapshot(), _prerollTicks);
        _handoffSnapshot = _handoff.EntrySnapshot;
        _handoffDone = true;
    }

    private void AbortHandoff(string reason)
    {
        _handoffReason = reason;
        _usBridge?.Dispose();
        _usBridge = null;
        _handoffSnapshot = null;
        PublishStatus();
    }

    /// <summary>
    /// 展演我方动作：交接完成前必须 <c>null</c>（任何非 null 动作都会把角色切 Manual，
    /// FSM 预推进/SCORE_BLOCK 永不发生）；交接后与 CLI 展演同口径注入 11 维观测。
    /// 未交接（含进程早退/无 SCORE_BLOCK）时回到内置 FSM 语义。
    /// </summary>
    private RobotAction? DecideExhibitionUs()
    {
        if (_handoffReason is not null || !_handoffDone)
        {
            return null;
        }
        var bridge = _usBridge;
        if (bridge is null)
        {
            return RobotAction.Zero;
        }
        return bridge.Decide(BuildExhibitionObservation());
    }

    /// <summary>
    /// 交接后的我方 obs = 宿主 <see cref="MatchEngine.BuildObservation"/> + 加性
    /// <c>rlObservation</c>（唯一投影来自 <see cref="ScoreBlockExhibition.BuildObservation"/>，
    /// 口径与 CLI <c>MatchRunner.BuildExhibitionObservation</c> 逐位一致：车体状态取当前
    /// 引擎，在台/剩余时间取最近提交帧快照）。
    /// </summary>
    private Observation BuildExhibitionObservation()
    {
        var engine = _engine!;
        var observation = engine.BuildObservation(engine.Us);
        var field = engine.Scenario.Field;
        var lastSnapshot = _handoffSnapshot;
        return observation with
        {
            RlObservation = ScoreBlockExhibition.BuildObservation(engine, _handoff.TargetIndex,
                field.Platform, field.MatchDuration,
                UsOnPlatform(lastSnapshot),
                lastSnapshot?.Timer ?? engine.MatchTimer),
        };
    }

    private static bool UsOnPlatform(Snapshot? snapshot)
        => snapshot is not null
            && snapshot.Robots.TryGetValue(RoleNames.Us, out var us)
            && us.OnPlatform;

    private static RobotAction? Decide(ControllerProfile profile, ExternalControllerBridge? bridge,
        MatchEngine engine, RobotRuntime robot)
    {
        if (!profile.IsExternal)
        {
            return null;
        }
        if (bridge is null)
        {
            // A configured-but-unlaunchable external role stays in manual mode
            // with the same safe zero-action policy as a bridge fault.
            return RobotAction.Zero;
        }
        return bridge.Decide(engine.BuildObservation(robot));
    }

    private ExternalControllerBridge? StartBridge(ControllerProfile profile, string role)
    {
        if (!profile.IsExternal)
        {
            return null;
        }
        try
        {
            return ExternalControllerBridge.Start(profile.Command, profile.TimeoutMs);
        }
        catch (Exception error)
        {
            if (role == RoleNames.Us)
            {
                _usStartupFault = error.Message;
            }
            else
            {
                _themStartupFault = error.Message;
            }
            return null;
        }
    }

    private void PublishSnapshot(Snapshot snapshot)
    {
        _snapshots.Enqueue(snapshot);
        Interlocked.Increment(ref _snapshotCount);
        while (Volatile.Read(ref _snapshotCount) > SnapshotCapacity && _snapshots.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _snapshotCount);
        }
    }

    private void PublishStatus()
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }
        var handoff = _handoffDone && _handoff.TargetIndex >= 0 && _handoff.TargetIndex < engine.Blocks.Count
            ? $"展演交接 tick={_handoff.EntryTick} 目标={engine.Blocks[_handoff.TargetIndex].Name}"
            : null;
        var next = new DesktopLiveStatus(
            engine.TickIndex,
            engine.Phase,
            engine.Paused,
            engine.Done,
            engine.Scores,
            engine.RestartPenalties,
            BuildControllerStatus(_usProfile, _usBridge, _usStartupFault, _handoffReason),
            BuildControllerStatus(_themProfile, _themBridge, _themStartupFault),
            _driverFault,
            handoff,
            _handoffReason);
        Volatile.Write(ref _status, next);
    }

    private static DesktopControllerStatus InitialControllerStatus(ControllerProfile profile)
        => profile.IsExternal
            ? new(ControllerModes.External, true, false, 0, "启动中")
            : new(profile.Mode, false, true, 0, "");

    private static DesktopControllerStatus BuildControllerStatus(ControllerProfile profile,
        ExternalControllerBridge? bridge, string? startupFault, string? handoffReason = null)
    {
        if (!profile.IsExternal)
        {
            return new(profile.Mode, false, true, 0, "");
        }
        if (handoffReason is not null)
        {
            // 我方展演未交接（legacy 拒绝/无 SCORE_BLOCK/进程早退）：子进程已释放，
            // 状态行给出原因，不静默假装展演成功。
            return new(ControllerModes.External, true, false, 0, handoffReason);
        }
        if (bridge is null)
        {
            return new(ControllerModes.External, true, false, 1, startupFault ?? "启动失败");
        }
        return new(ControllerModes.External, true, bridge.IsRunning, bridge.Faults, bridge.LastFault);
    }

    private bool Post(DriverCommand command)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopRequested) != 0)
        {
            return false;
        }
        try
        {
            var added = _commands.TryAdd(command);
            _wake.Set();
            return added;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void TryAddStopCommand()
    {
        try
        {
            _commands.TryAdd(new DriverCommand(DriverCommandKind.Stop));
        }
        catch (InvalidOperationException)
        {
            // Collection is already being torn down.
        }
    }

    private enum DriverCommandKind
    {
        Arm,
        Pause,
        Resume,
        Restart,
        Stop,
    }

    private sealed record DriverCommand(DriverCommandKind Kind, string? Role = null);
}
