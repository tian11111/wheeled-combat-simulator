# 执行计划

## 前置检查与开始门槛

1. 阅读归档证据 `.trellis/tasks/archive/2026-09/09-26-score-block-v3-edge-speed-retrain/report.md` 和本任务 PRD/design；确认 v3 盲集已运行且失败。若证据冲突或 split 已被额外使用，停止并出具 No-Go。
2. 任务仍为 planning 时只完善文档；开始实现须等任务按 Trellis 流程进入 `in_progress`。开始前检查 `splits.py`、`evaluate.py`、`selftest.py` 与归档结果，保留其他并行改动。

## 实施顺序

1. 在 `splits.py` 扩展注册表、split 版本、默认开发集、revealed/blind 集合和 usage 文案；加入 v4 开发与最终集的精确 seed，沿用现有 disjoint 检查。
2. 在评估解析、冻结校验和输出中将 v3 设为分析专用；增加 v4 盲验冻结协议与一次性状态守卫，所有拒绝路径采用 fail-closed，不回写历史产物。
3. 更新 `selftest.py` 的固定表和 CLI 测试：测试未带 analysis-only 的 v3 拒绝、analysis-only 输出不 eligible、v4 seed 路由/互斥、错误冻结、盲验重复执行拒绝。
4. 更新 RL README 中 split 表与命令，明确 v3 已揭示、v4 才是唯一盲验集；只记录已实现守卫，不宣称训练或盲验已完成。

## 验证和 Go 判定

1. `python -X utf8 controllers/score_block_rl/selftest.py`：全部 split 与 freeze 路由用例通过；若产物目录不可用，报告中把仅适用于真实产物的检查标为未执行，不可伪报通过。
2. 在项目训练虚拟环境运行 `python -X utf8 controllers/score_block_rl/selftest.py --gym-check`（依赖和 `dotnet` 可用时），确认环境契约；不可用须在报告中说明，不影响 split 单测结果但不能夸称此项通过。
3. `git diff --check` 通过。若代码触及 C# 或共享物理，超出本任务范围，回退该越界改动并报告。
4. 写入 `.trellis/tasks/09-27-rl-v4-split-guard/report.md`：变更摘要、测试命令/结果、split manifest、失败路径测试证据及 **Go/No-Go**。只有所有 split/freeze 守卫通过才能 Go。
5. **No-Go 时停止基线 profiling、训练提速、多 seed 策略和盲验任务**；不得用 `--force` 绕过守卫。Go 只允许下一阶段 profiling，不等于策略目标已经达到。

## 回滚点

保留历史 split 的 seed、归档评测 JSON 和报告；新增代码可由本任务提交单独回滚。若发现旧入口将已揭示 split 当盲集，保持防护并修复后重测，不可通过恢复旧逻辑解除守卫。
