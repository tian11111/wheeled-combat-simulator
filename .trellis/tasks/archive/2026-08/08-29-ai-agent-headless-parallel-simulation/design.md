# Technical Design — AI Agent 无头并行快速仿真

## 已确认的产品决策

- 新增 `batch` CLI 子命令，专门面向 agent；现有 `match` 保留原来的单场和顺序批量语义，不把并行行为悄悄塞进旧命令。
- `batch` 固定输出 `JSONL`：一行对应一个输入 seed，完成后才按输入顺序写出；不在 stdout 输出人类事件日志。
- 并发是单机有界 worker pool。默认并行度为 `min(Environment.ProcessorCount, 8)`，允许范围 `1..32`；seed 数量限制为 `1..4096`。超过限制在启动 worker 前返回用法错误。
- 外部控制器按每个比赛实例独立启动和回收；不复用 Python 进程，不引入多路复用协议。
- 不启动 Godot，不引入常驻 HTTP/JSON-RPC 服务，不跨机器调度。文件/进程/并发编排全部留在 `Sim.Cli`，`Sim.Core` 继续只负责确定性仿真。

## 用户入口与输出契约

```text
dotnet run --project src/Sim.Cli -- batch \
  --seeds 1,2,3,4 --parallelism 4 --duration 3

dotnet run --project src/Sim.Cli -- batch \
  --seeds 1,2,3,4 --parallelism 4 \
  --controller-us "python controllers/example_controller.py" \
  --out artifacts/batch.jsonl
```

`batch` 支持 `--seed N`（单值别名）、`--seeds a,b,c`、`--scenario`、`--duration`、
`--controller-us`、`--controller-them`、`--timeout-ms`、`--parallelism` 和可选的
`--out`。它不接受 `--events`，避免污染机器输出；需要完整事件文本时继续使用
`match --events` 或 `replay-record`。

每行使用 `sim-batch-result-v1`，由 `Sim.Protocol` 中的 typed DTO 序列化，形状固定为：

```json
{"schemaVersion":"sim-batch-result-v1","inputIndex":0,"seed":1,"status":"completed","scenarioId":"wushu-ring-2026","ticks":60,"scores":{"us":0,"them":0},"penalties":{"us":0,"them":0},"doneReason":"比赛时间结束","faults":{"us":0,"them":0},"eventCount":0,"eventFingerprint":"<sha256>","resultFingerprint":"<sha256>"}
```

- `inputIndex` 是输入列表中的零基位置；重复 seed 允许存在，并通过该字段区分。
- `status` 为 `completed` 或 `failed`。失败行保留 `inputIndex`、`seed`、`status`、`faults`，并填充 `failure.kind/message`；未完成的 ticks、scores、penalties、doneReason 和指纹为 null，不伪造比赛结果。
- `eventFingerprint` 是按事件顺序拼接现有 `seq|tick|type|class|message` 行后计算的 SHA-256；`resultFingerprint` 再把 seed、ticks、比分、判罚、结束原因和事件行一起纳入。两者均使用 UTF-8、InvariantCulture、小写十六进制，不包含时间、线程号、绝对路径或调度顺序。
- `--out` 使用临时文件加同目录原子替换；无 `--out` 时所有 JSONL 在 worker 全部结束后一次写到 stdout。这样参数预检失败不会留下部分产物，worker 失败也能返回完整的 N 行失败/成功记录。

退出码固定为：`0` = 所有场次完成；`1` = 至少一条场次失败或最终输出失败；`2` = 参数/场景预检失败。预检错误只写 stderr，不写 JSONL。

## 分层架构与数据流

```text
Program.Main("batch")
       │
       ▼
BatchCommand.Parse + preflight
       │  统一解析 seeds / scenario / duration / timeout / parallelism
       │  读取并校验 scenario，一次生成 canonical payload
       ▼
BatchExecutor (bounded parallelism)
       │  inputIndex 保留输入位置，结果写入预分配 slots[index]
       ├── worker 0: 独立 Scenario → MatchEngine → PythonBridge(s) → MatchExecution
       ├── worker 1: 独立 Scenario → MatchEngine → PythonBridge(s) → MatchExecution
       └── worker k: 独立 Scenario → MatchEngine → PythonBridge(s) → MatchExecution
       ▼
BatchMatchResult projection → slots 按 inputIndex 排序 → JSONL writer
```

- `src/Sim.Protocol` 新增 `BatchMatchResult` 及其失败/控制器 fault 子结构，只描述稳定的跨进程输出，不引用 CLI、Process 或线程类型。
- `src/Sim.Cli` 负责严格参数解析、场景文件 IO、worker 调度、控制器进程、指纹投影和原子输出。
- `src/Sim.Core` 不新增并发、文件或进程能力。每个 `MatchEngine` 继续拥有自己的 RNG、机器人、方块、事件、FSM 和 vision adapter。
- `Godot` 不在这条数据流中出现；batch 的成功验收必须能在没有 Godot 安装/窗口的情况下完成。

