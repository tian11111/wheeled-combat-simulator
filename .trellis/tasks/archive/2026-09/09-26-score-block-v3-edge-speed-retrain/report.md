# SCORE_BLOCK v3 修复后重训与近沿限速候选 — 验收报告

- 任务：`09-26-score-block-v3-edge-speed-retrain`
- 分支：`test/score-block-ppo-checkpoint-round`（HEAD `e7ffb4a`）
- 产物目录：`.sim_runs/score-block-v3-baseline-20260926b-codex/`（由 `.gitignore` 排除，不入库）
- 结论时间线：训练完成 → `development_v3` 选模 → 冻结 `ppo_score_block.zip` → `final_holdout_v3` 单次盲验未通过

## 0. 结论摘要

- **AC1 通过**：无防护基线完成 500,000 请求步（实际 501,760 步），模型、9 个 checkpoint、`run-config.json`、`progress.csv`、`episodes.monitor.csv` 与 SHA-256 均可核对；`development_v3` 20 个 seed 的 PPO/FSM 配对结果完整。
- **AC2 不适用（未触发）**：基线在开发集存在合格候选，按 R3 直接冻结，未实现 `target_edge_speed_cap_v1` 限速层，因此不产生限速边界/倒车/角速度/reset 首步/train-eval 一致性的定向验证需求。报告在此明确标记为不适用。
- **AC3 通过**：开发集选模与冻结遵守 R2–R5；`development_v3` 有合格模型，未在无合格模型的情况下打开最终集；冻结记录写入时间早于任何 v3 最终集运行；最终集 `final_holdout_v3` 只运行一次，结果无论成败均如实记录（本轮为**未通过**）。
- **AC4 通过**：原始 Python 自测在本次收尾复跑中 **40 通过 / 0 失败 / 0 跳过**（含 Gymnasium `check_env` 与固定 seed 确定性）；完整 Sim.Tests（388/0/0）、六份 legacy 回放、官方 MuJoCo 新模式回放及 `git diff --check` 已通过。默认比赛链路未改动。
- **AC5 通过**：本报告即交付物，含逐 seed 裁判事件、PPO/FSM 对照、训练诊断、模型/场景/CLI 哈希、限速触发量说明（不适用）与失败场次，并声明单次训练与特权状态的结论范围。

**盲验结论（本轮核心结果）**：冻结候选在 `final_holdout_v3` 50 个 seed 上取得 **2 次**真实锁定目标 `BlockScore`，同入口 FSM 取得 **6 次**；门槛项 `locked_target_scores_not_below_fsm` 为 `false`，`new_round_blind_gate_passed` 为 `false`。我方掉台 25 ≤ FSM 29 通过；得分可追溯性通过。按设计，本轮到此停止：**不根据最终集换模型、不改门槛、不使用 `--force` 重判**，下一次算法修改需另建 split。

## 1. 训练产物与可核对性（R1 / AC1）

- 命令（按 `README.md` 模板，`--out` 指向本次独立目录）：`python -X utf8 controllers/score_block_rl/train.py --steps 500000 --out .sim_runs/score-block-v3-baseline-20260926b-codex`。注意：日志中未留存逐字的完整命令行；`train-console.out.log` 只保存了训练结束时的 `run-config.json` 内容。
- 输出：`.sim_runs/score-block-v3-baseline-20260926b-codex/`，其中的大型权重与 CSV 全部位于 Git 跟踪目录之外。

`run-config.json` 关键字段：

| 字段 | 值 |
| --- | --- |
| `status` | `completed` |
| `algorithm` | `Stable-Baselines3 PPO MlpPolicy` |
| `total_timesteps_requested` | 500000 |
| `total_timesteps_trained` | 501760 |
| `elapsed_seconds` | 1631.238 |
| `steps_per_second` | 307.595 |
| `train_seed` | 20260925 |
| `checkpoint_interval_single_env_steps` | 51200 |
| `ppo_parameters.n_envs` | 1 |
| `ppo_parameters.device` | `cpu` |
| `split_version` | `score-block-split-v3` |
| `best_model_selection.uses_eval_callback_mean_reward` | `false` |
| `platform` | `Windows-11-10.0.26200-SP0` |
| `python` | `3.12.10` |

