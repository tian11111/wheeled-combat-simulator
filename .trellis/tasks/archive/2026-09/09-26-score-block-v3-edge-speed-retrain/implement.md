# 实施清单：SCORE_BLOCK v3 修复后重训

> 2026-09-26 已全部执行完毕。此前一条「第 3 步仅到 440,320 步」的交接记录是上一轮中断时的陈旧状态：本轮已按同一独立输出目录完成整轮训练（实际 501,760 步），并完成开发集选模、冻结、单次盲验与 AC4 检查。结果见 `report.md`。

> 结果概览：基线在 `development_v3` 存在合格候选，R3 未触发，因此第 5–7 步（限速实现与限速重训）标记为不适用。`final_holdout_v3` 单次盲验得分门槛未通过（策略 2 < FSM 6），按 R5 停止，不换模型、不改门槛、不使用 `--force`。

1. [x] 记录当前 Git/依赖/CLI DLL/场景状态，保留其他人的工作区改动；核对归属修复提交与 v3 split。检查训练 venv、`pip check` 与 .NET SDK。
2. [x] 构建当前官方 MuJoCo CLI；用现有 `selftest.py --gym-check` 和归属定向测试确认训练入口。抽查锁定目标真实 `BlockScore` 的奖励为 +1.0，错误的 `BlockOff` 惩罚不再施于该事件。
3. [x] 无防护基线：按现有 `README.md` 命令请求 500,000 步，输出到独立且由 `.gitignore` 排除的 `.sim_runs/score-block-v3-baseline-20260926b-codex` 目录；核对 `run-config.json`（`status completed`、实际 501,760 步）、9 个 checkpoint 哈希、Monitor（926 行）和 `progress.csv`（245 行 / 17 列）。
4. [x] 仅在 `development_v3` 对基线 9 个 checkpoint + 最终模型做 deterministic sweep（7001–7020，20 seed），保留 FSM 和逐 seed 数据。合格候选 2 个（`rl_model_409600_steps.zip`、`ppo_score_block.zip`），按要求跳到第 8 步。
5. [ ] **不适用 / 未触发**：基线在 `development_v3` 有合格候选，R3 未触发，未在 `gym_env.py` 实现近沿正向限速，也未在 `train.py`/`evaluate.py` 增加 `--safety-mode`。默认行为与协议保持不变。
6. [ ] **不适用 / 未触发**：不存在限速层，因此没有阈值内/外、等于阈值、倒车、角速度、reset 首步、缺失距离或 train/eval 模式一致性的定向验证需求。`selftest.py --gym-check`（含 `check_env` 与固定 seed 确定性）仍作为 AC4 证据执行。
7. [ ] **不适用 / 未触发**：未启用限速候选，未做限速重训，也不需要因「仍无合格模型」而写失败报告。
8. [x] 冻结唯一合格候选，记录路径、步数、SHA-256、模式（无防护，无限速层）、场景/CLI 哈希及开发集汇总；冻结记录时间 2026-09-26T13:48:11+00:00，冻结时不得已有 v3 最终结果。
9. [x] 只运行一次 `final_holdout_v3` 8001–8050；逐 seed 核对真实锁定目标 `BlockScore`、我方 `Drop`、歧义、fault 和位姿事件交叉核验，不依据结果换模型。结果：策略得分 2 < FSM 6，`new_round_blind_gate_passed false`；掉台 25 ≤ FSM 29 通过；全 50 seed fault 0、归因歧义 0。
10. [x] 运行完整 Sim.Tests（388 passed / 0 failed / 0 skipped）、六份 legacy 回放（全 PASS）和官方 MuJoCo 新回放（8:3、117/117、bit-for-bit PASS）；`git diff --check` 通过。报告写明实际命令、哈希、吞吐（307.595 steps/s）、失败 seed（8029/8031）、特权状态和单 RNG 限制；不晋升 `fidelity.json`。
