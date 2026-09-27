# 执行计划

## 前置检查与 Go / No-Go

1. 检查 `.trellis/tasks/09-27-rl-v4-split-guard/report.md`；必须明确为 **Go**，且引用已完成的 split/freeze 守卫测试。若不存在或为 No-Go，保持本任务 planning，不运行 profiler。
2. 阅读 v3 归档报告第 28–42 行、当前 `controllers/score_block_rl/gym_env.py`、`train.py`、`benchmark.py` 和 `src/Sim.Cli/RlEnvCommand.cs`，记录当前基准结构与真实运行版本。

## 实施顺序

1. 先定义统一机器/run manifest：CPU 型号与逻辑核数、OS、Python/.NET 和依赖版本、运行模式、电源/磁盘状态、场景/CLI SHA-256、训练参数、warm-up 和每轮样本数。
2. 扩展现有 `benchmark.py` 或添加 opt-in profiler，将环境完整 reset、step 的原始样本写入 `.sim_runs/`；保留现有入口默认行为。
3. 增加持久 JSONL echo 测量路径，对实际 Python 管线记录请求到响应时长；在 .NET `rl-env` 增加仅诊断模式的 tick 计时以分开模拟计算与 wire/序列化差值。日志走 stderr 或旁路 JSON，不能污染 stdout JSONL。
4. 增加只用于性能研究的 PPO profiler，锁定 11 维、当前 PPO 参数和每次 2048 transitions 的 rollout 语义；分别记录 rollout/environment 与 optimizer update 时间、进程和主机资源。所有探针关闭时训练产物和协议不变。
5. 按 design.md 固定样本数量完成 5 轮测量。保存每轮原始数据，不抹掉 fault/timeout；无效轮须标记原因并重跑。
6. 汇总 p50/p95、方差/范围、IPC 占比、CPU/RSS 峰值与是否饱和；对照 v3 一次历史记录时标明机器差异、配置差异及不可直接比较项。

## 验证

- 现有 benchmark 行为保持兼容；计时输出解析和 run identity 校验新增自测。
- 诊断开启/关闭时，固定 seed 的 reset observation、step observation/reward/terminated/truncated/info 均一致；JSONL stdout 中无诊断杂行。
- optimizer profiler 采集数据总 transitions=2048/update；PPO 参数、场景和模型都写入 manifest。
- 相关 Python 自测、受影响的 .NET 测试、`python controllers/score_block_rl/benchmark.py --out <绝对 JSON 路径>`、有适用改动时的 `dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 --no-restore` 和 `git diff --check` 通过。
- 报告 `.trellis/tasks/09-27-rl-v4-baseline-profiling/report.md` 包含完整数据索引和 **Go/No-Go**。只有每类测量完成、重复轮数满足、身份可复核且无未解释 fault 才 Go；测出 IPC 低于 25% 或 CPU 饱和仍可 Go 完成本证据任务，但报告必须 No-Go 原生 batch。

## 依赖任务控制

- 前置 split 守卫 No-Go：不开始测量。
- 本任务测量完整但 IPC 不高：报告完成，后续吞吐任务仍可先评估独立单环境并行和 SubprocVecEnv；原生 batch 禁止。
- 任一必要测量缺失或报告不能复现：No-Go，不启动吞吐任务和策略任务。