依赖版本：`numpy 2.5.0`、`gymnasium 1.3.0`、`stable-baselines3 2.9.0`、`torch 2.13.0`。PPO 参数取自所装 SB3 版本默认值：`MlpPolicy`、`n_steps 2048`、`batch_size 64`、`n_epochs 10`、`gamma 0.99`、`gae_lambda 0.95`、`clip_range 0.2`、`normalize_advantage true`、`ent_coef 0.0`、`vf_coef 0.5`、`max_grad_norm 0.5`、`learning_rate_initial 3e-4`、`target_kl null`、`use_sde false`。观测 11 维、`privileged_state true`；动作契约 `Box(-1,1)^2 -> v=action[0] m/s, w=2*action[1] rad/s`。`block_coordinates_are_simulator_ground_truth` 为 `true`。

`checkpoint_audit`：`ok true`、`issues []`、注册 9 / 磁盘 9、`progress_csv_rows 245`（17 列）、`monitor_csv_rows 926`、`episode_log_rows 926`。

checkpoint 与最终模型 SHA-256：

| 训练步数 | 文件 | SHA-256 |
| --- | --- | --- |
| 51200 | `rl_model_51200_steps.zip` | `39e6f3c6bb02c8f2103d860a8ceaa7f60eee18c2506bcd6a89e675b37cd71ed2` |
| 102400 | `rl_model_102400_steps.zip` | `fc8d2da5788c0c3ce63eee757d278b1b4175c99ae40b68378eca5454f9dfd229` |
| 153600 | `rl_model_153600_steps.zip` | `8487adbfb93b4d10c1caee152880848f18b88ae52849c445175dd098134c2ffd` |
| 204800 | `rl_model_204800_steps.zip` | `c80566d25260d4397a3db8b8b7622f10149c31a67aa485c03db28b2d08711340` |
| 256000 | `rl_model_256000_steps.zip` | `b6561e3b35a8408d60a5301424a33fd3c2c31566496e462b4b8fa4fee71e9de0` |
| 307200 | `rl_model_307200_steps.zip` | `c19b521ce99996007f797b62855717c60ed12a9545c88e5da8da85c6f2855a4f` |
| 358400 | `rl_model_358400_steps.zip` | `d2de636d1a6beaeffdd0fe43a282d8086331817fae5d21011ad8bc49e6fabcc7` |
| 409600 | `rl_model_409600_steps.zip` | `c7cfa4e6cb5f81f0f404228ec48b7535523c0784e55130a8fbedea96017ae6c1` |
| 460800 | `rl_model_460800_steps.zip` | `0e9f476d757e849e8f9fce19dd8b0effba432fda230ac479ee7d1e15a9ad033d` |
| 501760（最终） | `ppo_score_block.zip` | `8669147f96ecba2f9ccccbc5f119d25275c738671dbda75aa6e7efbcf4c39c5a` |

场景与 CLI 身份（与记录一致，本次复核重复计算确认）：

- 场景 `scenarios/wushu-ring-2026-mujoco.json` SHA-256 `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3`
- CLI `src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll` SHA-256 `4b99f2e118152d2da44969d53b50bccadf627f168dbee294b8ce453763a0813c`
- dotnet 可执行文件 `C:\Users\Neco\AppData\Local\Temp\robot-simulator-dotnet-sdk\dotnet.exe` 存在

## 2. 开发集选模（R2 / AC1）

