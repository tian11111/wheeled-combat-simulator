# SCORE_BLOCK RL split v4 契约

## 1. Scope / Trigger

改 `controllers/score_block_rl/splits.py`、`evaluate.py`、`train.py` 的 split 元数据、冻结或盲验路径时适用。v3 最终集已经运行并失败；不能因旧代码仍将它标为 blind 而重用。唯一预注册来源是 `splits.py`，历史报告不回写。

## 2. Signatures

```text
resolve_selection(split=None, custom_seeds=None, final_holdout=False) -> Selection
evaluate.py --split <name> [--analysis-only] [--select-candidate --freeze <path>]
evaluate.py --split final_holdout_v4 --model <zip> --require-freeze <path> --out <json>
```

默认 split 是 `development_v4`。`--final-holdout` 只别名 `legacy_final_holdout`，不指向 v4。

## 3. Contracts

| 集合 | 精确 seed | 用途 |
| --- | --- | --- |
| `development_v4` | 9001–9020 | 当前开发集，允许裁判事件选模 |
| `final_holdout_v4` | 10001–10050 | 当前唯一一次正式盲验 |
| `final_holdout_v3` | 8001–8050 | 已揭示，仅分析 |

`SPLIT_VERSION="score-block-split-v4"`；`BLIND_SPLITS` 只含 `final_holdout_v4`；`REVEALED_HOLDOUT_SPLITS` 含 legacy、v2、v3 最终集。结果字段 `is_blind_holdout` 与 `is_revealed_holdout` 描述运行前注册状态，二者互斥；已揭示集的 `analysis_only=true`、`gate_evidence_eligible=false`。正式 v4 结果另有 `blind_run_consumed=true` 和 `blind_run_index_path`。

冻结记录必须绑定 v4 开发/最终 split 及精确 seed、唯一模型 SHA-256/训练步数、场景和 CLI DLL SHA-256。当前工作树的正式运行索引为 `.sim_runs/score-block-final-holdout-v4-run.json`；开始前以独占创建消耗机会，失败或中断仍阻止重跑。该本地索引是操作守卫，归档报告仍是跨工作树的最终证据。

索引的失效面：`.sim_runs/` 在仓库 `.gitignore` 中忽略（本地评测产物不入库），因此索引**只对当前工作树有效** —— 换机器、重新 clone 或清理该目录后它不再阻止重跑。由此产生一条人工要求：正式 v4 盲验前须先核对仓库内是否已有 `final_holdout_v4` 的揭示证据（归档任务报告、结果 JSON、历史索引副本），有则视为该盲集已消费、不得再运行；索引只增不改，任何删除或改写都必须在任务归档报告里留证（`--force` 从不绕过索引）。

## 4. Validation & Error Matrix

| 输入或状态 | 行为 |
| --- | --- |
| v3 最终集未带 `--analysis-only` | CLI 拒绝并提示该标志 |
| v3 最终集带 `--analysis-only` | 可分析，门槛资格恒 false |
| 自定义 seed 与任一命名 split 或训练池重叠 | `SplitError` 拒绝 |
| v4 最终集缺冻结记录、模型/seed/场景/CLI 哈希不符 | 在仿真前拒绝 |
| v4 运行索引已存在，包括 `started` 或失败记录 | 拒绝再次正式运行，`--force` 也不能绕过 |
| 索引缺失，但归档报告/结果 JSON 已含 v4 揭示记录 | 人工判定为已消费：停止并留证，不得当作首次盲验 |
| `--freeze` 与 `--out` 同路径或冻结文件已存在 | 在写入前拒绝 |

## 5. Good / Base / Bad Cases

- Good：v4 开发集选出唯一候选并写冻结记录；在索引不存在时运行一次 v4 最终集，结果无论通过与否保留索引。
- Base：`final_holdout_v3 --analysis-only` 输出已揭示和门槛不合格标签；历史比较不改变 v3 失败结论。
- Bad：将 8001–8050 作为自定义 seed，或用 v3 冻结记录授权 v4；两者都必须在运行前失败。

## 6. Tests Required

`python -X utf8 controllers/score_block_rl/selftest.py` 断言精确 seed、互斥、默认路由、v3 分析资格、CLI 拒绝、冻结身份和一次性索引。可用 .NET CLI DLL 时加 `--gym-check` 验证 Gymnasium 与固定 seed 确定性。实际 50-seed v4 最终集只在候选冻结后的独立验收任务运行，本任务测试不得提前打开它。

## 7. Wrong vs Correct

```text
Wrong: 把 final_holdout_v3 留在 BLIND_SPLITS，或仅改 README 而不改 CLI 拒绝路径。
Correct: v3 进入 REVEALED_HOLDOUT_SPLITS，v4 成为唯一 blind；评测和输出从同一 Selection 元数据派生。
```

## 8. 复用训练产物做评测时的身份核对

复用既有训练产物（如吞吐套件）跑开发集评测前，按顺序核对：

1. **预注册字段一致**：训练 `run-config.json` 的训练 seed、`n_envs`、steps、`scenario_sha256`、依赖版本与当前评测环境一致。
2. **源码逐位一致**：`git diff <套件运行时提交>..HEAD -- src/ controllers/` 为空且工作区这两处无未提交改动。
3. **CLI DLL 哈希漂移不等于身份破坏**：各会话用临时 SDK 重编译，`cli_dll_sha256` 必然变化。哈希不同时必须在验收报告披露新旧哈希与原因，并用行为佐证补强：Sim.Tests 全绿、`replay-check` 逐位复现、`selftest.py --gym-check` 全绿、同一轮评测内 FSM 基线逐位一致。
4. **任一源码不一致 → 不得复用**，须按当前代码重新训练；不得把不同构建/配置的产物混进同一统计。

## 9. 复核既有 checkpoint 的诊断轨迹

`diagnose.py analyze --compare-recorded <evaluate.json>` 当前只读取 `models[0].per_episode_policy` 和 `fsm_baseline.per_episode`。若轨迹来自 sweep 中后续 checkpoint，先从原始评测 JSON 生成只保留该 checkpoint 的本地摘录，保留模型 SHA-256、训练步数、场景/CLI SHA-256 和 FSM 逐场数据；不要修改原始评测文件。否则脚本会把目标轨迹与第一个 checkpoint 比较，产生误导性的逐场 mismatch。

该核对精确比较 `locked_target_us_block_scores`、`us_drops`、`unowned_block_offs`、`policy_ticks`，并以 1e-6 容差比较 `total_reward`。跨 CLI DLL 哈希的诊断即使前四项逐场一致，reward 仍可能漂移；须分别列出每个字段的 mismatch，`recorded_cross_check.reproduces=false` 时不能声称完整复现，更不能据裁判计数相同推断逐 tick 轨迹相同。
