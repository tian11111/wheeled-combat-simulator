# 设置界面现状盘点（只读代码审计，2026-10-06）

> 来源：Explore 代理全量盘点。所有锚点以当日代码为准；实施前如行号漂移，按符号名重查。

## 定位：两套独立 UI

1. **桌面设置页**：`godot/src/SettingsPanel.cs`（1467 行，`Control`）。**没有专属 .tscn**——整个界面在 `Build()` 里用 C# 运行时构建（`SettingsPanel.cs:159`）。由 `Main.cs:124` 实例化后挂到 `Hud` CanvasLayer（`Main.cs:127`）；`Main.tscn` 只定义 Main/Hud 骨架（`godot/scenes/Main.tscn:91-100`）。入口：HUD 按钮 `⚙ 设置 (F10)`（`HudPanel.cs:177-185`）+ `settings_toggle` 输入动作（`godot/project.godot:101`，F10）。
2. **布局编辑器**（独立面板）：`godot/src/LayoutEditor.cs`（`Node3D`，程序化）+ HUD 顶栏 `HudPanel.cs:294-368` + `FileDialog`（`LayoutEditor.cs:101-118`）；纯模型在 `godot/src/LayoutDraft.cs`。入口 `E`（`editor_toggle`，`Main.cs:1788`），门控 `godot/src/EditorGate.cs`。
3. 相关前端规范：`.trellis/spec/frontend/state-management.md`（布局编辑用 LayoutDraft、不得改运行中的引擎）、`type-safety.md`（先 Deserialize 再 Validate）、`component-guidelines.md`、`quality-guidelines.md`。无设置页专属规范。

设置页共 6 个标签页（`SettingsPanel.cs:210-221`）：显示与窗口 / 仿真参数 / 小车控制器 / 小车 / 视觉 / 能量块。

## A. 已暴露设置清单

### A1. 显示与窗口（`BuildDisplayPage`，`SettingsPanel.cs:248-278`）
| 字段 | 控件 | 范围/默认 | 锚点 |
|---|---|---|---|
| `Window.Width` | SpinBox，后缀 `px` | 640–7680，默认 1280 | `SettingsPanel.cs:260`, 加载 `:534` |
| `Window.Height` | SpinBox，后缀 `px` | 360–4320，默认 720 | `:262`, `:538` |
| `Window.Mode` | OptionButton（窗口化/全屏） | 默认 `windowed` | `:266`, `:540-543` |
| `UiScale` | SpinBox，后缀 `x` | 0.8–1.4 step 0.05，默认 1.0 | `:269`, `:544-547` |

（无 `AllowLesser/Greater`，`MakeSpin` 默认关闭越界 `:1273-1289`。）

### A2. 仿真参数（`BuildSimulationPage`，`SettingsPanel.cs:280-301`）
24 个键，来自 `SimulationParameterCatalog`（`DesktopSettings.cs:625-651`），分两组（`SettingsPanel.cs:298-299`）：
- **常用 9 项**：`EDGE_THRESHOLD` 边缘阈值/灰度 400、`FALL_THRESHOLD` 掉台阈值/灰度 150、`ON_STAGE_THRESHOLD` 登台阈值/灰度 500、`grayNoise` 灰度噪声 30、`irNoise` 红外噪声 0.02、`IR_TRIGGER` 红外触发 0.35、`MOUNT_SPEED` 登台速度 780、`classifyRate` 视觉识别成功率 % 100、`RECOVER_LIMIT` 恢复次数上限 3。
- **高级 15 项**：`STALL_TIME`、`STALL_SPEED`、`STALL_RELEASE`、`STALL_DISPLACEMENT`、`cmdLatencyFrames`、`IR_HYST_BAND`、`graySpotRadius`、`BLOCK_STICK_SPEED`、`BLOCK_MU_K`、`COLLISION_RESTITUTION`、`MOUNT_V_MIN`、`MOUNT_ANGLE_MAX`、`antiStallBladeAmp`、`antiStallBladePeriodUs`、`antiStallBladePeriodThem`。

控件统一为 SpinBox + 单位后缀（`MakeSpin(definition.Minimum,...,definition.Unit)` `SettingsPanel.cs:499`），每组按 `Experimental` 显示"实验性"标签（`:505-508`）；`AllowAutomatic` 的键额外挂一个 `CheckButton "自动"`（`:509-521`），关闭自动才可编辑。范围/默认/整数性见 `DesktopSettings.cs:627-650`；校验见 `:664-689`（含 `MOUNT_V_MIN`/`MOUNT_ANGLE_MAX` 的开区间语义）。

