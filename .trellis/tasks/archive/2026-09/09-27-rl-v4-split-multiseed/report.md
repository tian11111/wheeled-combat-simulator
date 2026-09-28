# RL v4 多训练 seed 原版 PPO 基线 — 验收报告

- 结论：**No-Go（负面基线）**：5 个训练 seed 中 **3/5** 通过 `development_v4` 门槛，低于预注册的 **4/5** 直接冻结门槛。不冻结候选、不打开 `final_holdout_v4`；按设计只允许进入条件性任务 `09-27-rl-v4-reward-credit`，其"完整负面基线（通过数 <4/5）"前置由本报告满足。
- 复用：预指定吞吐任务 suite-01 的五份训练产物，逐 seed 身份校验全部通过，未重新训练。
- 评测时间：2026-09-28（20 个开发 seed × 每 seed 10 个模型 = 200 场 deterministic PPO + 每调用 20 场配对 FSM，五路并行总墙钟约 10 分钟）。

## 0. 结论摘要

| 项 | 结果 |
| --- | --- |
| 通过训练 seed 数 | **3/5**（20260929、20260930、20261001） |
| 直接冻结 Go（≥4/5） | **不成立** |
| 候选冻结记录 | 未写入（本任务不产生 freeze 文件；`--select-candidate` 未配 `--freeze`） |
| `final_holdout_v4` 消耗 | **0 次**（运行索引未创建，盲集保持锁闭） |
| 下一步 | 仅允许 `09-27-rl-v4-reward-credit`（单变量奖励归因实验） |

## 1. 前置报告与门槛

| 前置 | 状态 |
| --- | --- |
| `.trellis/tasks/archive/2026-09/09-27-rl-v4-split-guard/report.md` | Go；v4 开发集 9001–9020 与盲集 10001–10050 已预注册 |
| `.trellis/tasks/archive/2026-09/09-27-rl-v4-training-throughput/report.md` | 下游策略 Go；中位墙钟 21.945 分钟 ≤ 60 分钟，5/5 run `faults=0` |

## 2. 产物复用与身份

### 2.1 套件预指定

预指定 `.sim_runs/score-block-v4-independent-throughput-20260927/suite-01`。依据：吞吐报告第 4 节证明三套有效套件逐 seed 的模型状态哈希逐位一致（suite 间可复现），套件选择不影响模型内容；本任务不从重复计时套件中挑优。

### 2.2 逐 seed 产物校验

| 训练 seed | status | transitions | faults | audit.ok | checkpoints | 最终模型 |
| --- | --- | --- | --- | --- | --- | --- |
| 20260927 | completed | 501,760 | 0 | true | 9 | 有 |
| 20260928 | completed | 501,760 | 0 | true | 9 | 有 |
| 20260929 | completed | 501,760 | 0 | true | 9 | 有 |
| 20260930 | completed | 501,760 | 0 | true | 9 | 有 |
| 20261001 | completed | 501,760 | 0 | true | 9 | 有 |

501,760 = 245 × 2048（500,000 向上取整到 PPO rollout 粒度），≥ 500k 要求满足。训练身份（`run-config.json`）：五个固定 RNG seed、`n_envs=1`、11 维特权观测、reward v2、`scenario_sha256 = 52ec978e…`（与当前仓库场景逐位一致）、episode seed 流按 v4 互斥分区（每 seed 200–201 个，合计 1001 个不重复）。

### 2.3 评测环境与哈希披露

- Python 依赖与训练时完全一致：`torch 2.13.0+cpu`、`stable-baselines3 2.9.0`、`gymnasium 1.3.0`、`numpy 2.5.0`、Python 3.12.10。为让 selftest 的 profiling 辅助检查可导入，向 venv 补装了 `psutil 7.2.2`（不在 `requirements.txt` 锁定清单内，评测与训练路径均不导入该模块）。
- **CLI DLL 哈希差异披露**：训练产物记录 `cli_dll_sha256 = 5a94e6e0…`，本次评测实际加载 `298eb248…`。原因：本会话以便携 SDK 8.0.425 重新编译了解决方案（源代码未变——`git diff 1b4fb92..HEAD -- src/` 为空，工作区 `src/`、`controllers/` 无任何改动；`scenario_sha256` 不变）。行为一致性佐证：Sim.Tests 389 项全绿（含固定 seed 环境回归）、`replay-check replays/seed-42.json` 逐位复现 PASS、selftest 47 项全绿（含 Gymnasium `check_env` 契约与 seed-42 reset/action 确定性）、且五次独立评测的 FSM 基线逐位一致（见 §3）。本报告结论方向（通过数不足）不受该差异影响，但按证据规则如实记录。

## 3. development_v4 评测结果

配对方式：同 seed、同首个 `SCORE_BLOCK` 入口（reset 预推进）；FSM 基线与模型无关（每调用每 seed 一次，五次调用的逐 episode 数据 SHA-256 摘要全部为 `159b824c…`，逐位一致）。单模型合格条件（evaluate.py 门控）：锁定目标真实 `BlockScore` ≥ 1、总分不低于配对 FSM、我方 `Drop` 不高于 FSM、得分归因可追溯（`traceability_ok`）且 `controller_faults = 0`。训练 seed 通过 = 存在至少一个合格模型。

