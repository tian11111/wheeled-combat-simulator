# 技术设计：玻璃控制台、运行设置与自定义小车控制器

## 1. 边界与已确认决策

- Godot 继续是桌面表现与编排壳；`Sim.Core` 不新增 Godot、文件、进程、线程或时钟依赖。
- 玻璃 UI 只消费 `RenderFrame`、`SessionMode` 和编辑器状态；设置与控制器偏好不进入 `Scenario`、`Snapshot`、回放指纹或 `fidelity.json`。
- 显示/窗口设置即时应用；仿真参数和控制器配置只在新比赛或显式重置后应用。
- 自定义小车使用外部命令/脚本 + 既有 JSONL stdio 协议，不做内嵌代码编辑器、进程内动态编译或远程执行。
- 现有未提交的中央圆环移除、README/spec 记录和其他用户改动不在本任务范围内，不覆盖、不回滚。

## 2. 组件拓扑

```text
Main (Godot 主线程)
 ├─ HudPanel
 │   └─ SettingsPanel (模态玻璃层，事件回调回 Main)
 ├─ ArenaVisualizer / MatchCamera / LayoutEditor
 └─ MatchSession / DesktopLiveDriver
       ├─ immutable RenderFrame/Snapshot 队列
       └─ Sim.Controller.ExternalControllerBridge (每场独立)

DesktopSettings (Godot-free DTO + validator)
 ├─ WindowSettings / UiSettings
 ├─ SimulationParameters (现有 SimParameters 白名单)
 └─ ControllerProfiles (us/them 命令、timeout)
```

`SettingsPanel` 不直接操作 `MatchEngine`。它只编辑临时 settings draft，点击应用后通过类型化回调交给 `Main`；`Main` 决定即时应用显示项或把比赛相关设置挂起到下一次会话重建。

## 3. 设置数据与持久化

### 3.1 DTO

新增 Godot-free 的桌面配置记录（建议放在 `godot/src/DesktopSettings.cs`，保持不引用 Godot）：

- `SchemaVersion`：配置格式版本。
- `Window`: 宽、高、窗口模式。
- `Ui`: 0.8–1.4 的 UI scale（默认 1.0）。
- `SimulationParameters`: 仅允许 `SimParameters.FromDictionary` 已登记的键。
- `Controllers`: 我方/对手模式（内置 FSM 或 external）、命令文本、timeout。

仿真参数按“常用/高级”分组，但序列化仍使用既有精确键名：
`EDGE_THRESHOLD`、`FALL_THRESHOLD`、`ON_STAGE_THRESHOLD`、`grayNoise`、`irNoise`、
`IR_TRIGGER`、`MOUNT_SPEED`、`classifyRate`、`RECOVER_LIMIT`、`STALL_TIME`、
`STALL_SPEED`、`STALL_RELEASE`、`STALL_DISPLACEMENT`、`cmdLatencyFrames`、
`IR_HYST_BAND`、`graySpotRadius`、`BLOCK_STICK_SPEED`、`BLOCK_MU_K`、
`COLLISION_RESTITUTION`、`MOUNT_V_MIN`、`MOUNT_ANGLE_MAX`、
`antiStallBladeAmp`、`antiStallBladePeriodUs`、`antiStallBladePeriodThem`。

参数元数据集中在一个纯 C# 白名单表中，包含 label、单位、默认值、最小/最大值、整数/小数格式、常用/高级分组和“实验性”标记。`null` 语义的恢复默认项（恢复系数和反僵局周期等）用“自动/默认”开关表达；默认状态从字典中省略键，不能写入伪造数值。

### 3.2 验证与应用

- `SettingsValidator` 在保存和应用前执行：未知键、非有限值、越界值、错误尺寸、空命令和非法 timeout 都拒绝，并返回字段级错误。
- `SettingsStore` 只负责 `user://wushu-ring-settings.json` 的读取/原子写入；JSON 损坏、版本未知或字段缺失时使用默认值并输出 `[settings]` 诊断。
- 显示配置通过 `GetWindow().Size`、窗口模式和已有 CanvasItem stretch 设置应用；不修改设计视口。
- 仿真配置通过克隆 `Scenario.Parameters` 生成新的 `Scenario`，再创建新的 `MatchSession`。运行中的 `MatchEngine`、回放缓存和编辑草稿不被就地修改。
- 控制器配置只在新 live session 创建时实例化；回放继续按回放自身的动作流运行，不启动外部控制器。

