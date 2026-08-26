using System.Collections.Concurrent;
using System.Diagnostics;
using Sim.Core;
using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// External controller adapter that speaks the legacy JSONL stdio protocol:
/// each tick it writes one observation JSON line to the child process's stdin
/// and expects one <c>{"v": .., "w": .., "requestId": ..}</c> line back.
///
/// Fault policy (CONTRACT.md section 2): request-id mismatches and unparseable
/// lines are dropped; a deadline miss or dead process falls back to a zero
/// action. The match itself never blocks indefinitely on a misbehaving
/// controller.
/// </summary>
public sealed class PythonBridge : IControllerAdapter, IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _reader;
    private readonly TimeSpan _deadline;
    private bool _disposed;

    private PythonBridge(Process process, TimeSpan deadline)
    {
        _process = process;
        _deadline = deadline;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "controller-stdout" };
        _reader.Start();
    }

    /// <summary>Fault count: timeouts, dead-pipe writes and unmatched deadlines.</summary>
    public long Faults { get; private set; }

    /// <summary>Launches <paramref name="command"/> (executable plus optional arguments).</summary>
    public static PythonBridge Start(string command, double timeoutMs)
    {
        var (fileName, arguments) = SplitCommand(command);
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
        return new PythonBridge(process, TimeSpan.FromMilliseconds(timeoutMs));
    }

    public RobotAction Decide(Observation observation)
    {
        if (_disposed || _process.HasExited)
        {
            Faults++;
            return RobotAction.Zero;
        }
        try
        {
            _process.StandardInput.WriteLine(ProtocolJson.Serialize(observation));
            _process.StandardInput.Flush();
        }
        catch (Exception)
        {
            Faults++;
            return RobotAction.Zero;
        }

        var expectedId = observation.RequestId.ToString();
        var cutoff = DateTime.UtcNow + _deadline;
        while (DateTime.UtcNow < cutoff)
        {
            if (!_lines.TryTake(out var line, millisecondsTimeout: 2))
            {
                continue;
            }
            if (!ProtocolJson.TryParseActionLine(line, out var action, out _) || action is null)
            {
                continue; // log lines / diagnostics from the child are not actions
            }
            // Missing requestId is accepted (legacy bridge semantics); a stale or
            // future id belongs to another frame and must never be applied here.
            if (action.RequestId is null || string.Equals(action.RequestId, expectedId, StringComparison.Ordinal))
            {
                return action;
            }
        }
        Faults++;
        return RobotAction.Zero;
    }

    private void ReadLoop()
    {
        try
        {
            while (!_process.HasExited)
            {
                var line = _process.StandardOutput.ReadLine();
                if (line is null)
                {
                    break;
                }
                _lines.Add(line);
            }
        }
        catch (Exception)
        {
            // pipe torn down during dispose — nothing to recover
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // already gone
        }
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