| 训练 seed | 通过 | 候选 checkpoint | steps | 锁定目标得分（我方/FSM） | 我方 Drop（/FSM） | fault |
| --- | --- | --- | --- | --- | --- | --- |
| 20260929 | ✅ | `rl_model_204800_steps.zip` | 204,800 | **9** / 5 | **0** / 14 | 0 |
| 20260930 | ✅ | `rl_model_460800_steps.zip` | 460,800 | **6** / 5 | **12** / 14 | 0 |
| 20261001 | ✅ | `rl_model_102400_steps.zip` | 102,400 | **5** / 5 | **12** / 14 | 0 |
| 20260928 | ❌ | （最佳 `rl_model_358400_steps.zip`） | 358,400 | 4 / 5 | 12 / 14 | 0 |
| 20260927 | ❌ | （最佳 `rl_model_460800_steps.zip`） | 460,800 | 3 / 5 | 12 / 14 | 0 |

- **失败模式**：两个未通过 seed 的全部 checkpoint 都满足"有得分、我方 Drop 不高于 FSM"，但**总锁定目标得分低于 FSM（3–4 vs 5）**，即 `locked_target_scores_not_below_fsm = false`；无归因歧义（`ambiguous_attribution_episodes = 0`）、无 fault。
- 跨 seed 排序（design.md：`(BlockScore−FSM)` 降序 → `(FSM Drop−我方 Drop)` 降序 → checkpoint step 升序，平局按训练 RNG seed 升序）：20260929 → 20260930 → 20261001。逐 seed 候选与 evaluate.py 自身 `selection.candidate` 一致（聚合脚本交叉核对无分歧）。
- 分布：通过 seed 候选得分 {9, 6, 5}、我方 Drop {0, 12, 12}；未通过 seed 最佳得分 {4, 3}。五个训练 seed 只是稳定性初筛，本报告不将跨 seed 区间描述为强总体保证。

## 4. 门槛判定

预注册直接冻结门槛为"至少 4/5 个训练 seed 的模型分别通过开发集 20 场门槛"。实测 3/5：

1. **不冻结唯一候选**，不产生 freeze 记录（冻结协议属于 `09-27-rl-v4-blind-gate`，仅在其前置满足时执行）。
2. **不打开 `final_holdout_v4`**：本轮盲集 0 次消耗，未创建运行索引。
3. 保留本负面基线报告；`09-27-rl-v4-reward-credit` 的前置"原版未达 4/5 的完整负面基线"由本报告满足。

## 5. 验证

| 检查 | 结果 |
| --- | --- |
| `controllers/score_block_rl/selftest.py`（含 `--gym-check`） | **47 passed, 0 failed**（check_env 契约、seed-42 reset/action 确定性、split 守卫、checkpoint 审计、profiling `--quick` 端到端） |
| `dotnet test src/Sim.Tests` | **389 passed, 0 failed** |
| `replay-check replays/seed-42.json` | PASS，`scores 4:49 events 752/752` 逐位复现 |
| `git diff --check` | clean |
| 旧回放/受影响测试范围 | 本任务未修改任何 .NET/Python 代码；上述为环境与身份佐证 |

## 6. 下一步

- `09-27-rl-v4-reward-credit`：前置已满足，但启动前仍须先完成其自身门槛——用轨迹定义可审计的接触归因规则（若无法可靠区分我方接触/对手接触/自主运动，则报告 No-Go，不训练变体）。
- `09-27-rl-v4-blind-gate`：保持 planning，前置不满足（两条路线均未达 4/5 前不得运行最终集）。
- 父任务 `09-27-rl-v4-experiment-roadmap` 不得因本负面结果宣称策略目标达成；负面报告原样保留。

## 7. 证据索引

| 证据 | 位置 |
| --- | --- |
| 5 份逐 seed 扫描原始 JSON/日志 | `.sim_runs/score-block-v4-split-multiseed-20260928/dev-sweep-seed-*.json|.log` |
| 聚合脚本与摘要 | `.trellis/tasks/09-27-rl-v4-split-multiseed/evidence/aggregate_multiseed.py`、`multiseed-summary.json` |
| 训练产物（复用） | `.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-*/` |
| 训练身份 | 各 seed 目录 `run-config.json`（seed、n_envs、transitions、scenario/CLI 哈希、依赖版本） |

## 8. Go / No-Go

**No-Go（负面基线）**：3/5 < 4/5，直接冻结路线关闭。按预注册分支仅解锁 `09-27-rl-v4-reward-credit`；其若达 4/5 方可进入候选冻结与一次盲验，否则本轮路线终止。声明：11 维观测含仿真真值块坐标（特权状态），本试点全部结果不代表真机可部署性；最终比分与 episode 回报只作参考，门槛判定只用真实裁判事件。
