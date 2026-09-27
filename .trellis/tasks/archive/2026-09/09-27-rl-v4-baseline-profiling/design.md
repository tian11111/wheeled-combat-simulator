# 技术设计：基线与瓶颈剖析

## 当前结构与已知证据

- Python Gymnasium `ScoreBlockEnv` 持有一个长期运行的 .NET `rl-env` 子进程；每个 reset/step 通过 JSONL stdin/stdout 请求响应。reset 在 .NET 侧新建 episode/runtime，step 推进 MuJoCo 并序列化 observation/info。
- 当前 PPO 是 SB3 默认参数、`n_envs=1`；已记录的设备为 CPU，11 维特权观测。训练产物已有 `steps_per_second`、`progress.csv`、`episodes.monitor.csv`、配置及哈希。
- `benchmark.py` 与 `TrainingResetPerformanceTests` 已有 reset/step 性能测量入口，但没有证据表明它们已经分离 JSONL IPC 和 PPO optimizer 耗时。
- 归档 v3 报告中的 307.595 steps/s 来自一次约 27 分钟训练，只能当历史观测。

## 测量方案

1. **环境阶段**：使用 `benchmark.py` 的完整预推进 reset（100 次）与 policy step（1000 次）原始样本；每个测量过程先完成一次 reset 和 100 个丢弃 step 预热。重复 5 轮，报告每轮和汇总 p50/p95；另外运行已有 reset 性能用例并保留其原始 JSON。
2. **IPC 阶段**：复用相同 Python JSON 序列化/flush/readline 路径，以测试用的持久本地 JSONL echo 进程测量纯请求-响应延时，至少 1000 往返 × 5 轮；另对真实 `rl-env` 记录 reset 与 step 整个往返延时。估计 step IPC 占比时，用同一机器上 .NET tick 内部计时与 Python 整体往返时间作配对差值，说明差值包含序列化和 pipe 调度，不伪装成硬件计数器。
3. **Optimizer 阶段**：固定当前 SB3/PPO 超参数和 2048 transitions/update，先预热 5 次，再采集连续 50 次真实 rollout 的 update wall time × 5 轮。区分环境采样时间与 PPO 优化时间，禁止用 PPO `time/fps` 单字段代替端到端速度。
4. **资源监控**：在上述固定长度探针中同步采样进程 CPU、主机总 CPU、每逻辑处理器使用率、Python/.NET RSS 和峰值；按固定采样间隔记录，注明测量工具、权限与采样成本。本任务不重复完整 500k 训练；完整五训练 seed 套件归吞吐任务。

所有项目各做 5 轮，使用同一依赖环境与场景/CLI 文件；原始 CSV/JSON、摘要和 run identity 写在 `.sim_runs/score-block-v4-profile-<machine-tag>/`。用现有 `.gitignore` 外目录保存大数据。

## 瓶颈判定

- `ipc_fraction = max(0, median(end_to_end_policy_step) - median(in_process_step)) / median(end_to_end_policy_step)`，使用同轮配对样本；报告原始分布及差分方法。
- 将主机 CPU 判为饱和：主机总 CPU 中位数达到 85% 以上；同时报告逐逻辑处理器占用，以识别单核热点。内存压力单独列出，不混作 CPU 结论。
- 只有 **IPC/序列化占比 ≥25% 且 CPU 未饱和** 才可建议下一阶段尝试原生 .NET batch。否则明确 No-Go 原生 batch；仍可按 throughput 任务测量独立训练和评估 SubprocVecEnv。
- 测量结果可以显示其它瓶颈；根据数据描述，不为达到门槛重分类或删样本。

## 兼容与保护

- 计时探针默认关闭，协议无探针时字节兼容；若必须改接口，使用显式 opt-in，不把诊断字段注入训练 observation/reward/info。
- 不改变 reset、episode 预推进、控制动作、reward、终止条件、seed 顺序或裁判指标。
- 任一 fault、超时或损坏样本使该轮无效；报告失败并可完整重跑该轮，保留原始记录和重跑标记。
