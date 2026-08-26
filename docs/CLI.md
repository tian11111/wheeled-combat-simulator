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

## 退出码

- `0` 成功 / 回放一致
- `1` 运行错误或回放分叉
- `2` 参数错误
