# RL v4 基线与吞吐测量 — 验收报告

- 结论：**Go（本证据任务通过；IPC 占比门槛以 0.2605 勉强满足，CPU 未饱和 → 允许下一阶段尝试原生 .NET batch，但该门槛余量很小，须在吞吐任务复测）**
- 分支：`test/score-block-ppo-checkpoint-round`
- 原始数据：`.sim_runs/score-block-v4-profile-20260927/`（Git 忽略目录）
- 范围：固定长度 profiling 探针与 `.NET` 计时接口；未运行 5×500k 训练套件，未打开 v4 开发集/最终盲集，未改 reward、观测、动作、FSM、物理或场景。

## 0. 结论摘要

| 项 | 结果 |
| --- | --- |
| 测量完整性 | env / ipc / ppo 各 5 轮，全部 `status=valid`，无 fault、无超时、无样本丢失；`timing_missing_count=0`、`degraded=false` |
| env reset | n=500，p50 **62.45 ms**，p95 **561.31 ms** |
| env policy step | n=5000，p50 **0.5202 ms**，p95 **0.8633 ms** |
| JSONL echo 纯往返 | n=5000，p50 **0.0283 ms** |
| rl-env step 端到端 | n=5000，p50 **0.5115 ms** |
| rl-env step 进程内 tick | n=5000，p50 **0.3783 ms**（.NET `Stopwatch`，仅 `engine.Tick`） |
| IPC/序列化占比 | **0.2605**（逐轮 0.2294 / 0.2669 / 0.2667 / 0.3058 / 0.2493） |
| 主机 CPU | 中位 **10.06%**，未饱和；无任何逻辑核中位数 ≥50% |
| 内存 | Python 峰值 RSS 319.6 MB、.NET 峰值 RSS 77.9 MB；主机内存使用峰值 60.7%，无压力 |
| 原生 batch 判定 | **Go（仅入口门槛）**：`ipc_fraction>=0.25` 为 true、CPU 饱和为 false |

307.595 steps/s 是 v3 单次 500k 训练的历史观测，本轮不作为基线，也不与本轮固定长度探针直接比较。

## 1. 运行身份

manifest 记录于 `.sim_runs/score-block-v4-profile-20260927/manifest.json`：

| 字段 | 值 |
| --- | --- |
| 机器 | `neco`，Intel64 Family 6 Model 183 Stepping 1，32 逻辑核 |
| OS | `Windows-11-10.0.26200-SP0` |
| Python | 3.12.10 |
| .NET SDK | 8.0.425（`%LOCALAPPDATA%\Temp\robot-simulator-dotnet-sdk\dotnet.exe`；PATH 上的 `dotnet` 只有运行时，构建显式使用该 SDK 路径） |
| 依赖 | numpy 2.5.0、gymnasium 1.3.0、stable-baselines3 2.9.0、torch 2.13.0+cpu、psutil 7.2.2 |
| scenario | `scenarios/wushu-ring-2026-mujoco.json`，SHA-256 `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3` |
| CLI DLL | `src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll`，SHA-256 `5a94e6e05adc784fbcd617c786ce838deaa718d1d73052f615785b0c953d6334` |
| 训练 RNG seed | 20260925 |
| 资源采样 | psutil，间隔 0.2 s；`resources.csv` 覆盖全部相位共 6778 行，其中 env 与 PPO（含 warm-up）子集 6742 行用于饱和判定 |
| 输出 | `manifest.json`、`summary.json`、`resources.csv`、`raw/{env,ipc,ppo}-round-*.json` |

测量期间未改代码；CLI 哈希在测量前后一致。

## 2. 测量方法

`.NET` 侧新增 opt-in `timing` 字段（`src/Sim.Cli/RlEnvCommand.cs`）：请求带 `"timing": true` 时，响应 `info.timing` 返回 reset 的 `resetMs`/`prerollTicks`/`prerollMs`/`totalMs` 与 step 的 `tickMs`/`totalMs`；字段缺失时响应形状与旧版逐字节相同。默认训练、评测与 Gym 请求不带该字段。

`controllers/score_block_rl/profile.py` 是显式诊断入口：

- env 阶段用 `ScoreBlockEnv` 单个持久 `rl-env` 进程，动作固定 `(0, 0)`，episode 终止即按 benchmark 语义 reset；每轮预热为 1 次 discard reset + 100 个 discard step。
- ipc 阶段先用同进程持久 Python JSONL echo 子进程测纯往返，再用原始 JSONL 客户端对真实 `rl-env` 测 reset/step 往返，并同时读取 .NET `tickMs`。
- ppo 阶段按 `OnPolicyAlgorithm.learn()` 的顺序手动执行 `_setup_learn → collect_rollouts → progress update → train()`，断言 `n_envs=1`、`n_steps=2048`；预热 5 次完整更新后连续采集 50 次 update/轮。

