# 技术设计：已揭示 split 守卫与 v4 预注册

## 事实与边界

- `splits.py` 是 `train.py`、`evaluate.py`、`selftest.py` 和诊断入口共用的唯一注册表。目前它仍把 `final_holdout_v3`（8001–8050）列为唯一 `BLIND_SPLITS`；已揭示列表仅覆盖旧 4001–4010 和 6001–6050。
- v3 盲验已经揭示且失败：冻结 PPO 在 50 场得到锁定目标 `BlockScore` 2 次，配对 FSM 6 次；掉台 25 对 29。证据在归档报告第 16、141–156 行。
- `evaluate.py` 已有 `--analysis-only`、freeze 校验和 gate eligibility 输出；`selftest.py` 已覆盖多轮 split 路由及冻结守卫，因此扩展现有守卫，避免另建并行 seed 来源。
- 本任务只改 split/评测守卫和相应测试/说明；不得更改训练算法、奖励、物理、FSM、场景或开启 v4 训练。

## 注册与评测数据流

1. 扩展 `splits.py` 单一 registry：v3 加入 revealed 集；v4 开发集和最终盲集登记精确 seed 与用途；更新版本标识、默认开发 split、blind/revealed 元数据与训练/eval manifests。
2. 解析器拒绝所有已揭示集合被当作盲验的路径，包括显式命名 split、自定义 seed、旧兼容选项以及 freeze 协议校验。对历史公开集合可保留查询能力，但必须是分析结果且永不 eligible。
3. 评估入口按 split 元数据决定是否需要 `--analysis-only` 和冻结记录。v4 的正式最终集还需确认唯一候选冻结与盲验前无现存运行结果；完成一次后登记 run index 和已揭示状态，拒绝再次作为 gate 运行。
4. `selftest.py` 以真实 CLI 子进程覆盖拒绝与成功路径，确保结构化输出中的 split version、usage、`is_blind_holdout`、`is_revealed_holdout`、`analysis_only` 和 `gate_evidence_eligible` 一致。

## 不变量

- v4 两个集合分别为 `range(9001, 9021)` 和 `range(10001, 10051)`；既与彼此，也与所有命名历史集和 `TRAIN_EPISODE_SEEDS` 互斥。
- `final_holdout_v3` 的评估结果即便数值达到旧门槛也不具备 v4 或任何后续轮次的门槛资格。
- 只有 `final_holdout_v4` 是 v4 的正式盲集；必须在候选冻结之后运行一次。失败后保留失败证据，不用重跑补救。
- v4 最终盲验用的 50 个场次与训练池、v4 开发集、全部历史 split 相互独立。

## 兼容与失败处理

- 历史任务报告、已有 JSON 和已归档 split 记录保持原样；不改写 v3 的历史结论。
- 对已揭示 split 的命令报错应指出 split 已揭示并给出 `--analysis-only` 用法；分析输出写明不可作 gate evidence。
- 若冻结文件仍声明 v3 为最终集或版本不匹配，v4 最终评测 fail closed，不自动迁移冻结文件。
- 若不能确认历史 split 注册完整或盲验是否已经执行，判 No-Go 并要求人工核实报告，不猜测或重置状态。
