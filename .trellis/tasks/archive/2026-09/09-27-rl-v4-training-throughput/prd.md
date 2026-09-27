# RL v4 五 seed 训练提速

## Goal

验证同一台 Windows x64 机器能否在 60 分钟内完成 5 个独立 PPO seed、每个 500k transitions 的训练套件；先测并行的五个单环境任务，再按预定门槛试 `SubprocVecEnv`，只有 IPC 瓶颈满足条件时才试原生 .NET batch。

## 前置报告与 Go / No-Go

- 必须先有 `.trellis/tasks/archive/2026-09/09-27-rl-v4-split-guard/report.md`，结论为 **Go**，并证明 v3 只能分析、v4 split 已隔离。
- 必须先有 `.trellis/tasks/archive/2026-09/09-27-rl-v4-baseline-profiling/report.md`，明确其测量数据完整、相同机器身份有效，并给出 IPC/CPU/RSS 瓶颈结论。profiling 的负面结论可完成任务，但缺数据不能 Go。
- split 守卫或性能测量缺失/No-Go：不运行本任务。正式吞吐目标失败是有效的负面研究结果，但其下游策略任务必须 No-Go/停止。
- 完整执行测量路线后，**下游策略 Go** 仅当 5×500k 套件中位总墙钟时间 ≤60 分钟且所有语义/故障/产物检查通过；否则报告 No-Go 并停止多 seed PPO、奖励实验、冻结和盲验。

## Requirements

- 每次训练支持显式 `--train-seed`、`--out` 和 `--n-envs`；默认保持 `--n-envs 1` 和当前单环境行为。固定策略训练 seed 为 `20260927、20260928、20260929、20260930、20261001`，每个模型独立产物目录和完整 identity manifest。
- 第一候选为五个 `n_envs=1` 独立 500,000-transition 训练进程同时运行；最少 3 个完整套件重复测量以计算墙钟中位数。五个模型使用固定训练 seed；重复套件只用于计时，目录不得互相覆盖。
- 若该路径中位数 >60 分钟，评估 `SubprocVecEnv` 候选；每次 PPO update 总 rollout 固定为 2048 transitions（按 `n_envs` 选择可整除的 `n_steps`），保留其余 PPO 参数、批量和 epoch 语义。
- checkpoint 每 51,200 总 transitions；向量环境中按 `n_envs` 调整 callback 调用间隔，并记录 `num_timesteps`/全局 transition 数，不能继续把 vector step call 当成单环境 transition。
- 仅在 profiling 与重复训练测量显示 JSONL IPC/序列化占 policy step 至少 25%、且 CPU 未饱和时允许评估原生 .NET batch。必须为每个并行槽位使用独立 `mjData`/episode runtime；共用 `mjModel` 只限现有工厂已证实安全的只读模型。
- 不改 GPU/MJX、算法、奖励、11 维观测、物理、FSM、场景或正式盲集。没有通过提速门槛不得继续策略实验。

## Acceptance Criteria

- [x] 至少 3 个同机器、相同训练配置的套件计时；每个套件恰有 5 个 seed、每个完成至少 500,000 transitions；报告中位数、逐套件墙钟时间和资源数据。见 `report.md` §3。
- [x] 任何被计入套件的训练均 `faults=0`、状态 completed、模型/checkpoint hash 和 run config 审计通过；缺失/失败工作不能静默移出统计。见 `report.md` §3.1、§4。
- [x] 固定 seed 可复现；5 个 seed 的 SB3 RNG 与 episode/worker seed 流互相隔离；输出目录独立；训练产物完整记录场景、CLI、split/代码、依赖、seed、n_envs、设备、PPO 参数和硬件身份。见 `report.md` §2、§4。
- [x] 向量候选保留每 update 2048 transitions、每 51,200 transitions checkpoint cadence；环境 seed、episode reset、终止/truncation 行为无意外改变。`--n-envs > 1` 路径已实现并由自测覆盖 cadence/seed 隔离，但候选一已达标故本轮未启用向量路径（见 `report.md` §6.1）。
- [x] 若执行原生 batch，单槽位轨迹与单环境对照一致，多个槽位重复运行确定，reset/终止状态互不串扰，Sim.Tests 和既有回放通过且所有槽位 fault=0。本轮未执行原生 batch：正式并行训练 CPU 已饱和，不满足前置条件，未改相关代码。
- [x] 达到 ≤60 分钟中位数才给 **下游策略 Go**。达到后不得宣称策略得分提升；未达到则此证据任务可完成，但下游策略明确 No-Go。实际中位数 21.945 分钟，给下游 Go；`report.md` §10。