### A3. 小车控制器（`BuildControllerPage`/`BuildControllerSection`，`SettingsPanel.cs:303-421`）
每角色（我方 BLUE / 对手 RED）一组：
| 字段 | 控件 | 范围/默认 | 锚点 |
|---|---|---|---|
| `UsController.Mode` / `ThemController.Mode` | OptionButton（内置 FSM / 内置 MBri / 外部命令） | 默认 `builtin` | `:366-370`, `:663-668` |
| `.TimeoutMs` | SpinBox，后缀 `ms` | 1–5000，默认 100 | `:372-374`, `:674-675` |
| `.Command` | LineEdit（扩大占位符示例） | 默认空 | `:376-384` |
| 预检 | Button "预检" + 结果 Label（非设置字段） | — | `:391-402`, `:423-473` |

### A4. 小车（车体 + 传感器覆盖 + 外观）（`BuildVehiclePage`，`SettingsPanel.cs:766-854`）
车体四参数（`GridContainer`）：
| 字段 | 控件 | 范围/默认 | 锚点 |
|---|---|---|---|
| `Vehicle.Mass` | SpinBox `kg` | 0.2–20，默认 3.5 | `:791-792`, `:548-551` |
| `Vehicle.MotorRpm` | SpinBox `RPM` | 10–2000，默认 120 | `:795-796`, `:552-555` |
| `Vehicle.MotorTorque` | SpinBox `N·m` | 0.05–50，默认 1.72（**仅存档**） | `:799-800`, `:556-559`, 说明 `:1007-1010` |
| `Vehicle.WheelRadius` | SpinBox `m` | 0.005–0.1，默认 0.0325 | `:803-804`, `:560-563` |
实时派生提示（轮端极速）`_vehicleNote`：`:812-814`, `:998-1011`。

传感器覆盖（`:816-947`）：
| 字段 | 控件 | 范围/默认 | 锚点 |
|---|---|---|---|
| `Vehicle.SensorProfileId` | OptionButton（跟随场景 / wheeledCombat11 / legacy14） | 默认 null=跟随 | `:826-831`, `:585-592` |
| `Vehicle.SensorDisabled[]` | 每通道 CheckButton "启用" | 默认全启用 | `:930-935`, `:724-727` |
| `Vehicle.SensorOffsets[ch]` | 每通道 4 个 SpinBox：dx/dy ±0.5 m step .001、dz ±0.2 m、dyaw ±π rad | 默认 0（0 不落盘） | `:930-947`, `:961-974` |

通道行按预设重建（`RebuildSensorChannelRows` `:897-948`；跟随场景时用 legacy14 列表展示 `:904-906,918-921`）。

外观模型（渲染层，`BuildRobotModelSection` `:857-883`，每角色一组；独立文件 `RobotModelLoader.cs:13-28`）：
| 字段 | 控件 | 范围/默认 | 锚点 |
|---|---|---|---|
| `RobotModelConfig.Path` | LineEdit（无文件选择器） | 默认空=primitive | `:862-864` |
| `.Scale` | SpinBox `x` | 0.05–10，默认 1 | `:869`, `:605` |
| `.YawOffset` | SpinBox `rad` | −2π..2π，默认 0 | `:873`, `:606` |
| `.HeightOffset` | SpinBox `m` | −0.2..0.5，默认 0 | `:877`, `:607` |

### A5. 视觉（`BuildVisionPage`，`SettingsPanel.cs:1114-1177`）
| 字段 | 控件 | 范围/默认 | 锚点 |
|---|---|---|---|
| `Vision.Source` | OptionButton 四选一（classifyRate/visionReplay/liveBridge/liveProcess） | 默认 classifyRate | `:1129-1133`, `:612-621` |
| `Vision.EvidencePath` | LineEdit（无文件选择器） | 空 | `:1136-1138` |
| `Vision.CsvPath` | LineEdit | 空 | `:1140-1142` |
| `Vision.ProcessCommand` | LineEdit | 空 | `:1144-1147` |
| `Vision.MaxAgeMs` | SpinBox `ms` | 1–5000，默认 500 | `:1149-1151`, `:634-637` |
按来源禁用不相关输入（`UpdateVisionInputs` `:1180-1200`），动态说明 `UpdateVisionNote` `:1202-1220`。

