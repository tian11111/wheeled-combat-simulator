# godot/ — Godot 4 .NET 桌面端（已验证）

本目录是 2026 武术擂台模拟器的 3D 桌面客户端。**仿真权威在 `src/Sim.Core`**；
本客户端只消费 `Snapshot`（经 `SnapshotView` 投影）并发出裁判指令
（arm/pause/resume/restart/加载回放），不复刻规则、不跑自己的物理判分
（`Godot` 物理仅做可视摆放与截图 QA）。

## 状态

- ✅ 已编译验证：Godot 4.7.2 Mono（`Godot.NET.Sdk/4.7.2`）下 `--headless --editor --quit`
  与 `--build-solutions` 均通过，无脚本/场景解析错误。
- ✅ 桌面可运行：实况、回放两模式在 1280×720 / 1920×1080 窗口冒烟通过
  （证据截图见 `docs/desktop-720.png`、`docs/desktop-1080.png`、`docs/desktop-replay-720.png`）。
- ✅ 跨端一致性：`--parity-check` 与 `Sim.Cli replay-check` 语义一致；对
  `replays/godot-parity-seed42.json` 得到最终比分、结束原因、末帧 2400、752 条事件指纹逐位一致。
- ✅ `src/SnapshotView.cs`、`src/MatchSession.cs`、`src/ParityCheck.cs` 为无 Godot 依赖的纯 C#
  层，经 `Sim.Tests` 编译链接纳入回归（含回放重构与跨端比对测试）。

## 布局

```
godot/
├─ project.godot           # 输入映射 (Enter 发令/P 暂停/R T 重启/F5 重置/C 镜头/L 打开回放)
├─ GodotSim.csproj         # Godot.NET.Sdk (4.7.2), 依赖 Sim.Core + Sim.Protocol
├─ scenes/Main.tscn        # 相机/灯光/环境/可视化器/HUD 场景树
├─ docs/*.png              # 桌面冒烟截图证据
└─ src/
   ├─ SnapshotView.cs      # 纯快照→RenderFrame 投影与插值（可单测）
   ├─ MatchSession.cs      # 纯会话门面: 固定步长实况 + 回放重构/缓存/导航（可单测）
   ├─ ParityCheck.cs       # 纯跨端一致性校验（可单测, 与 CLI replay-check 同语义）
   ├─ Main.cs              # 壳入口: 指令路由 + 无头 parity-check / 截图 QA 参数
   ├─ ArenaVisualizer.cs   # 程序化场地/机器人/能量块网格 + RenderFrame 摆放
   ├─ HudPanel.cs          # 锚点 HUD: 状态卡/事件/操作帮助/回放时间轴
   └─ MatchCamera.cs       # 概览/跟随/俯视 三模式相机
```

## 操作

| 键 | 实况模式 | 回放模式 |
| --- | --- | --- |
| Enter | 发令 (arm) | — |
| P | 暂停 / 继续 | — |
| R / T | 我方 / 对手重启判罚 (+4) | — |
| F5 | 重置为同 seed 新比赛 | 回到实况 |
| C | 切换镜头 (概览→跟随→俯视) | 同左 |
| L | 打开回放文件对话框 | 同左 |
| 空格 / ← / → / Home / End | — | 播放暂停 / 单步 / 到首尾帧 |
| 时间轴滑块 | — | 跳转到任意 tick |

窗口底部回放条仅在回放模式显示；时间轴滑块把回放缓存中每个 tick 快照
（由内核按 CLI 录制的动作/指令流重构）直接呈现，**不重写任何规则**。

## 无头参数（`godot --path godot -- ...`）

| 参数 | 作用 |
| --- | --- |
| `--parity-check <replay.json>` | 跨端一致性校验，按 CLI `replay-check` 语义比对比分/结束原因/末帧/事件指纹；成功退出码 0 |
| `--replay-path <replay.json>` | 启动即加载回放（也读导出属性 `ReplayPath`） |
| `--replay-tick <n>` | 加载回放后跳到第 n tick |
| `--auto-arm` | 启动即发令（演示/截图用） |
| `--capture <out.png>` | 渲染 30 帧后保存视口 PNG 并输出分桶像素统计（视觉 QA 证据），随后退出 |

## 与无头端的一致性承诺

同一 `seed` + 同一动作序列下，客户端渲染的比赛与
`dotnet run --project src/Sim.Cli -- match --seed N` 完全一致——因为两边共用
同一个 `MatchEngine`，客户端不做任何本地仿真。渲染掉帧只影响画面流畅度，
不改变仿真时钟（固定步长累加器）。

## 无头校验脚本

```powershell
# 生成基线并验证跨端一致
dotnet run --project src/Sim.Cli --no-build -- replay-record --seed 42 --out replays/godot-parity-seed42.json
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json
```