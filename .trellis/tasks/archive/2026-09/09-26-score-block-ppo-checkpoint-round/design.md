# 设计：SCORE_BLOCK PPO 下一轮（checkpoint 选模与新留出集盲验）

## 边界与决策

本轮**只改 Python 侧训练/评测脚本与文档**，不改 C#、场景、裁判、物理、奖励语义、观测维度或默认 FSM。理由是：上一轮已把训练桥（`rl-env`）、11 维观测与奖励 v2 冻结并有测试；本轮要补的是“训练快照可观测 + 数据划分不可混用 + 选模与盲验纪律”，这些全在 Python 与 Trellis 记录层。

三条不可退让的纪律：

1. `EvalCallback` 默认按平均 episode 回报更新 `best_model`，与本项目的裁判事件指标（锁定目标真实 `BlockScore`、我方 `Drop`）无关，因此**不引入** `EvalCallback`；只用 `CheckpointCallback` 做无偏快照。
2. 4001–4010 已揭示，`--final-holdout` 继续只指它；新模型盲验必须走新 split，历史 split 只能标为 legacy 对照。
3. 最终集盲验前必须存在冻结记录，且最终结果文件已存在时默认拒绝重跑。

## 数据划分（split v2）

新增 `controllers/score_block_rl/splits.py` 作为划分的**单一来源**，供 `train.py`、`evaluate.py`、`selftest.py` 共用，避免三处各写一份 seed 列表而漂移。

| split 名称 | seeds | 用途标签 |
|---|---|---|
| `legacy_development` | 3001–3010 | 历史开发（已揭示，仅对照） |
| `legacy_final_holdout` | 4001–4010 | 历史最终留出（已揭示，`--final-holdout` 语义） |
| `development_v2` | 5001–5020 | 本轮选模开发集 |
| `final_holdout_v2` | 6001–6050 | 本轮唯一盲验集 |
| `exploratory` | 调用方自定义 | 探索，不计入任何门槛 |

- `SPLIT_VERSION = "score-block-split-v2"`，写进 `run-config.json` 与评测 JSON 的 `split_version` 字段。
- 训练 seed 域 `TRAIN_EPISODE_SEEDS = {42, 20260925} ∪ [1000, 1999]`；历史已揭示 seed 域 `HISTORICAL_SEEDS = [3001,3010] ∪ [4001,4010]`。
- 校验函数 `resolve_selection(split, custom_seeds)` 统一实现路由与互斥：
  - 未知 split、split 与自定义 seed 同时给出 → 错误；
  - 自定义 seed 少于 10 个、有重复、命中训练池或历史域 → 错误；
  - 自定义 seed 与任何命名 split 相交 → 错误（禁止“半个开发集”式的混合评测）；
  - 命名 split 的 seed 列表必须与注册表逐字一致。
- 旧 CLI 兼容：`--final-holdout`（无参数）仍解析为 `legacy_final_holdout`，且不允许与 `--split`/`--seeds` 同时使用。

## 训练侧（`train.py`）

保持单环境、PPO `MlpPolicy`、SB3 默认超参、`seed=20260925`、Monitor 逐集 CSV 与最终模型不变，新增：

- **PPO 诊断 CSV**：`CSVLogger(folder=out, filename="progress")` → `<out>/progress.csv`。SB3 每次 `logger.dump()` 写一行，含 `rollout/ep_rew_mean`、`rollout/ep_len_mean`、`time/fps`、`time/total_timesteps`，以及 PPO `train/` 段的 `approx_kl`、`clip_fraction`、`explained_variance`、`value_loss`、`entropy_loss`、`learning_rate`、`loss` 等。该文件只用于解释训练，**不是**裁判得分证据。
- **`CheckpointCallback`**：`save_freq=51200`、`save_path=<out>/checkpoints`、`name_prefix="rl_model"`。
  - 使用锁定 SB3 2.9.0 的语义：`save_freq` 的单位是 `env.step()` 调用次数；本任务固定 `n_envs=1`，所以 51,200 次调用 = 51,200 个单环境 step，**不需要** `save_freq // n_envs`。代码里显式断言单环境（`model.n_envs == 1`），否则报错。
  - 文件名由 SB3 生成为 `rl_model_<num_timesteps>_steps.zip`；`num_timesteps` 就是训练步数，因此步数可从文件名解析并与 `run-config.json` 交叉核对。
- **`run-config.json` 扩展**：`split_version`、`evaluation_seed_splits`（含 v2 标签）、`checkpoint_interval_single_env_steps`、`checkpoint_callback`（名称/路径/`n_envs=1` 说明）、`progress_csv`、`checkpoints`（每个文件的 `path`/`filename`/`training_steps`/`sha256`）、`logger`（CSV logger 说明 + `log_interval`）、`best_model_selection`（显式记录“未使用 EvalCallback 平均回报选模”）。
- 训练结束后重新枚举 checkpoint 目录并核对“文件名步数 ↔ 注册表步数 ↔ SHA-256”，任何不一致都写入 `run-config.json` 的 `checkpoint_audit` 并在状态中标注失败。

