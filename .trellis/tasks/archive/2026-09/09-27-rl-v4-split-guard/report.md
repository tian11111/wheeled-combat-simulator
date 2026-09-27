# RL v4 盲集防复用与 split 预注册验收报告

- 结论：**Go（仅解锁下一项基线与瓶颈测量）**
- 分支：`test/rl-v4-plan`
- 范围：split registry、评测/冻结守卫、自测、训练产物的 split usage 元数据、RL README 与 Sim 规范；未训练模型、未打开 v4 最终集。

## 前置事实

归档 `.trellis/tasks/archive/2026-09/09-26-score-block-v3-edge-speed-retrain/report.md` 第 12、16 行记录 `final_holdout_v3` 已正式运行一次且得分门槛失败：锁定目标 `BlockScore` 为 PPO 2、配对 FSM 6；我方 Drop 25、FSM 29。实施前 `splits.py` 仍将它列为唯一 blind，故必须降级为已揭示集合。

## 实现与验收证据

| 要求 | 结果 |
| --- | --- |
| v3 防复用 | `final_holdout_v3` 进入 `REVEALED_HOLDOUT_SPLITS`；CLI 未带 `--analysis-only` 拒绝，分析结果 `gate_evidence_eligible=false`；v3 冻结记录不能授权 v4。 |
| v4 预注册 | `SPLIT_VERSION=score-block-split-v4`；`development_v4=9001–9020`（20 个）、`final_holdout_v4=10001–10050`（50 个）。8 个命名集合共 230 个 seed，与训练 episode 池互斥。 |
| 默认及自定义路由 | 默认开发集为 v4；`--final-holdout` 仍只指旧 4001–4010。自定义 seed 与任一命名 split 或训练池重叠均拒绝。 |
| 冻结与一次性运行 | 正式 v4 评测要求唯一模型及匹配的 v4 split、开发/最终 seed、模型训练步数/SHA-256、场景和 CLI 哈希。开始前独占创建 `.sim_runs/score-block-final-holdout-v4-run.json`；即使中断也消耗本工作树的正式运行机会，`--force` 不能绕过索引。 |
| 输出语义 | v4 运行前 `is_blind_holdout=true`、`is_revealed_holdout=false`；结果另标 `blind_run_consumed=true` 和索引路径。v3 分析结果 blind=false、revealed=true、eligible=false。 |

训练入口只同步 `evaluation_split_usage` 的注册表元数据，未改 PPO、训练 seed、观测、reward、物理、FSM 或场景。自测的自由探索 seed 从 9001–9010 迁至 11001–11010，避免碰撞新开发集。具体命令、字段和拒绝矩阵见 `.trellis/spec/sim/rl-split-contract.md`。

## 验证

| 命令/检查 | 退出码与结果 |
| --- | --- |
| `python -X utf8 controllers/score_block_rl/selftest.py --gym-check --out .trellis/tasks/09-27-rl-v4-split-guard/research/selftest-results.json` | 0；38 passed、0 failed、8 skipped。原始结果：[selftest-results.json](research/selftest-results.json)。split、CLI 拒绝、冻结身份、索引重复拒绝均通过。 |
| `python -m compileall -q controllers/score_block_rl` | 0。 |
| `python ./.trellis/scripts/task.py validate .trellis/tasks/09-27-rl-v4-split-guard` | 0；implement/check 上下文清单各 4 项有效。 |
| `git diff --check` | 0。 |
| `.sim_runs/score-block-final-holdout-v4-run.json` 存在性 | `False`；本任务没有实际打开最终集。 |
| `dotnet build RobotSimulator.sln -m:1` | 1；当前主机的 `dotnet` 只有运行时，提示 `No .NET SDKs were found`。 |

8 个 skipped 中，5 项需要本任务不生成的训练产物，1 项需要尚未产生的 v4 开发集 sweep；另 2 项 Gymnasium `check_env`/seed-42 实机桥检查因当前工作树无 CLI DLL 且主机缺 .NET SDK 未执行。它们**不是通过结果**。本任务只改变 Python split/评测防护；下一项 profiling 在实际运行环境须先构建 CLI 并验证 Gymnasium 契约。

## Go 范围与后续约束

split、冻结和一次性索引的定向验收通过，准许开始 `09-27-rl-v4-baseline-profiling` 的前置准备与环境测量。**此 Go 不表示吞吐目标、策略得分或 v4 盲验通过。** 当前索引是本工作树的自动守卫；跨工作树还必须以归档报告和最终盲验任务的冻结记录核对，不能把缺少本地索引视为“从未打开”。若后续发现 v4 集已在别处使用，应按下游任务 No-Go 处理。