### A6. 能量块（`BuildBlocksPage`，`SettingsPanel.cs:1017-1069`）
| 字段 | 控件 | 范围/默认 | 锚点 |
|---|---|---|---|
| `BlockLayout`（整体开关） | CheckButton "自定义能量块布局" | 默认关=跟随场景 | `:1037-1040`, `:566-570` |
| `BlockLayout.BuffCount` | SpinBox `个` | 0–`Scenario.MaxBlocks`(=12)，默认 2 | `:1048`, `:571-574` |
| `BlockLayout.DebuffCount` | SpinBox `个` | 0–12，默认 1 | `:1051`, `:575-578` |
| `BlockLayout.RandomPositions` | OptionButton（官方坐标优先/全部随机） | 默认官方 | `:1054-1057`, `:579-582` |
实时提示含截断规则（`UpdateBlockNote` `:1091-1112`）。协议侧对应 `Observation.Debuff`/`Observation.Debuffs`（`src/Sim.Protocol/Observation.cs:286-289`）与 `Scenario.MaxBlocks=12`（`src/Sim.Protocol/Scenario.cs:321`）。

## B. UX 问题清单（17 条）

1. **【高】模态窗固定 980×620、无外层滚动，小窗/高缩放会被裁切**——`_dialog` 用固定尺寸居中（`SettingsPanel.cs:169-176`），`uiScale` 最大 1.4 时有效尺寸 1372×868 超过默认视口 1280×720；而窗口最小允许 640×360（`DesktopSettings.cs:190-197`）。只有标签页内部滚动，对话框本身无滚动。锚点：`SettingsPanel.cs:123-126, 169-176, 205`。
2. **【低】参数行单位重复显示**——`MakeSpin(...definition.Unit)` 已设 `Suffix`（`:499`），紧接着又 `AddLabel(row, definition.Unit)`（`:504`），每行单位出现两次。锚点 `SettingsPanel.cs:499,504`。
3. **【中】参数行 tooltip 暴露内部键名而非说明**——`label.TooltipText = definition.Key`（`SettingsPanel.cs:498`），悬停只看到 `EDGE_THRESHOLD` 这类机器键，无参数含义/影响说明。
4. **【中】绝大多数控件缺 tooltip/单位说明**——全文件仅 6 处 `TooltipText`（`:198,381,392,498,938,1299`）；窗口模式、界面缩放、能量块落位、视觉来源、各 SpinBox 范围均无说明。锚点 `SettingsPanel.cs:265-270, 1054-1057, 1129-1133`。
5. **【中】路径/命令输入无文件选择器，全靠手打**——视觉证据包目录、CSV、推理命令行、外观模型路径均为纯文本框（`MakePathInput` `:1292-1303`；调用 `:1137,1141,1145,862`）。整个 `SettingsPanel.cs` 无 `FileDialog`（仅布局编辑器有，`LayoutEditor.cs:101-118`）。
6. **【高】切换传感器预设会丢弃当前未应用的偏移/禁用编辑**——`RebuildSensorChannelRows` 从 `_settings.Vehicle`（上次已保存值）回填，而非当前控件（`SettingsPanel.cs:907,941-945`）；`ItemSelected += RebuildSensorChannelRows`（`:832`）。用户改完通道再换预设，改动静默丢失。
7. **【中】"恢复默认"不重置外观模型区**——`RestoreDefaults` 只重置 `_settings` 并 `LoadControls`（`:1230-1235`），而模型路径/变换来自 `_robotModels`（`:595-609`），点"恢复默认"后模型绑定仍在。
8. **【中】校验错误文案是英文、与全中文 UI 混排**——`DesktopSettings.Validate()` 产出 `settings: ... must be ...`（`DesktopSettings.cs:181-307`），直接拼进红色错误行（`SettingsPanel.cs:741-746,1244-1252`）。
9. **【中】保存/应用交互模型不统一，无"已应用/待生效"逐项反馈**——点"应用设置"即保存并关闭（`:679-751`）；显示项即时生效，仿真/控制器/视觉/能量块"保存后自动重置生效"，小车页却是"下一场或 F5"（`:783`、`Main.cs:1073-1091`）。全局提示文案（`:138-139,223-225`）未提小车页，用户无法预判某项是否立即生效。
10. **【中】仅改小车参数时不触发自动重置，且日志谎报"显示设置已应用"**——`MatchSettingsEqual` 不比较 `Vehicle`（`Main.cs:1340-1345`），于是 `matchChanged=false` 走 else 分支打印"显示设置已应用"（`Main.cs:1088-1091`），实际车辆/传感器改动要等 F5。
11. **【低】外观模型每次点"应用"都无条件重写 `robot-models.json` 并重挂**——`RobotModelsApplied` 总在 `ApplyDraft` 触发（`SettingsPanel.cs:750`），`SaveRobotModels` 无条件写文件+`ApplyRobotModels`（`Main.cs:1435-1460`），仅改显示设置也会写模型文件。
12. **【中】`MakeOption` 忽略传入的 `Id`，选项含义完全依赖下标顺序**——`MakeOption` 只 `AddItem(item.Label)`（`SettingsPanel.cs:1305-1318`），所有 `("...", id)` 里的 id 是死数据；读取端全按下标 switch（`:886-891,1122-1128,753-764`）。加/删/换序选项极易错位且无编译期保护。
13. **【高】"传感器覆盖"与"能量块"存在功能重叠且互相覆盖**——能量块页自定义会整体替换 `scenario.Blocks`（`DesktopSettings.ApplyBlocks` `DesktopSettings.cs:402-430`）；应用顺序为 `ApplyBlocks` 在场景模板之上（`Main.cs:1209-1213`）。若用户先在布局编辑器摆放/冻结能量块（`ApplyLayoutScenario` `Main.cs:1868-1876`）又开启"自定义能量块布局"，编辑结果被设置页覆盖。
14. **【低-中】分区/命名不一致与硬编码文案**——标签页名"小车控制器"与"小车"易混；大量说明/占位符硬编码中文与示例路径（如 `:324,334-335,862,1033-1034,1106`）；`_vehicleNote` 写死"博创尚和 2342 电机"（`:1007-1010`）。
15. **【中】结构性：22 个仿真参数 + 最多 14 路传感器（每路 5 个控件）全量平铺，无搜索、无折叠、无筛选**——仿真页两组展开（`:483-528`），传感器 6 列网格一次渲染全部通道（`:930-947`）；没有搜索框/折叠组/"仅显示已修改"过滤。6 个标签页也无页内导航。
16. **【低】全屏模式下宽高输入仍可编辑、无联动禁用**——`_windowMode` 与 `_width/_height` 同页且互不约束（`:265-270`），全屏时宽高被忽略但输入框仍可用。
17. **【低】视觉来源为默认 classifyRate 时，路径框"可见但禁用"，用户易误以为坏掉**——`UpdateVisionInputs` 只置 `Editable=false` 不隐藏（`:1180-1200`）；注释也承认"保持可见但禁用"。