`dev-v3-sweep.json` 元数据：`evaluation_split development_v3`、`split_version score-block-split-v3`、`evaluation_split_usage "current-round model-selection development set"`、`analysis_only false`、`gate_evidence_eligible false`、`evaluation_mode "PPO deterministic"`、`deterministic_actions true`。合格判定规则：`至少一次真实锁定目标 BlockScore` 且 `目标得分总数 ≥ FSM` 且 `我方掉台 ≤ FSM` 且 `所有得分可追溯`；排序 `[目标得分多, 掉台少, 训练步数多]`。

10 个模型的开发集结果（`sc` = 真实锁定目标得分总数，`drop` = 我方掉台，`nsb` = `no_score_block` 集数，`themEv` = 对方得分事件，`faults` = 控制器 fault）：

| 模型 | 训练步数 | `gate_passed` | sc | drop | nsb | themEv | faults |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `rl_model_51200_steps.zip` | 51200 | false | 1 | 15 | 3 | 4 | 0 |
| `rl_model_102400_steps.zip` | 102400 | false | 0 | 16 | 3 | 6 | 0 |
| `rl_model_153600_steps.zip` | 153600 | false | 1 | 16 | 3 | 2 | 0 |
| `rl_model_204800_steps.zip` | 204800 | false | 0 | 17 | 3 | 4 | 0 |
| `rl_model_256000_steps.zip` | 256000 | false | 0 | 16 | 3 | 7 | 0 |
| `rl_model_307200_steps.zip` | 307200 | false | 0 | 16 | 3 | 7 | 0 |
| `rl_model_358400_steps.zip` | 358400 | false | 0 | 13 | 3 | 2 | 0 |
| `rl_model_409600_steps.zip` | 409600 | **true** | 1 | 7 | 3 | 4 | 0 |
| `rl_model_460800_steps.zip` | 460800 | false | 0 | 10 | 3 | 7 | 0 |
| `ppo_score_block.zip` | 501760 | **true** | 1 | 5 | 3 | 7 | 0 |

FSM 开发集基线汇总（20 集）：`no_score_block 3`、`episodes_with_locked_target_us_block_score 1`、`total_locked_target_us_block_scores 1`、`total_us_block_score_events 1`、`total_them_block_score_events 2`、`total_target_block_offs 0`、`total_unowned_block_offs 0`、`total_us_drops 12`、`total_controller_faults 0`、`final_score_us_sum 17`、`final_score_them_sum 41`、`ambiguous_attribution_episodes 0`。全 20 seed 归因歧义均为 false。

合格模型 2 个，排序结果为 `ppo_score_block.zip`（`ranking_key [-1, 5, -501760]`）优于 `rl_model_409600_steps.zip`（`ranking_key [-1, 7, -409600]`）。选中最终模型 `ppo_score_block.zip`。

选中候选的逐 seed PPO/FSM 配对（`p_sc`/`f_sc` = 真实锁定目标得分，`p_drop`/`f_drop` = 我方掉台，`nsb` = 无得分块集，`cross` = 得分的位姿/事件交叉核验）：

| seed | p_sc | f_sc | p_drop | f_drop | p_nsb | f_nsb | cross |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 7001 | 0 | 0 | 1 | 0 | 0 | 0 | – |
| 7002 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7003 | 0 | 0 | 0 | 0 | 0 | 0 | – |
| 7004 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7005 | 0 | 0 | 0 | 0 | 0 | 0 | – |
| 7006 | 0 | 0 | 0 | 0 | 1 | 1 | – |
| 7007 | **1** | 0 | 0 | 1 | 0 | 0 | true |
| 7008 | 0 | 0 | 1 | 1 | 0 | 0 | – |
| 7009 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7010 | 0 | 0 | 0 | 0 | 1 | 1 | – |
| 7011 | 0 | 0 | 1 | 1 | 0 | 0 | – |
| 7012 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7013 | 0 | 0 | 0 | 0 | 0 | 0 | – |
| 7014 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7015 | 0 | **1** | 1 | 0 | 0 | 0 | – |
| 7016 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7017 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7018 | 0 | 0 | 1 | 1 | 0 | 0 | – |
| 7019 | 0 | 0 | 0 | 1 | 0 | 0 | – |
| 7020 | 0 | 0 | 0 | 0 | 1 | 1 | – |

