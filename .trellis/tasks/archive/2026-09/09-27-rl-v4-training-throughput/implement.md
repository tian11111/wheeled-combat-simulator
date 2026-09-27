# 执行计划

## 前置门槛

1. 读取 `.trellis/tasks/archive/2026-09/09-27-rl-v4-split-guard/report.md`：必须明确 Go。
2. 读取 `.trellis/tasks/archive/2026-09/09-27-rl-v4-baseline-profiling/report.md`：必须有全部重复测量、资源数据和 Go；单独记录原生 batch 的条件判定。缺失任何前置证据就保持本任务 planning。

## 顺序

1. 为 `train.py` 增加显式 `--train-seed`、独立输出目录、完整 manifest 与总 transition checkpoint 口径；默认 `n_envs=1` 行为保留。
2. 先用固定五 seed 做独立单环境并行套件，按同机规则重复至少三套，保存每套启动/结束时间、CPU/RSS、fault、模型和 checkpoint 哈希。
3. 若中位墙钟≤60 分钟且语义检查通过，记录下游策略 Go，停止提速探索。
4. 否则试 `SubprocVecEnv`，先检查 1/2/4 等适合本机资源的环境数，再对最有希望且语义合格的候选重复完整套件。保持 2048 transitions/update、51,200 transitions/checkpoint。
5. 若仍未达标，且 profiling 满足 IPC≥25% 与 CPU 未饱和，才实现原生 batch 的小规模等价性验证和完整套件。条件不满足则记录 No-Go 原生 batch，不为它改代码。
6. 写 `report.md`：逐候选原始样本索引、全部有效与无效套件、身份、轨迹/seed 隔离、checkpoint、测试结果及明确的**下游策略 Go/No-Go**。

## 验证与停止

- 固定 seed 的单环境确定性、Gymnasium 接口、向量与单环境的 reset/终止语义和裁判事件一致；检查 worker 种子流无重叠。
- 原生 batch 若实现，验证独立 `mjData`、固定动作轨迹、重复 reset、终止后行为、Sim.Tests 与旧回放。所有候选均要求 fault=0。
- Python 自测、受影响的 Sim.Tests、旧回放及 `git diff --check` 通过。测试或轨迹失败时修复本任务并重测，不启动多 seed 策略任务。
- 完整负面性能结论可完成本证据任务，但未达到 60 分钟中位数时下游策略 No-Go；不以单次最快套件代替中位数。
