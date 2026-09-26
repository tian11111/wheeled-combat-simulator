# 无头批量仿真契约（sim-batch-result-v1）

> 来源：任务 08-29-ai-agent-headless-parallel-simulation。`batch` 命令是 AI agent
> 的无 Godot 快速仿真入口：有界并行、机器可读、逐位可复现。改动 `BatchCommand`/
> `BatchExecutor`/`BatchMatchResult`/`BatchFingerprint` 前必读本文件。

## 1. Scope / Trigger

- 触发：batch 命令选项、JSONL schema、指纹算法、worker 调度或控制器生命周期的
  任何改动。
- 分层：DTO 在 `src/Sim.Protocol`（纯协议，无 CLI/进程/线程类型）；编排全部在
  `src/Sim.Cli`；`src/Sim.Core` **永远不**获得并发/文件/进程能力。

## 2. Signatures

- `dotnet run --project src/Sim.Cli -- batch --seeds a,b,c [--seed N] [--scenario f]
  [--duration s] [--controller-us cmd] [--controller-them cmd] [--timeout-ms ms]
  [--parallelism k] [--out file]`
- 并行度默认 `min(Environment.ProcessorCount, 8)`，范围 1–32；seed 数 1–4096。
- 输出：每输入 seed 一行 JSON（`sim-batch-result-v1`），按 `inputIndex`（零基输入
  位置）排序；stdout 只有 JSONL，人类诊断一律 stderr；`--out` 走同目录临时文件
  + 原子替换。

## 3. Contracts（不变量）

- **确定性边界**：并行度只改变 wall-clock，不改变单场 tick 顺序、控制器逐帧请求
  顺序、动作校验/钳位、zero-action 回退或 Sim.Core 规则。同输入重复运行（排除
  运行元数据）per-seed 核心字段与指纹逐位一致。
- **实例隔离**：每场独立 scenario 副本（canonical JSON 反序列化，不共享嵌套容器）、
  独立 `MatchEngine`、独立的我方/对手 `PythonBridge` 与控制器子进程；bridge/进程
  永不跨场共享；`try/finally` 保证所有路径回收。
- **指纹**：SHA-256、UTF-8、InvariantCulture、小写 hex；事件行 `seq|tick|type|
  class|message` 逐行含尾换行；结果指纹再纳入 seed/ticks/比分/判罚/结束原因；
  排除 CreatedAt/路径/线程/调度数据。canonical 形式由固定向量测试锁定，改动即
  破坏 agent 兼容。
- **失败诚实**：失败行保留 `inputIndex/seed/status/faults` + `failure.kind/message`，
  其余字段 null，不伪造比赛结果；预检失败零输出；运行期失败仍输出完整 N 行。
- **旧命令冻结**：`match`/`replay-record`/`replay-check` 的人类输出、顺序语义与
  退出行为是回归基线（`MatchRunner` 抽取必须保持字节等价）。

## 4. Validation & Error Matrix

| 条件 | 行为 |
|---|---|
| 非法/超限 parallelism、duration、timeout、seed、未知选项、`--events`、坏场景、不可写 `--out` | worker/controller 启动前退出 2，stderr-only，零 JSONL，无残留探针文件 |
| 单场运行异常/控制器启动失败 | 该 slot 转 `failed` 行（调度缺口补 `batch_scheduler`），完整 N 行后退出 1 |
| 控制器超时/坏行/requestId 不匹配/死进程 | 沿用 zero-action/fault 语义，fault 计入所属 seed 的所属角色 |
| 全部完成 | 退出 0 |

## 5. Good/Base/Bad Cases

- Good：4 seed × parallelism 4 → 4 行按输入序，exit 0；重复 seed 以 inputIndex 区分。
- Base：parallelism=1 与 legacy `match --seeds` 的稳定字段投影一致（字节级回归锁定）。
- Bad：把并行度当作性能声明的一部分写进指纹；为吞吐复用控制器进程；预检失败
  后仍写部分 JSONL。

## 6. Tests Required

- DTO round-trip + null omission + 固定指纹向量（含 `sha256("")` 与非 ASCII 行）。
- barrier seam 证明并发重叠（峰值活跃 worker ≥ 2）+ 每 slot 恰写一次；**禁止**
  用时间比值断言并发。
- 控制器 fixture（echo/wrongid/bad/die/hang）：独立生命周期、fault 只落所属
  seed/角色、hang 路径进程回收；wrongid 类故障注入必须用"不可能别名"的 id
  （如 +1000），否则迟到回放会与下一帧期望 id 合法匹配导致计数漂移。
- 并行度 1 vs 4 同指纹；预检矩阵 exit 2 零 stdout；legacy 字节级对比。

## 7. Wrong vs Correct

### Wrong

- 在 Sim.Core 加 Task/Process/File 以"顺手"实现并行。
- 共享 bridge/引擎/场景嵌套对象；指纹混入时间戳、线程号或绝对路径。
- 用 sleep/耗时比值断言并发；静默吞掉失败 seed。

### Correct

- 编排全部在 Sim.Cli；DTO 加性演进；并发重叠用 barrier seam 证明；失败按
  seed 如实报告并计入退出码。