策略得分 seed 为 7007（`position_event_cross_check true`）；FSM 得分 seed 为 7015。全 20 seed 的 `fault` 为 0、`attribution_ambiguous` 为 false、`target_block_offs`/`unowned_block_offs` 均为 0。完整 10 模型 × 20 seed 矩阵见 `dev-v3-sweep.json` 的 `models[].paired_per_seed` 与 `fsm_baseline.per_episode`。

**R3 未触发**：基线在 `development_v3` 存在合格候选，按 R3 直接冻结，未实现 `target_edge_speed_cap_v1` 近沿限速层（`gym_env.py`、`train.py`、`evaluate.py` 均未加入 `--safety-mode` 与限速逻辑）。因此没有阈值 `< 0.45 m`、正向上限 `+0.35 m/s`、触发次数、模式一致性等限速专属证据，AC2 标记不适用。

## 3. 冻结（R5）

`candidate-freeze.json`（`protocol score-block-freeze-v1`、`frozen_at_utc 2026-09-26T13:48:11+00:00`）：

| 字段 | 值 |
| --- | --- |
| `candidate.path` | `...\.sim_runs\score-block-v3-baseline-20260926b-codex\ppo_score_block.zip` |
| `candidate.filename` | `ppo_score_block.zip` |
| `candidate.training_steps` | 501760 |
| `candidate.sha256` | `8669147f96ecba2f9ccccbc5f119d25275c738671dbda75aa6e7efbcf4c39c5a` |
| `ranking_key` | `[-1, 5, -501760]` |
| `gate` | `has_at_least_one_locked_target_score true`、`locked_target_scores_not_below_fsm true`、`us_drops_not_above_fsm true`、`traceability_ok true`、`gate_passed true` |
| `development_split` / `final_holdout_split` | `development_v3` / `final_holdout_v3` |
| `scenario_sha256` | `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3` |
| `cli_dll_sha256` | `4b99f2e118152d2da44969d53b50bccadf627f168dbee294b8ce453763a0813c` |
| `frozen_before_final_holdout` | `true` |
| `note` | `freeze record written before any final_holdout_v3 run` |

冻结时未存在任何 v3 最终集结果。`final_holdout_v3` 运行时的 `freeze_checks` 四项全部为 `true`：`split_version_matches`、`final_holdout_split_matches`、`candidate_sha256_matches`、`candidate_training_steps_matches`。

## 4. 单次盲验结果（R5 / AC3）

`final-holdout-v3.json`：`evaluation_split final_holdout_v3`、`split_version score-block-split-v3`、`is_blind_holdout true`、`analysis_only false`、`gate_evidence_eligible true`、`blind_run_index 1`、`evaluated_at_utc 2026-09-26T13:50:32+00:00`、`new_round_blind_gate_passed false`、`ac4_claim_eligible false`（上一轮 4001–4010 的 AC4 已按失败关闭，与本次盲验相互独立）。

门槛明细：

| 门槛项 | 结果 |
| --- | --- |
| `has_at_least_one_locked_target_score` | true |
| `locked_target_scores_not_below_fsm` | **false**（策略 2 < FSM 6） |
| `us_drops_not_above_fsm` | true（25 ≤ 29） |
| `traceability_ok` | true |
| `gate_passed` | **false** |
| `scores_without_position_event_cross_check` | `[]` |

备注原文：`gate uses locked-target real BlockScore and our own Drop only; reward and final score are not gate evidence`。

