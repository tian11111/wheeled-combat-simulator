# PRD：可靠性审计修复——MuJoCo 回放自复现 + 控制器挂死 + duration 静默截断

## 背景

2026-10-22 可靠性审计（用户视角：鲁棒性/真实性/可靠性三项评测）以 10 种子对照实验实证了三个缺陷。
本任务只修这三个，不做超出审计范围的改动。

### 实证记录（审计产出，全部可复现）

1. **MuJoCo 回放自复现失败（正确性缺陷）**：
   - 10 种子 MuJoCo `replay-record` 后立刻 `replay-check`：录制含 restart 命令的
     6/10 FAIL（seed 1/4/5/8/9/10），无命令的 4/10 PASS（seed 2/3/6/7）——二分 100% 确定。
   - 首个分歧恒为 `Restart` 事件 tick 差 1（replay tick N vs record tick N+1）。
   - **根因对照实验**：把 replay JSON 的 `commands` 剥离后重放 → 46/46 PASS
     （tmp/audit/mj-30-nocmd.json，本地不入库）。
   - 根因定位：录制侧 `RecordReplayTick`（MatchEngine.cs:1167）把命令记为 `TickIndex + 1`，
     语义是"命令产生的效果出现在下一个 Tick() 里"；而回放侧（Program.cs:257-259）在 `Tick()`
     **之前** `ApplyCommands`，`EventBus.Tick` 在 `StepSimExt` 内被设为 `TickIndex + 1`
     （MatchEngine.cs:659）——命令在回放中比录制早一帧落地。
   - 为什么 legacy fixture `restart-replay-seed42.json` PASS：其命令 tick（300/600）由测试
     **在 `Tick()` 循环内先调 `RestartRobot` 再 `Tick()`**（RestartRobotTests.cs:75-82）恰好
     复刻了回放侧顺序——测试与回放侧同序所以绿，属巧合等价，不是机制正确。
   - **影响**：MuJoCo 场次凡发生翻覆自动重启（Fsm.cs:422 翻覆 5s 超时 → `RestartRobot`），
     其回放不可复现；而 MuJoCo 10 种子中 8 个出现 Incapacitated。ARCHITECTURE.md 宣称的
     "replay-check 是确定性持续证据"在 MuJoCo 模式下大面积失效。
   - `RestartPenalty`（`restart:key:kind`，MatchEngine.cs:447）走同一条 `RecordCommand` 路径，
     疑似同样受影响（审计未单测该路径，修复时须补负例）。
   - Godot 桌面侧 `MatchSession.cs:220-231` 与 `godot/src/ParityCheck.cs:55-66` 是同样的
     "Tick 前应用命令"回放消费方——三处必须同修，否则跨端一致性破。

2. **外部控制器可挂死整场仿真（鲁棒性缺陷）**：
   - 实测：策略进程读完 5 帧后死循环（不读 stdin）→ 整场永久挂死（45s 强杀无输出）；
     每 tick sleep 0.5s → 同样挂死。
   - 正常降级路径已验证良好：进程退出 / NaN/Inf / 非 JSON 垃圾 → 400 fault + 零动作兜底 + 比赛跑完。
   - 根因：`ExternalControllerBridge.Decide()`（ExternalControllerBridge.cs:92-93）写观测行
     `_process.StandardInput.WriteLine` **无超时保护**；`--timeout-ms` 只保护"等应答"。
     管道缓冲写满后阻塞在同步写上，永远到不了应答超时判断。
   - CONTROLLER_PROTOCOL.md 宣称"非法/超时按零动作处理"——当前契约与实现不符。

3. **`--duration` 两个静默失败（CLI 契约缺陷）**：
   - `MatchRunner.MaxTicks = 10_000`（=500s）：`--duration 600/1200` 被静默截到 500s，
     `doneReason=(未结束)`，无警告 exit 0。batch 同样。CLI.md 未记载该上限。
   - `--duration abc/12x/""`：`double.TryParse` 失败静默回退默认 120s（Program.cs:116），
     与 batch 已有的"非法输入返回 2"约定不一致。

## Requirements

### R1 MuJoCo 回放自复现（正确性，P0）

- 修复后：审计 10 种子中含 restart 命令的 6 个 MuJoCo 回放 `replay-check` 全部 PASS；
  再加 `restart:`（RestartPenalty 路径）自录自校负例 PASS。
