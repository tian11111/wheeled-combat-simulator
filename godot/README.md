# godot/ — Godot 4 .NET 桌面端（脚手架，未编译验证）

本目录是 2026 武术擂台模拟器的 3D 展示壳。**仿真权威在 `src/Sim.Core`**；
本客户端只消费 `Snapshot` 并发出裁判指令（arm/pause/resume/restart），
不复刻规则、不跑自己的物理判分（design.md「Godot physics 仅作诊断」）。

## 状态

- ⚠️ **脚手架**：需要安装 [Godot 4.x .NET (Mono)](https://godotengine.org/download) 才能打开与编译。
  当前开发机未安装 Godot，`src/Main.cs` / `src/ArenaVisualizer.cs` / `scenes/Main.tscn`
  未经编辑器验证；安装后第一次打开时由编辑器生成 `.godot/` 缓存并校正 `GodotSim.csproj`。
- ✅ **可验证部分**：`src/SnapshotView.cs` 是无 Godot 依赖的纯类型视图适配器
  （快照 → 渲染帧 + 帧间插值），已通过 `Sim.Tests` 的编译链接纳入回归测试。

## 布局

```
godot/
├─ project.godot           # Godot 4 项目（输入映射: Enter 发令, P 暂停, R 重启判罚）
├─ GodotSim.csproj         # Godot.NET.Sdk, 依赖 Sim.Core + Sim.Protocol
├─ scenes/Main.tscn        # 相机/灯光/可视化器/HUD 场景树
└─ src/
   ├─ SnapshotView.cs      # 纯快照→RenderFrame 投影与插值（可单测）
   ├─ Main.cs              # 固定步长驱动内核 + 指令入口
   └─ ArenaVisualizer.cs   # RenderFrame → 节点变换 + HUD 文本
```

## 与无头端的一致性承诺

同一 `seed` + 同一动作序列下，本客户端渲染的比赛进程与
`dotnet run --project src/Sim.Cli -- match --seed N` 完全一致——
因为两边共用同一个 `MatchEngine`，客户端不做任何本地仿真。
渲染掉帧只影响画面流畅度，不改变仿真时钟（固定步长累加器）。

## 后续（Godot 安装后）

1. `godot --headless --path godot --editor --quit` 生成缓存并验证项目可加载；
2. 给 `UsRobot`/`ThemRobot` 挂可视网格（当前为空 Node3D）；
3. 按 implement.md 第 7 项补 Godot↔Sim.Cli 同 seed 对比测试；
4. 回放时间轴控件（`loadReplay`/`step` 指令）——依赖 Sim.Cli 的 `replay-check` 数据格式。
