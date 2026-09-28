# RL v4 训练 seed 行为诊断

## Goal

解释原版 PPO 训练 seed 20260929 与 20260927/20260928 在同一 `development_v4` 场次中真实目标块得分差异，形成下一轮单变量实验的证据或明确记录证据不足。
本任务是诊断，不改变 v4 的 No-Go 结论。

## Background

- [多 seed 报告](../archive/2026-09/09-27-rl-v4-split-multiseed/report.md)：本次选取的三个诊断 checkpoint 在开发集上的目标块得分分别是 9、3、4，FSM 是 5；失败 seed 的 fault 为 0，掉台 12 次，低于 FSM 的 14 次。失败 seed 未产生正式合格候选。
- [路线总报告](../archive/2026-09/09-27-rl-v4-experiment-roadmap/report.md)：原版只达 3/5，奖励门控变体 1/3；没有候选冻结，`final_holdout_v4` 未打开。
- 现有胜出模型在开发集 9001–9004 有逐 tick 轨迹；失败模型缺同口径轨迹。本地三 seed 的 20 场开发集评测 JSON 已存在。

## Requirements

- 使用原版 PPO 已有的三个诊断 checkpoint（胜出 seed 的合格候选及两个失败 seed 的高分未合格模型），固定场景与物理/CLI 身份；保留每个 checkpoint 的训练 seed、步数、SHA-256 和诊断输出身份。
- 在相同的 `development_v4` 场次上比较胜出与失败模型的真实裁判事件、掉台、接触和块出界；优先使用现成评测 JSON，只有逐帧行为缺失时才补采诊断轨迹。
- 将真实裁判得分与轨迹采集的计数逐 seed 对照，记录任何哈希或计数不一致，禁止把不一致的轨迹用作因果证据。
- 形成可观察的行为差异、备选解释和最小单变量实验建议。无法区分原因时明确写出证据缺口。

## Acceptance Criteria

- [x] Trellis `report.md` 列出三个模型的身份、可复核证据路径、逐 seed 配对裁判事件及诊断样本范围。
- [x] 报告区分客观差异、解释假设和未验证内容；对已存评测结果做计数与身份核对。
- [x] `final_holdout_v4` 消耗仍为 0，未训练、未冻结、未改 PPO/物理/奖励代码；本任务不声称新策略通过验收。
- [x] 若建议下一轮实验，写清楚唯一变量、预期信号、停止条件以及新开发集与最终盲集的预注册要求。

## Out of Scope

- 新训练、超参数搜索、奖励改动、原生 batch、真机部署。
- 使用 `final_holdout_v4` 做诊断或选模；用已揭示的 `development_v4` 产生新一轮门槛证据。