汇总对照（50 集）：策略 `total_locked_target_us_block_scores 2`、`episodes_with_locked_target_us_block_score 2`、`total_us_block_score_events 3`、`total_them_block_score_events 17`、`total_us_drops 25`、`total_target_block_offs 1`、`total_unowned_block_offs 1`、`no_score_block_episodes 3`、`total_controller_faults 0`、`final_score_us_sum 174`、`final_score_them_sum 106`、`ambiguous_attribution_episodes 0`。FSM 同集：`total_locked_target_us_block_scores 6`、`total_us_block_score_events 6`、`total_them_block_score_events 12`、`total_us_drops 29`、`total_target_block_offs 0`、`total_unowned_block_offs 0`、`no_score_block_episodes 3`、`total_controller_faults 0`、`final_score_us_sum 40`、`final_score_them_sum 87`、`ambiguous_attribution_episodes 0`。

逐 seed 裁判事件（`p` = 策略，`f` = FSM；`tbo/ubo` = 策略的目标块出界 / 无归属出界）：

| seed | p_sc | f_sc | p_drop | f_drop | nsb(p/f) | cross | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 8001 | 0 | 1 | 1 | 0 | 0/0 | – | FSM 得分 |
| 8002 | 0 | 0 | 0 | 0 | 0/0 | – | 策略 themEv 1 |
| 8003 | 0 | 0 | 0 | 0 | 1/1 | – | 双方无得分块 |
| 8004 | 0 | 0 | 0 | 1 | 0/0 | – | 策略 themEv 1 |
| 8005 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8006 | 0 | 0 | 1 | 0 | 0/0 | – | |
| 8007 | 0 | 0 | 0 | 1 | 0/0 | – | |
| 8008 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8009 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8010 | 0 | 0 | 0 | 0 | 0/0 | – | |
| 8011 | 0 | 0 | 0 | 1 | 0/0 | – | 策略 themEv 1 |
| 8012 | 0 | 0 | 0 | 1 | 0/0 | – | 策略 themEv 1 |
| 8013 | 0 | 0 | 0 | 0 | 0/0 | – | |
| 8014 | 0 | 0 | 0 | 0 | 0/0 | – | 策略 themEv 1 |
| 8015 | 0 | 0 | 1 | 0 | 0/0 | – | |
| 8016 | 0 | 0 | 1 | 0 | 0/0 | – | |
| 8017 | 0 | 1 | 0 | 0 | 0/0 | – | FSM 得分；策略 themEv 1 |
| 8018 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8019 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8020 | 0 | 1 | 1 | 0 | 0/0 | – | FSM 得分 |
| 8021 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8022 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8023 | 0 | 0 | 0 | 1 | 0/0 | – | |
| 8024 | 0 | 0 | 1 | 1 | 0/0 | – | 策略 themEv 1 |
| 8025 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8026 | 0 | 0 | 0 | 0 | 1/1 | – | 双方无得分块 |
| 8027 | 0 | 0 | 0 | 0 | 0/0 | – | 策略 themEv 1 |
| 8028 | 0 | 0 | 0 | 0 | 0/0 | – | |
| 8029 | 1 | 0 | 0 | 1 | 0/0 | true | 策略得分 |
| 8030 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8031 | 1 | 0 | 0 | 1 | 0/0 | true | 策略得分 |
| 8032 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8033 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8034 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8035 | 0 | 0 | 0 | 0 | 0/0 | – | 策略 themEv 2 |
| 8036 | 0 | 0 | 1 | 1 | 0/0 | – | 策略 themEv 1 |
| 8037 | 0 | 0 | 1 | 1 | 0/0 | – | 策略 themEv 1 |
| 8038 | 0 | 0 | 0 | 0 | 0/0 | – | 策略 tbo 1 / ubo 1 |
| 8039 | 0 | 0 | 0 | 1 | 0/0 | – | 策略 themEv 1 |
| 8040 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8041 | 0 | 0 | 0 | 1 | 0/0 | – | |
| 8042 | 0 | 0 | 1 | 1 | 0/0 | – | 策略 themEv 1 |
| 8043 | 0 | 0 | 0 | 0 | 0/0 | – | |
| 8044 | 0 | 1 | 1 | 0 | 0/0 | – | FSM 得分 |
| 8045 | 0 | 1 | 1 | 0 | 0/0 | – | FSM 得分 |
| 8046 | 0 | 0 | 0 | 1 | 0/0 | – | 策略 themEv 1 |
| 8047 | 0 | 1 | 0 | 0 | 0/0 | – | FSM 得分；策略 themEv 2 |
| 8048 | 0 | 0 | 1 | 1 | 0/0 | – | |
| 8049 | 0 | 0 | 0 | 0 | 1/1 | – | 双方无得分块 |
| 8050 | 0 | 0 | 0 | 1 | 0/0 | – | |

