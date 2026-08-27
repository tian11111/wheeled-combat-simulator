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

## sensor-calibration import — MBri 传感器标定证据导入（离线，另一条标定线）

与 `calibrate`（物理遥测 telemetry-v1）不同，本命令导入 **MBri 真车传感器判定模型**
（灰度四路、前头双路 ADC、铲下双路），产出 `sensor-calibration-v1` 证据报告。
采集规范与选择规则见 `telemetry/README.md` 的传感器章节；仓库不复制原始数据。

```bash
dotnet run --project src/Sim.Cli -- sensor-calibration import   --data-dir "D:/project/robocup/MBri/data" --manifest selection.json   --out calibration/sensor-report.json [--config config.py] [--force]
```

要点：
- 只消费选择清单点名的文件（表头精确匹配），其余文件**全部列入 ignored**，
  被拒文件带原因；绝不按列名猜测导入 187 个文件。
- 对导入模型回放对应原始 CSV：灰度 zone/white 迟滞、前差带 left/forward/right、
  铲子悬空/收回；输出就绪/无效样本数、决策分布、误判与失败文件、运行时候选标志。
- stored 模型 / 全量重算 / config.py 快照三者差异全部进 comparison 表，
  **只报告不合并**；任何不一致把状态压回 `evidence_only`/`rejected`。
- 灰度数据无坐标 → 报告恒含 `coordinateData=false` 与不能构造 GrayGridMap 的限制。
- 不触碰 FieldModel/SensorSampler/FSM/官方场景/fidelity.json/回放；纯离线产物。
- 退出码：0 报告已写（允许 evidence_only）；1 校验/IO 错误（零输出）；2 用法。

## 退出码

- `0` 成功 / 回放一致
- `1` 运行错误或回放分叉
- `2` 参数错误
