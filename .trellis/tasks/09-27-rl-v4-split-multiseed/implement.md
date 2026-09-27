# 执行计划

1. 阅读 split 守卫和吞吐任务 `report.md`；分别确认 Go 与下游策略 Go。否则保持 planning，不训练、不复用未核验产物。
2. 锁定五个训练 RNG seed、11 维观测、reward v2、场景、代码与 PPO 参数；检查提速产物是否可按完整身份直接复用。只选一个预先指定的有效套件，不从重复计时套件挑最好模型。
3. 对每个训练 seed 核对 ≥500k transitions、模型/checkpoint 哈希、seed 流、fault=0；缺失则补齐同配置训练并保留原失败记录。
4. 在 `development_v4` 20 场逐 checkpoint 运行 deterministic PPO 与配对 FSM；从裁判事件抽取锁定目标真实 `BlockScore`、我方 `Drop`、归因和 fault。
5. 按 design.md 的固定排序为每个训练 seed 选一个 checkpoint，再统计 5 个 seed 中通过的数量及逐项分布。不得用 mean reward 或 PPO loss 排名。
6. 运行 Python split/评估自测、Gymnasium checker、固定 seed 确定性；若触及 .NET，再运行相关 Sim.Tests 和旧回放；执行 `git diff --check`。
7. 写 `report.md` 和证据路径：`4/5` 及以上为直接冻结 Go 并标明奖励任务跳过；不足 4/5 为负面基线，下一步仅允许 `reward-credit`，最终盲集仍锁闭。