契约见 `.trellis/spec/sim/rl-profiling-contract.md`。

## 3. 环境阶段

`.sim_runs/score-block-v4-profile-20260927/raw/env-round-00{1..5}.json`

| 指标 | n | p50 | p95 | min | max |
| --- | --- | --- | --- | --- | --- |
| `reset_ms`（完整预推进 reset） | 500 | 62.45 | 561.31 | 51.98 | 852.86 |
| `step_ms`（policy step） | 5000 | 0.5202 | 0.8633 | 0.3109 | 2.5017 |

逐轮 reset p50 为 61.95 / 62.12 / 62.54 / 63.31 / 61.52 ms，轮间稳定；step p50 为 0.5195 / 0.5158 / 0.5435 / 0.5102 / 0.5180 ms。

reset 的长尾已归因，不是测量噪声：`preroll-probe.json` 对 env 第 3 轮使用的 seed 1300–1399 逐一记录 `prerollTicks`，慢样本（>100 ms，n=9）预推进中位 **1731 tick**，快样本（≤100 ms，n=91）中位 **167 tick**；最慢 4 例（693–851 ms）均跑满 2400 tick，第 5 例 593.9 ms 跑 1731 tick，五例均 `no_score_block=true`。即长尾来自"该 seed 整场都未进入 SCORE_BLOCK"的预推进上限，而非机器抖动。step 长尾同样轻微：5000 个样本中 61 个 >1 ms，p99 为 1.02 ms。

## 4. IPC 阶段

`.sim_runs/score-block-v4-profile-20260927/raw/ipc-round-00{1..5}.json`

| 指标 | n | p50 | p95 |
| --- | --- | --- | --- |
| echo 纯往返 | 5000 | 0.0283 | 0.0420 |
| reset 真实往返 | 5 | 65.96 | 75.01 |
| policy step 端到端 | 5000 | 0.5115 | 0.8522 |
| policy step 进程内 tick | 5000 | 0.3782 | 0.6946 |

逐轮配对差值给出的占比为 0.2294 / 0.2669 / 0.2667 / 0.3058 / 0.2493，轮中位 0.2667，与汇总的 0.2605 一致。

差值口径已如实标注：`end_to_end - tickMs` 包含 JSON 编解码、stdin/stdout 管道与调度，**也包含 `Step()` 中 tick 之外的工作**（事件扫描、目标归因、观测构建）。因此 0.2605 是"IPC + 序列化 + 非 tick 环境开销"的上界估计，不是硬件级 IPC 计数器。纯 echo 仅 0.0283 ms 说明裸管道往返本身很小，差值主要来自 JSON 处理与两侧非 tick 计算。

只有 ipc 阶段显式请求计时；env/训练/评测路径的请求不带 `timing`，响应形状与旧版一致（由 `.NET` 测试覆盖）。

## 5. PPO 阶段

`.sim_runs/score-block-v4-profile-20260927/raw/ppo-round-00{1..5}.json`，每个 update 固定 2048 transitions（250 个样本全部为 2048）。

| 指标 | n | p50 | p95 | min | max |
| --- | --- | --- | --- | --- | --- |
| `rollout_seconds`（环境采样） | 250 | 1.8924 | 12.65 | 1.4390 | 21.28 |
| `train_seconds`（optimizer） | 250 | 0.9051 | 4.89 | 0.7817 | 5.19 |

静态配置下，单次 update 的稳定区间约为 rollout 1.75–1.81 s + train 0.89 s ≈ 2.7 s / 2048 transitions，等价约 760 transitions/s 的环境采样上限（不含 checkpoint 与 EOF 处理）。

**测量污染披露**：第 4、5 轮 rollout p50 升至 3.29 s / 2.28 s，且有 24 / 21 个 update 超过 5 s（前 3 轮为 0 个）。同窗口主机 CPU 中位仅 11.0% / 10.5%（前 3 轮 8.6–10.3%），因此不是本进程 CPU 饱和所致，判定为**未解释的外部负载干扰**。按契约保留全部原始样本，不剔除、不重跑；因此 rollout 的 p95/max 不可作为稳定基线，train 时间受影响较小（p50 0.875→0.978 s）。

## 6. 资源与饱和判定

`.sim_runs/score-block-v4-profile-20260927/resources.csv`（全部相位 6778 行，其中 env、ppo warm-up、ppo 三阶段子集 6742 行参与判定）

