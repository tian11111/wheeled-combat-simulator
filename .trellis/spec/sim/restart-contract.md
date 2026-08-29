# 真实重启契约（restart-v1）

> 来源：任务 08-28-godot-camera-gray-restart。R（己方）/ T（对手）触发的
> "真实重启"与旧 `restart:<role>:<kind>` 罚分命令语义不同；本文件是两者的
> 可执行契约。改动 `RestartRobot`、`restart_robot:` 解码或 `EventKind.Restart`
> 之前先读本文件。

## 1. Scope / Trigger

- 触发：任何涉及 `MatchEngine.RestartRobot`、`restart_robot:` 命令解码、
  `EventKind.Restart` 事件或 Godot R/T 按键路由的改动。
- 跨层契约：Sim.Core（权威）→ `src/Sim.Cli/Program.cs` replay-check、
  `godot/src/ParityCheck.cs`、`godot/src/MatchSession.cs` 三处解码必须保持
  同一语义（同一循环位置、同一引擎调用顺序）。

## 2. Signatures

- 引擎：`bool MatchEngine.RestartRobot(string role)`（`src/Sim.Core/MatchEngine.cs`）。
  返回 `false` 表示当前 phase 拒绝（零副作用）；未知 role 抛 `ArgumentException`。
- 命令行（追加式，只出现在录制命令流中）：`restart_robot:<role>`，
  `role ∈ RoleNames`（`us` / `them`）。
- 事件：`EventKind.Restart`，category `"score"`，消息前缀 `[referee] 真实重启`，
  payload `{ role, points: 3, scorer: <对手 role> }`。

## 3. Contracts（语义不变量）

- 对手总分恰好 +3 一次（2026 规则: 举手示意并经裁判同意的重启 = 对方 +3；+4 是
  "未经同意"的违规判罚，只存在于 legacy `restart:<role>:<kind>` 罚分命令，真实重启
  不使用）；被重启方 penalty 总数 +3（`_penaltyUs`/`_penaltyThem`）。
- 比赛计时、另一台机器人、场上方块、既有事件序列保持不变；目标 FSM 的
  `SimT`/`Timer` 设为当前比赛时间，比赛不延长、按原计划结束。
- 目标机器人：场局部出生点经 `FieldModel.Transform` 映射回位（禁止手写旋转）；
  速度、指令队列、堵转/铲斗/掉台标志、传感器状态全部清零；armed、位于
  `MountRing`、重新走 mount/recovery 流程；已完赛机器人在比赛进行中可复活。
- 仅 `Running`/`Paused` 合法；`Prep`/`Ready`/`Finished` 拒绝且不产生
  score/event/replay 变化。
- 旧 `restart:<role>:<kind>` 命令与 `RestartPenalty` 保持逐位兼容：只罚分、
  不回位、不清瞬态，解码路径不得重解释旧回放字节。

## 4. Validation & Error Matrix

| 条件 | 行为 |
|---|---|
| `role == null` | `ArgumentNullException` |
| role 未知（引擎层） | `ArgumentException` |
| `restart_robot:<unknown>`（CLI 解码） | warning + 忽略，不改状态 |
| `restart_robot:<unknown>`（Godot 解码） | 静默忽略（录制时已校验；损坏文件经事件指纹 parity 大声失败） |
| phase ∉ {Running, Paused} | 返回 `false`，零副作用 |

## 5. Good/Base/Bad Cases

- Good：Running 中按 R → 己方回出生点、对方 +3 一次、`EventKind.Restart` 与
  `restart_robot:us` 入流、回放逐位复现。
- Base：Paused 中按 T → 同上；Finished 的目标在比赛活跃时复活且不再延长时钟。
- Bad：Prep/Ready/Finished 阶段调用 → `false` 且无任何变化；把旧 `restart:`
  命令重解释为真实重启 → 禁止（破坏逐位兼容）。

## 6. Tests Required

- `src/Sim.Tests/RestartRobotTests.cs`：相位门控、恰好一次 +3、经
  `FieldTransform` 回位、瞬态/FSM 清理、计时/他方/方块保持、复活、同调度
  逐位一致、fixture 重生成字节一致、Godot/CLI parity、旧命令兼容
  （`LegacyCommandReplay_StillVerifies_BitForBit`、`OldOfficialFixture_StillParityVerifies`）。
- fixture：`src/Sim.Tests/fixtures/restart-replay-seed42.json`（测试内重生成、
  断言字节一致；禁止手改，见 sim/index.md 基线规则）。

## 7. Wrong vs Correct

### Wrong

- 在 Godot/渲染层写回分数或位置（渲染不复刻规则）。
- 把真实重启塞进 `RestartPenalty` 或重解释旧命令字节。
- 改旧 fixture / 基线比分来掩盖 parity 差异。

### Correct

- 权威状态只在 Sim.Core；新语义走追加命令 + 既有 `EventKind.Restart` 成员；
  三处解码同步修改并各自带测试；R/T 在 Main 层先做 live-phase 预检给提示，
  引擎层再权威校验。
