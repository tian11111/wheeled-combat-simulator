# Sim.Cli 命令参考

无头评测、固定种子批量评估、回放录制/校验。全部在仓库根目录运行。

## 公共选项

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `--seed N` / `--seeds a,b,c` | `42` | 确定性种子（可多个做批量评估） |
| `--scenario <path>` | 官方默认布局 | `scenarios/*.json` 场景文件 |
| `--duration <s>` | 120 | 覆盖比赛时长 |
| `--controller-us <cmd>` | 内置 FSM | 我方外部策略进程命令 |
| `--controller-them <cmd>` | 内置 FSM | 对手外部策略进程命令 |
| `--timeout-ms <ms>` | 100 | 策略单帧响应截止时间 |
| `--events` | 关 | 逐条打印事件日志 |
| `--out <path>` | — | `replay-record` 输出文件 |

## match — 无头比赛

```bash
dotnet run --project src/Sim.Cli -- match --seed 42
dotnet run --project src/Sim.Cli -- match --seeds 1,2,3,4,5 --events
dotnet run --project src/Sim.Cli -- match --seed 42 \
  --controller-us "python controllers/example_controller.py"
```

输出每种子一行（步数、比分、结束原因、fault、判罚），多种子时附胜负汇总。
未指定 `--controller-*` 的角色使用内置 FSM。

## replay-record — 录制回放

```bash
dotnet run --project src/Sim.Cli -- replay-record --seed 42 --out replays/seed-42.json
dotnet run --project src/Sim.Cli -- replay-record --seed 42 \
  --controller-us "python controllers/example_controller.py" --out replays/seed-42-pyus.json
```

生成 `sim-replay-v1` 文件：场景 + 每步被接受的真实动作流 + 最终比分与事件指纹。
只记录**实际被接受**的动作（request-id 匹配与钳位之后）；超时/坏行不落盘。

## replay-check — 校验回放

```bash
dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
```

用文件里的场景重建内核，逐步重放记录的动作流，逐位比对**比分与事件序列**。
`PASS`（exit 0）= 完全复现；`FAIL`（exit 1）打印首个分叉事件。这是确定性的持续证据。

## calibrate — 离线真机标定（遥测 → 参数报告 → 场景/保真度）

```bash
dotnet run --project src/Sim.Cli -- calibrate --input telemetry/data/robot-01.json \
  --out calibration/robot-01-report.json \
  --emit-scenario scenarios/calibrated-robot-01.json   # 可选：直接生成可加载场景
# 复核报告后才登记保真度晋升（只晋升留出达标且 source=real 的子系统）：
dotnet run --project src/Sim.Cli -- calibrate --input telemetry/data/robot-01.json \
  --out calibration/robot-01-report.json --force --update-fidelity
```

消费 `telemetry-v1` 契约（见 `telemetry/README.md`）：入口一次性校验 SI 单位、时间戳、
kind 必填字段；**任何校验失败都不会产出报告或 patch**。拟合算法数值等价迁移自遗留
`sim_calibrate.js`（指数衰减/块摩擦一维搜索/恢复系数最小二乘/堵转阈值分类）；
每个参数分列**拟合集与留出集**指标，晋升要求留出达标且数据为真机
（合成自测数据永不晋升）。登台门控只验证不拟合：误判率/覆盖不足时如实报告模型不足。
报告带输入 SHA-256 与内容指纹（排除生成时间，重跑逐字节一致）；结果应用为新场景
文件 + `--update-fidelity` 显式登记，官方场景与旧回放逐位不变。

## 退出码

- `0` 成功 / 回放一致
- `1` 运行错误或回放分叉
- `2` 参数错误
