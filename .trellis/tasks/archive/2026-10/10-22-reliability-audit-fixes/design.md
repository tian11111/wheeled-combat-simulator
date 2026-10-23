# Design：可靠性审计修复

## 0. 取证结论（写死在实现前，防止方向漂移）

两个回放 fixture 的实测语义**不一致**，这是缺陷的根源：

| 录制路径 | 命令记录 tick | Restart 事件 tick | 命令调用时刻的 `_events.Tick` |
| --- | --- | --- | --- |
| legacy `BuildRestartReplay`（Tick 外调用 RestartRobot） | 300 | **299** | 299（上一轮残留） |
| MuJoCo 翻覆自动重启（StepSimExt 内 FsmTick 触发） | 316 | **316** | 316（本轮 StepSimExt 已设） |

`_events.Tick` 只在 `StepSimExt` 内被设为 `TickIndex + 1`（MatchEngine.cs:659）。命令在
`StepSimExt` **外**调用时事件戳用的是上一轮残留值；**内**调用时是本轮值。回放侧
（CLI `Program.cs` / Godot `MatchSession.cs` / `ParityCheck.cs` 三处同构）在 `Tick()` 之前
`ApplyCommands`，事件戳恒为上一轮残留值——与"外调用"录制路径巧合一致（所以 legacy fixture
PASS），与"内调用"路径差 1（所以 MuJoCo FAIL）。审计 10 种子二分（含命令 6/10 FAIL、
无命令 4/10 PASS）与剥离 commands 后 46/46 PASS 的对照实验均吻合此模型。

**推演警告（实现者必读）**：三个候选方向（a 回放侧挪到 Tick() 后 / b 改录制帧 / c 只同步
`_events.Tick`）纸面推演各有破坏面：方向 a 会把 legacy fixture 的事件戳从 299 变 300 且物理
晚一帧生效；方向 c 在 MuJoCo 路径上后续 FSM 事件（MOUNT_RING 等）仍可能差 1。**实现时必须
每步实测分叉点，不许纯推演定案**；下述方案是按"统一语义 + 重录 fixture"给出的推荐，若实测
推翻，按实测走并在执行记录里留证。

## 1. 统一语义（本修复的裁决）

**命令记录 tick = N 表示：命令在 1-based tick N 的 `StepSimExt` 之前的应用点落地，
其事件戳 = N，物理效果在 tick N 的 `StepSimExt` 内生效。**

即 MuJoCo 自动重启的现状语义（内调用路径）。理由：

1. 它是引擎**自然产生**命令的唯一路径（翻覆超时自动重启在 `StepSimExt` 内）；legacy fixture
   的语义来自测试在 `Tick()` 外手工调用 `RestartRobot`，是合成路径，向自然路径对齐合理。
2. 协议铁律 `ReplayTick` 不加字段不变（只改 tick 值语义与三处消费方顺序）。
3. 两个 fixture 均为确定性自生成（`restart-replay-seed42.json` 由 `EnsureFixture()` 生成、
   `godot-parity-seed42.json` 由 `replay-record` 重录），重录合法且有 10-14 基线再生成先例。

## 2. 改动面（按文件）

### R1 回放时序

- `src/Sim.Cli/Program.cs` `RunReplayCheck` 循环：`ApplyCommands` 前把
  `engine` 的事件帧同步到当前循环帧。最小实现：给 `MatchEngine` 加一个**内部纯同步**
  方法（如 `internal void SyncEventTick()`，设 `_events.Tick = TickIndex + 1`，
  `InternalsVisibleTo` 已有先例），三处回放消费方在 `ApplyCommands` 前调用。
  - Sim.Core 铁律不破：该方法无 IO/时钟/随机，只是把既有的 tick 戳赋值复述一遍。
  - 若实测发现"命令物理生效帧"也差 1（MOUNT_RING 等后续事件分叉），则升格为：
    回放侧把 `ApplyCommands` 挪到 `Tick()` **之后**、下一轮 `Tick()` 之前（等价于
    "命令效果出现在下一 tick"），同时对 `RecordReplayTick` 的 tick 语义改为落地帧——
    以实测分叉点为准，两个方向都不许在未跑 MuJoCo 10 种子复验前定案。
- `godot/src/MatchSession.cs:220-231` 与 `godot/src/ParityCheck.cs:55-66`：与 CLI 同步改
  （三处循环结构同构，diff 应可逐行对照）。
- `src/Sim.Core/MatchEngine.cs`：`RecordReplayTick` 若需改 tick 值，保持命令/动作同一
  `ReplayTick` 行的既有合并语义（actions 与 commands 同帧记录的现状不改）。
- fixture 处理：
  - `restart-replay-seed42.json`：删除后由 `EnsureFixture()` 用**新录制序**重生成。
    `BuildRestartReplay` 的调用点从"tick==300 时先 RestartRobot 再 Tick()"改为与统一
    语义一致的序（命令应用点 = tick 300 的 StepSimExt 之前，事件戳 300）——即把
    `RestartRobot` 调用挪到第 300 轮循环的 `Tick()` 之前并显式同步事件帧（与回放侧
    同构）。断言更新为事件 tick == 命令 tick。
  - `godot-parity-seed42.json`：重录后核对唯一 diff 是 Restart 事件 tick（299→300 一类），
    逐条列明，非为绿而绿。
  - `replays/seed-42.json`（无命令）必须逐位不变——它是"命令路径修复不波及无命令回放"的
    回归锚。
