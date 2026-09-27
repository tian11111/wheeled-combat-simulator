# 设计：原版 PPO 多训练 seed 基线

## 运行身份

五个独立 SB3 RNG seed 为 `20260927、20260928、20260929、20260930、20261001`；开发评估 seed 为 `9001–9020`。`splits.py` 是命名评估 seed 的唯一来源。训练 episode seed 流按训练 seed 派生且不触碰任何评估集合。每个训练产物有单独目录、run manifest 和模型/checkpoint 哈希。

## 固定变量

模型维持 11 维 privileged observation、reward v2、PPO MlpPolicy、现有物理与默认 FSM。若提速任务用向量环境产物，其每次 PPO update 仍为 2048 总 transitions，配置须与本任务预注册身份完全匹配。不得把不同训练配置的产物放进同一个 5-seed 稳定性统计。

## 开发集评估与排序

每个模型的 checkpoint 在相同 20 场上 deterministic 推理，并与同场 FSM 配对。先检查 fault=0 与得分归因；再按锁定目标 `BlockScore` 不低于 FSM、我方 Drop 不高于 FSM 判定该训练 seed 是否通过。一个 seed 若有多个合格 checkpoint，先按 `(BlockScore−FSM BlockScore)` 降序，再按 `(FSM Drop−我方 Drop)` 降序，再按 checkpoint step 升序选一个。跨 seed 候选仍按同序，最后按训练 RNG seed 升序破平局。该排序必须在最终集打开前写入冻结记录。

报告五个 seed 的完整配对原始指标、通过率 4/5 门槛和范围。五个训练 seed 只是稳定性初筛，不能将区间估计描述为强总体保证。开发集可以用于选模；最终集保持未读。

## 结果分支

至少 4 个训练 seed 通过时，冻结唯一候选并跳过奖励实验。若不足 4 个，保留负面原版报告，只允许进入奖励归因单变量实验；不能从失败基线直接打开盲集。
