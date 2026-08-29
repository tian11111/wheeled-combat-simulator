# Implementation Plan — AI Agent 无头并行快速仿真

1. **协议 DTO 与稳定指纹契约**
   - 在 `src/Sim.Protocol` 增加 `sim-batch-result-v1` 的 typed DTO；完成/失败字段、fault 结构、失败类别和 `Validate()` 规则按 `design.md` 固定。
   - 在 `src/Sim.Cli` 增加只处理稳定字段的 SHA-256 canonicalizer：事件指纹和结果指纹统一使用 invariant 数值格式、小写 hex，排除 `ReplayHeader.CreatedAt`、路径、线程和调度数据。
   - 增加 JSON round-trip、null omission、指纹固定值测试；不得把 DTO 放进 `Sim.Core`。

2. **抽取单场执行器，保持旧命令不变**
   - 从 `src/Sim.Cli/Program.cs` 抽取当前 `Options`/`RunOne` 的共享执行部分为内部 `MatchRunner`（或等价单一实现），让 `match`、`replay-record` 和 `batch` 共用同一 tick/bridge 生命周期。
   - 将场景诊断写 stderr；batch 预检只加载一次场景，按 canonical JSON 为每个 job 创建独立副本，再覆盖 seed/duration。
   - 保持旧 `match` 的中文输出、`--events`、顺序 `--seeds`、replay 文件内容和既有退出行为；先运行既有 `CliTests` 和 replay-check，再继续 batch。

3. **实现严格 batch 解析与预检**
   - 在 `src/Sim.Cli/BatchCommand.cs` 增加 `batch` 分派和 parser，支持 `--seed/--seeds`、`--scenario`、`--duration`、双方 controller、`--timeout-ms`、`--parallelism`、`--out` 和 `--help`；未知选项、缺值和非 invariant 数字均返回用法错误。
   - 固定常量：默认并行度 `min(Environment.ProcessorCount, 8)`、最大并行度 32、最大 seed 数 4096；`parallelism`、duration、timeout 必须为有限正数/合法整数。
   - 在启动任何 worker/controller 前完成 seed 列表、scenario IO + `Validate()`、输出路径和所有数值选项校验；预检失败返回 2、stderr 仅输出错误、无 JSONL。
   - `batch` 固定 JSONL，不接受 `--events`；所有人类诊断和失败摘要走 stderr。

4. **实现有界并行与实例隔离**
   - 新增内部 `BatchExecutor` 或等价 worker pool，使用 `MaxDegreeOfParallelism`，按 `inputIndex` 写入预分配结果槽位，禁止共享可变结果容器。
   - 每个 worker 调用共享 `MatchRunner`，使用独立 scenario 副本、engine、vision adapter 和每个角色的 `PythonBridge`；用 `try/finally` 确保 controller process/reader/bridge 回收。
   - 将单场未预期异常转换为该 seed 的 `failed` DTO；确保 worker 数量固定、不会死锁、不会静默漏掉 input index，并在全量结束后计算最终 exit code。
   - 增加调度 fake-worker 测试 seam（仅供 `Sim.Tests`），用 barrier/计数器验证确实存在重叠，而不是用不稳定的时间倍数断言。

5. **实现 JSONL 输出与文件原子性**
   - 所有 worker 完成后按 `inputIndex` 投影 DTO 并序列化，每行一个结果，末尾换行；stdout 不混入 `match` 的人类摘要或事件文本。
   - 无 `--out` 时一次性写 stdout；有 `--out` 时写同目录临时文件、flush/close 后原子替换目标文件，输出失败返回 1 并清理临时文件。
   - 运行失败仍输出完整 N 行（成功行 + 失败行）并返回 1；参数/场景预检失败不输出任何行并返回 2。

6. **补齐测试 fixture 与端到端验收**
   - 新增 `BatchCommandTests`：单场兼容、N seed 数量、重复 seed/inputIndex、输出顺序、parallelism=1/4 指纹一致、非法参数零输出、`--out` 文件内容。
   - 新增最小 JSONL controller fixture/测试：有效 requestId 回显、控制器异常或坏行只影响所属 job，所有进程最终退出；不把测试临时路径或 PID 写入结果指纹。
   - 运行 full test、`replay-check`、Godot parity/edit-smoke（使用已确认的 Mono console executable）以及短时 batch smoke；记录执行命令和结果到任务 implement/check 证据。

7. **更新文档并完成质量门禁**
   - 更新 `docs/CLI.md`、`README.md`、`docs/CONTROLLER_PROTOCOL.md` 和必要的 `docs/ARCHITECTURE.md`：加入 batch 调用示例、JSONL schema、并行限制、controller 每场生命周期、退出码和无 Godot 依赖说明。
   - 执行 `trellis-check` 要求的 build/test/replay/parity、`git diff --check` 和跨层数据流检查；确认 `Sim.Core` 无 IO/线程/进程新增依赖。
   - 只有质量门禁全部通过后才把任务从实现阶段推进到收尾；不修改物理参数、视觉保真度、官方场景、旧回放或 Godot UI。

## Validation Commands

```powershell
dotnet test src/Sim.Tests/Sim.Tests.csproj --no-restore -m:1 /p:UseSharedCompilation=false
dotnet run --project src/Sim.Cli --no-build -- batch --seeds 1,2,3,4 --parallelism 4 --duration 3
dotnet run --project src/Sim.Cli --no-build -- replay-check src/Sim.Tests/fixtures/restart-replay-seed42.json
git diff --check
```

Godot parity 仅作为既有回归门禁，不是 batch 的运行时依赖；命令使用已验证的
`Godot_v4.7.2-stable_mono_win64_console.exe`，并区分用户设置目录警告和 parity 断言失败。

## Risky Files

- `src/Sim.Cli/Program.cs`：命令分派和旧命令兼容性风险，必须先锁定旧输出/回放。
- `src/Sim.Cli/PythonBridge.cs`：每场进程隔离、Dispose 和 fault 语义风险，禁止共享实例。
- `src/Sim.Protocol/BatchMatchResult.cs`：agent 消费的稳定 schema，字段变更必须加性且有 round-trip 测试。
- `src/Sim.Core/MatchEngine.cs`：只允许执行器抽取所需的最小改动；不得改变 RNG draw order 或物理逻辑。
- `docs/CLI.md`、`README.md`、`docs/CONTROLLER_PROTOCOL.md`：调用方式和协议说明必须与实际命令一致。