- 新增回归测试 `src/Sim.Tests/ReplayCommandTickTests.cs`：
  1. MuJoCo 场景 + seed 1（审计 FAIL 种子）自录自校逐位 PASS；
  2. `restart:`（`RestartPenalty`）路径自录自校 PASS（该路径同样经 `RecordCommand`，
     审计未覆盖，必须补）；
  3. 命令 tick 与事件 tick 相等断言（新语义钉死）；
  4. 无命令回放逐位不变（seed-42 锚）。

### R2 控制器写超时

- `src/Sim.Controller/ExternalControllerBridge.cs`：`Decide()` 内写观测行改为带超时的写。
  .NET 的 `StandardInput`（StreamWriter）无原生异步超时；实现取**写线程 + 超时等待**：
  - 现状同步写换成：写任务提交到单线程写通道（或每调用 `Task.Run` + `Task.Wait(timeout)`），
    超时未完成 → `Fault("controller stdin write timeout")` + 返回 `RobotAction.Zero`，
    **不杀进程**（与应答超时语义对齐：慢/暂塞的进程在下一 tick 仍可恢复）。
  - 写任务必须幂等可重入：管道写满时阻塞的旧写任务在进程恢复后最终会完成或随进程退出
    失败——需要防止**多个阻塞写任务堆叠**（每 tick 一个新写会把内存/管道搞爆）。
    设计裁决：同一时刻只允许一个在途写；新 Decide 到来时若上一写未完成，直接按写超时
    记 fault 零动作（丢本 tick 观测帧），不再排队第二个写。语义上"进程没消化上一帧，
    本帧必然也无法消化"成立，且与真实比赛"策略掉帧"一致。
  - 读循环（`ReadLoop`）不动；`ExitDrainGrace`/requestId 匹配/垃圾行丢弃语义全部不动。
  - 观测帧丢弃时序披露：写失败的那个 tick 策略收不到观测——与"应答超时零动作"同一
    失败域，CONTROLLER_PROTOCOL.md 的"非法/超时按零动作处理"由此变为完整事实。
- 新增回归 `src/Sim.Tests/ExternalControllerBridgeStallTests.cs`（或并入现有 bridge 测试类）：
  - 假进程 = 启动一个永不读 stdin 的子进程（用 `cmd /c ping` 或 python 死循环脚本——
    测试用进程选型实现时定，要求 Windows CI 可用）；断言 Decide 在 timeout 量级内返回
    Zero、Faults 递增、无异常逃逸；连续多 tick 调用不堆叠写任务（耗时线性）。
  - 既有 `EchoController` 正常路径测试保持全绿（faults=0）。

### R3 duration 校验

- `src/Sim.Cli/Program.cs`（`Get("--duration")` 处，:116）：
  - 值存在但 `double.TryParse` 失败或解析结果非有限 → `error: invalid --duration '<raw>'`
    + exit 1（与 match 现有错误路径一致）。
  - 解析成功但换算 ticks > `MatchRunner.MaxTicks`（10000）→ `error: --duration <v> exceeds
    the 10000-tick replay/match cap (500s at 0.05s ticks)` + exit 1。
- `src/Sim.Cli/BatchCommand.cs`（:369 同型解析处）：同口径，非法 → exit 2（batch 既有约定）。
- 上限判定放 CLI 层不动 `Sim.Core`（`RunToEnd(maxTicks)` 参数化是内核 API，不收紧）。
- `docs/CLI.md`：`--duration` 行补"有限正数；上限 500s（10000 ticks），超限报错而非截断；
  非法值报错"。
- 存量核查：grep tools/ 与 controllers/ 对 `--duration` 的全部调用点，确认无 >500s 用法
  （审计已知 sensitivity/oracle ≤120s），执行记录留 grep 输出。

## 3. 依赖与顺序

R1 → R3 无耦合可并行，但提交分组要求 R1 先行（正确性优先）。R2 独立于两者。
推荐批序：批1 = R1（含 fixture 重录）→ 批2 = R2 → 批3 = R3 + 文档。每批独立
build+test 全绿再进下一批。

## 4. 回滚

- 批边界即回滚点（`git revert` 单批提交）。
- R1 若实测推翻统一语义裁决：降级为"只修 MuJoCo 路径、legacy 合成路径保持旧语义"——
  即回放侧按命令来源无法区分时，保留现状行为并在 CLI.md/ARCHITECTURE.md 如实披露
  "MuJoCo 自动重启回放与 legacy 手工重启回放语义差异"（不推荐，留作兜底）。

## 5. 兼容性

- 协议：零字段变更（铁律③保持）。
- 回放身份：MuJoCo 模型哈希/版本链路不动。
- `fidelity.json`：不晋升（本任务是缺陷修复，不是保真度证据）。
- 行为基线：MuJoCo 回放从"含命令即 FAIL"变为可复现，属缺陷修复；legacy 无命令回放
  逐位不变是硬约束。