- 主机总 CPU 中位 **10.06%**，p95 77.4–80.0%，最大 97.7%：瞬时高值来自其它进程，中位数远低于 85% 饱和线。
- 逐逻辑核：中位数最高的两个核为 38.5% / 35.7%，**没有任何逻辑核中位数 ≥50%**，即不存在单核热点瓶颈。
- 内存：Python 峰值 RSS 319.6 MB、.NET 峰值 RSS 77.9 MB、主机内存使用峰值 60.7%、最小可用内存约 14.87 GB；无内存压力。

结论：**CPU 未饱和，内存不是限制**。

## 7. 瓶颈归因与后续门槛

```text
ipc_fraction = max(0, 0.5115 - 0.3782) / 0.5115 = 0.2605
```

- 判定输入：`ipc_fraction >= 0.25` → **true**；主机 CPU 中位饱和（≥85%）→ **false**。
- 因此 summary 输出 `Go: 建议尝试原生 .NET batch`，允许 `09-27-rl-v4-training-throughput` 先做重复测量并在达标后尝试原生 batch。
- **须强调的边界**：0.2605 仅比 0.25 高 0.0105，且 5 轮中 2 轮（0.2294、0.2493）低于门槛。该门槛在当前口径下是"擦线通过"，不能据此宣称 IPC 是决定性瓶颈。吞吐任务若决定投入原生 batch，应先在同机重复本测量并复核该比例是否稳定在 0.25 以上。
- 次要可优化点（不改变本轮判定）：env reset 的中位 62 ms 与长尾 561 ms 均来自预推进；未进入 SCORE_BLOCK 的 seed 会跑满 2400 tick。若后续需要提升 reset 吞吐，应优化预推进/seed 筛选，而不是 IPC。

本任务不声明吞吐达标、不声明提速已实现、不声明策略得分。5×500k 训练套件与子向量环境评估属于下一任务。

## 8. 验证

| 命令 / 检查 | 退出码与结果 |
| --- | --- |
| `dotnet build RobotSimulator.sln -m:1`（临时 SDK） | 0；0 错误 |
| `dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 --no-restore` | 0；389 通过 / 0 失败 / 0 跳过（含新增 `RlEnvCommandTests` timing 用例） |
| `dotnet test --filter FullyQualifiedName~TrainingResetPerformanceTests` | 0；2 通过；原始样本 `reset-performance.json` |
| `python -X utf8 controllers/score_block_rl/selftest.py --gym-check --out .sim_runs/score-block-v4-selftest-20260927.json` | 0；**42 通过 / 0 失败 / 6 跳过**（含 profiler 契约自测与 `--quick` 端到端） |
| `python -m compileall -q controllers/score_block_rl` | 0 |
| `python controllers/score_block_rl/profile.py --rounds 5 --resets 100 --steps 1000 --ipc-round-trips 1000 --ppo-updates 50` | 0；15/15 轮 valid；env 5500+、ipc 15005、ppo 750 个原始样本 |
| `git diff --check` | 0 |

6 个跳过项均为需要外部训练产物/开发集 sweep 的工件检查（`--train-dir`/`--dev-sweep` 未提供），与本次改动无关。

## 9. 交付物索引

| 交付物 | 位置 |
| --- | --- |
| 契约 | `.trellis/spec/sim/rl-profiling-contract.md` |
| profiler | `controllers/score_block_rl/profile.py` |
| .NET 计时接口 | `src/Sim.Cli/RlEnvCommand.cs` |
| .NET 计时测试 | `src/Sim.Tests/RlEnvCommandTests.cs` |
| 用法文档 | `controllers/score_block_rl/README.md`（"分阶段 profiling"） |
| manifest / summary | `.sim_runs/score-block-v4-profile-20260927/{manifest,summary}.json` |
| 原始样本 | `.sim_runs/score-block-v4-profile-20260927/raw/` |
| 资源样本 | `.sim_runs/score-block-v4-profile-20260927/resources.csv` |
| reset 长尾归因 | `.sim_runs/score-block-v4-profile-20260927/preroll-probe.json` |
| 冷/热 reset | `.sim_runs/score-block-v4-profile-20260927/reset-performance.json` |

## 10. Go / No-Go

**Go**：split guard 为 Go；五轮测量类别齐全、身份可复核、无 fault；`ipc_fraction`、CPU 与内存结论明确。

解锁：`09-27-rl-v4-training-throughput`。该任务必须先做同机重复测量确认 0.2605 的门槛稳定性，再按 PRD 的 5×500k 中位墙钟 ≤60 分钟验收；本任务的 Go 不代表该目标可达。

已披露限制：IPC 差值含非 tick 环境开销（上界口径）；PPO 第 4–5 轮受外部负载干扰、rollout 分位数不可作稳定基线；原生 batch 门槛为擦线通过。