失败/诊断要点：

- **失败 seed（得分门槛）**：策略得分 seed 8029、8031；FSM 得分 seed 8001、8017、8020、8044、8045、8047。策略目标得分 2 < FSM 6，这是唯一失败门槛项。
- **`no_score_block`**：8003、8026、8049（PPO 与 FSM 一致）。
- **掉台**：策略 25 次，FSM 29 次，掉台门槛通过。
- 全 50 seed `fault` 为 0、`attribution_ambiguous` 为 false。策略 `target_block_offs`/`unowned_block_offs` 仅出现在 seed 8038（1/1）。
- 策略对方得分事件：单次 13 个 seed（8002、8004、8011、8012、8014、8017、8024、8027、8036、8037、8039、8042、8046），两次 2 个 seed（8035、8047）。
- 两个策略得分 seed（8029、8031）的 `position_event_cross_check` 均为 `true`，`scores_without_position_event_cross_check` 为空，得分可追溯性通过。

## 5. 代码变更与 AC4 验证

本次代码改动仅两处，均在 Python 侧，未触碰 `Sim.Core`、`Sim.Mujoco`、场景、默认 FSM、奖励常量、11 维观测或 `fidelity.json`：

1. `controllers/score_block_rl/evaluate.py`：把得分位姿/事件交叉核验纳入门槛。原因：原先 `gate_passed` 只合并「至少一次真实得分」「得分不低于 FSM」「掉台不高于 FSM」，而追溯性只在开发集筛选时单独检查，可能让得分不可追溯时的 `new_round_blind_gate_passed` 误报通过。现改为 `traceability_ok = not untraceable`、`gate_passed = all(project_gate.values()) and traceability_ok`。
2. `controllers/score_block_rl/selftest.py`（+17）：新增用例 `selection: untraceable score fails gate`，构造一个真实得分但 `position_event_cross_check` 为 false 的行，断言 `gate_passed is False` 且该 seed 进入 `scores_without_position_event_cross_check`；把交叉核验置 true 后断言 `gate_passed is True`。

AC4 实际执行结果：

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 完整 Sim.Tests | `dotnet test src\Sim.Tests\Sim.Tests.csproj -m:1 --no-restore` | **388 passed / 0 failed / 0 skipped**，日志 `.sim_runs/simtests2.log` |
| legacy 回放 6/6 | `dotnet <Sim.Cli.dll> replay-check replays\<f>.json` | 全部 exit 0 PASS |
| MuJoCo 新模式回放 | `replay-check .sim_runs\...\replay-seed-42-mujoco-v3.json` | scores 8:3（expected 8:3）events 117/117，`PASS: replay reproduces the recorded match bit-for-bit.` exit 0 |
| `git diff --check` | `git diff --check` | exit 0 |
| Python 自测（含 Gym，本次收尾复跑） | `selftest.py --train-dir ... --dev-sweep ... --freeze ... --gym-check --out .sim_runs/score-block-v3-baseline-20260926b-codex/selftest-ac4-rerun.json` | **40 passed / 0 failed / 0 skipped**；`check_env` 与 seed 42 的 101 帧确定性检查通过 |

