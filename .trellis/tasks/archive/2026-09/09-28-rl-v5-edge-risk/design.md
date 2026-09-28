# 设计：RL v5 台沿风险奖励单变量

## 风险项

```
risk_t = −w · max(0, v_out_t − v0) · 1[dist_t < d0]
v_out_t = max(0, (dist_{t-1} − dist_t) / 0.05s)      # 逼近台沿速度 (m/s)
dist_t = Field.DistToNearestEdge(我方位置)            # 与 trace 同源
```

- 常数：`d0 = 0.27`、`v0 = 0.2`；`w` 由校准程序从 `{0.005, 0.01, 0.02}` 中选定并冻结（判据见 implement.md 步骤 2）。
- 豁免：本 tick 目标块接触记录含 `us` → `risk_t = 0`（复用与 `EdgeShapingApplies` 同源的接触读取，但语义独立）。
- 生效范围：policy 与 FSM 路径统一；NaN 距离或首 tick（无 prev）→ `risk_t = 0`。
- 量级锚点：`TargetReward=+1.0`、`OurDropPenalty=−1.0`、`EdgeShapingScale=0.1/m`、`StepCost=−0.0001/tick`。单场累计 |risk| ≤ 1.0 的判据保证风险项不压过得分信号。

## 代码落点

| 位置 | 改动 |
| --- | --- |
| `src/Sim.Cli/RlEnvCommand.cs` | ① 回退 `9d519fb`：Step() 的 edge shaping 恢复 `if (!IsNaN(after) && !IsNaN(before))` 原样，删除 `EdgeShapingApplies`/`TargetContacts`；② 新增风险项：`EpisodeState` 增加我方 edge_distance 逐 tick 状态（`PrevUsEdgeDistance`，reset 置 NaN），Step() 内计算 `risk_t` 并加入 reward；③ 新增纯函数 `EdgeRiskPenalty(prevDist, curDist, usOnTarget)` 便于定向测试 |
| `src/Sim.Core/RuntimeState.cs` | `EpisodeState`（或等价状态类）增加 `PrevUsEdgeDistance` 字段 |
| `src/Sim.Tests/RlEnvCommandTests.cs` | 删除归因门控测试，新增风险项测试（逼近/远离/带外/豁免/NaN 首帧） |
| `controllers/score_block_rl/splits.py` | 注册 `development_v5 = 11001–11020`；`development_v4` 移入已揭示集合（路由为 analysis-only）；`BLIND_SPLITS` 仍只含 `final_holdout_v4` |
| `controllers/score_block_rl/selftest.py` | split 路由/互斥/守卫断言同步 v5 |

## 与 v4 的身份差异

- reward 标记：`reward v3 (v2 + edge risk term)`（报告与 run-config 经由 CLI DLL 哈希区分）。
- 训练池/episode seed 流/PPO 超参/观测与 v4 完全一致 → 同 seed 与原版 3/5 基线可逐项配对对照。

## 回滚

风险项实现为独立提交；任何阶段失败可 `git revert` 单提交回到 v2 行为，不影响评测链与守卫。
