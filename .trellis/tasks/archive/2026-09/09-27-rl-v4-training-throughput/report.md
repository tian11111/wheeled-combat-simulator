# RL v4 五 seed 训练提速 — 验收报告

- 结论：**Go（吞吐门槛通过；解锁下游多 seed PPO 策略任务）**
- 分支：`test/score-block-ppo-checkpoint-round`
- 原始数据：`.sim_runs/score-block-v4-independent-throughput-20260927/`、`.sim_runs/score-block-v4-independent-throughput-20260927-rerun/`（均为 Git 忽略目录）
- 范围：训练入口的 seed/输出/向量参数与身份记录、三套并发独立单环境训练计时、checkpoint 与产物审计；未改 reward、观测、物理、FSM、场景，未打开 v4 开发集或最终盲集。

## 0. 结论摘要

| 项 | 结果 |
| --- | --- |
| 第一候选（5 × 独立 `n_envs=1` 并行） | 三套全部有效；墙钟 1247.031 / 1316.672 / 1317.766 s |
| 中位墙钟 | **1316.672 s = 21.945 分钟**，门槛 ≤60 分钟 **通过** |
| 每套规模 | 5/5 seed，每个 **501,760** transitions，`faults_total=0`，9 个 checkpoint，产物审计 `ok` |
| 固定 seed 可复现 | 5 个 seed 的模型状态哈希在三套之间逐位一致 |
| episode seed 隔离 | 每套 1001 个 episode seed，`unique_episode_seed_count == episode_seed_count`，无重叠 |
| 主机资源 | 三套主机 CPU 中位数 95.9% / 93.45% / 93.5%；内存峰值 67.9% / 77.1% / 67.6% |
| `SubprocVecEnv` 候选 | 未评估——候选一已通过，设计规定通过即停止提速探索 |
| 原生 .NET batch | **No-Go**：正式并行训练中主机 CPU 已饱和，不满足“CPU 未饱和”前提，未实现、未修改相关代码 |
| 下游策略 | **Go**：`09-27-rl-v4-split-multiseed` 的前置吞吐门槛成立 |

本结论只证明训练吞吐门槛与产物完整性；**不表示 PPO 策略得分、跨 seed 稳定性或 v4 盲验通过**。

## 1. 前置报告与门槛

| 前置 | 结果 |
| --- | --- |
| `.trellis/tasks/archive/2026-09/09-27-rl-v4-split-guard/report.md` | Go；v3 仅分析、v4 开发集 9001–9020 与盲集 10001–10050 已预注册 |
| `.trellis/tasks/archive/2026-09/09-27-rl-v4-baseline-profiling/report.md` | Go；五轮 env/ipc/ppo 测量完整，`ipc_fraction=0.2605`、CPU 未饱和，并要求本任务复测该比例 |

两项前置均满足，本任务按 PRD 启动。

## 2. 测量方法

`controllers/score_block_rl/run_throughput_suite.py` 是显式测量入口：

- 每套同时启动 5 个独立 `n_envs=1` 训练进程，训练 seed 固定为 `20260927、20260928、20260929、20260930、20261001`；每个进程各自拉起一个持久 .NET `rl-env` 子进程。
- 每个套件有独立目录，套件内每个 seed 有独立输出目录；runner 拒绝非空输出根目录，训练入口拒绝非空输出目录。
- 采样频率 1 s，记录每个训练进程树的 CPU/RSS、主机 CPU、主机内存与可用内存。
- 每个 run 结束后从 `run-config.json` 审计：退出码、训练状态、seed 一致性、`n_envs`、实际 transition 数、fault 数、checkpoint/manifest 哈希与产物哈希。
- 套件有效要求：恰有 5 个 run 且全部有效、套件内身份字段一致、episode seed 流无重叠。
- 重复套件不筛掉慢或失败运行；失败会使该套件无效并公开重测原因。

本次运行身份（15 份 `run-config.json` 完全一致）：

