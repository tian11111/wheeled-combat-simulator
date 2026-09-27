# RL v4 唯一候选冻结与一次盲验

## Goal

从已达到开发集门槛的原版 PPO 或奖励变体中按预注册裁判事件排序冻结**唯一模型**，在 `final_holdout_v4=10001–10050` 上只执行一次正式配对盲验。

## 前置报告与 Go / No-Go

- 必须有 `.trellis/tasks/archive/2026-09/09-27-rl-v4-split-guard/report.md` Go、`09-27-rl-v4-training-throughput/report.md` 下游策略 Go，及以下二者之一：`09-27-rl-v4-split-multiseed/report.md` 记录原版至少 4/5 通过；或原版未达标且 `09-27-rl-v4-reward-credit/report.md` 记录变体至少 4/5 通过。
- 若两条策略路线均未过 4/5、任一身份/哈希不全、v4 最终集已被打开或存在结果泄露，本任务 No-Go，不能运行最终集。
- 冻结前完成全部开发集选模、代码与配置哈希审计。冻结后不得换模型、改场景、门槛或根据最终集调参。

## Requirements

- 选择唯一开发集候选：先按锁定目标真实 `BlockScore−FSM` 降序，再按 `FSM Drop−我方 Drop` 降序；其余平局按 checkpoint step、训练 RNG seed 的预注册升序规则处理。仅合格模型入围，reward/PPO loss 不参与。
- 冻结记录包含模型/checkpoint SHA-256、训练 seed、代码 commit/dirty tree 摘要、场景、CLI、split、依赖、PPO/reward/观测配置和开发集选模证据；评估前验证一致。
- 只在 `final_holdout_v4` 50 场使用 deterministic PPO 与同入口配对 FSM。`BlockScore` 必须是锁定目标的真实裁判得分，逐场事件可追溯；同时记录我方 Drop、FSM Drop 和 fault。
- 首次正式运行后无论成败标为已揭示。失败如实报告，不换模型、不复用 v4 最终集做新一轮门槛证据。统计只描述本次五训练 seed 初筛与 50 场盲验，不声称真机泛化。

## Acceptance Criteria

- [ ] 所有前置报告正向 Go、最终集未揭示、唯一候选及完整身份在运行前冻结，冻结校验通过。
- [ ] 最终 50 个 seed 精确且只运行一次；每场 PPO/FSM 同入口配对，裁判事件、归因、Drop、fault 和原始结果可复核。
- [ ] 通过门槛：锁定目标真实 `BlockScore ≥` 配对 FSM、我方 `Drop ≤` 配对 FSM、得分归因可追溯且 fault=0。任一失败即盲验 No-Go。
- [ ] 固定 seed 确定性、Gymnasium 接口、Sim.Tests、旧回放及 `git diff --check` 通过；异常或缺失结果不得补跑为“首次正式盲验”。
- [ ] `report.md` 明确冻结前后时间、唯一运行身份、逐场/汇总结果、Go/No-Go 和 v4 最终集已揭示状态；失败后父任务不得宣称策略目标达成。
