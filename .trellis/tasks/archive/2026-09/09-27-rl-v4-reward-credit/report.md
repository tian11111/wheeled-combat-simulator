# RL v4 奖励归因单变量实验 — 报告

结论：**No-Go（负面结果，本轮路线终止）**。三 seed 筛查仅 **1/3** 通过（< 2/3 停止线），按预注册规则立即停止、不补跑后两个 seed（最好情形 1+2=3 仍 < 4/5）；`final_holdout_v4` 全程封存（0 次消耗）。假设"边缘进展奖励缺少我方接触归因影响真实得分"未获支持。

## 1. 前置与条件触发

| 前置 | 状态 |
| --- | --- |
| `09-27-rl-v4-split-guard/report.md` Go | ✅ |
| `09-27-rl-v4-training-throughput/report.md` 下游策略 Go | ✅ |
| `09-27-rl-v4-split-multiseed/report.md` 完整负面基线（<4/5） | ✅（3/5 通过） |

原版 3/5 < 4/5，本任务条件**触发**。

## 2. 归因规则预注册（训练前冻结，2026-09-28）

单变量边界：reward v2 的 step cost、目标得分/掉台/`NotOurs` 惩罚、`EdgeShapingScale` 系数全部不变；**唯一改动**是 edge progress 项增加归因门控。观测仍为 11 维特权状态；门控只影响 reward，不向 actor 观测注入任何信息（含未来信息与裁判真值）。

规则（对 policy 与 FSM 路径统一生效）：

1. **门控对象**：仅 `reward += EdgeShapingScale * (edgeBefore - edgeAfter)` 这一项（含正负两个方向）。
2. **归因判据（同 tick）**：取本 tick 目标块的接触记录 `BlockRuntime.ContactThisStep`（逐子步 `(role, t)`，与 trace 的 `blocks[].contacts` 同源）。当且仅当记录**非空且全部角色为 `us`**（含单机器人多点接触）时，该项按原式计入；其余情形（仅 `them`、双方同 tick、无接触）该项记 0。
3. **窗口**：仅同 tick，不做跨 tick 窗口。依据见 §3：policy 路径的无接触正向位移在我方接触后 5 tick 内出现的比例为 **0.0–0.1%**，放宽窗口无可回收信号；FSM 路径同 tick 门控保留 90.8% 的正向位移。
4. **冲突归属**：同一 tick 出现双方角色 → 该 tick 记 0（保守剔除；可行性数据中占 policy 正向位移 15.0%）。
5. **失败/缺数据处理**：`target_index < 0` 或 edge 距离为 NaN 时维持现有 NaN 守卫（该项本就不计）；接触数据在引擎内始终可得，不依赖 opt-in trace。未知角色串按"非我方"处理（防御分支，可行性数据中未出现）。
6. **诊断字段**：不改默认 JSONL 响应形状（trace/timing 先例：默认逐字节不变）。"触发门控比例"在训练完成后用 `diagnose.py collect` 轨迹按 §3 同一脚本口径统计，不进 Monitor CSV、不改 `INFO_LOG_FIELDS`。
7. **身份**：改动会使 CLI DLL 哈希变化，训练/评测加载同一构建并按 rl-split-contract §8 记录与佐证；reward 变体在本报告记为 "reward v2 + edge attribution gate"。

## 3. 可行性研究（预注册依据）

数据：`diagnose.py collect --split development_v4 --mode both --max-episodes 4`，模型为原版通过 seed 20260929 的合格候选 `rl_model_204800_steps.zip`，dev seeds 9001–9004，共 8 段逐 tick 轨迹（`.sim_runs/score-block-v4-attribution-feasibility-20260928/traces/`）。

目标块正向位移（toward edge）按同 tick 接触角色分类：

| 类别 | policy 路径 ticks / 正向位移（占比） | FSM 路径 ticks / 正向位移（占比） |
| --- | --- | --- |
| 仅我方 | 1116 / 0.5304（**28.2%**） | 727 / 4.0118（**90.8%**） |
| 仅对手 | 1645 / 0.6437（34.3%） | 163 / 0.1748（4.0%） |
| 双方 | 418 / 0.2815（15.0%） | 16 / 0.0092（0.2%） |
| 无接触 | 5206 / 0.4226（22.5%） | 1051 / 0.2240（5.1%） |