## 4. 玻璃 UI

- 在现有 `HudPanel` 样式工厂上收敛统一玻璃 token：半透明冷色填充、细边框、圆角、内边距、顶部高光和轻阴影；原有蓝/红/黄/绿状态颜色与文字标签保留。
- 玻璃效果采用纯 Godot UI：透明 `StyleBoxFlat` + 层次叠加，不依赖第三方主题或必须支持的全屏模糊。若后续加入背景模糊，必须可独立关闭并保留此回退样式。
- SettingsPanel 使用与 HUD 相同的 token，作为模态层显示时拦截鼠标和快捷键；关闭后恢复之前的 HUD/编辑器/回放状态。
- UI scale 统一作用在一个设计根节点或统一 token 层，避免逐个控件散落乘法；锚点仍以 1280×720 设计视口为基准。

## 5. 自定义控制器运行时

### 5.1 共享桥接层

将现有 `src/Sim.Cli/PythonBridge.cs` 抽取到小型 `src/Sim.Controller` 类库（引用 `Sim.Core`、`Sim.Protocol`），CLI 和 Godot 共用同一实现；保留 JSONL、request-id、timeout、zero-action、fault 和杀进程树语义。不要把 Process/线程代码放进 `Sim.Core`。

### 5.2 非阻塞桌面驱动

当前桥接 `Decide` 是同步等待响应，不能在 Godot 主线程直接调用。新增 `DesktopLiveDriver`（Godot-free 核心编排类）：

- 一个 driver 独占一个 `MatchEngine`、一个固定步长循环、双方各自的 bridge（未配置角色仍走 null/FSM）。
- worker 线程/任务只访问自己的 engine、controller 和命令队列；每 tick 建 observation、收 action、调用 `engine.Tick`，把不可变 `Snapshot` 放入有界队列。
- Godot 主线程只投递 `Arm`、暂停/继续、重启、重置和停止命令，并消费最新 snapshot；任何外部控制器等待都不能阻塞渲染线程。
- 命令按序在 driver 线程执行；重置/关闭先停止 tick，再 dispose bridges，避免 engine 与 bridge 并发访问。
- controller status（启动中、已连接、fault、退出）通过只读状态快照回传 HUD；故障不改变核心规则，仍由 bridge 返回零动作并计数。

`MatchSession` 保留内置 FSM 与回放路径的现有语义；Main 只在 live 且至少配置一个 external controller 时启用 driver，其他路径不引入后台线程，降低回归面。

## 6. 数据流与兼容性

```text
SettingsPanel draft
  ├─ display fields → Main → Window (即时)
  ├─ simulation fields → pending Scenario clone → next MatchSession
  └─ controller fields → pending ControllerProfiles → next DesktopLiveDriver

DesktopLiveDriver → Snapshot → SnapshotView → ArenaVisualizer/HudPanel
```

设置文件中的绝对路径和命令只用于桌面启动，不进入 replay header。加载 replay 时忽略 pending controller，保证回放继续消费记录动作。布局编辑仍只在 Prep 工作，设置模态层打开时相机/编辑器输入让位给 UI。

## 7. 回滚与风险

- 视觉问题优先回滚 `HudPanel`/`SettingsPanel` 样式，不触碰 session/core。
- 控制器 runtime 若出现线程/回收问题，关闭桌面 external driver 入口仍可恢复内置 FSM；共享 bridge 与 CLI 回归必须独立验证。
- 运行中外部控制器的非确定性不宣称核心确定性；旧回放/CLI batch 仍以既有结果为准。
- 不做真正的代码沙箱或安全承诺；用户明确选择外部命令后，UI 只做路径/命令可执行性和协议预检，错误必须可见。
