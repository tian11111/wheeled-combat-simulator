# SCORE_BLOCK PPO 五 seed 吞吐测量契约

## 1. Scope / Trigger

`controllers/score_block_rl/run_throughput_suite.py` 是 RL v4 训练吞吐门槛的显式测量入口，只用于回答"五个固定训练 seed 各 500k transitions 能否在 60 分钟内跑完"，不训练策略候选、不选模、不打开任何评估集。

它与 [rl-profiling-contract.md](./rl-profiling-contract.md) 分工不同：profiling 测单环境各阶段时延与瓶颈比例；本测量测多进程并发套件的总墙钟与产物完整性。两者结论互不替代。

## 2. Command

```text
python -X utf8 controllers/score_block_rl/run_throughput_suite.py \
    --out-root <fresh-absolute-directory> --dotnet <dotnet.exe> \
    [--steps 500000] [--suite-count 3] [--first-suite 1] \
    [--checkpoint-interval 51200] [--sample-interval 1.0] \
    [--scenario <path>] [--cli-dll <path>]
```

`--out-root` 必须是全新目录；已存在且非空时拒绝，避免覆盖既有计时证据。`--suite-count` 至少 3 才构成规格符合的正式测量（`spec_conformant_measurement_size`）。训练 seed 固定为 `20260927、20260928、20260929、20260930、20261001`，不接受外部覆写。

## 3. Contracts

输出布局：

```text
<out-root>/
  runner-config.json          # runner 脚本哈希、Python/platform、场景与 CLI DLL 哈希、测量参数、固定 seed 列表
  aggregate.json              # 增量汇总；每套完成后重写
  suite-NN/
    resources.csv             # 逐秒：时间、套件内已过秒数、seed、pid、进程状态、进程树 CPU/RSS、主机 CPU/内存
    logs/seed-<seed>.log      # 每个训练进程的 stdout+stderr
    seed-<seed>/              # 训练入口的独立产物目录（run-config.json、progress.csv、episodes.monitor.csv、checkpoints/、模型）
    suite-result.json         # 套件结论与逐 run 审计
```

套件有效（`status="valid"`）的必要条件，全部满足才成立：

1. 恰有 5 个 run，且全部 `valid=true`；
2. 每个 run：`exit_code=0`、`status="completed"`、`train_seed` 与请求一致、`n_envs=1`、`actual_global_transitions >= --steps`、`faults_total=0`、`audit.ok=true`、最终模型 SHA-256 存在；
3. 套件内 `scenario_sha256`、`cli_dll_sha256`、`code_identity`、`hardware_identity`、`dependencies`、`dotnet_runtime` 一致；
4. 五个训练 seed 的 episode seed 流两两不重叠，且完成集合恰好覆盖五个预注册 seed。

`aggregate.json` 的 `acceptance_gate` 只有在 `--steps == 500000`、`--suite-count >= 3`、`--first-suite == 1`、全部套件 valid、五个 seed 的状态哈希跨套一致、且墙钟中位数 `<= 3600 s` 时才为 `GO`。

## 4. Validation & Error Matrix

| 条件 | 行为 |
| --- | --- |
| `--out-root` 已存在且非空 | 启动前报错退出，不覆盖 |
| `--steps`/`--suite-count`/`--first-suite` 非正或 `first-suite < 1` | 启动前报错退出 |
| `--checkpoint-interval`/`--sample-interval` 非正 | 启动前报错退出 |
| `--dotnet`/`--scenario`/`--cli-dll` 不存在 | 启动前报错退出 |
| 任一训练进程非零退出 | 该 run `valid=false`，其上套件 `invalid`，`acceptance_gate=NO-GO` |
| 任一 run 有 fault | 同上，且 fault 数写入套件结果 |
| 套件内身份字段不一致 | 记录 `identity_issues`，套件 `invalid` |
| episode seed 流重叠或 seed 集合不完整 | 记录 `seed_isolation_issues`，套件 `invalid` |
| 套件中途被外部终止（会话结束等） | 无 `suite-result.json`；原始 `resources.csv`/`progress.csv` 保留，须按 §6 记录并重测 |

失败或中断的套件不得从统计中静默剔除，也不得用"重跑直到好看"替代记录。

## 5. Semantics

- 每个训练进程保持 `n_envs=1`、每次 PPO update 2048 transitions、每 51,200 全局 transitions 一个 checkpoint；这是第一候选的冻结语义。
- 同一训练 seed 在三套之间必须得到逐位一致的模型状态（`policy.pth`、`policy.optimizer.pth`、`pytorch_variables.pth` 的内容哈希，忽略 SB3 ZIP 时间戳与运行期元数据）；不一致说明存在未受控随机性或身份漂移。
- 五个训练 seed 的 PPO RNG 与 episode seed 流分离；episode seed 从注册训练池 `{42, 20260925, 1000–1999}` 确定性分区，五个 v4 训练 seed 各分得 200 或 201 个，互不重叠。
- `artifact_hashes` 必须覆盖最终模型、`episodes.monitor.csv`、`progress.csv`，并由 checkpoint 审计逐项比对。

## 6. Interrupted suites

套件因宿主会话结束、进程被杀或机器中断而没有 `suite-result.json` 时，必须在套件目录留下可审计记录（时间、原因、各 seed 最后进度、清洗与重测决定），并明确：

1. 该次尝试不计入中位数；
2. 重测必须使用完全相同的冻结 runner 哈希、seed、场景与 CLI DLL；
3. 重测结果单独存放，不覆盖原残留证据。

## 7. Tests Required

- `selftest.py` 覆盖：固定五 seed 的 episode 流确定性互斥、SB3 seed 与 episode seed 分离、输出目录冲突拒绝、训练进程异常时保留失败记录、checkpoint 按全局 transition cadence 审计。
- 提交正式测量前先跑小规模 runner smoke（`--steps 2048 --suite-count 3`），断言全部套件 valid、episode seed 无重叠、五个 seed 的状态哈希跨套一致。
- 正式测量要求三套全部 valid、15/15 run 有效、faults=0、`spec_conformant_measurement_size=true`。

## 8. Wrong vs Correct

### Wrong

把第一套失败或中断后直接删目录、只重跑一套并只汇报成功套件，或用整包 ZIP 哈希判断固定 seed 复现（SB3 ZIP 含时间戳，会假性判定不一致）。

### Correct

保留中断套件的原始采样与进度并写 `INTERRUPTED.json`，在同一冻结条件下重测；用 `policy.pth`/`policy.optimizer.pth`/`pytorch_variables.pth` 的成员内容哈希判断固定 seed 复现。
