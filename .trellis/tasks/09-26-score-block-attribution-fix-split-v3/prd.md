# SCORE_BLOCK 归属判定修复与 split v3 预注册

## Goal

按诊断任务 `09-26-score-block-failure-diagnosis` 的结论，**只改一处明确问题**：
修 `Sim.Core` 的块出界归属判定（`Physics.FinalizeBlockContacts` 用"接触记录条数"而非
"不同角色数"判定 `"simultaneous"`），并据此**预注册全新的开发集与最终留出集**，
把已揭示的 4001–4010、5001–5020、6001–6050 全部降级为"仅分析"。

本任务**不训练、不跑新一轮盲验门槛判定**（那需要先冻结候选，由后续任务承接）。

## Confirmed Baseline（诊断任务已证事实）

- 两轮盲验合计 14 次 `BlockOff`，**14 次全部**为单台机器人多点接触被误判为 `"simultaneous"`；
  真·双方争抢 **0** 次。27 次 `BlockScore` 全部为 `single_contact_*`（max 接触时刻恰好 1 条记录）。
- 缺陷行：`src/Sim.Core/Physics.cs` `FinalizeBlockContacts`：
  `o.LastContactRole = last.Count == 1 ? last[0].Role : "simultaneous";`
- 旁证：seed 6037 我方距块 0.218 m、对手 1.782 m；seed 6048 我方 0.223 m、对手 2.164 m；
  seed 4005（FSM）我方 0.353 m、对手 2.431 m —— 均被判 `"simultaneous"`。
- 归属修复投影（诊断报告 §2.5）：6001–6050 策略锁定目标得分 5 → 9，FSM 8 → 8；
  4001–4010 策略 1 → 1，FSM 0 → 1；**掉台不变**，故门槛的掉台条件仍失败。
- 该缺陷同时污染训练奖励：`RlEnvCommand` 在 `targetLost && targetBlockOff` 时给 −0.5，
  而成功推出界本应 +1.0（单事件 1.5 的方向性错误）。
- 既有 split（v2）：训练池 42 + 1000–1999 + 20260925；`legacy_development` 3001–3010；
  `legacy_final_holdout` 4001–4010；`development_v2` 5001–5020；`final_holdout_v2` 6001–6050。

## Requirements

### R1. 修复归属判定（唯一的行为改动）

`FinalizeBlockContacts` 改为按 max 接触时刻处的**不同角色数**判定：
恰好一个不同角色 → 该角色；多于一个 → `"simultaneous"`。不得改动其他物理/裁判/计分语义。

### R2. Sim.Core 回归测试

新增覆盖以下三类的测试：

1. 单机器人单条记录 → 该角色（既有行为，防回归）；
2. **单机器人多条记录（同角色）→ 该角色**（本次修复点）；
3. 两个角色并列在 max 接触时刻 → `"simultaneous"`（真争抢，保持不计分）。

### R3. 用已揭示集验证真实增量

用**已揭示**的 6001–6050 / 4001–4010 重放冻结模型（仅分析，不作门槛证据），
量出修复后的 `locked_target_us_block_scores` / `unowned_block_offs`，
并与诊断投影（策略 5 → 9、无归属 10 → 0）逐项对照。**不一致必须如实报告并排查。**

### R4. split v3 预注册

- `SPLIT_VERSION` → `"score-block-split-v3"`。
- 新增 `development_v3` = 7001–7020（20）、`final_holdout_v3` = 8001–8050（50）；
  二者与训练池（42、1000–1999、20260925）及 3001–3010、4001–4010、5001–5020、6001–6050 全部互斥。
- `development_v3` 成为默认 split；`final_holdout_v3` 成为唯一盲验 split。
- `legacy_development` / `legacy_final_holdout` / `development_v2` / `final_holdout_v2`
  保留可访问（历史与对照），但语义降级为"已揭示、仅分析"，并纳入 `REVEALED_SEEDS`。

### R5. 已揭示留出集不得再当盲验

`evaluate.py`：对已揭示的留出集（`legacy_final_holdout`、`final_holdout_v2`）要求显式
`--analysis-only`，并在输出中写 `gate_evidence_eligible: false` 与 `analysis_only: true`；
`--select-candidate`/`--freeze`/`--require-freeze` 的 split 作用域全部迁到 v3。

### R6. 纪律与回归

- 不训练、不改奖励/观测常量、不放宽门槛、不触碰 `fidelity.json`、不改默认 FSM。
- 不把已揭示 seed 集重新当作盲验。
- 必须通过：`dotnet build`、`dotnet test RobotSimulator.sln -m:1 --no-restore`、
  既有 `replays/*.json` 的 `replay-check`（逐位 PASS）、`selftest.py` 全绿。

## Acceptance Criteria

- [ ] AC1 `Physics.FinalizeBlockContacts` 按不同角色数判定；无其他 `Sim.Core` 语义改动。
- [ ] AC2 新增 Sim.Core 测试覆盖 R2 的三类情形；`dotnet build` 0 错误；`dotnet test` 全绿。
- [ ] AC3 6 份 legacy `replays/*.json` 仍逐位 PASS。
- [ ] AC4 已揭示集重放结果与诊断投影逐项对照，差异已解释（或如实报告不一致）。
- [ ] AC5 `splits.py` v3 注册完成且互斥校验通过；`development_v3` 默认；
      `final_holdout_v3` 唯一盲验；已揭示集进入 `REVEALED_SEEDS`。
- [ ] AC6 `evaluate.py` 对已揭示留出集要求 `--analysis-only` 且 `gate_evidence_eligible=false`；
      freeze/require-freeze/select-candidate 作用域为 v3。
- [ ] AC7 `selftest.py` 全绿（期望值更新到 v3）；README 的 split 表与命令更新。
- [ ] AC8 报告：修复前后对照、回归结果、新 split 表、限制与诚实边界、
      以及"本轮只改这一处、未训练、未跑新盲验"的明确声明。

## Out of Scope

- 训练新模型、跑新一轮盲验门槛判定、宣布任何 AC4/门槛结果。
- 掉台侧改动（奖励/观测/行为纪律）——诊断已判定为独立的第二个问题，须单独任务。
- 改评测门槛、改 11 维观测、改奖励常量、改默认 FSM、`fidelity.json` 晋升。

## Notes

- 诊断报告明确：单改这一处**不足**以让门槛通过（掉台 28 > 21 不变）。
  本任务的定位是"修正确缺陷 + 让下一轮实验可归因"，不是"过门槛"。
- legacy 2D 路径每机器人每 tick 恰好 1 条接触记录，`Distinct()` 在其上是空操作，
  因此既有回放应保持逐位一致；MuJoCo 侧归属结论改变属**修正**。