| 字段 | 值 |
| --- | --- |
| 机器 | `neco`，Intel64 Family 6 Model 183 Stepping 1，32 逻辑核，32 GiB |
| OS | `Windows-11-10.0.26200-SP0` |
| Python | 3.12.10 |
| .NET | `C:\Program Files\dotnet\dotnet.exe`，runtime 8.0.31（SDK `--version` 在该主机不可用，已写入 manifest） |
| 依赖 | numpy 2.5.0、gymnasium 1.3.0、stable-baselines3 2.9.0、torch 2.13.0+cpu、psutil 7.2.2 |
| 场景 | `scenarios/wushu-ring-2026-mujoco.json`，SHA-256 `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3` |
| CLI DLL | `src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll`，SHA-256 `5a94e6e05adc784fbcd617c786ce838deaa718d1d73052f615785b0c953d6334` |
| split | `score-block-split-v4` |
| PPO | MlpPolicy、`n_envs=1`、`n_steps=2048`、batch 64、10 epochs、device cpu；除 PPO seed 外参数完全相同 |
| checkpoint | 每 51,200 全局 transitions，`n_envs=1` 下 `save_freq` 不再除以环境数 |
| 代码身份 | `git_commit=8b89b3798b833de4173941007a6ded118fecb95a`，`git_worktree_dirty=true`（本任务的未提交改动），四个受审计源文件 SHA-256 在 15 份 manifest 中一致 |

## 3. 三套计时结果

`.sim_runs/score-block-v4-independent-throughput-20260927/aggregate-3suite.json`

| 套件 | 来源目录 | 墙钟 (s) | 有效 run | 主机 CPU 中位 | 内存峰值 | fault 合计 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `score-block-v4-independent-throughput-20260927/suite-01` | 1247.031 | 5/5 | 95.9% | 67.9% | 0 |
| 2 | `score-block-v4-independent-throughput-20260927/suite-02` | 1316.672 | 5/5 | 93.45% | 77.1% | 0 |
| 3 | `score-block-v4-independent-throughput-20260927-rerun/suite-01`（重测） | 1317.766 | 5/5 | 93.5% | 67.6% | 0 |

中位数 **1316.672 s（21.945 分钟）**≤ 60 分钟；三套 `identity_issues` 与 `seed_isolation_issues` 均为空。

### 3.1 中断与重测披露

第一轮 suite-03 在约 **1062 s** 时随所属 Codex 会话结束被终止，五个 seed 停在约 42 万 transitions，没有写出 `suite-result.json`。该次尝试：

- 不计入三次中位数；
- 原始 `resources.csv` 与各 seed `progress.csv` 保留在 `.sim_runs/score-block-v4-independent-throughput-20260927/suite-03/`；
- 中断事实、最后采样时间、各 seed 更新数与处置方式记录在 `suite-03/INTERRUPTED.json`；
- 使用与三套完全相同的冻结 runner、seed、场景与 CLI DLL，在新目录重测一次（即上表套件 3）。

重测不是"结果不合意而重跑"：中断发生在计时中途且无有效产物，属于必须记录的无效套件。

## 4. 逐 seed 产物与复现性

每个 seed 在每套中均为：退出码 0、`status=completed`、`actual_global_transitions=501760`、`faults_total=0`、9 个 checkpoint、`audit.ok=true`、`valid=true`。501,760 = 245 × 2048，是 500,000 向上取整到 PPO rollout 粒度的结果。

| 训练 seed | 三套模型状态哈希（`policy.pth`+`policy.optimizer.pth`+`pytorch_variables.pth`） | 逐位一致 |
| --- | --- | --- |
| 20260927 | `336c4bc4…` × 3 | 是 |
| 20260928 | `4b44d543…` × 3 | 是 |
| 20260929 | `230d79bc…` × 3 | 是 |
| 20260930 | `5cde69bf…` × 3 | 是 |
| 20261001 | `1fb69780…` × 3 | 是 |

哈希按成员内容计算，忽略 SB3 ZIP 时间戳、运行时间与 episode 诊断日志；五个 seed 的哈希互不相同，说明不同训练 seed 得到不同模型。episode seed 流按训练 seed 从注册训练池做确定性互斥分区，每套聚合得到 1001 个互不重复的 episode seed（注册池为 `{42, 20260925, 1000–1999}`，五个 v4 训练 seed 各分得 200 或 201 个）。

