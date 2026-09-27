# 执行计划

1. 核对 split 守卫 Go、吞吐下游策略 Go、原版多 seed 报告不足 4/5。若原版已达标，报告“条件未触发”，保持本任务 planning，直接由原版候选进入冻结。
2. 收集固定 seed 轨迹，标注我方、对手、双方及无接触位移；在训练前把窗口、冲突归属、诊断字段与失败处理写进本任务报告的预注册部分。无法建立可靠规则则 No-Go。
3. 仅修改 edge progress shaping 的归因门控，增加对应定向测试；核对其他 reward 项和 scale、观测、算法、物理、FSM、场景哈希不变。
4. 用前三个固定训练 seed 各完成≥500k transitions，逐模型开发集评估和配对 FSM；不足 2 个通过即停止，不补跑后两 seed。
5. 前三 seed 至少 2 个通过时补齐后两 seed；输出五项通过/失败、真实得分和掉台、归因 trace 与原版同 seed 差异。
6. 跑 Python 自测、固定 seed 确定性、Gymnasium checker、受影响 Sim.Tests、旧回放、`git diff --check`。报告任何 fault 或缺测，不能判正向 Go。
7. `report.md` 写明确 Go/No-Go。只有 4/5 通过且证据完整，才可进入唯一候选冻结；否则停止本轮路线，保留负面研究结果。
