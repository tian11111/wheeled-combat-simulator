# RL v4 盲集防复用与 split 预注册

## Goal

将已经揭示的 v3 盲集永久降为仅分析，并在唯一 split 注册源中预注册与训练池、历史 split 互斥的 v4 开发集和最终盲集。实现和验收本任务后，才允许启动 v4 吞吐测量或策略训练。

## 前置报告与 Go / No-Go

- 前置证据：`.trellis/tasks/archive/2026-09/09-26-score-block-v3-edge-speed-retrain/report.md`，其中记录 `final_holdout_v3` 已运行一次、PPO 2 分低于 FSM 6 分；该集合已揭示，不能再次提供门槛证据。
- 参考材料：`C:/Users/Neco/Downloads/deep-research-report.md`。报告内容是待核查参考，不覆盖仓库现有实现与 v3 验收记录。
- **Go**：报告确认 v3 已打开；新 split 种子精确、互斥、可由现有评测入口强制执行；全部对应自测通过，`report.md` 明确写出通过/未通过与证据路径。只有 Go 才可启动基线与吞吐测量。
- **No-Go**：发现新 split 与训练池、任一历史 split 或彼此重叠，v3 仍可能产生合格门槛证据，或测试未通过。此时停止所有 v4 训练和后续子任务，修好本任务并重新出具报告。

## Requirements

- 继续以 `controllers/score_block_rl/splits.py` 为 split 唯一来源；新增 `development_v4=9001–9020`、`final_holdout_v4=10001–10050`，命名 split 版本升至 v4。
- `final_holdout_v3=8001–8050` 标为已揭示、仅分析；评估必须显式传 `--analysis-only`，结果 `gate_evidence_eligible=false`，不可创建/接受 v3 冻结门槛证据。
- v4 最终盲集仅允许在开发集候选冻结后进行一次正式评估；失败也必须记为已揭示，禁止重跑、换模型后重跑或将其改回盲集。
- 新 split 与训练 episode pool、历史 split、彼此互斥。自定义 seed 不得绕过命名 split 的占用规则。
- 不修改物理、奖励、策略、场景、默认 FSM 或训练实现；不启动训练或盲验。

## Acceptance Criteria

- [ ] Registry、usage 标注、blind/revealed 判定及 split 版本统一表达 v4 状态；v3 在 CLI、冻结验证和输出中均为 analysis-only / gate-ineligible。
- [ ] `development_v4` 精确包含 20 个 seed，`final_holdout_v4` 精确包含 50 个 seed；二者及全部已有训练/评估集合互斥。
- [ ] 自测覆盖已揭示 v3 未加 `--analysis-only` 被拒、显式分析输出不可作为门槛证据、v4 开发/盲集路由、重叠/重复 split 拒绝、冻结和盲验一次性约束。
- [ ] 相关 Python 自测、Gymnasium 检查（适用时）与 `git diff --check` 全通过；验收报告附运行命令、退出码和结果路径。
- [ ] 任务报告结论为明确 Go 或 No-Go；未通过不得触发依赖本报告的任务。
