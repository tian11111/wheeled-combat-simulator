# RL v4 基线与吞吐测量

## Goal

在同一台机器上建立可复核的基线与分阶段性能数据，判断耗时来自 MuJoCo reset/step、PPO optimizer、Python/.NET JSONL IPC，还是 CPU/内存资源；结论用于决定后续是否试向量环境或原生 batch。

## 前置报告与 Go / No-Go

- 必须先有 `.trellis/tasks/09-27-rl-v4-split-guard/report.md` 且结论为 **Go**，确认 v3 仅分析、v4 seed 已注册且守卫测试通过。
- 必须核对 `.trellis/tasks/archive/2026-09/09-26-score-block-v3-edge-speed-retrain/report.md` 中的单次结果（307.595 steps/s、500k 请求步、Windows 11/Python 3.12、CPU、n_envs=1）；该值是历史观察，不是性能承诺或统计基线。
- **Go**：按本 PRD 输出所有预定阶段的重复原始样本、机器/依赖/场景身份、p50/p95 和瓶颈结论。结论可为“未发现 IPC 瓶颈”，但这仍是成功的证据任务。
- **No-Go**：缺少 split guard Go、测量类别/重复轮次/机器信息不全、测量有 fault 或不可复现，或把一次 307.595 steps/s 当成当前基准。No-Go 时不得启动吞吐优化任务。

## Requirements

- 固定并记录机器型号/CPU逻辑核数、OS、Python/.NET、依赖、MuJoCo 场景哈希、CLI 哈希、训练配置及磁盘/电源状态；同一比较不得混用机器或配置。
- 预热后重复测量完整 reset、仿真 step、JSONL 请求-响应 IPC、PPO rollout/optimizer 更新，并在各阶段记录 wall time、process/host CPU 和 RSS。
- 每类至少 5 个独立样本轮；性能分位数由原始样本计算，不只记录汇总值。保存到 Git 忽略的 `.sim_runs/`，任务报告引用原始数据路径。本任务只做固定长度 profiling，不要求跑完整 5×500k 训练套件。
- 使用 v4 开发 split/训练池隔离契约；性能探针不运行 final holdout、不改 reward/观测/策略，也不选择候选模型。
- 产出明确瓶颈归因和后续动作门槛；不得承诺外部研究报告给出的并行加速倍数或工期。

## Acceptance Criteria

- [ ] reset、step、IPC、PPO rollout/optimizer 固定长度探针的时间/CPU/RSS 均有 warm-up 规则、至少 5 轮原始样本、p50/p95 与样本计数。
- [ ] IPC 纯往返样本使用持久 JSONL 进程和协议 payload；在线 step/reset 的总延时与进程内模拟耗时分别记录，方法差异写清。
- [ ] 每个性能结果可追溯到相同机器、场景/CLI/dependency 哈希和 run config；历史 307.595 steps/s 只作为单次旧观测注明。
- [ ] 无 fault、超时、样本丢失；如发生则标记无效并说明重测，不能从统计中静默剔除。
- [ ] 报告明确 IPC/序列化占策略 step 时间的比例、CPU 是否饱和、内存是否限制并给出 **Go/No-Go**。结论为负面可以 Go 完成本证据任务，但不能声明吞吐达标或提速已实现。
- [ ] 完成本任务后才允许启动 `.trellis/tasks/09-27-rl-v4-training-throughput/`；若测量不完整则禁止启动。