说明：完整 Sim.Tests 必须带 `-m:1 --no-restore`；默认并行会扇出大量 0 CPU 的 dotnet 进程并挂起。

六份 legacy 回放逐条结果：

| 回放文件 | 比分 | 事件 | 结果 |
| --- | --- | --- | --- |
| `arena-layout-audit-seed42.json` | 4:49 | 752/752 | PASS |
| `godot-parity-seed42.json` | 4:49 | 752/752 | PASS |
| `rotated-seed42.json` | 13:6 | 278/278 | PASS |
| `seed-42-pyus.json` | 5:1 | 38/38 | PASS |
| `seed-42.json` | 4:49 | 752/752 | PASS |
| `task-one-review-seed42.json` | 4:49 | 752/752 | PASS |

MuJoCo 新模式回放身份：`physicsBackend=mujoco`、`physicsEngineVersion=3.14.0`、`coreVersion=sim-core-1.0.2`、`seed=42`、`physicsModelSha256=1ad75271868e3068230ab5f154218f2f3bf3b028150ab2504a83823ae0a75308`；场景 `wushu-ring-2026`、`layoutVersion=arena-layout-v1`、`modelVersion=wushu-mjcf-v1`；`finalScores {us:8, them:3}`；`eventFingerprints` 117。文件 SHA-256 `656ea5ea7b7405fd58eff2b7b969fb7649114aeaa393881a094794535fea4fb7`，21,508 字节。

说明：本任务未修改 `Sim.Mujoco` 或 MJCF，按 `spec/sim/index.md` 不强制重录新模式回放；AC4 的「官方 MuJoCo 新回放通过」以在本任务代码上对该新模式回放执行 `replay-check` 并逐位通过作为证据。

**此前 Python 自测 2 项失败的根因**（保留历史记录；本次直接复跑已全绿）：

- 失败项 `checkpoint: discovery, SHA-256, step uniqueness`（`selftest.py:265` / `:269`）与 `CLI guard: pre-registered split runs once`（`selftest.py:463` / `:465`），报错均为 `PermissionError [WinError 5]`，指向 `%TEMP%` 下 `tempfile.TemporaryDirectory` 创建的目录。
- 最小复现证明：Python 3.12.10 在该环境把 `mkdtemp(mode=0o700)` 真正应用为「仅属主」ACL，而沙箱进程以不同身份运行，导致写入/清理被拒绝；对照实验中 `os.mkdir(p, 0o777)` 写入成功，`os.mkdir(p, 0o700)` 写入失败。
- 等价检查（绕过 `TemporaryDirectory`、不改仓库代码）通过：`EQUIV checkpoint discovery: PASS ([51200, 102400])`、`EQUIV CLI guard existing --out: PASS; rc=2`，产物在 `.sim_runs/score-block-v3-baseline-20260926b-codex/equiv-check/`。
- 其余 38 项全部通过，包含：split 路由与注册表（6 个命名 split、160 个 split seed 互斥、默认 `development_v3`、`--final-holdout` 仍指 4001–4010、`final_holdout_v3` 为 50 seed 盲集、已揭示留出集 analysis-only、自定义 seed 视为探索性）、`selection: untraceable score fails gate`、互斥参数 11 项、checkpoint 步数解析、冻结训练默认值（51200 / 20260925）、run-config 注册表与磁盘一致（9 个 checkpoint）、run-config split 版本、`progress.csv: 245 rows, 17 columns`、`episodes.monitor.csv: 926 rows`、run-config checkpoint 审计 ok、CLI guard（盲集需冻结、`--require-freeze` 作用域、已揭示集 analysis-only、split 混用、自定义 seed 复用）、`gym: check_env contract (observation_space=(11,))`、`gym: seed 42 determinism 101 frames bit-identical`、`selection: freeze matches the development sweep`。