## C. 持久化与生效链路

### 配置文件
- **主设置文件**：`user://wushu-ring-settings.json`（Windows：`%APPDATA%\Godot\app_userdata\WushuRingSim\`）。路径构造 `Main.cs:1048`，常量 `SettingsStore.DefaultFileName`（`DesktopSettings.cs:510`）。格式：`ProtocolJson`（camelCase、null 省略、紧凑 JSON，`ProtocolJson.cs:26-66`）。读写：`SettingsStore.Load/Save`（`DesktopSettings.cs:525-583`），原子写（`.tmp` + `File.Move`）；加载时先 `Validate()`，失败/损坏回退 `DesktopSettings.Default` 并打诊断（`:540-552`）。
- **外观模型文件**：`res://robot-models.json`（即 `godot/robot-models.json`）或 `--robot-models <file>`（`Main.cs:1437,1463-1488`）。已 gitignore（`.gitignore:57`）。格式同上，`Dictionary<string, RobotModelConfig>`。
- **布局文件**：布局编辑器走 `arena-layout-v1` Scenario JSON，`LayoutDraft.SaveTo/ReadScenario`（`LayoutDraft.cs:363-393`），由 `FileDialog` 选路径。
- **默认值来源**：`DesktopSettings.Default`（`DesktopSettings.cs:152-175`）+ `SimulationParameterCatalog` 各定义默认（`:627-650`）+ `RobotModelConfig` 默认（`RobotModelLoader.cs:13-28`）。加载失败一律回退内置默认。

### profile 机制（传感器）
`Vehicle.SensorProfileId` + `SensorDisabled[]` + `SensorOffsets{}` 序列化在设置 JSON 内（`DesktopSettings.cs:93-99`）。应用时 `ResolveSensorProfile` 以选中预设（或场景自带 profile）为基底，调 `SensorProfileCustomizer.Apply`（`src/Sim.Protocol/SensorProfileCustomizer.cs:22-52`）克隆出 `custom:<baseId>`，再写进双方 `VehicleProfile.Sensors`（`DesktopSettings.cs:437-458`）。**无独立 profile 存档/命名机制**，只有"预设 + 当前覆盖"。

