# Godot 深色赛事控制台 UI 技术设计

## Boundary

改造集中在 `godot/src/HudPanel.cs` 和必要的 Godot 场景 UI 配置。`Main.cs`、`LayoutEditor.cs`、`SnapshotView`、`MatchSession` 和 `Sim.Core` 继续作为状态/规则来源，不新增协议字段，不修改回放或布局数据。

## Visual System

- 使用程序化 `StyleBoxFlat` 和 Godot theme override 建立统一的深色面板、边框、圆角、内边距和字体层级。
- 顶部/左侧比赛状态卡突出阶段、倒计时和比分；双方状态使用黄/蓝强调，同时保留文字标签，避免只依赖颜色。
- 最近事件使用独立事件卡，区分普通事件、判罚和比赛结束信息，动态文本在固定容器内滚动/裁剪而不挤压主卡。
- 右上帮助区改为紧凑的快捷键面板；编辑模式显示一组独立工具栏，应用按钮和不可用状态具有明显反馈。
- 回放控制条保持现有按钮回调顺序和时间轴行为，统一按钮尺寸、间距、悬停/禁用视觉。

## Layout and Data Flow

`RenderFrame/HudState + SessionMode + editor state → HudPanel presentation controls → Godot renderer`。

动态数据仍由 `UpdateFrame`、`UpdateEditor` 写入现有状态，不从 UI 反向计算比分、阶段、碰撞或布局。使用锚点和 `MarginContainer`/`VBoxContainer`/`HBoxContainer` 组合，避免依赖固定绝对坐标；仅保留必要的最大宽度和边距约束。

## Compatibility

- 快捷键：Enter、P、R、T、F5、C、L、E、Ctrl+Z/Y、方向键和回放控制动作不变。
- 截图 QA 继续使用真实 renderer 的 `--capture`，headless dummy renderer 只作逻辑/加载验证。
- 不改变 `Scenario`、`Snapshot`、回放事件指纹、Sim.Core 规则和 Godot parity。

## Rollback

若截图出现遮挡、文字截断或编辑逻辑回归，回退 `HudPanel.cs` 的视觉层改动；不需要回滚核心仿真或协议。新 UI 的视觉验证失败不能通过降低截图门槛解决。
