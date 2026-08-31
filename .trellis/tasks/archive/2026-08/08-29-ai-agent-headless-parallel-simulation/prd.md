# AI Agent 无头并行快速仿真

## Goal

为 AI agent 提供不启动 Godot 画面的可脚本化快速仿真入口，支持多个独立种子并行运行、外部控制器隔离和机器可读结果；首轮保持 Sim.Core 权威，不引入常驻服务或分布式调度。

## Confirmed Baseline

- `src/Sim.Cli/Program.cs` 已有 `match --seed/--seeds`、场景、时长、外部控制器和单帧超时选项；但 `RunMatch` 当前对种子使用顺序 `foreach`，多个种子不会并行。
- `src/Sim.Core/MatchEngine.cs` 的比赛循环是同步的；随机数、机器人、方块、事件和回放状态都属于单个 `MatchEngine` 实例，没有证据表明可以共享实例状态。
- `src/Sim.Cli/PythonBridge.cs` 每个桥接实例拥有一个外部控制器进程、stdout 读取线程、请求响应队列和 fault 计数；当前 JSONL 协议按请求逐帧交互。并行场次必须为每个场次创建独立 bridge/子进程，不能把一个 bridge 交给多个比赛共享。
- Godot 是展示壳，不是无头仿真入口；本任务的成功标准不要求启动窗口、编辑器或 Godot 进程。
- 当前 `match --seeds 1,2,3 --duration 3` 实测输出 3 条完成记录，每场 60 ticks，说明现有顺序批量行为可作为兼容基线。

## Requirements

### R1. AI agent 可直接调用

- 提供明确的无头命令入口，调用只依赖 .NET CLI/已构建的 `Sim.Cli`，不要求启动 Godot、桌面窗口、编辑器或人工交互。
- 保留单场 `match --seed` 的现有用法与结果语义；批量入口必须能表达种子列表、场景、比赛时长、双方控制器和单帧超时。
- 默认使用现有内置 FSM；指定外部控制器时沿用 JSONL stdio 协议，并把每场控制器生命周期限制在对应场次内。

### R2. 有界并行与状态隔离

- 支持显式 `parallelism`/worker 数，默认值和上限必须在 CLI 帮助及文档中说明；拒绝零值、负值、非数字和明显超过允许上限的配置。
- 并行度只限制同时运行的场次数，不改变单场 ticks、事件顺序、动作校验、超时回退或 `Sim.Core` 规则。
- 每场使用独立 `Scenario`、`MatchEngine`、随机流、事件/结果收集器，以及需要时独立的我方/对手 `PythonBridge` 和子进程；禁止共享可变 adapter、控制器进程或跨场次请求队列。
- 一个场次的控制器退出、超时、坏行或启动失败不得把其他场次的结果/请求串线；每场 fault 必须独立计数并出现在该场结果中。
- 采用有界 worker pool 或等价机制，不能为每个输入 seed 无限制创建线程/进程；正常结束时必须回收 worker、bridge 和控制器子进程。

### R3. 机器可读且可复现的输出

- 为 agent 提供 JSON 或 JSONL 输出模式；每个 seed 至少包含 `seed`、ticks、双方得分、判罚、结束原因、双方 fault，以及稳定的事件/结果指纹。
- 并行执行可以乱序完成，但对外输出必须按输入 seed 顺序稳定排列，或为每条记录提供明确的输入序号且文档固定其消费方式；不能让 stdout 出现多 worker 交错的人类事件日志。
- JSON 输出中不得把生成时间、线程调度顺序或机器路径作为确定性结果的一部分；同一场景、同一种子、同一控制器输入重复运行时，核心结果和指纹必须保持一致。
- 人类可读模式继续支持现有摘要；机器模式不得依赖解析中文展示文本。参数校验或整体批量失败时不写出伪造的部分成功报告，并遵循项目既有 CLI 退出码约定。

### R4. 控制器与错误边界

