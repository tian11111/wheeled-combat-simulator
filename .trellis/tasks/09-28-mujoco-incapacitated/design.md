# 技术设计：翻覆态检测与停车

## 1. 检测（物理层，无状态判定）

```csharp
// IPhysicsBackend
/// <summary>车体是否倾覆（MuJoCo 有三维姿态；legacy 恒 false）。</summary>
bool IsFlipped(RobotRuntime robot);
```

- `PhysicsWorld`（legacy）：`public bool IsFlipped(RobotRuntime robot) => false;`
- `MujocoPhysicsBackend`：在 `CopyStateToRuntime()` 里已解出每车姿态四元数，顺带保存
  `upright = 1 − 2(qx² + qy²)`（旋转矩阵第三行第三列 = 车体局部 Z 轴在世界 Z 上的分量），
  `IsFlipped => upright < 0.5`（倾角 > 60°）。`ResetRobot()` 复位后把 upright 置 1。
- 选点积而不是欧拉角：避免万向节歧义；阈值 0.5 两侧余量都大——正常行驶 |roll| < 20°、
  撞坡瞬态 |pitch| ≤ 37°（实验数据），翻覆态 |roll| ≈ 180°（upright ≈ −1）。

## 2. 计时与状态机（Sim.Core）

- `FsmRuntime.FlipT` / `FsmRuntime.UprightT`：翻覆/直立持续计时（秒）。
- `FsmTickFor(r, dt)` 顶部、`CrisisGateFor` 之后：

```text
if (st.State == FsmState.Finished) return-ish（比赛结束优先）
if (!st.Armed) { FlipT = UprightT = 0 }
else if (_physics.IsFlipped(r)) { FlipT += dt; UprightT = 0 }
else { UprightT += dt; FlipT = 0 }

if (st.State != Incapacitated && FlipT >= 0.5)  → 进入：State = Incapacitated；V = W = 0；
     Log(EventKind.Incapacitated, "车体翻覆, 失去行动能力 → 停车等待裁判重启", "warn")
if (st.State == Incapacitated) {
     V = W = 0; SetAct("翻覆停车: 等待裁判重启");
     if (UprightT >= 0.5) → State = Search（Log Recover 类："车体恢复直立 → 重新搜索"）
     return;   // 不再走 CrisisGate/其它状态
}
```

- 进入判定放在 `CrisisGateFor` 之前还是之后？放在**之后**：掉台/悬空等既有危机优先处理，
  翻覆计时照常累加；只有持续翻覆才覆盖。实现上在 `FsmTickFor` 里 `CrisisGateFor(r)` 之后插入。
- 裁判重启：`ResetRobotToStart` 重建 `RecoverState`/`MountState`，本任务显式把
  `FlipT = UprightT = 0`（同处已重置 Armed/State）。
- 事件只发一次：进入分支带 `State != Incapacitated` 条件。

## 3. 协议与前端

- `EventKind.Incapacitated` 追加到枚举末尾（additive；老 replay 不受影响）。
- `FsmStateNames.ToWire(FsmState.Incapacitated) => "INCAPACITATED"`；
  `HudPanel.StateChip` 是通用 `{side}方 [{robot.State}]`，无需改动即显示。
- 不改 Snapshot 字段、不改 ReplayHeader。

## 4. 身份与兼容

| 路径 | 影响 |
| --- | --- |
| legacy 场景 | `IsFlipped` 恒 false → 轨迹逐位不变（`replay-check replays/seed-42.json` 必须 PASS） |
| MuJoCo 场景 | FSM 行为变更 → 轨迹变化（翻覆后停车）→ `CoreVersion` 升 `sim-core-1.0.3`，旧的 MuJoCo replay 身份校验失败（预期，披露） |
| rl-env / RL | 同一 FSM：策略动作在翻覆后会被强制覆盖为 0（训练语义变化，报告披露） |

## 5. 验证门

```bash
dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1
dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
git diff --check
```

新测试 `IncapacitatedTests`：

1. `UprightDot_IsOneWhenLevel_AndNegativeWhenInverted`：纯函数（四元数 → upright）覆盖 0°/90°/180° 与边界 0.5。
2. `MujocoScenario_FlipsIntoIncapacitatedAndStops`：v1 几何 MuJoCo 场景 seed 42，跑到 1200 tick；一旦出现 |roll| > 60°，断言此后 10 tick 内 `state == INCAPACITATED` 且 `V == W == 0`，且事件只发一次；若整场未翻覆则测试显式失败（记录轨迹已变，需要复核）。
3. `LegacyScenario_NeverEntersIncapacitated`：legacy 场景跑 600 tick，断言无该状态/事件。

## 6. 风险

- 阈值（0.5）与持续时间（0.5 s）为工程取值：撞坡瞬态最大 37°，翻覆 180°，余量充足；持续时间防止单帧接触抖动误判。
- 若 v1 几何 seed 42 在新 CoreVersion 下不再翻覆（不太可能，物理未变），测试 2 会显式失败而不是静默通过——这是有意的设计。
