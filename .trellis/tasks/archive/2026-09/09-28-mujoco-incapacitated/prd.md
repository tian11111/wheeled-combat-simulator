# MuJoCo 翻覆态处理：失去行动能力时停车等待重启

## Goal

MuJoCo 物理下车会翻覆（实测：v1 几何 seed 42 我方 tick 509 roll→−180°，此后 94 s 底朝天"苟活"；v2 真车几何在 6 cm 台沿下"能上但不稳"）。legacy 2D 没有 roll/pitch 自由度，所以 FSM、裁判、HUD 都没有"翻覆"概念。本任务补上：翻覆持续时判定为失去行动能力 → 停车 + 事件 + HUD 显示，等待裁判重启（现有 R/T，对方 +3）；车被撞回直立则自动恢复。

## Requirements

- R1 **检测（物理层）**：`IPhysicsBackend` 暴露即时倾覆判定——车体 up 轴与世界 Z 的点积 < 0.5（倾角 > 60°）。legacy 恒 false（无该自由度）；MuJoCo 用当前姿态四元数。
- R2 **FSM**：翻覆持续 ≥ 0.5 s（10 tick）进入新状态 `FsmState.Incapacitated`（V=W=0、一次性事件、动作文案"翻覆停车: 等待裁判重启"）；恢复直立持续 ≥ 0.5 s → 回 SEARCH；裁判重启（`ResetRobotToStart` → MountRing）自动脱离；`FINISHED` 优先不被覆盖。
- R3 **协议**：新增 `EventKind.Incapacitated`（枚举末尾追加，additive）；`FsmStateNames.ToWire` → `"INCAPACITATED"`（HUD 的 `[state]` 普通显示自动生效，无需改 HudPanel）。
- R4 **身份**：`CoreVersion` `sim-core-1.0.2` → `sim-core-1.0.3`（FSM 行为变更会改变 MuJoCo 轨迹；MuJoCo 回放身份校验比较该字段）。legacy replay 必须仍逐位 PASS；既有两份 MuJoCo replay 身份失效，属预期并在 report 披露。
- R5 **测试**：倾覆判定单测（四元数 → 判定，覆盖直立/侧躺/底朝天/边界）；MuJoCo 集成测试（v1 几何 seed 42：出现翻覆后 10 tick 内 `state==INCAPACITATED` 且 V=W=0，此后不再给驱动）；legacy 场景断言该状态永不出现；全量回归。
- R6 **披露**：RL 语义变化（翻覆后策略动作被强制停车）写入 report。

## Acceptance Criteria

- [ ] `dotnet test` 全量通过（含新测试）；`replay-check replays/seed-42.json` 逐位 PASS。
- [ ] legacy 场景跑完整场不出现 `INCAPACITATED` 事件（逐位不变的直接证据）。
- [ ] MuJoCo 场景翻覆后：状态为 INCAPACITATED、V=W=0、事件只发一次。
- [ ] CoreVersion 已升为 1.0.3，report 披露受影响的既有 MuJoCo replay 清单。
- [ ] `git diff --check` 干净；提交为独立批次。

## Notes

- 不做"半悬/卡死"判定：与现有 RECOVER/stall 机制重叠且阈值模糊，列入 report 的未覆盖项。
- v2 真车几何的登台困难是用户已接受的真实约束（见 `archive/2026-09/09-28-mujoco-vehicle-mesh/report.md`），本任务不碰物理参数。
