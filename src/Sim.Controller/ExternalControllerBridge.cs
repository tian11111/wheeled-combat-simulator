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

    /// <summary>
    /// 观测行未能在 <c>--timeout-ms</c> 内写进管道（含"上一帧的写仍阻塞"）:
    /// 桥放弃等待本帧观测送达, 零动作, 计一次 fault; 进程保留, 下一帧仍可恢复。
    /// </summary>
    private const string WriteTimeoutFault = "controller stdin write timeout";

    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _reader;
    private readonly Thread _writer;
    private readonly object _writeGate = new();
    private readonly TimeSpan _deadline;
    private WriteJob? _inFlight;
    private string _lastFault = "";
    private string _lastProtocolFault = "";
    private long _faults;
    private int _disposed;

    /// <summary>一帧待写观测。写线程持有它直到管道写入/失败；同一时刻至多一个。</summary>
    private sealed class WriteJob(string payload)
    {
        public string Payload { get; } = payload;

        private readonly TaskCompletionSource _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>写完成（成功或失败）时完成；调用方按超时等待。</summary>
        public Task Completion => _done.Task;

        public Exception? Failure { get; private set; }

        public void Complete(Exception? failure)
        {
            Failure = failure;
            _done.TrySetResult();
        }
    }

    private ExternalControllerBridge(Process process, TimeSpan deadline)
    {
        _process = process;
        _deadline = deadline;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "controller-stdout" };
        _reader.Start();
        // 常驻写线程: 管道写满时它会阻塞在这一帧的写上, 调用方按超时放弃;
        // 专用线程不占线程池, 别的桥阻塞也不会拖慢本桥的下一帧。
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "controller-stdin" };
        _writer.Start();
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

        string payload;
        try
        {
            payload = ProtocolJson.Serialize(observation);
        }
        catch (Exception error)
        {
            // 与既有口径一致: 序列化失败也归入 stdin 失败这一确定性故障类别。
            return Fault($"controller stdin failed: {error.Message}");
        }
        var expectedId = observation.RequestId.ToString();
        WriteJob job;
        lock (_writeGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                // Dispose 与 Decide 竞争: 写线程已退出, 排进去的写永远不会完成。
                return Fault("controller process is not running");
            }
            // 单在途写裁决: 上一帧的写还卡在管道里时, 本帧的观测必然也送不进去
            // (策略没消化上一帧)。直接按写超时记 fault 零动作, 不排队第二个写 ——
            // 否则每 tick 一个阻塞写任务会堆叠, 内存/管道一起爆。
            if (_inFlight is not null)
            {
                return Fault(WriteTimeoutFault);
            }
            job = new WriteJob(payload);
            _inFlight = job;
            Monitor.Pulse(_writeGate);
        }

        // 写与应答共用 --timeout-ms 口径: 管道写满时写线程会一直阻塞在 WriteLine,
        // 调用方等满一个 TimeoutMs 就放弃本帧 (写线程仍在后台, 管道恢复后自行完成,
        // 进程不杀 —— 与应答超时"慢/暂塞仍可恢复"的语义对齐)。写与应答各自计满一个
        // 截止, 慢写的进程不会因此挤掉原本的应答预算。非正截止按 0 处理: 既有语义
        // 下这种配置本就等不到任何应答, 这里不能让它变成 Task.Wait 的参数异常。
        if (!job.Completion.Wait(_deadline > TimeSpan.Zero ? _deadline : TimeSpan.Zero))
        {
            return Fault(WriteTimeoutFault);
        }
        if (job.Failure is { } writeError)
        {
            return Fault($"controller stdin failed: {writeError.Message}");
        }

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

    /// <summary>
    /// 常驻写线程: 串行执行 <see cref="_inFlight"/> 里的观测行。管道写满时它会
    /// 阻塞在这一帧的写上; <see cref="Decide"/> 超时后不再排新写（单在途裁决），
    /// 所以这里最多只有一个阻塞写，管道恢复/进程退出后它自行完成或失败。
    /// </summary>
    private void WriteLoop()
    {
        while (true)
        {
            WriteJob? job;
            lock (_writeGate)
            {
                while (_inFlight is null && Volatile.Read(ref _disposed) == 0)
                {
                    Monitor.Wait(_writeGate);
                }
                job = _inFlight;
                if (job is null)
                {
                    return; // disposed with no pending write
                }
            }

            Exception? failure = null;
            try
            {
                _process.StandardInput.WriteLine(job.Payload);
                _process.StandardInput.Flush();
            }
            catch (Exception error)
            {
                failure = error;
            }

            lock (_writeGate)
            {
                // 只有自己仍是在途写时才清空: Dispose 之后不再接收新写。
                if (ReferenceEquals(_inFlight, job))
                {
                    _inFlight = null;
                }
            }
            job.Complete(failure);
        }
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
        // 唤醒写线程: 它可能正阻塞在管道写上（子进程已不再读）。Kill 关掉读端后
        // 该写会自行失败, 线程看到 disposed 且无在途写即退出。有界等待而不是无限
        // join: 管道异常时 Dispose 也不能挂死; 等它退出只是为了避免与
        // StandardInput 的并发关闭（StreamWriter 非线程安全）。
        lock (_writeGate)
        {
            Monitor.PulseAll(_writeGate);
        }
        _writer.Join(TimeSpan.FromSeconds(1));
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
