using System.Collections.Concurrent;
using System.Diagnostics;
using Sim.Core;
using Sim.Protocol;

namespace Sim.Controller;

/// <summary>
/// External controller adapter for the JSONL stdio contract. One instance owns
/// one child process; callers must create one bridge per role and per match.
/// </summary>
public sealed class ExternalControllerBridge : IControllerAdapter, IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _reader;
    private readonly TimeSpan _deadline;
    private string _lastFault = "";
    private long _faults;
    private int _disposed;

    private ExternalControllerBridge(Process process, TimeSpan deadline)
    {
        _process = process;
        _deadline = deadline;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "controller-stdout" };
        _reader.Start();
    }

    /// <summary>Fault count: timeouts, dead-pipe writes and rejected responses.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>Last fault category for a desktop status badge or CLI diagnostic.</summary>
    public string LastFault => Volatile.Read(ref _lastFault);

    public bool IsRunning
        => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    /// <summary>Launches an executable plus optional arguments from a command string.</summary>
    public static ExternalControllerBridge Start(string command, double timeoutMs)
    {
        var (fileName, arguments) = SplitCommand(command);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Controller command must not be empty.", nameof(command));
        }
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(info)
            ?? throw new InvalidOperationException($"failed to start controller process: {command}");
        return new ExternalControllerBridge(process, TimeSpan.FromMilliseconds(timeoutMs));
    }

    public RobotAction Decide(Observation observation)
    {
        if (!IsRunning)
        {
            return Fault("controller process is not running");
        }

        try
        {
            _process.StandardInput.WriteLine(ProtocolJson.Serialize(observation));
            _process.StandardInput.Flush();
        }
        catch (Exception error)
        {
            return Fault($"controller stdin failed: {error.Message}");
        }

        var expectedId = observation.RequestId.ToString();
        var cutoff = DateTime.UtcNow + _deadline;
        while (DateTime.UtcNow < cutoff)
        {
            if (!_lines.TryTake(out var line, millisecondsTimeout: 2))
            {
                continue;
            }
            if (!ProtocolJson.TryParseActionLine(line, out var action, out var error) || action is null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Volatile.Write(ref _lastFault, $"invalid action: {error}");
                }
                continue; // diagnostics/log lines are not actions
            }
            // Missing requestId is accepted for legacy controllers. A stale or
            // future id belongs to another frame and must never be applied here.
            if (action.RequestId is null || string.Equals(action.RequestId, expectedId, StringComparison.Ordinal))
            {
                return action;
            }
        }
        return Fault($"controller response timeout for requestId={expectedId}");
    }

    private RobotAction Fault(string message)
    {
        Interlocked.Increment(ref _faults);
        Volatile.Write(ref _lastFault, message);
        return RobotAction.Zero;
    }

    private void ReadLoop()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && !_process.HasExited)
            {
                var line = _process.StandardOutput.ReadLine();
                if (line is null)
                {
                    break;
                }
                try
                {
                    _lines.Add(line);
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Volatile.Write(ref _lastFault, $"controller stdout failed: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process may have exited between HasExited and Kill.
        }
        _lines.CompleteAdding();
        _process.Dispose();
        _lines.Dispose();
    }

    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        command = command.TrimStart();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
            {
                return (command[1..end], command[(end + 1)..].TrimStart());
            }
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].TrimStart());
    }
}
