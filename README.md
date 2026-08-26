# robot-simulator — 2026 RoboCup 武术擂台轮式对抗模拟器

可维护、可复现、可替换控制器的桌面仿真：**.NET 8 确定性内核** + **Godot 4 .NET 3D 桌面壳** +
**Python 策略桥**，共享同一套比赛状态协议与规则。

## 快速开始

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
dotnet build                                   # 构建
dotnet test                                    # 89 个回归测试(规则/确定性/回放/视图)

# 无头比赛(内置 FSM)
dotnet run --project src/Sim.Cli -- match --seed 42

# 接入外部 Python 策略(我方)
dotnet run --project src/Sim.Cli -- match --seed 42 \
  --controller-us "python controllers/example_controller.py"

# 录制并校验回放(确定性证据)
dotnet run --project src/Sim.Cli -- replay-record --seed 42 --out replays/seed-42.json
dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
```

3D 桌面端在 `godot/`，需要安装 Godot 4 .NET（当前为脚手架，见 `godot/README.md`）。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/Sim.Core` | 确定性比赛内核（规则/物理/传感器/事件/快照），无引擎依赖 |
| `src/Sim.Protocol` | 版本化协议 DTO 与 JSON 校验 |
| `src/Sim.Cli` | 无头评测/回放 + Python 进程适配器 |
| `src/Sim.Tests` | xUnit 回归 |
| `godot/` | Godot 4 .NET 桌面壳（脚手架） |
| `controllers/` | 示例外部策略（JSONL stdio） |
| `scenarios/` | 固定布局回归场景 |
| `replays/` | 回放文件（不入库） |
| `tools/legacy-baseline.js` | 从旧原型再生成回归基线 |
| `fidelity.json` | 保真度声明 |

## 文档

- [架构与确定性契约](docs/ARCHITECTURE.md)
- [控制器协议 decide(obs)→{v,w}](docs/CONTROLLER_PROTOCOL.md)
- [Sim.Cli 命令参考](docs/CLI.md)
- [移植决策记录](docs/PORTING_NOTES.md)
- [从旧原型迁移](docs/MIGRATION.md)

## 保真度边界

规则已验证；场地灰度为手绘、视觉为随机桩、摩擦/碰撞/堵转/登台未标定。
见 [`fidelity.json`](fidelity.json)。**模拟结果不能直接宣称为真机成绩。**
