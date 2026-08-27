# 架构

2026 RoboCup 武术擂台轮式对抗模拟器。目标：**可维护、可复现、可替换控制器**的桌面仿真。

## 分层与职责边界

```
┌──────────────────────────┐   ┌──────────────────────────┐
│ godot/  (Godot 4 .NET)   │   │ Sim.Cli  (.NET 8 无头)    │
│ 3D 展示壳: 渲染/相机/HUD/ │   │ 评测/回放: match /        │
│ 回放时间轴               │   │ replay-record / replay-check│
│ 只消费快照+发裁判指令     │   │ 可拉起 Python 策略进程    │
└──────────┬───────────────┘   └──────────┬───────────────┘
           │  同一 MatchEngine / 同一协议    │
           ▼                               ▼
      ┌──────────────────────────────────────────┐
      │ Sim.Core   确定性内核 (无引擎/无 IO 依赖) │
      │ 固定步长 0.05s · 种子化随机 · 规则裁判    │
      │ 传感器/感知 · 物理接触 · 事件流 · 快照    │
      └──────────────┬───────────────────────────┘
                     │ 版本化 DTO (camelCase JSON)
                     ▼
      ┌──────────────────────────────────────────┐
      │ Sim.Protocol   协议与校验                  │
      │ Observation / Action / Snapshot / Event /  │
      │ Scenario / ReplayHeader                    │
      └──────────────────────────────────────────┘
```

**单一权威**：比分、回放、AI 观测全部以 `Sim.Core` 的确定性 2D 模型为准。
Godot 物理仅用于可视摆放与可选诊断，**不参与判分**。未来若引入 3D 权威物理，
必须显式新增模式/版本并单独提供确定性与保真度证据，不属于当前范围。

## 确定性契约

- 固定步长 `tickSeconds = 0.05s`；同一种子 + 同一被接受的动作序列 ⇒
  逐位一致的事件流与比分。
- 随机数用 mulberry32 并按 `(seed, step, role, channel)` 派生独立传感器噪声流，
  增删一个传感器通道不会重排比赛逻辑里的随机序列。
- 渲染掉帧/客户端关闭 **不改变**仿真时钟：展示端用固定步长累加器推进内核。

## 跨端一致性

桌面壳与无头端共用同一个 `MatchEngine`，因此同种子下进程完全一致。验证手段：

| 手段 | 命令 | 说明 |
| --- | --- | --- |
| 内核回归 | `dotnet test` | 95 个测试：规则、确定性、回放复现、跨端校验、视图适配 |
| 无头复现 | `dotnet run --project src/Sim.Cli -- replay-check <file>` | 用记录的动作流逐位重放并比对 |
| 跨端一致 | `godot --headless --path godot -- --parity-check <file>` | Godot 壳按 CLI `replay-check` 语义比对最终比分/结束原因/末帧/事件指纹（无 Godot 时由 `CrossEndTests` 回归同一代码路径） |
| 视图适配 | `SnapshotViewTests` | 快照→渲染帧投影/插值不失真 |

Godot↔CLI 的同种子一致性已闭环：`godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json`
返回 PASS（比分 4:49、结束原因、末帧 2400、752 条事件指纹逐位一致）；另见 `godot/README.md` 与 `src/Sim.Tests/CrossEndTests.cs`。

## 保真度

见根目录 [`fidelity.json`](../fidelity.json)。规则已验证；场地灰度为手绘、视觉为随机桩、
摩擦/碰撞/堵转/登台未标定。**模拟结果不能直接宣称为真机成绩。**

## 关键文件

- `src/Sim.Core/MatchEngine.cs` — 比赛内核（发令/暂停/重启判罚/单步/快照/回放头）。
- `src/Sim.Core/{Physics,Sensors,Fsm,RuntimeState,DeterministicRandom,Js}.cs` — 物理/传感器/状态机/随机数。
- `src/Sim.Protocol/` — 版本化协议 DTO 与 JSON 校验。
- `src/Sim.Cli/Program.cs` — 无头命令；`PythonBridge.cs` — 外部策略进程适配。
- `godot/src/SnapshotView.cs` — 快照→渲染帧（无 Godot 依赖，可单测）。
- `godot/src/MatchSession.cs` — 会话门面：固定步长实况 + 回放重构/缓存/导航（无 Godot 依赖，可单测）。
- `godot/src/ParityCheck.cs` — 跨端一致性校验（无 Godot 依赖，可单测）。
- `godot/src/{Main,ArenaVisualizer,HudPanel,MatchCamera}.cs` — 桌面壳入口与渲染/HUD/相机。
- `scenarios/*.json` — 固定布局回归场景；`replays/` — 回放文件。