## 5. 资源与瓶颈判定

### 5.1 正式并行训练

- 每套主机 CPU 中位数 **93.45%–95.9%**，p95 为 100%，即 32 逻辑核在套件大部分时间内接近饱和。
- 每个训练进程树峰值 CPU 约 2350%–2420%（约 24 核），峰值 RSS 约 361–366 MB；主机内存峰值 67.6%–77.1%，最小可用内存 7.8–11.0 GB，无内存压力。
- 三个套件的墙钟差异 70.7 s（1247.0–1317.8 s），约 5.7%，说明结论不是单次偶然。

### 5.2 单环境瓶颈复测

`.sim_runs/score-block-v4-throughput-reprofile-20260927/summary.json`（15/15 轮 valid，无 fault）：

| 指标 | 值 |
| --- | --- |
| `ipc_fraction` | **0.2707**（端到端 p50 0.5301 ms − 进程内 tick p50 0.3866 ms） |
| 主机 CPU 中位 | 10.34%（未饱和） |
| Python / .NET 峰值 RSS | 315.2 MB / 76.0 MB |
| 采样 | psutil 0.2 s，4005 个样本 |

单环境门槛（`ipc_fraction ≥ 0.25` 且 CPU 未饱和）在复测中成立，但这一前提只在**单环境**下成立。

### 5.3 原生 .NET batch 判定

PRD 要求原生 batch 需“**profiling 与重复训练测量**显示 IPC/序列化 ≥25% **且 CPU 未饱和**”。正式五进程训练的主机 CPU 中位数已饱和（93.45%–95.9%），第二项不成立。

因此：**不评估原生 .NET batch，不为其改代码**。已达标的第一候选使提速探索按设计停止；这不是失败，而是设计规定的正常终点。

## 6. 实现改动

| 文件 | 改动 |
| --- | --- |
| `controllers/score_block_rl/train.py` | 新增 `--train-seed`、`--n-envs`；独立输出目录与非空目录拒绝；身份 manifest（代码/场景/CLI/依赖/硬件/PPO 参数/episode seed 流）；checkpoint 按全局 transition 口径并记录 `num_timesteps`；保留默认单环境与历史 seed 行为 |
| `controllers/score_block_rl/run_throughput_suite.py`（新） | 并发五 seed 套件、独立目录、进程树与主机资源采样、逐 run 产物审计、套件与跨套复现性汇总、拒绝覆盖已有目录 |
| `controllers/score_block_rl/gym_env.py` | 新增 `initial_episode_seed`（首次 reset 显式 seed，其后回到 pool）；close 时容忍已断开管道，避免清理异常掩盖原始训练失败 |
| `controllers/score_block_rl/train_artifacts.py` | checkpoint 审计改为按全局 transition cadence 校验；新增 final model / monitor CSV / progress CSV 产物哈希校验 |
| `controllers/score_block_rl/selftest.py` | 新增 seed 流隔离、输出目录冲突、管道清理、全局 cadence 审计等用例 |
| `controllers/score_block_rl/README.md` | 记录多 seed 与并发套件用法 |
| `.trellis/spec/sim/index.md` | 记录 v4 训练 seed、PPO RNG 与 episode seed 分离、按全局 transition 的 checkpoint 口径 |

### 6.1 设计边界

- `--n-envs > 1` 已实现（`n_steps = 2048 // n_envs`、checkpoint 间隔按全局 transition 折算、每个 worker 派生子 seed 流），但本轮**未使用**；候选一已达标，向量路线未进入验收。
- `SubprocVecEnv` 的 worker 初始化、终止/truncation 语义与裁判事件对照因此**未验证**，不能据此声称向量路径合格。

## 7. 验证

