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
    string? DriverFault = null);

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
    private readonly BlockingCollection<DriverCommand> _commands = new(128);
    private readonly ConcurrentQueue<Snapshot> _snapshots = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _lifecycleGate = new();
    private Thread? _worker;
    private MatchEngine? _engine;
    private ExternalControllerBridge? _usBridge;
    private ExternalControllerBridge? _themBridge;
    private string? _usStartupFault;
    private string? _themStartupFault;
    private string? _driverFault;
    private DesktopLiveStatus _status;
    private int _snapshotCount;
    private int _stopRequested;
    private int _disposed;

    public DesktopLiveDriver(Scenario scenario, ControllerProfile? usProfile, ControllerProfile? themProfile)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _scenario = scenario;
        _usProfile = usProfile ?? new ControllerProfile();
        _themProfile = themProfile ?? new ControllerProfile();
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

    private void Run()
    {
        try
        {
            _engine = MatchEngineHost.Create(_scenario);
            _usBridge = StartBridge(_usProfile, RoleNames.Us);
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

        RobotAction? usAction = null;
        RobotAction? themAction = null;
        if (_engine.Phase == MatchControlPhase.Running)
        {
            usAction = Decide(_usProfile, _usBridge, _engine, _engine.Us);
            themAction = Decide(_themProfile, _themBridge, _engine, _engine.Them);
        }
        var snapshot = _engine.Tick(usAction, themAction);
        PublishSnapshot(snapshot);
        PublishStatus();
    }

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
        var next = new DesktopLiveStatus(
            engine.TickIndex,
            engine.Phase,
            engine.Paused,
            engine.Done,
            engine.Scores,
            engine.RestartPenalties,
            BuildControllerStatus(_usProfile, _usBridge, _usStartupFault),
            BuildControllerStatus(_themProfile, _themBridge, _themStartupFault),
            _driverFault);
        Volatile.Write(ref _status, next);
    }

    private static DesktopControllerStatus InitialControllerStatus(ControllerProfile profile)
        => profile.IsExternal
            ? new(ControllerModes.External, true, false, 0, "启动中")
            : new(ControllerModes.BuiltIn, false, true, 0, "");

    private static DesktopControllerStatus BuildControllerStatus(ControllerProfile profile,
        ExternalControllerBridge? bridge, string? startupFault)
    {
        if (!profile.IsExternal)
        {
            return new(ControllerModes.BuiltIn, false, true, 0, "");
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
