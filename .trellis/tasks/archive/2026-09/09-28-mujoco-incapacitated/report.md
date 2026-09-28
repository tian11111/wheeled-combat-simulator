# 验收报告：MuJoCo 倾覆门控（翻覆后停车等待重启）

## 结论

MuJoCo 下的翻覆不再被无视：车体倾覆持续 0.5 s 即进入新的 `INCAPACITATED` 状态——停车、发一次性事件、HUD 自动显示 `我方 [INCAPACITATED]`，等待裁判按 R/T 重启（对方 +3）；被撞回直立则自动回到 SEARCH。提交 `9b12367`。

- `dotnet test`：**405/405**（393 基线 + 9 条 v2 几何 + 3 条本任务新增）。
- legacy 逐位不变：`replay-check replays/seed-42.json` 与 `replays/godot-parity-seed42.json` 双双 PASS（4:49 / 752 events）。
- Godot 工程构建 0 错误；`git diff --check` 干净。

## 行为证据

| 证据 | 内容 |
| --- | --- |
| `IncapacitatedTests.UprightProjection_MatchesLevelSideAndInvertedPoses` | 姿态投影：直立 = 1.0、侧躺 90° = 0.0、底朝天 180° = −1.0；阈值边界 0.5 不算倾覆、0.4999 算 |
| `IncapacitatedTests.MujocoMatch_FlippedRobotStopsInIncapacitatedState` | v1 几何 seed 42：翻覆（\|roll\|>60°）后 **≤10 tick** 进入 `INCAPACITATED`，状态期间 `V = W = 0`，事件发出 1–2 次（只在进入时） |
| `IncapacitatedTests.LegacyMatch_NeverEntersIncapacitated` | legacy 场景跑满 2400 tick，双方从不进入该状态、无该事件（逐位不变的行为证据） |
| 两份 `replay-check` | legacy 事件指纹与比分逐位复现（CoreVersion 门只对 MuJoCo 生效） |

## 实现要点

- `IPhysicsBackend.IsFlipped(robot)`：即时判定，无状态。legacy 恒 `false`；MuJoCo 用
  `upright = 1 − 2(qx²+qy²)`（车体局部 Z 轴在世界 Z 上的分量）< 0.5（倾角 > 60°）。
  阈值两侧余量充足：实测正常行驶 |roll| < 20°、撞坡瞬态 |pitch| ≤ 37°，翻覆态 ≈ −1。
- 计时与状态：`FsmRuntime.FlipT/UprightT`；`FsmTickFor` 里 `CrisisGateFor` 之后的 `FlippedGateFor`
  在 `FINISHED`/未发令 时清计时，持续倾覆 0.5 s 进入 `Incapacitated`（`V=W=0` + `EventKind.Incapacitated`），
  恢复直立 0.5 s 回 `SEARCH`（记 `EventKind.Recover`）。裁判重启清计时并回 `MOUNT_RING`。
- 协议：`EventKind.Incapacitated` 追加在枚举末尾（additive）；`FsmStateNames.ToWire` → `"INCAPACITATED"`
  （HudPanel 的 `[state]` 通用显示自动生效，未改前端代码）。
- 身份：`CoreVersion` `sim-core-1.0.2` → `sim-core-1.0.3`；`MujocoIntegrationTests` 的版本门控断言
  与 `fixtures/restart-replay-seed42.json` 同步更新。

## 身份影响清单（披露）

- **受影响的既有 MuJoCo replay（1 份）**：`.sim_runs/score-block-v3-baseline-20260926b-codex/replay-seed-42-mujoco-v3.json`
  ——FSM 行为变更使 MuJoCo 轨迹不再可复现，回放身份校验（比较 `CoreVersion`）会拒绝它。这是**预期**：
  不得为了让旧 replay 通过而放宽校验。文件保留作为历史证据。
- **legacy replay 全部不受影响**：`replays/*.json` 均无 `physicsBackend`（legacy 不比较 CoreVersion），
  两份校验逐位 PASS。

## RL 语义变化（重要）

`rl-env` 与我方策略路径共用同一 FSM：**翻覆后策略动作会被强制覆盖为 `V = W = 0`**（本门控在
`FsmTickFor` 内，先于策略动作落地），而此前翻覆后策略仍可输出（物理上无效但动作被记录）。
影响：训练中"翻车"的负信号更干净（不再有底朝天苟活的样本），但既有训练数据的分布解释随之变化；
v2 场景下是否更容易/更难翻覆需要重新测量。既有 checkpoint 在 v2 几何下本就作废（几何身份变更），
本轮不涉及重训。

## 未覆盖

- **半悬/卡死**：真车在 6 cm 台沿上"能上但不稳"（末态半悬、前轮压在倒角上）这一状态**不判为失去行动能力**
  ——它与现有 `RECOVER`/stall 机制重叠且阈值模糊（实测半悬 |pitch| ≈ 16°，与撞坡瞬态同一量级）。
  列入后续任务。
- **v2 真车几何下的实际翻覆率**未测（本轮行为证据用 v1 几何 seed 42，因为它是已知会翻覆的确定性轨迹）。
- Godot 桌面端未做人工验证（仅构建 + 无头 parity 的既有回归）。

## 环境

便携 SDK `C:\Users\Neco\AppData\Local\Programs\robot-simulator-dotnet`（`DOTNET_ROOT` +
`NUGET_PACKAGES=C:/Users/Neco/.nuget/packages`）；命令在 `D:\project\robot-simulator`（分支
`test/score-block-ppo-checkpoint-round`）执行。
