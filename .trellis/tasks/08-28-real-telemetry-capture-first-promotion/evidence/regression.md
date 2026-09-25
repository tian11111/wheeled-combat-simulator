# 回归结果(阶段 5, 晋升提交的硬门禁)

| 门禁 | 命令 | 结果 | 日期 |
|---|---|---|---|
| 全套测试 | `dotnet test RobotSimulator.sln -m:1` | 待跑 | |
| 旧回放基线 | `replay-check replays/seed-42.json`(及其他旧回放) | 待跑 | |
| Godot parity | `--headless --path godot -- --parity-check <replay>` | 待跑 | |
| 候选场景 smoke | `match --scenario scenarios/calibrated-<vehicle>.json --seed 42` | 待跑 | |