- 明确外部控制器是“每场启动”还是可安全复用的资源；首轮按每场独立进程设计，不新增隐式多路复用协议。
- 控制器响应超时、requestId 不匹配、坏 JSONL、死进程继续按现有 zero-action/fault 语义处理；失败详情必须能定位到 seed 和角色。
- 场景文件、种子、duration、timeout 和并行度在启动 worker 前统一校验；非法输入返回用法/校验错误，不留下未回收的控制器进程。
- 单场运行异常必须形成可消费的失败记录或明确的整体失败结果，不能静默丢 seed，也不能让一个 worker 的异常导致死锁。

### R5. 性能证据与兼容性

- 增加自动化测试证明多个 seed 的结果数量、输入顺序、隔离性和 deterministic fingerprint；至少覆盖内置 FSM 与一个外部控制器/故障控制器场景。
- 增加可复现的短时基准或并发观测证据，证明 `parallelism > 1` 确实允许场次重叠；性能验收以相同机器/参数下的相对证据为准，不承诺固定倍数。
- 现有单场、顺序 `--seeds`、`replay-record/replay-check`、核心测试和 Godot parity 行为不得回归；新路径不能把 UI 或视觉回放逻辑带入核心。
- 更新 `docs/CLI.md`、控制器协议/README 中的调用示例，说明无窗口调用、并行度、输出 schema、控制器生命周期、退出码和资源限制。

## Out of Scope

- 不修改 Godot UI、相机、渲染或桌面启动流程；不以 Godot headless 作为 AI agent 的运行时依赖。
- 不改写 `Sim.Core` 物理/规则模型，不把并发调度、文件 IO、子进程管理塞进核心层。
- 不做跨机器分布式调度、队列服务、数据库、云端任务编排或训练框架集成。
- 不做常驻 HTTP/JSON-RPC daemon、模型服务复用或跨场次共享 Python 进程，除非开放决策明确改范围。
- 不在本任务内做真机物理标定、视觉真值晋升或 UI 优化。

## Acceptance Criteria

- [ ] 从仓库根目录执行无头批量命令时不创建 Godot 窗口、不依赖 Godot 资源，并能在 N 个输入 seed 下返回 N 条机器可读结果。
- [ ] `parallelism=1` 与现有顺序基线在相同场景/种子/控制器下得到相同的分数、ticks、结束原因和稳定指纹；旧单场命令继续可用。
- [ ] `parallelism=k`（k>1）在不超过上限的前提下运行多个独立场次；结果按约定稳定消费，测试证明至少两个场次在时间上重叠且无状态/请求串线。
- [ ] 使用外部控制器时每场的 bridge/子进程独立创建和回收；一个故障控制器只影响所属 seed，fault 与角色信息可在该 seed 记录中读取。
- [ ] 非法 seed、场景、duration、timeout 或 parallelism 在 worker 启动前失败，返回明确非零退出码且不留下部分成功 JSON/JSONL 产物或孤儿进程。
- [ ] 同一输入重复执行，排除允许变化的运行元数据后，机器结果中的 per-seed 核心字段与指纹一致；并发调度顺序不改变结果。
- [ ] 自动化测试、`replay-check`、Godot/Sim.Core parity 和短时并发基准均通过；文档包含一条可复制的 AI agent 调用示例与完整输出字段说明。

## Open Decision

- 推荐首轮采用“新增/扩展 CLI 批量入口 + `--parallelism` + JSONL”方案：AI agent 可直接通过 shell 调用，生命周期简单，且保留现有 `match` 兼容性。请确认是否接受该方案，还是必须做常驻 JSON-RPC 服务；后者可减少重复进程启动开销，但会增加端口、会话生命周期、状态清理和并发隔离范围。

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- 这是复杂任务；开放决策确认后再补充 `design.md` 与 `implement.md`，然后才允许 `task.py start` 进入实现阶段。
- 本任务当前保持 `planning`；创建任务和编写 PRD 不等于授权修改产品代码。
