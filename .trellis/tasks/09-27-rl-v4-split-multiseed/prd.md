# RL v4 多训练 seed 原版 PPO 基线

## Goal

用五个独立训练 RNG seed 测试现有 11 维观测、reward v2、PPO 策略的得分与跨 seed 稳定性，只用 v4 开发集裁判事件选模。

## 前置报告与 Go / No-Go

- 必须先有 `.trellis/tasks/archive/2026-09/09-27-rl-v4-split-guard/report.md` 的 Go，以及 `09-27-rl-v4-training-throughput/report.md` 的**下游策略 Go**（五个 500k 训练中位总墙钟≤60 分钟，语义与零 fault 通过）。缺一则不启动本任务。
- 本任务可复用提速阶段事先指定、运行身份完全一致的训练产物；若算法、reward、观测、场景、seed、步数、配置或代码哈希任一不同，须按本任务重新训练，不能拼凑产物。
- **直接冻结 Go**：至少 4/5 个训练 seed 的模型分别通过开发集 20 场门槛。未达标时，本任务可如实完成负面报告，但只允许启动条件性 `reward-credit`，不得冻结或开盲集。

## Requirements

- 训练 RNG seed 固定 `20260927–20261001`，各训练至少 500,000 transitions；固定 11 维特权观测、reward v2、物理、FSM、PPO 超参数和场景。训练与开发 seed 池隔离。
- 每个模型在 `development_v4=9001–9020` 上与同场次 FSM 配对比较。单模型通过条件：锁定目标真实 `BlockScore ≥` 配对 FSM、我方 `Drop ≤` 配对 FSM、得分归因可追溯且 fault=0。
- 仅根据开发集裁判事件按预注册顺序选择 checkpoint 和唯一候选。reward、PPO loss、训练回报和最终集结果不得参与选模。报告五个训练 seed 的逐项分数与分布，不用最佳模型代替稳定性判断。
- 保留完整运行身份、checkpoint/model SHA-256、评估 JSON 和裁判事件索引；声明特权观测不代表可直接上真机。
- 不运行 `final_holdout_v4`；不改 reward、观测、物理、FSM、场景或算法。

## Acceptance Criteria

- [ ] 五个固定训练 seed 均有 ≥500k transitions 的完整产物与唯一身份；无 fault，checkpoint 间隔按总 transitions 校验。
- [ ] 每个训练 seed 的候选在 20 个开发 seed 上有同入口配对 FSM、真实裁判事件、得分归因和 Drop 明细。
- [ ] 报告逐训练 seed 是否通过及 5 项分布；至少 4/5 通过才给直接冻结 Go，否则只给奖励实验条件入口。
- [ ] 固定 seed 确定性、Gymnasium 接口、受影响的 Sim.Tests 与旧回放及 `git diff --check` 通过；缺项在报告中明示，不能判正向 Go。
- [ ] `report.md` 明确结论、候选排名和下一步：Go 则跳过 `reward-credit`，No-Go 则只允许按该任务前置条件开展单变量实验。
