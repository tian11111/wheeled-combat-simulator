using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Sim.Core;

/// <summary>外部视觉流进程的生命周期状态。</summary>
public enum VisionProcessState
{
    /// <summary>stdout 仍在读取(进程尚未退出)。</summary>
    Running,

    /// <summary>进程以退出码 0 正常结束: 已缓冲的完整行仍会交付, 之后不再有新帧。</summary>
    Exited,

    /// <summary>流断裂(读失败)或进程非零退出: 已缓冲行仍可用, 但必须按"源已死"处理(桥随后记 stale/no_frame)。</summary>
    Faulted,
}

/// <summary>
/// 外部视觉进程源(阶段 2 真推理接入点): 启动一个子进程, 逐行读它的 stdout
/// JSONL —— 每行一个 <see cref="VisionStreamFrame"/>(字段同真车 CSV 列名契约,
/// 见 <see cref="VisionStreamFrame"/> 的文档), 喂给 <see cref="LiveVisionBridge"/>。
///
/// 子进程契约(写 requirements, 违反即流断裂):
/// 1. stdout **只**放 JSONL 帧, 一行一帧; 诊断/日志走 stderr(本类不重定向 stderr,
///    子进程日志直接进父进程 stderr, 不会因管道写满而阻塞);
/// 2. **必须逐帧 flush**(Python: <c>python -u</c> 或 <c>print(..., flush=True)</c>;
///    其它语言等价 Flush)。整块缓冲会让多帧在同一时刻成簇到达 —— 桥看到的不是
///    "实时流"而是"回放", 帧龄与 stale 率随之失真;
/// 3. 帧时间戳必须按升序输出(sidecar 证据包要求单调非降, 桥的会话锚点取首个交付帧);
/// 4. 进程退出/被杀死后不再有帧: 这不是异常而是流结束 —— 已缓冲的完整行继续交付,
///    之后的 classify 由桥按 stale/no_frame 记账(<see cref="VisionReplayConsumeRecord.Reason"/>),
///    引擎绝不被异常炸掉。
///
/// 非阻塞语义(Windows 命名/匿名管道要特别注意): 匿名管道上的同步 ReadFile 在无数据时
/// 会阻塞, 因此 <see cref="StreamReader.Peek"/> 不是非阻塞原语。本类用一条后台读取线程
/// 做阻塞 <see cref="StreamReader.ReadLine"/>, 把完整行放进并发队列; <see cref="PumpUntil"/>
/// 只排空该队列, 是纯内存操作 —— 引擎步进永不等待外部进程。
///
/// 时间轴: 外部源按**墙钟**到达(到达 = 读到该行的时刻), <see cref="PumpUntil"/> 的
/// <c>simTimeSeconds</c> 只作审计/调用方节流之用, 不用来裁剪帧。因此外部流必须配
/// CLI 的 <c>--realtime 1x</c> 跑(引擎步进按墙钟对齐): 快跑会让引擎时钟远超墙钟,
/// 窗内无新帧 ⇒ 全 stale, 那是假阴性而不是"实时结果"。
/// 会话锚点(<c>SimT 0</c>)由桥取首个交付帧的时间戳, 与 CSV 源同一时基契约。
///
/// 纪律: 本类只在 <see cref="PumpUntil"/> 被调用方驱动时做 IO(排空已读缓冲),
/// 自己不引入时钟、不做随机抽样、不读世界真值 —— 仿真内核路径保持零 IO/零时钟。
/// </summary>
public sealed class ExternalProcessStreamSource : IVisionStreamSource, IDisposable
{
    /// <summary>
    /// 进程会话的默认标签(sidecar/报告里的 session; 帧流本身不携带会话名)。
    /// 命令行的完整文本进报告 <c>source.path</c>, 两者合起来才是可审计的出处。
    /// </summary>
    public const string DefaultSession = SourceTag;

    /// <summary>报告/证据包的出处标记(与 CSV 的 "mbri-csv" 对应)。</summary>
    public const string SourceTag = "yolo-bridge-process";

    /// <summary>审计用方言标记(报告 source.dialect; 与 CSV 的 mbri-hunt-detections 区分)。</summary>
    public const string StreamDialect = "yolo-bridge-jsonl";