## 评测侧（`evaluate.py`）

保留既有单模型开发/最终评测能力，扩展为**多模型 sweep**：

- `--model` 可重复；`--checkpoints-dir DIR` 自动发现 `rl_model_*_steps.zip` 并追加最终模型。模型解析出 `(path, training_steps, sha256)`。
- `--split {development_v2, final_holdout_v2, legacy_development, legacy_final_holdout, exploratory}`；`--final-holdout` 为 `legacy_final_holdout` 的别名。
- 评测顺序（每个模型都重新 `reset(seed)`，因此互相独立）：
  1. 先用同一份 `ScoreBlockEnv`（独立于训练进程）对 20/50 个 seed 各跑一次 **FSM 基线**，得到 model-independent 的配对基准（reset 内双方内置 FSM 预推进到同一首次 `SCORE_BLOCK` 入口）。
  2. 再逐个模型加载、`deterministic=True` 跑同一 seed 列表，与第 1 步同 seed 配对。
  - 输出显式记录 `fsm_baseline_is_model_independent: true` 与 `paired_by: "same seed, same first SCORE_BLOCK entry (reset pre-roll)"`，避免看起来像省略了配对。
- 逐 seed 指标沿用上一轮：`no_score_block`、阶段入口 tick、锁定目标索引、锁定目标我方真实 `BlockScore`、`us/them BlockScore` 事件数、`target_block_offs`、`unowned_block_offs`、`us_drops`、`controller_faults`、最终比分、归因歧义、`position_event_cross_check`、目标块入口/终末位姿。
- **选模（`--select-candidate`，仅 `development_v2`）**：对每个模型计算
  - `has_at_least_one_locked_target_score`、`locked_target_scores_not_below_fsm`、`us_drops_not_above_fsm` 三个门槛；
  - `traceability_ok`：所有计分的 seed 都 `position_event_cross_check=true`（“真实 BlockScore”的可追溯条件；失败者记录原因并从候选中排除，但不隐藏其分数）；
  - 排序键 `(-total_locked_target_us_block_scores, total_us_drops, -training_steps)`；
  - 输出 `candidate`（唯一）或 `null` + `stop_reason="no_qualified_model"`。
- **冻结记录**：`--freeze <path>` 把候选的 `path/training_steps/sha256/scenario_sha256/cli_dll_sha256/dev_summary` 写成独立 JSON，并在原 dev sweep JSON 内嵌同内容。
- **最终单次盲验**：
  - `--split final_holdout_v2` 时**必须**给 `--require-freeze <freeze.json>`；校验冻结记录的 `sha256` 与当前模型文件一致、且 `training_steps` 一致，否则拒绝运行；
  - `--out` 已存在时默认拒绝覆盖（`--force` 才能重写），把“只运行一次”变成可检查的机器约束；
  - 输出 `evaluation_split="final_holdout_v2"`、`blind_run_index=1`、`frozen_candidate` 摘要、逐 seed 事件与门槛结论；
  - `ac4_claim_eligible` 不再沿用旧含义：本轮改为 `previous_round_ac4_claim_eligible=false` 常量 + 新字段 `new_round_blind_gate_passed`，显式表示新结果不能追认上一轮 AC4。

## 定向验证（`selftest.py`）

新增纯 Python 检查脚本，覆盖 AC1/AC2 的机器可判定部分：

1. split 路由：四个命名 split 的 seed 列表、总长度、首尾元素、互斥性；
2. 互斥/重叠拒绝矩阵：训练 seed、历史 seed、跨 split 混合、重复 seed、过短清单、split+seeds 混用、`--final-holdout`+`--split` 混用，逐项断言抛错且错误信息含关键 seed；
3. checkpoint 步数解析与哈希：`rl_model_51200_steps.zip` → 51200；非法名拒绝；对真实训练目录核对 `run-config.json` 里每个 checkpoint 的步数与 SHA-256；
4. CSV 可读性：用 `csv` 模块读 `progress.csv` 与 `episodes.monitor.csv`，断言表头存在、行数 > 0、`train/approx_kl` 等诊断列可解析为 float、Monitor 行包含本任务的 `info_keywords`；
5. 最终单次盲验守卫：对已存在的输出文件、缺失/不匹配的冻结记录断言拒绝。

产物相关检查（3/4）需要真实训练目录，用 `--train-dir` 传入；没有则标记 `skipped` 而不是假装通过。

## 失败与回退

- 训练中断：保留已写出的 `progress.csv`/Monitor/checkpoint，`run-config.json.status="failed"`；不把中断步数算作训练步数。
- 开发集无合格模型：写 `stop_reason="no_qualified_model"`，**不**运行 6001–6050，任务结论为“本轮未产出合格候选”，不换 split、不放宽门槛。
- 最终集未过门槛：如实记录失败与逐 seed 事件，不据此换模型，不追认上一轮 AC4。
- 任何 split/seed 校验失败都在启动子进程之前报错，避免半途产出可被误读的分数。
