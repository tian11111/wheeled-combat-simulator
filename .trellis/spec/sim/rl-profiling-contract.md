# SCORE_BLOCK PPO 性能剖析契约

## 1. Scope / Trigger

`controllers/score_block_rl/profile.py` 是显式运行的性能诊断工具。默认训练、评测和 Gym 请求不带计时字段；profiler 输出只写入 `--out` 目录，不进入训练产物、观测、reward 或常规协议响应。profiling 不运行 final holdout，也不改变 split、场景、动作或训练语义。

## 2. Command

```text
python -X utf8 controllers/score_block_rl/profile.py --out <absolute-directory>
    [--rounds 5] [--resets 100] [--steps 1000]
    [--ipc-round-trips 1000] [--ppo-updates 50] [--quick]
    [--dotnet <dotnet.exe>] [--cli-dll <path>] [--scenario <path>]
```

`--out` 必须是绝对路径，已有非空目录拒绝覆盖。完整测量默认 5 轮；每轮测 100 次完整 reset、1000 个 policy step、至少 1000 次 JSONL echo 往返和 50 次 PPO update。参数可覆写。`--quick` 将每个 stage 缩为一轮少量样本，仅用于端到端自测，输出必须保留缩小后的 effective 参数，不可当成正式性能结论。

## 3. Output layout

```text
<out>/
  manifest.json
  summary.json
  resources.csv
  raw/
    env-round-001.json
    ipc-round-001.json
    ppo-round-001.json
    ...
```

Manifest 固定记录机器名/型号、逻辑核数、OS、Python 与 .NET 版本及可执行路径、numpy/gymnasium/stable-baselines3/torch/psutil 版本、scenario 和 CLI DLL 绝对路径及 SHA-256、PPO 参数、seed、warm-up、每轮样本数和输出布局。Resources CSV 每行含 epoch 时间、phase、Python/.NET RSS、主机 CPU、每逻辑处理器 CPU、主机内存占用及可用字节。

## 4. Stages and fields

| Stage | Method | Raw sample fields |
| --- | --- | --- |
| `env` | `ScoreBlockEnv` 单个持久 `rl-env` 进程；确定动作 `(0, 0)`；episode 结束按 benchmark 规则 reset | `reset_ms`, `step_ms`，毫秒 |
| `ipc` echo | 同进程启动持久 Python JSONL echo 子进程；紧凑 JSON 编码、flush、readline、JSON decode 往返 | `echo_round_trip_ms` |
| `ipc` rl-env | 原始 JSONL 客户端；reset 和 step 往返请求显式携带 `timing: true` | `reset_round_trip_ms`, `policy_step_end_to_end_ms`, `policy_step_tick_ms`；单位毫秒 |
| `ppo` | SB3 PPO 单环境、默认参数，2048 step/update | `rollout_seconds`, `train_seconds`, `transitions` |

环境正常路径的 `ScoreBlockEnv` 请求不带 `timing`。只有单独的 rl-env timing probe 显式启用计时。若 response `info.timing` 或 `tickMs` 缺失、非数值或非有限值，原始 `policy_step_tick_ms` 对应元素必须为 JSON `null`，`timing.missing_count` 递增并设置 `timing.degraded=true`；测量继续。IPC fraction 对有效配对计时值作汇总，降级状态在 summary 明示。

PPO 调用顺序与 `OnPolicyAlgorithm.learn()` 对齐：一次 `_setup_learn`，每 update 执行 `collect_rollouts(env, callback, rollout_buffer, n_steps)`、更新 `progress_remaining`，再执行 `train()`。先做 5 次完整更新预热，再记录连续 update；使用 PPO 默认 callback，不增加动作或随机数消费。rollout wall time 和 `train()` wall time 分开采集；每个 update 必须记录 `transitions=2048`。

## 5. Warm-up

- 每个 env 轮：一次 discard reset，然后 100 个 discard policy step；终止时按 benchmark 语义 reset。以上样本不进性能统计。
- 每个 IPC 轮：JSONL echo discard 请求数和真实 rl-env discard step 数写入 manifest；预热响应不计入时延样本。
- PPO：第一次采集前 5 次 rollout+train 更新 discard；之后连续记录 50 次 update × 5 轮。warm-up 样本不可混进 summary。
- `--quick` 可缩短 IPC/PPO warm-up 以完成快测，但需在 manifest 中反映实际规则；env 的 100 step warm-up 仍使用同一固定规则。

## 6. Invalid rounds and retention

每轮原始 JSON 即使遇到 fault、timeout、无效响应也必须落盘，包含 `status: "invalid"` 和 `faults` 中的异常类型、信息；已收集的部分样本仍留在该轮原始文件。Summary 单独列出无效轮号和原始部分样本数，不纳入有效样本的分位数；不得静默删除或重命名故障轮。缺失的内部 timing 是 degraded 数据而不是协议故障，不得伪造为 0。

所有时延按升序 nearest-rank 计算 p95（索引 `ceil(0.95*n)-1`），p50 为中位数；summary 同时报有效样本数、每轮分布和无效轮状态。

## 7. Bottleneck decision

```text
ipc_fraction = max(0, median(end_to_end_policy_step) - median(in_process_tick_ms))
               / median(end_to_end_policy_step)
```

中位数分别使用所有有效轮的有限毫秒样本；tick 缺失样本不参与 tick 中位数，并计入 degradation 数。分母为空/非正或 tick 中位数不可得时 `ipc_fraction=null`，不得给出 Go 建议。

主机 CPU 饱和的精确定义：env 与 PPO（包含 PPO warm-up）阶段采样到的主机总 CPU 使用率中位数 `>= 85%`。同时逐逻辑处理器报告样本数、p50、p95、最小/最大值。内存压力独立报告：Python/.NET 峰值 RSS、主机内存使用率分布、最小可用内存；主机内存使用率峰值 `>= 85%` 标记为压力，不并入 CPU 饱和判定。

仅当 `ipc_fraction >= 0.25` 且主机 CPU 未饱和时，summary 输出「建议尝试原生 .NET batch」。其余情况（包含计时降级到无法计算）明确输出 `No-Go 原生 batch`。该结论只是性能优化入口门槛，不表示 batch 已实现或训练已达标。

## 8. Tests

`selftest.py` 定向覆盖 p50/p95、IPC 比例门槛及负差 clamp、无效轮保留、manifest 必备字段；`--gym-check` 还运行真实 `profile.py --quick` 并核对 manifest、summary、逐 stage 原始文件和资源 CSV。标准 profiling 仍由主任务按完整默认重复规模采集，quick 数据不代替正式测量。