| 命令 / 检查 | 退出码与结果 |
| --- | --- |
| `python -X utf8 controllers/score_block_rl/selftest.py --gym-check --train-dir <rerun seed-20260927> --out …` | 0；**52 passed / 0 failed / 1 skipped**（跳过项需开发集 sweep，不属本任务） |
| `python -X utf8 controllers/score_block_rl/selftest.py --gym-check --out …`（无产物目录） | 0；47 passed / 0 failed / 6 skipped |
| checkpoint smoke：51,200 transitions 单环境训练 | 0；81.1 s、`faults=0`、生成 `rl_model_51200_steps.zip`，checkpoint SHA 与 manifest 审计通过 |
| 运行器 smoke：3 套 × 2,048 步 | `.sim_runs/score-block-v4-throughput-runner-smoke-system-dotnet-20260927/`；3/3 套 valid、15/15 run valid；每套 1001 个 episode seed 无重叠；按当前状态哈希口径重算 `policy.pth`/`policy.optimizer.pth`/`pytorch_variables.pth`，五个 seed 跨三套逐位一致 |
| 三套正式测量 | 3/3 valid、15/15 run valid、0 fault、全部审计通过 |
| `git diff --check` | 0 |

`trellis-check` 角色在当前运行时不可用（`CODEY_SUBAGENT_ROLE_UNKNOWN`）；改由只读 `codey_deep_research` 角色独立核查实现与证据链，结论：三套数据与汇总自洽、`train.py` 满足 PRD；提出两项未来防护改进（runner 未强制校验 split/PPO 必需字段、`artifact_hashes` 缺失时不失败）与一项记录改进（中断套件缺独立记录）。

三项的处置：

- 记录改进：已补 `suite-03/INTERRUPTED.json`（中断时间、原因、各 seed 更新数、处置方式）。
- 产物哈希缺口：已交叉验证本次 15 份 `run-config.json` **全部**带 `artifact_hashes`，且字段与哈希审计通过，不影响本次结论。
- runner 必需字段校验：本次 15 份 manifest 的 `split_version`、`checkpoint_interval_transitions`、`n_envs`、状态、transition 数、checkpoint 审计逐项独立核验无异常；该加固项留待后续任务，不在本任务扩大改动范围。

## 8. 已知限制

1. 第一轮套件 3 因会话中断无效并重测一次；重测原因、时间与原始残留已记录，但两轮计时的宿主外部负载不完全可比。
2. 正式并行训练主机 CPU 近饱和，墙钟受 32 核可用性影响；换机或后台负载变化会改变绝对值。
3. 原生 .NET batch 与 `SubprocVecEnv` 均**未验证**，本文不声明其收益或正确性。
4. 本任务不评估策略得分、跨 seed 策略稳定性与盲集结果；这属于下游任务。
5. 训练使用 11 维特权观测，不能据此推断真机可部署性。

## 9. 交付物索引

| 交付物 | 位置 |
| --- | --- |
| 三套合并汇总 | `.sim_runs/score-block-v4-independent-throughput-20260927/aggregate-3suite.json` |
| 套件 1/2 结果 | `.sim_runs/score-block-v4-independent-throughput-20260927/suite-0{1,2}/suite-result.json` |
| 套件 3 重测结果 | `.sim_runs/score-block-v4-independent-throughput-20260927-rerun/suite-01/suite-result.json` |
| 中断套件记录 | `.sim_runs/score-block-v4-independent-throughput-20260927/suite-03/INTERRUPTED.json` |
| 瓶颈复测 | `.sim_runs/score-block-v4-throughput-reprofile-20260927/summary.json` |
| checkpoint smoke | `.sim_runs/score-block-v4-throughput-checkpoint-smoke-20260927/run-config.json` |
| 最终自测 | `.sim_runs/score-block-v4-throughput-selftest-final-with-artifacts.json` |
| 测量入口 | `controllers/score_block_rl/run_throughput_suite.py` |

## 10. Go / No-Go

**Go（下游策略）**：三套同机、相同配置的 5×500k 训练全部有效，中位墙钟 21.945 分钟 ≤60 分钟；每套 5/5 run 满足 `faults=0`、completed、产物与 checkpoint 审计通过、固定 seed 三套复现；训练 seed 与 episode seed 隔离且目录独立。

解锁：`09-27-rl-v4-split-multiseed`（多 seed 原版 PPO 开发集基线）。该任务仍须自行验证开发集门槛；本 Go 不代表其策略指标通过。

明确 No-Go：原生 .NET batch（CPU 已饱和前提不成立）。未评估：`SubprocVecEnv`。
