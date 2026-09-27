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

## 4. Validation & Error Matrix

| 输入或状态 | 行为 |
| --- | --- |
| v3 最终集未带 `--analysis-only` | CLI 拒绝并提示该标志 |
| v3 最终集带 `--analysis-only` | 可分析，门槛资格恒 false |
| 自定义 seed 与任一命名 split 或训练池重叠 | `SplitError` 拒绝 |
| v4 最终集缺冻结记录、模型/seed/场景/CLI 哈希不符 | 在仿真前拒绝 |
| v4 运行索引已存在，包括 `started` 或失败记录 | 拒绝再次正式运行，`--force` 也不能绕过 |
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
