using Sim.Core;

// 视觉流 JSONL 桩子进程(测试夹具, 非产品): 按 VisionStreamFrame 契约逐帧写 stdout,
// **每帧 Flush**(子进程契约的硬要求, 见 ExternalProcessStreamSource 文档), 可注入
// 慢启动/坏行/非零退出/长驻, 用于 ExternalProcessStreamSource 与 `vision live --process`
// 的协议冒烟(C# 侧, 不依赖 Python/权重)。
//
// 用法: VisionStreamStub [--count N] [--start-ms E] [--step-ms M] [--interval-ms W]
//                        [--lead-ms L] [--initial-delay-ms D] [--status target|no_target|error|no_data_or_stale]
//                        [--debuff-every K] [--garbage-at I] [--raw-file <path>] [--exit-code C] [--linger]
var count = 5;
var startMs = 1_786_931_530_037.0;
var stepMs = 200.0;
int? intervalMs = null; // null = 墙钟节奏 = 时间戳步长(按真实节奏推帧)
var leadMs = 0;
var initialDelayMs = 0;
var status = "target";
var debuffEvery = 0;
var garbageAt = -1;
var exitCode = 0;
var linger = false;
string? rawFile = null; // 指定时原样吐出该文件的行(契约攻击用例: detections:null / JSON null / [null])

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--count":
            count = int.Parse(args[++i]);
            break;
        case "--start-ms":
            startMs = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--step-ms":
            stepMs = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--interval-ms":
            intervalMs = int.Parse(args[++i]);
            break;
        case "--lead-ms":
            leadMs = int.Parse(args[++i]);
            break;
        case "--initial-delay-ms":
            initialDelayMs = int.Parse(args[++i]);
            break;
        case "--status":
            status = args[++i];
            break;
        case "--debuff-every":
            debuffEvery = int.Parse(args[++i]);
            break;
        case "--garbage-at":
            garbageAt = int.Parse(args[++i]);
            break;
        case "--raw-file":
            rawFile = args[++i];
            break;
        case "--exit-code":
            exitCode = int.Parse(args[++i]);
            break;
        case "--linger":
            linger = true;
            break;
        default:
            Console.Error.WriteLine($"VisionStreamStub: 未知参数 {args[i]}");
            return 2;
    }
}

if (initialDelayMs > 0)
{
    Thread.Sleep(initialDelayMs);
}

// JSONL 走 stdout 字节流(UTF-8 无 BOM), 逐帧 Flush —— 与真实桥的契约一致
// (python -u / flush=True 的等价做法; 控制台编码与重定向语义都不参与)。
using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
{
    AutoFlush = false,
};
if (rawFile is not null)
{
    foreach (var raw in File.ReadAllLines(rawFile))
    {
        stdout.WriteLine(raw);
        stdout.Flush();
    }
    return exitCode;
}
// 墙钟节奏 = --interval-ms(显式覆盖)否则时间戳步长; --lead-ms 把整条时间表提前
// (补偿本进程启动/JIT/管道延迟: 帧源等价确认要求"时间戳 ≤ 锚点+SimT*1000 的帧
// 在对应 classify 之前已经到达")。
var cadenceMs = intervalMs ?? stepMs;
var clock = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < count; i++)
{
    if (cadenceMs > 0)
    {
        var waitMs = i * cadenceMs - leadMs - clock.Elapsed.TotalMilliseconds;
        if (waitMs > 1)
        {
            Thread.Sleep((int)waitMs);
        }
        else if (waitMs > 0)
        {
            Thread.Sleep(0);
        }
    }
    if (i == garbageAt)
    {
        // 坏行: 桥必须记帧级故障并继续, 绝不能把半截数据当成检测。
        stdout.WriteLine("{ 这不是 JSON");
        stdout.Flush();
    }
    var isTarget = status == "target";
    var label = isTarget && debuffEvery > 0 && i % debuffEvery == 0 ? "debuff" : "buff";
    var classId = label == "debuff" ? 1 : 0;
    var frame = new VisionStreamFrame
    {
        Sequence = i + 1,
        TimestampMs = startMs + i * stepMs,
        Status = status,
        ReceivedAgeMs = 7.5,
        DetectionCount = isTarget ? 1 : 0,
        SelectedTargetIndex = isTarget ? 0 : null,
        FrameWidth = 640,
        FrameHeight = 480,
        Fps = 5.0,
        InferenceMs = 180.0,
        ArrivalSimT = i * stepMs / 1000.0,
        Detections = isTarget
            ?
            [
                new VisionStreamDetection
                {
                    ClassId = classId,
                    // target_type 是原始 YOLO 类别(good/bad), label 是映射后的标签(buff/debuff):
                    // 契约要求两者一致, 不能把映射标签填进原始类别字段。
                    TargetType = classId == 0 ? "good" : "bad",
                    Label = label,
                    Confidence = 0.8,
                    BboxX1 = 300, BboxY1 = 220, BboxX2 = 340, BboxY2 = 260,
                    CenterX = 320, CenterY = 240,
                    OffsetX = 0.0, OffsetY = 0.0,
                },
            ]
            : [],
    };
    stdout.WriteLine(frame.ToJsonLine());
    // 逐帧 flush: 整块缓冲会让帧成簇到达, 桥看到的就不是"实时流"。
    stdout.Flush();
}

if (linger)
{
    // 长驻(模拟真推理进程): 不发帧也不退出, 等父进程 Kill。
    Thread.Sleep(Timeout.Infinite);
}
return exitCode;
