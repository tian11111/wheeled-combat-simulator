# 执行计划：父任务

1. 核对 `research/report-audit.md` 的仓库证据与公开资料版本，确定事实、假设和本轮边界。
2. 检查六项子任务的 PRD、设计和执行计划；确认每项的前置报告和 Go/No-Go 可由独立证据复核。
3. 按 `split-guard → baseline-profiling → training-throughput → split-multiseed → 条件 reward-credit → blind-gate` 的顺序执行。每一步开始前读取前置 `report.md`；无正向 Go 时保持后续任务 `planning`。
4. 集成验收只汇总实际完成的子报告。吞吐目标、4/5 开发集门槛或最终盲验任一失败，父任务不得宣称策略目标达成；负面报告原样保留。
5. 本次规划完成后只校验任务树、文档一致性、无 TBD、文件格式与 `git diff --check`；不运行 `task.py start`、训练或盲验。

## 回滚与变更控制

如需调整 seed、门槛或排名规则，应在任何 v4 开发/最终评估前显式修订父子任务并记录原因。最终集打开后不得修改冻结协议来重判本轮。归档 v3 报告和历史产物保持原样。