### UI → 配置 → 运行时
1. `F10`/按钮 → `Main.OpenSettings`（`Main.cs:1187-1194`）→ `SettingsPanel.Open` 载入当前设置（`:128-144`）。
2. 点"应用设置" → `ApplyDraft`：组装 draft → `draft.Validate()`，有错则只显示错误、不关闭（`SettingsPanel.cs:741-746`）；成功则 `_settings=draft`、`Visible=false`、触发 `Applied` + `RobotModelsApplied`（`:747-750`）。
3. `Main.ApplyDesktopSettings(settings)`（`Main.cs:1056-1092`）：保存文件 → `ApplyDisplaySettings`（立即，`:1171-1185`）→ `RebuildVisionFactory`（`:1098-1116`，失败高声告警并回退默认源）→ `RebuildControllerWiring`（`:1124-1137`）→ 若 `matchChanged` 且实况且非编辑中，`ResetLiveSession` 自动重建会话（`:1078-1081`），否则 `_pendingMatchSettings=true` 留待下一场/F5（`:1082-1086`）。
4. 场景叠加顺序（`Main.cs:1209-1213`）：`ApplySimulationParameters` → `ApplyBlocks` → `ApplyVehicleOverrides` → `ApplyControllerSelection`。仿真参数进 `scenario.Parameters`，最终由 `SimParameters.FromDictionary` 消费（`src/Sim.Core/SimParameters.cs:42-80`，有测试钉死 `DesktopSettingsTests.cs:75-90`）。
5. 视觉源：`DesktopSettings.CreateVisionFactory`（`DesktopSettings.cs:469-500`）在装配时预检（visionReplay 读包/哈希、liveBridge 读 CSV、liveProcess 试启动进程），每场新建适配器。
6. 外观模型：`RobotModelLoader.Apply` 立即重挂（`Main.cs:1409-1428`），与仿真完全隔离。

### 已知断点
- **`Vehicle`/传感器覆盖只在 `mujoco` + `ModelVersion.V2` 场景生效**（`DesktopSettings.cs:369-372`）；legacy/v1 场景下质量/转速/轮径/传感器覆盖静默无效（车辆页有文字提示 `:783`）。
- **`MotorTorque` 不进入仿真**，仅存档（`DesktopSettings.cs:79-80`、`SettingsPanel.cs:1007-1010` 已披露）。
- **仅改小车参数不进入 `MatchSettingsEqual`**，不触发自动重置且日志误导（B10，`Main.cs:1340-1345`）。
- **能量块设置覆盖布局编辑器结果**（B13）。
- **`Rpm/Mass` 只在 mujoco v2 的 `ApplyVehicleOverrides` 写入 `Mass`/`MaxSpeed`**；轮径只通过 `MaxSpeed` 派生间接生效，v1/legacy 不看。
- **可视化路径按进程 CWD 解析**，未走 `Main.ResolveUserPath`（对比 `Main.cs:1236-1250`）；证据包路径在 `VisionEvidencePackage.Load` 直接 `Directory.Exists`（`src/Sim.VisionReplay/VisionEvidencePackage.cs:54-59`），相对路径相对于 `godot/`，与 `--scenario-path` 的解析规则不一致。
- **面板"预检"按钮的结果不喂给 Apply 的自动预检**：Apply 时 `Main.RebuildControllerWiring(..., probeExternal:true)` 会再跑一次（`Main.cs:1071,1131-1134`），面板结果仅用于 HUD 提示（`Main.cs:131-132`）。

## D. 需实机目检才能确认的点

1. 模态在 1280×720 下各标签页/页脚是否真的不被裁切——项目自带 `--settings-smoke --capture <png>`（`Main.cs:234-250`, `godot/README.md:236`）可生成证据。
2. 默认字体下中文长说明是否截断/换行是否难看（`AutowrapMode.WordSmart` + `ClipText=true` 混用，如 `SettingsPanel.cs:226,498,937`）。
3. 传感器 6 列网格在窄窗口下的列宽/水平溢出（GridContainer 固定 `CustomMinimumSize` 列，无水平滚动 `SettingsPanel.cs:837-840`）。
4. 预检结果/控制器状态在真实外部进程下的时序观感。
5. 切换 uiScale 后对话框缩放是否与 HUD 视觉一致、是否有模糊/裁边（`SettingsPanel.cs:121-126,1362-1365`）。
6. "恢复默认"后的实际视觉状态（尤其外观模型区，见 B7）。
7. 能量块设置页与布局编辑器同时使用时的最终结果（B13 需实机确认用户路径）。
8. 各 SpinBox 的 `Suffix` 与相邻单位 Label 在真实字体下的对齐/间距（B2）。