收尾复跑的原始 `selftest.py` 结果保存在 `.sim_runs/score-block-v3-baseline-20260926b-codex/selftest-ac4-rerun.json`，并复制到任务内的 `evidence/selftest-ac4-rerun.json` 供归档核对；其 `passed=40`、`failed=0`、`skipped=0`。此前的等价检查只解释旧环境故障，不再承担 AC4 通过证据。

## 6. 范围与结论限制

- **特权状态**：11 维观测含仿真真值块坐标（`block_coordinates_are_simulator_ground_truth true`），策略直接读取目标块世界坐标。本结论只适用于该仿真特权状态，**不能据此声称真机部署或感知/视觉策略有效**。观测字段：`target_relative_forward/platform_side`、`target_relative_left/platform_side`、`target_x/platform_side`、`target_y/platform_side`、`us_forward_speed/vehicle_max_speed`、`us_yaw_rate/vehicle_max_turn_rate`、`us_on_platform`、`target_on_platform`、`remaining_match_time_ratio`、`us_x_from_platform_center/platform_half_side`、`us_y_from_platform_center/platform_half_side`。
- **单训练 RNG seed**：本轮只训练了 `train_seed 20260925` 一个 PPO 种子，未做多种子重复，因此「合格/不合格」的结论带有单 seed 的训练方差，不能当作算法稳定性的统计证据。
- **历史集合已揭示**：`legacy_development`、`legacy_final_holdout`、`development_v2`、`final_holdout_v2` 均为非盲或已揭示集，其评测只作诊断；本轮不宣称在已揭示集合上的新盲验通过。`final_holdout_v3` 为本轮唯一一次盲验，已按失败如实记录。
- **盲验失败即停止**：本轮不根据最终集换模型、不改门槛、不使用 `--force` 重判；若继续改进算法，应另建 split 并重新预注册。
- **未实现限速层**：`target_edge_speed_cap_v1` 未实现，也未被验证；不存在限速的阈值、上限、触发量与模式一致性证据。
- **`final_score_us_sum`/`final_score_them_sum` 不是门槛证据**：门槛只使用真实锁定目标 `BlockScore` 与我方 `Drop`；比赛最终比分仅供参考。
- **不晋升 `fidelity.json`**：本轮未改动保真度配置，新模式回放状态保持原样。
- **未验证项**：物理真机行为、感知输入策略、多种子方差、限速候选（未触发）、`Sim.Mujoco`/MJCF 改动后的重录与 MuJoCo 集成/协议测试（本轮无此类改动）。

## 7. 环境残留（如实记录）

本次执行期间 Windows 沙箱在部分临时目录上留下无法删除的空目录，均为环境问题、与仓库代码无关：

- `D:\project\robot-simulator\tmp38sgy5pc`：空目录，`git status` 报 `could not open directory 'tmp38sgy5pc/': Permission denied`，未被列为 untracked。
- 被 `.gitignore` 排除的残留：`.sim_runs\probe-plain`、`.sim_runs\selftest-tmp`、`.sim_runs\codey_tmp\*`、`.sim_runs\tmp69h1fpzh`、`.sim_runs\score-block-v3-baseline-20260926b-codex\equiv-check`。

这些目录未被 `git clean` 删除（用户明确禁止破坏性 Git 操作）；如需清理，需要提权或另行确认。

## 8. 交付边界

- 本轮**未推送**任何远端分支；GitHub 分支命名如需新建，按项目约定使用 `test/` 前缀。
- 收尾前工作树同时包含其他任务的改动（`.learnings/*`、`.trellis/.template-hashes.json`、已归档归属修复任务文件、`godot/src/ArenaVisualizer.cs`）；本任务只提交 `evaluate.py`、`selftest.py` 与仿真规范，其他改动保留原样。
- 本实验的执行与验收记录已完成；**归档表示这轮实验结束，不表示 PPO 通过了最终得分门槛**。下一轮算法修改需要另行预注册 split。
