# Evidence — 09-25 MuJoCo SCORE_BLOCK RL 试点（AC4 结案）

本目录保存试点轮（4001–4010 最终留出集）的**原始产物副本**，源目录为
`%TEMP%\robot-simulator-rl-11d-50k-clean-20260926`（Git 之外）。命令、参数与环境见
`../report.md`；重跑即可复现（模型权重本身不提交，SHA-256 见下）。

| 文件 | SHA-256 | 说明 |
|---|---|---|
| `final-holdout.json` | `6deb1b0aee7d6aec4f18be1b2d42e6f003450a285583e0ad2307101df1f8fc14` | **本轮最终留出集（4001–4010）逐 seed 原始结果**，`ac4_claim_eligible=true`；策略锁定目标真实 `BlockScore` 1（seed 4002）、我方 `Drop` 8；FSM 0 / 7 → AC4 失败 |
| `dev-evaluation.json` | `e5a33b690376dc346dfc9b623ab034d75a91b0a91a63aee174eb920eced7557a` | 开发验证集（3001–3010）逐 seed 结果；策略 1 得分 / 6 掉台，FSM 1 / 6 |
| `run-config.json` | `117c21c1ad27988dfadaa014b4a17e2aea1a527ea02e58c31adb5d54bdb1e67f` | 训练配置：51,200 步、SB3/PPO 默认参数、11 维观测定义、场景与 CLI 哈希、模型 SHA-256 |
| `episodes.monitor.csv` | `00455705e51acd95fa7b18337ead1fd80d69f05249031860ee3c03b0d10e0349` | 150 行逐集训练日志（SB3 `Monitor`） |

冻结候选身份（不得回溯改写）：

- 模型：`%TEMP%\robot-simulator-rl-11d-50k-clean-20260926\ppo_score_block.zip`
- `model_zip_sha256 = b943a92d2bee0095cd2be04535be07086ea4dbbeb61915afe15c4c23860104b7`
- 场景 `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3`
- CLI DLL `5ae11229095716c2d3e01709d0063debc13382d0b21cec134cf880a9de29eda4`

未提交（体积/约定原因）：`ppo_score_block.zip`。

## 与 checkpoint 轮的关系

第二轮盲验集（6001–6050）的原始结果保存在
`.trellis/tasks/archive/2026-09/09-26-score-block-ppo-checkpoint-round/evidence/final-holdout-v2.json`，
判定为 `new_round_blind_gate_passed=false`。两轮留出集原始结果均已随各自任务归档保存，
且**都不再作为后续模型的盲验集**（已揭示）。