## 场景与实例隔离

预检阶段加载场景并调用现有 `Scenario.Validate()`。worker 不再次读取同一个文件；它从预检得到的 canonical JSON payload 反序列化自己的场景副本，再设置该 job 的 seed 和可选 duration。这样 `Scenario` 中的 nested dictionary/list 不会被 worker 共享。

每个 job 创建新的 `MatchEngine`。现有引擎构造函数已经把运行时机器人、方块、事件、物理世界、传感器和 FSM 建立在实例字段中；worker 不共享 engine、vision adapter、事件列表或随机流。所有外部角色各自创建一个 `PythonBridge`，其子进程、stdout reader、响应队列和 fault 计数只属于这个 job。

`RunOne` 的核心 tick 逻辑抽到可复用的 `MatchRunner` 后，旧 `match`、`replay-record` 和新 `batch` 共用同一条执行路径。旧命令继续使用人类可读投影；batch 只把同一执行结果投影为稳定 DTO，不能复制一份可能漂移的物理循环。

## 调度、失败和回收

- 使用 `Parallel.For`/等价的 `MaxDegreeOfParallelism` worker pool；不为每个 seed 无限制创建专用线程。结果数组按 input index 预分配，worker 只写自己的槽位。
- worker 内用 `try/finally` 包住控制器创建和比赛执行，确保 `PythonBridge.Dispose()` 在正常完成、控制器启动失败、异常和取消路径都执行。
- 控制器超时、坏行、requestId 不匹配和死进程沿用 `PythonBridge` 当前的 zero-action/fault 语义；其 fault 计数挂在该 seed 的结果上。
- 单个 worker 的未预期异常转为该 job 的 `failed` 行，记录稳定的错误类别和可读信息；其他 worker 继续完成。若调度器本身异常导致某个 slot 没有结果，则补一条 `batch_scheduler` 失败行，禁止静默丢 seed。
- 所有 worker 结束后才决定退出码和输出。非法参数、场景读取/校验失败发生在 worker 之前，直接返回 2；运行时失败返回完整 JSONL 后返回 1。
- batch stdout 永远只有 JSONL；诊断信息和错误写 stderr。`--out` 的父目录按需创建，临时文件与目标文件位于同一目录，避免跨卷替换失败。

## 确定性边界

- `ReplayHeader.CreatedAt` 等 volatile 元数据不进入 batch DTO；不能直接序列化完整 `MatchResult.Header`。
- 指纹字段只使用现有事件内容和数值结果的 invariant canonical form。重复执行同一场景、seed、控制器输入时，结果指纹不受 worker 完成顺序影响。
- 并行度只改变 wall-clock 调度，不改变单场 tick 顺序、控制器逐帧请求顺序、动作校验/钳位、zero-action 回退或 Sim.Core 规则。
- 如果控制器自身依赖真实时间或非确定性，batch 只能如实报告其 fault/结果，不宣称外部控制器行为具备核心级确定性。

## 测试策略

- 协议测试：`BatchMatchResult` JSON round-trip、完成/失败两种形状、字段命名和 null omission。
- CLI 测试：单 seed batch、多个 seed、重复 seed、`parallelism=1` 与旧 `match --seeds` 的稳定字段投影一致、结果按输入顺序输出、stdout 无人类日志。
- 调度测试：通过 `BatchExecutor` 的内部测试 seam 使用带屏障的 fake worker，断言 `parallelism=2` 的峰值活跃 worker 至少为 2，且每个 input slot 恰好写一次；不使用易抖动的固定耗时阈值证明并发。
- 控制器测试：使用最小 JSONL fixture 验证每个 job 独立启动/回收、requestId 仍匹配，另用故障 fixture 验证 fault 只落在所属结果。
- 预检测试：非法 seed、空列表、duration/timeout、parallelism、未知选项、坏场景和不可写输出路径均在启动 controller 前返回 2 且不写部分 JSONL。
- 确定性测试：同一输入分别以 `parallelism=1` 和 `parallelism=4` 执行两次，比较 per-seed 核心字段和指纹；不比较生成时间或完整 header。
- 回归：全量 `Sim.Tests`、基线 `replay-check`、Godot/Sim.Core parity 和现有 vision/calibration 命令必须保持通过。

## 兼容与回滚

- 第一处回滚点是 `Sim.Protocol` batch DTO 与 `BatchCommand` 新命令；删除 batch 分派即可恢复原有命令。
- 第二处回滚点是 `MatchRunner` 抽取；若重构影响 replay，可恢复旧 `Program.RunOne`，batch 暂停，不修改核心物理。
- 不修改旧回放 fixture、官方场景、`fidelity.json` 或 Godot 工程来掩盖回归。
