using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Sim.Core;
using Sim.Protocol;

namespace Sim.Controller;

/// <summary>
/// External controller adapter for the JSONL stdio contract. One instance owns
/// one child process; callers must create one bridge per role and per match.
/// </summary>
public sealed class ExternalControllerBridge : IControllerAdapter, IDisposable
{
    /// <summary>
    /// 进程退出后给 stdout 读取线程的排空宽限: 应答可能在进程退出前已写入管道,
    /// 但还没被读取线程送进队列。宽限耗尽仍无应答 = 命令实际起不来, 立刻报死,
    /// 不必等满 TimeoutMs。
    /// </summary>
    private static readonly TimeSpan ExitDrainGrace = TimeSpan.FromMilliseconds(100);

    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _reader;
    private readonly TimeSpan _deadline;
    private string _lastFault = "";
    private string _lastProtocolFault = "";
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

    /// <summary>
    /// 最近一次被丢弃的应答原因（非法动作行 / requestId 不匹配）。与
    /// <see cref="LastFault"/> 不同: 随后的应答超时不会覆盖它 —— 桌面预检据此把
    /// "进程活着但应答是垃圾/答了别的帧"与"首帧加载慢"分开（前者确定性失败,
    /// 后者保留外部控制器并告警）。不影响 Faults/LastFault 的既有语义。
    /// </summary>
    public string LastProtocolFault => Volatile.Read(ref _lastProtocolFault);

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
            // JSONL 契约固定 UTF-8 (docs/CONTROLLER_PROTOCOL.md): 不显式设置时 .NET 用
            // Console.InputEncoding, 随宿主控制台代码页变化(中文 Windows 控制台是 cp936),
            // obs 里的中文标签会被写成乱码。ASCII 动作行字节不变, 既有语义不变。
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
        // 命令能启动但当场退出(脚本不存在/解释器报错)时, 读取线程可能还在把退出前
        // 写出的行送进队列: 先给 ExitDrainGrace 排空, 仍无应答就立即按"进程已退出"
        // 报错, 不必空等满 TimeoutMs —— 结论同为拒绝, 但原因不再误写成应答超时。
        var exitedAt = DateTime.MinValue;
        while (DateTime.UtcNow < cutoff)
        {
            if (!_lines.TryTake(out var line, millisecondsTimeout: 2))
            {
                if (_process.HasExited)
                {
                    if (exitedAt == DateTime.MinValue)
                    {
                        exitedAt = DateTime.UtcNow;
                    }
                    if (DateTime.UtcNow - exitedAt >= ExitDrainGrace)
                    {
                        return Fault("controller process exited without a response"
                            + " (command failed to start or crashed)");
                    }
                }
                continue;
            }
            if (!ProtocolJson.TryParseActionLine(line, out var action, out var error) || action is null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Volatile.Write(ref _lastFault, $"invalid action: {error}");
                    Volatile.Write(ref _lastProtocolFault, $"invalid action: {error}");
                }
                continue; // diagnostics/log lines are not actions
            }
            // Missing requestId is accepted for legacy controllers. A stale or
            // future id belongs to another frame and must never be applied here.
            if (action.RequestId is null || string.Equals(action.RequestId, expectedId, StringComparison.Ordinal))
            {
                return action;
            }
            // 丢弃原因单独留痕(不改 Faults/LastFault 语义): 桌面预检据此把
            // "应答了别的帧/坏行"与"首帧加载慢没应答"分开。
            Volatile.Write(ref _lastProtocolFault,
                $"dropped action for requestId '{action.RequestId}' (expected '{expectedId}')");
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