- 三处回放消费方同步：CLI Program.cs / Godot MatchSession.cs / godot ParityCheck.cs
  ——三处行为必须一致（同 tick 序），`CrossEndTests` 保持绿。
- 既有 legacy 回放（replays/seed-42.json、restart fixture）不得失配：若修复使
  fixture 重生成（`EnsureFixture()` 自生成，禁止手改），须逐条核对差异来自时序修正本身，
  非为绿而绿。
- 方向二选一（design.md 定）：a) 回放侧把命令应用挪到 `Tick()` 之后（语义 = "命令效果出现在
  下一个 tick"）；b) 录制侧把命令 tick 改为命令实际落地的 tick。**判据**：与
  `RestartRobotTests.BuildRestartReplay`（先命令后 Tick 的录制序）逐位兼容的方向优先，
  避免动 legacy fixture 基线。
- **协议铁律**：`ReplayTick` 不加字段（ARCHITECTURE.md 已写死"同证据复现凭哈希锁定的证据包 +
  场景 + 已录动作"）；修的是时序语义，不是 schema。

### R2 控制器挂死防护（鲁棒性，P0）

- 修复后：审计两个挂死用例（停读 stdin / 每 tick sleep 超时）在有限时间内完成整场，
  受害 tick 计 fault，按零动作兜底，比赛正常结束并给出可诊断输出。
- 写超时与读（应答）超时共用 `--timeout-ms` 口径；超时后该 tick 记 fault。
- 进程持续不读但窗口恢复（慢而非死）：不误杀能恢复的进程——写阻塞只是 tick 级失败，
  不终止进程，与既有"应答超时不杀进程"语义对齐。
- `Sim.Tests` 新增回归：模拟阻塞管道的假进程（容量有界的管道对/不刷新的 StreamWriter）
  验证 Decide 返回 Zero + Faults 增加 + 总耗时 ≤ 超时量级。
- 边界：只动 `Sim.Controller`（CLI/桌面共享桥）；不改协议、不改 Sim.Core。

### R3 duration 校验（CLI 契约，P1）

- `--duration` 解析失败（非有限数值）→ 报错 exit 非零，不再静默回退 120s。
- `--duration` 超过 MaxTicks 上限（500s）→ 显式报错并指明上限（不允许静默截断）；
  需要长局的用户用 batch/多段拼接自行解决（或后续另立任务提高上限）。
- `batch --duration` 同口径校验（batch 已有"非法输入返回 2"约定，对齐即可）。
- CLI.md 补 `--duration` 上限与校验行为说明。
- 存量影响评估：仓库内工具（sensitivity.py/oracle.py/param-optimization）当前用量
  ≤120s，不受影响——修复时 grep 核实一遍并在执行记录留证。

### R4 范围外（明确不做）

- MuJoCo 串行 batch 超线性退化（n=8 每场 5.5s vs n=1 1.4s；parallelism=4 可规避）：
  性能问题非正确性，且训练 factory 已有模型缓存先例——另立任务。
- 翻覆率/比分方差等真实性议题：属建模/标定域，不属本修复。
- `vision live` 等其余入口的 duration 语义：只动 match/batch 公共解析处。
- RL 训练入口 `rl-env` 的 duration 语义（独立解析路径）：不动，如受影响另行披露。

## 验收清单

- [ ] R1：审计 10 种子 MuJoCo 回放 10/10 PASS（含原 6 个 FAIL）；`restart:` 路径负例 PASS；
      legacy seed-42/restart fixture/parity 三门禁全绿；新增回归测试绿。
- [ ] R2：两个挂死用例有限时间完成 + fault 计数正确；既有 840 测试全绿（允许 1 已知跳过）；
      example_controller 正常路径行为不变（faults=0）。
- [ ] R3：三类非法 duration 全部报错非零退出；合法值行为逐位不变；CLI.md 更新。
- [ ] build 0 警告 0 错误；`dotnet test` 全绿；replay-check 三基线 PASS；
      godot `--parity-check` PASS（桌面侧命令回放同修后）。
- [ ] 提交分组：①R1 回放时序修复 + 回归测试 ②R2 桥超时修复 + 回归测试 ③R3 CLI 校验 + 文档，
      各自独立可读；不 push（按项目惯例留主会话/用户确认）。
