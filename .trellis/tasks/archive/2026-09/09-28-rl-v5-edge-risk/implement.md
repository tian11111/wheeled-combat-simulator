# 执行计划

1. 回放校准（训练前）：脚本回放既有 60 段诊断 policy 轨迹（`.sim_runs/score-block-v4-seed-behavior-diagnosis-20260928/seed-*/traces/*policy*.jsonl.gz`），对 `w ∈ {0.005, 0.01, 0.02}` 计算：(a) 失败模型每次掉台前 25 tick 窗口的每 tick 平均罚值；(b) 胜出模型全程每 tick 平均罚值；(c) 任一单场最大累计 |risk|。选满足"失败 ≥ 胜出 ×3 且 (c) ≤ 1.0"的 w 并冻结；无解则报告 No-Go、不训练。
2. 核对冻结后的预注册（公式/常数/判据结果）写入报告，任何后续修改都必须在报告中留痕。
3. `splits.py` 注册 `development_v5 = 11001–11020`、`development_v4` 转已揭示仅分析；`selftest.py` 同步断言；跑 selftest 全绿。
4. 回退归因门控（恢复 v2）+ 实现风险项（纯函数 + `EpisodeState` 状态 + 定向测试）；单变量 diff 核对；Sim.Tests 全绿。
5. 提交后三 seed 训练（20260927/28/29，500k、n_envs=1、并行）至 `.sim_runs/score-block-v5-edge-risk-<date>/seed-*/`；核对 501,760 transitions、fault=0、审计通过。
6. 三路 `evaluate.py --split development_v5 --select-candidate`；逐 seed 门槛判定（<2/3 停止，不补跑）；≥2/3 补 20260930/20261001。
7. 逐 seed 与原版对照（同 seed 的 score/drop 差值、掉台位置/时刻、门控触发类诊断），防"只提升生存牺牲得分"。
8. 验证：Python 自测、Gymnasium checker、固定 seed 确定性、受影响 Sim.Tests、旧回放 `replay-check`、`git diff --check`。
9. `report.md` 写明 Go/No-Go：≥4/5 进入冻结协议（唯一候选 + `final_holdout_v4` 一次）；否则负面报告终止本轮，盲集保持 0 消耗。

## 回滚点

- 步骤 4 的实现为独立提交，可单独 revert 回 v2。
- 训练产物目录独立命名，失败轮次不影响 v4 产物。