- policy 路径仅我方可归因的正向位移占比 28.2%，信号存在且非平凡；现有 v2 把其余 71.8%（对手/争议/惯性）同样计酬，正是本实验要剔除的污染。
- policy 路径无接触正向位移**几乎不在我方接触后出现**（≤1 tick 0.0%、≤3 0.0%、≤5 0.1%）——惯性滑行主要源于对手接触的余波或漂移，放宽窗口不能把它归给我方。
- 结论：**同 tick"仅我方"门控可行**；数据不足或归因模糊的停止条款未触发（未知角色 0 例，双方占比 15% 属可保守剔除的明确语义）。

分析脚本：`.trellis/tasks/09-27-rl-v4-reward-credit/evidence/attribution_feasibility.py`。

## 4. 实现与测试

- 门控实现：`src/Sim.Cli/RlEnvCommand.cs` 新增 `TargetContacts` / `EdgeShapingApplies`（纯函数：接触记录非空且全部角色为 `us`），`Step()` 的 edge shaping 项以之门控；policy 与 FSM 路径统一。提交 `9d519fb`。
- 定向测试：`EdgeShaping_AppliesOnlyWhenAllTargetContactsAreOurs`（us 单点/多点、空、仅对手、双方、未知角色六种组合）。
- 单变量核对：`git diff` 仅含 RlEnvCommand.cs（+18/−1）与测试文件；scale、step cost、事件惩罚、观测、物理、FSM、场景哈希均不变。
- 验证：Sim.Tests **390 passed**（389+1 新增）、selftest **44 passed**、50 步冒烟正常；`git diff --check` clean。

## 5. 训练与评测

三 seed 筛查（RNG `20260927/28/29`，各 501,760 transitions、`n_envs=1`、fault=0、9 checkpoint，产物 `.sim_runs/score-block-v4-reward-gate-20260928/seed-*/`），`development_v4` 20 场 deterministic + 配对 FSM：

| 训练 seed | 门控版（最佳模型得分/掉台） | 原版（同 seed 最佳） | 通过 |
| --- | --- | --- | --- |
| 20260927 | 3 / 8（`ppo_score_block.zip`） | 3 / 12（❌） | ❌ |
| 20260928 | **5 / 7**（`rl_model_358400_steps.zip`） | 4 / 12（❌） | ✅ |
| 20260929 | 4 / 3（`ppo_score_block.zip`） | **9 / 0**（✅） | ❌ |

- **通过数 1/3 < 2/3**：触发预注册停止条款，不补跑 `20260930/20261001`（最好情形 1+2=3 仍不足 4/5）。同一筛选集上原版亦为 1/3（仅 20260929 通过），门控没有带来跨 seed 改善，且把原版唯一通过 seed 打残（9/0 → 最佳 4/3）。
- **触发门控比例**（预注册 §2.6：diagnose collect 轨迹、同 §3 口径、dev seeds 9001–9004）：门控版策略目标块正向位移中"仅我方"占比 **0.0% / 6.6% / 12.1%**（原版参考 28.2%）。门控在剔除对手/惯性噪声的同时，也移除了策略推块的主要激励——学到的策略几乎不再自身推动目标块，与通过数下降一致。
- 归因 trace 与逐 episode 数据：`.sim_runs/score-block-v4-reward-gate-20260928/{dev-sweep-seed-*.json,gate-ratio-traces/}`。

## 6. Go / No-Go

**No-Go**：筛查 1/3 < 2/3，按预注册立即停止；本轮 RL v4 路线（原版 → 奖励归因变体）终止，不进入候选冻结，`final_holdout_v4` 未打开（0 次消耗，运行索引未创建）。负面结果有效并原样保留；父任务不得宣称策略目标达成。声明：11 维观测含仿真真值块坐标（特权状态），本试点不代表真机可部署性；门槛判定只用真实裁判事件，reward/PPO loss 仅作诊断。
