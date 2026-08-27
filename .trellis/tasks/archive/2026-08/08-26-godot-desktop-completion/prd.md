# 完成 Godot 桌面端与跨端验收

## Goal

把当前 Godot 脚手架完成为可实际运行、可观察、可控制和可回放的桌面模拟器，并闭合 Godot 与 `Sim.Cli` 共用确定性内核的验收证据。

## Background

- 架构任务 `08-26-robot-simulator-architecture` 已完成并归档；`.NET` 协议、核心、CLI、Python JSONL 桥和 89 项测试可用。
- 2026-08-26 复核结果：项目级构建成功，`Sim.Tests` 为 89/89 通过；完整 seed 42 比赛、Python 控制器和回放校验均可运行。
- Godot 工程仍在 `godot/README.md:1`、`godot/src/Main.cs:1` 和 `godot/src/ArenaVisualizer.cs:1` 明确标记为“脚手架/未编译验证”，原任务的桌面端与跨端验收未闭环。
- 复核发现 `--duration` 参数无效：`FieldParams.MatchDuration` 能被 CLI 设置，但 `src/Sim.Core/MatchEngine.cs:54` 与 `src/Sim.Core/RuntimeState.cs:82` 把比赛计时器硬编码为 120 秒。实测 `--duration 3` 仍运行 2400 tick。

## Requirements

- `MatchEngine` 必须从 `Scenario.Field.MatchDuration` 初始化全局和双方计时器，默认 120 秒行为不变。
- Godot 4 .NET 工程必须能由编辑器和 headless 命令加载、构建及启动，不保留“未编译验证”状态。
- 桌面端必须展示完整场地、6 cm 擂台、双方机器人、2 个增益块和 1 个减益块；机器人和能量块位置只来自 `SnapshotView`。
- 桌面端至少支持发令、暂停/继续、双方重启判罚、重置同 seed 比赛、切换基础观察镜头。
- HUD 必须显示剩余时间、比分、比赛阶段、双方状态/动作和最近事件；动态内容不得改变布局或遮挡主要场景。
- 桌面端必须能加载 `Sim.Cli replay-record` 生成的回放，提供播放/暂停、单步和时间轴跳转；回放不得重新实现裁判规则。
- Godot 与 CLI 必须对同一 scenario、seed 和动作/命令序列得到相同最终比分、结束原因和事件指纹。
- Godot 的物理和渲染不得写回 `Sim.Core` 权威位置或判分状态。
- 继续兼容现有 Python `decide(obs) -> {v,w}` 协议和 `diagnostic-v1` 语义；本任务不重构协议。

## Acceptance Criteria

- [x] `dotnet run --project src/Sim.Cli -- match --seed 42 --duration 3` 在 60 tick 左右结束，结束原因是比赛时间到；默认 120 秒仍为 2400 tick。
- [x] 针对自定义比赛时长新增核心和 CLI 回归测试，完整 `dotnet test` 全绿。
- [x] `godot --headless --path godot --editor --quit` 成功且无脚本/场景解析错误。
- [x] Godot 桌面程序可启动并完成一场比赛；场地、机器人、能量块、HUD 和控制均可用。
- [x] Godot 可打开 CLI 生成的回放并完成播放、暂停、单步和跳转。
- [x] 固定 seed 跨端测试比对最终比分、结束原因和事件指纹，结果一致。
- [x] `Sim.Core` 仍不依赖 Godot、文件系统、网络、进程或系统时钟。
- [x] README、Godot 文档和架构文档不再把桌面端标记为未验证脚手架，并准确说明保真度边界。

## Out Of Scope

- 权威 3D/Jolt 物理、真实机器人摩擦/碰撞/登台标定。
- 微服务、数据库、联网对战、赛事分组和淘汰赛管理。
- YOLO 模型训练或真实相机接入。
- 高成本美术资产、粒子特效或最终品牌视觉精修。

## Risks And Deferred Items

- 当前机器未发现 Godot 4 .NET 可执行文件。实现者应先检查既有安装；若需要下载安装，按执行环境的权限流程获得授权后再继续。
- 图形可用性不得成为核心测试的前置条件；Godot 层失败时 `.NET` 无头流程仍必须可用。