    /// <summary>
    /// 已缓冲行上限(有界等待背压): 子进程输出远快于引擎消费(失控输出/引擎快跑)时,
    /// 读取线程停止并把流定性为故障, 由管道背压阻塞子进程 —— 队列不无界吃内存,
    /// 已缓冲的完整行仍可交付。合法流(8fps×120s ≈ 960 行)距此上限几个数量级。
    /// </summary>
    internal const int DefaultMaxQueuedLines = 100_000;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly Thread _reader;
    private readonly int _maxQueuedLines;
    private readonly List<VisionReplayFrame> _released = [];
    private readonly List<VisionStreamFrame> _delivered = [];
    private readonly object _gate = new();
    private VisionProcessState _state = VisionProcessState.Running;
    private int? _exitCode;
    private string? _lastFault;
    private string? _lastRejectedLine;
    private long _faults;
    private int _rejectedLines;
    private int _disposed;

    private ExternalProcessStreamSource(Process process, string command, string session, int maxQueuedLines)
    {
        _process = process;
        Command = command;
        Session = session;
        _maxQueuedLines = maxQueuedLines;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "vision-stream-stdout" };
        _reader.Start();
    }

    /// <summary>启动子进程并开始后台读取 stdout(不等待任何一行到达)。</summary>
    /// <param name="commandLine">完整命令行(可执行文件 + 参数); 拆分语义与外部控制器桥一致。</param>
    /// <param name="session">会话标签(sidecar/报告的 session 字段); 缺省 <see cref="DefaultSession"/>。</param>
    /// <param name="maxQueuedLines">已缓冲行上限(测试注入小上限用); 缺省 <see cref="DefaultMaxQueuedLines"/>。</param>
    public static ExternalProcessStreamSource Start(
        string commandLine, string session = DefaultSession,
        int maxQueuedLines = DefaultMaxQueuedLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        var (fileName, arguments) = SplitCommand(commandLine);
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        Process process;
        try
        {
            process = Process.Start(info)
                ?? throw new InvalidOperationException($"未返回进程句柄: {commandLine}");
        }
        catch (Exception error)
        {
            // 启动失败(可执行文件不存在/权限等)是配置错误: 收敛成一条明确消息, 调用方按校验失败处理。
            throw new InvalidOperationException($"启动视觉流进程失败 '{fileName}': {error.Message}", error);
        }
        return new ExternalProcessStreamSource(process, commandLine, session, maxQueuedLines);
    }

    /// <summary>完整命令行(报告 source.path 的出处)。</summary>
    public string Command { get; }

    /// <summary>会话标签(sidecar/报告的 session; 帧流契约本身不带会话字段)。</summary>
    public string Session { get; }

    /// <summary>进程生命周期状态; 已缓冲的完整行在进程结束后仍可 <see cref="PumpUntil"/> 交付。</summary>
    public VisionProcessState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>退出码; 进程未退出时为 null(读取线程结束后才取值)。</summary>
    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _exitCode;
            }
        }
    }

    /// <summary>断流/契约违例计数(非零退出记 1 次; 每个坏 JSONL 行记 1 次)。</summary>
    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>最近一条故障说明(JSONL 解析失败或非零退出); 无故障时 null。</summary>
    public string? LastFault
    {
        get
        {
            lock (_gate)
            {
                return _lastFault;
            }
        }
    }

    /// <summary>被拒的 JSONL 行数(不符合 <see cref="VisionStreamFrame"/> 契约的行)。</summary>
    public int RejectedLines
    {
        get
        {
            lock (_gate)
            {
                return _rejectedLines;
            }
        }
    }

    /// <summary>最近一条被拒的原始行(截断; 诊断用), 无则 null。</summary>
    public string? LastRejectedLine
    {
        get
        {
            lock (_gate)
            {
                return _lastRejectedLine;
            }
        }
    }

    public IReadOnlyList<VisionReplayFrame> Released => _released;

    /// <summary>
    /// 已交付的线格式帧(到达顺序) —— sidecar 证据包的工作集(与 CSV 源的
    /// <c>ReleasedEvidenceFrames</c> 对应)。帧在 <see cref="PumpUntil"/> 里按交付顺序追加。
    /// </summary>
    public IReadOnlyList<VisionStreamFrame> DeliveredFrames => _delivered;

    /// <summary>
    /// 排空已缓冲的完整行并交付其中的帧, 返回本次新增帧数。纯内存操作(见类文档的
    /// Windows 管道语义说明): 永不等待子进程写下一行。空行被跳过(管道尾部的
    /// 换行碎片不是数据); 坏行记入 <see cref="RejectedLines"/> 并继续 ——
    /// 数据契约错误绝不允许静默成帧, 也不允许炸掉引擎。
    /// </summary>
    public int PumpUntil(double simTimeSeconds)
    {
        var added = 0;
        while (_lines.TryDequeue(out var line))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            VisionStreamFrame frame;
            try
            {
                frame = VisionStreamFrame.ParseLine(line);
            }
            catch (Exception error)
            {
                // 外部字节的边界: 契约错误以 JsonException 报出, 但任何解析期异常
                // (如 JSON null 形状引发的 NullReferenceException) 也必须收敛成
                // 帧级故障 —— 一行坏数据永远不允许炸掉整场仿真。异常类型入账,
                // 便于区分"数据坏"与"解析器 bug"。
                Reject(line, $"JSONL 帧被拒 ({error.GetType().Name}): {error.Message}");
                continue;
            }
            _delivered.Add(frame);
            _released.Add(frame.ToReplayFrame());
            added++;
        }
        return added;
    }

    private void Reject(string line, string message)
    {
        lock (_gate)
        {
            _rejectedLines++;
            _faults++;
            _lastFault = message;
            _lastRejectedLine = line.Length <= 240 ? line : line[..240] + "…";
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                // 上限检查(单读者线程, 无竞态): 超限即停止读取并把流定性为故障 ——
                // 子进程随后被管道写满自然阻塞(背压), 队列内存有界; 已入队行仍可交付。
                if (_lines.Count >= _maxQueuedLines)
                {
                    Fault($"视觉流队列超过 {_maxQueuedLines} 行上限: 子进程输出远快于引擎消费"
                        + "(疑似失控输出或引擎快跑), 读取线程停止, 由管道背压阻塞子进程");
                    break;
                }
                var line = _process.StandardOutput.ReadLine();
                if (line is null)
                {
                    break; // stdout 关闭: 流结束(进程退出或句柄被回收)
                }
                _lines.Enqueue(line);
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Fault($"子进程 stdout 读取失败: {error.Message}");
            }
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>读取线程收尾: 取退出码并落状态(非零退出 = Faulted; 本类主动 Kill 的不算故障)。</summary>
    private void Finish()
    {
        int? code = null;
        try
        {
            // stdout 已关闭, 进程此刻处于退出过程: 给一个短上限, 不无限等一个不死的子进程。
            if (_process.WaitForExit(2000))
            {
                code = _process.ExitCode;
            }
        }
        catch (Exception)
        {
            // 进程对象已被 Dispose 竞态: 退出码拿不到就如实记 null。
        }
        var deliberate = Volatile.Read(ref _disposed) != 0;
        lock (_gate)
        {
            _exitCode = code;
            if (_state == VisionProcessState.Faulted)
            {
                return; // 读失败已定性(且更具体), 不被退出码覆盖
            }
            if (deliberate || code is null or 0)
            {
                // 场次结束时被本类回收(如真推理进程长驻): 是正常收尾, 不是流故障。
                _state = VisionProcessState.Exited;
            }
            else
            {
                _state = VisionProcessState.Faulted;
                _faults++;
                _lastFault = $"子进程非零退出: {code}";
            }
        }
    }

    private void Fault(string message)
    {
        lock (_gate)
        {
            _state = VisionProcessState.Faulted;
            _faults++;
            _lastFault = message;
        }
    }

    /// <summary>
    /// 杀掉子进程(整个进程树)、收尾读取线程。live 场次结束后必须调用:
    /// 真推理进程不会自己退出。
    /// </summary>
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
            // 进程可能在 HasExited 与 Kill 之间退出。
        }
        // 读取线程因管道关闭而结束; 不 Join 也不影响正确性(后台线程), 但 Join 一下
        // 让"已无写者"成为确定性事实, 便于测试断言 Dispose 后队列不再增长。
        try
        {
            _reader.Join(TimeSpan.FromSeconds(2));
        }
        catch (ThreadStateException)
        {
            // 线程未启动/已结束: 忽略。
        }
        _process.Dispose();
    }

    /// <summary>命令串拆分: 与 <c>ExternalControllerBridge.SplitCommand</c> 同一惯例(可执行文件 + 其余参数原样保留)。</summary>
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
