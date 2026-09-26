# Evidence — 09-26 SCORE_BLOCK PPO checkpoint 轮

以下文件是 2026-09-26 本机一次训练/评测的**原始产物副本**，源目录为
`%TEMP%\score-block-rl-v2-500k-20260926`（Git 之外）。命令、参数与环境见
`../report.md` 第 9 节；重跑即可复现（除模型权重本身不提交）。

| 文件 | 来源 | 说明 |
|---|---|---|
| `run-config.json` | `train.py` | 501,760 步、9 个 checkpoint 的步数与 SHA-256、`checkpoint_audit`、split v2 元数据、依赖版本、场景/CLI 哈希 |
| `progress.csv` | `CSVOutputFormat` | 245 行 PPO 训练诊断（含 `train/approx_kl`、`train/clip_fraction`、`train/explained_variance`、`train/value_loss`、`time/fps`） |
| `episodes.monitor.csv` | SB3 `Monitor` | 728 集逐集裁判信息（seed、entry_tick、us_block_scores、us_drops、faults、比分…） |
| `dev-v2-sweep.json` | `evaluate.py --split development_v2 --select-candidate --freeze` | 10 个模型的逐 seed 配对评测、三项门槛、排序与唯一候选；FSM 基线 20 集 |
| `candidate-freeze.json` | 同上 `--freeze` | 冻结记录（候选路径/步数/SHA-256、场景与 CLI 哈希、最终集 seed 列表） |
| `final-holdout-v2.json` | `evaluate.py --split final_holdout_v2 --require-freeze` | 6001–6050 单次盲验：50 集策略 + 50 集 FSM 逐 seed 事件、门槛结论 `new_round_blind_gate_passed=false` |
| `selftest-full.json` | `selftest.py --train-dir … --dev-sweep … --freeze … --gym-check` | 33/33 定向检查（split 路由/互斥、checkpoint 哈希、CSV、盲验守卫、Gymnasium） |
| `replay-seed-42-mujoco.json` | `Sim.Cli replay-record --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json` | 官方 MuJoCo seed 42 回放（8:3、117 events），`replay-check` 逐位通过 |

未提交（体积原因）：`checkpoints/rl_model_*_steps.zip`、`ppo_score_block.zip`。
其 SHA-256 全部记录在 `run-config.json.checkpoints[]` 与
`candidate-freeze.json.candidate.sha256`（候选
`rl_model_307200_steps.zip = 6ed360757633d63553978285e1455c980f66993a3685ae727ef6ddc4ed2bd829`）。
