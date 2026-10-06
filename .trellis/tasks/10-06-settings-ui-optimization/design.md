# 技术设计：设置界面优化（四批）

> 事实依据见 `research/settings-ui-audit.md`（UI 审计）与 `research/config-surface-gap.md`（配置面缺口）。锚点行号以 2026-10-06 代码为准，实施时按符号重查。

## 总体兼容性红线

1. `DesktopSettings` 只**新增可选字段**，camelCase + null 省略下旧配置文件反序列化后新字段=null → 走默认值 → 行为逐位不变；`schemaVersion` 保持 1 不升（新增字段向后兼容）。
2. 不改 `ProtocolJson` 序列化风格、回放格式、协议 schema。
3. L1/L2/L3、物理后端覆盖的**默认值等于现行为**；只有用户显式改选项才改变对局结果。
4. UI 全部走 `SettingsPanel.Build()` 程序化构建既有模式，不引入 .tscn。

## 批1：复现基座

### 1a. 配置包 bundle 格式（新文件 `godot/src/SettingsBundle.cs`）

```jsonc
{
  "bundleSchemaVersion": 1,          // 独立于 settings 的 schemaVersion
  "exportedAt": "2026-10-06T…",
  "settings": { …DesktopSettings 全量，即 wushu-ring-settings.json 内容… },
  "robotModels": { …robot-models.json 全量，可空… },
  "scenarioFile": { "fileName": "…json", "content": "…场景 JSON 原文…", "isLayout": true|false },  // 可空
  "trainConfig": { …训练配置 JSON 对象，可空… }
}
```

- 序列化用 `ProtocolJson` 现有 options（camelCase、null 省略），与设置文件一致（`DesktopSettings.cs:525-583` 的 `SettingsStore` 模式：原子写 `.tmp` + `File.Move`）。
- 导出来源：`SettingsPanel._settings`、`_robotModels`、当前场景（Main 暴露"当前场景文件路径 + 是否布局产物"，从 `--scenario-path` / 布局编辑器 `LayoutDraft.SaveTo` 的落点取；场景内容读文件原文内嵌，**不嵌路径引用**）。trainConfig 不在桌面侧持有，导出时弹文件对话框让用户选已有 `train-config.json`（可跳过）。

### 1b. 导出/导入接线

- `SettingsPanel` 页脚（`Build` 里页脚区，`:205` 附近）加两个 Button：`导出配置包…`、`导入配置包…`；`FileDialog` 参照 `LayoutEditor.cs:101-118` 的用法（`Access`/`Filters`）。
- `Main` 新增 `ExportSettingsBundle(path)` / `ImportSettingsBundle(path)`：
  - 导入流程：读文件 → 反序列化 → `bundleSchemaVersion != 1` → 中文错误"配置包版本不兼容（vX，本程序支持 v1），已拒绝导入"→ 通过则弹确认（列出将覆盖：主设置 / 外观模型 / 场景文件 / 训练配置）→ 应用：
    - 主设置：走现有 `ApplyDesktopSettings` 链路（保存 + 各页生效逻辑）；
    - 外观模型：写 `res://robot-models.json`（复用 `SaveRobotModels`，`Main.cs:1435-1460`，该文件已 gitignore）+ 重挂；
    - 场景文件：写 `user://imported/<fileName>`，作为当前场景重载（复用批2 的运行时场景重载入口）；
    - 训练配置：写 `user://imported/train-config.json`，完成弹窗列出各文件落点。
  - 导入/导出的 IO 一律经 `Main.ResolveUserPath` 语义，不走进程 CWD（对比审计"已知断点"里的路径解析不一致）。

### 1c. 训练配置文件（`controllers/score_block_rl/train.py`）

- 新 schema（snake_case，Python 侧惯例）：`steps / out / train_seed / n_envs / dotnet / cli_dll / scenario / reward / checkpoint_interval`。
- `argparse` 增加 `--config <json>`：先读配置文件填默认，**显式 CLI 传参覆盖配置字段**（对每个字段：CLI 显式给出才覆盖）；无 `--config` 时行为与现在逐位一致。
- 校验：`--config` 文件含未知键 → 报错拒绝（防拼写错误静默失效）；reward 字段值域校验同现有 `--reward`。

## 批2：比赛/场景页 + 高级折叠区

### 2a. DesktopSettings 新增字段（`DesktopSettings.cs`）

```csharp
// 嵌套 record，风格同 window/vision
public record MatchOverrides {
    public string? PhysicsBackendOverride;  // null/"follow" | "legacy" | "mujoco-v1" | "mujoco-v2"
    public string ScenarioPath = "";        // 空=跟随启动场景
    public double? MatchDuration;           // null=跟随场景
    public int? Seed;                       // null=跟随启动 seed
}
public record DevContact {                // 高级折叠区
    public bool L1VehicleVehicleObb = true;   // Physics.cs:22
    public bool L2VehicleBlockObb  = true;    // Physics.cs:29
    public bool L3BlockWallBlock   = true;    // Physics.cs:36
}
```

- `Validate()` 增补：backendOverride 值域、MatchDuration>0、Seed∈[0,4096]（与 `BatchCommand` 种子上限一致）。

### 2b. 生效链路（`Main.cs`）

- 装配顺序（现 `Main.cs:1198-1213`：场景模板 → ApplySimulationParameters → ApplyBlocks → ApplyVehicleOverrides → ApplyControllerSelection）中插入 **ApplyMatchOverrides（最先）**：对场景对象写 `physics.backend`/`physics.modelVersion`/`field.matchDuration`/`seed`，非 follow/空/null 时才覆盖。物理后端由场景字段驱动装配，改字段即换后端。
- 风险：`ResetLiveSession`（`:1078-1081`）重建会话时是否完整走装配链（即后端热切是否自然发生）。**实施第一步验证**；若后端选择发生在 Reset 之外的启动期，则后端覆盖降级为"下一场/F5 生效"并在 UI 标注（PRD 验收兼容此降级）。
- 换种子重开：`Main.Seed`（`[Export]`，`Main.cs:26`）改为运行时可写字段（保留 `[Export]`），比赛/场景页"换种子重开"按钮 = 写 seed → `ResetLiveSession`。
- L1/L2/L3：`DevContact` 在 `MatchEngine` 构造点（`ContactResolveOptions` 注入处）传入；实施时从 `Physics.cs:15-36` 的消费链反查构造锚点。
- 运行时场景重载：批1 导入与批2 场景选择共用"加载场景文件 → 全套 Apply 链 → ResetLiveSession"入口（参考 `ApplyLayoutScenario` `Main.cs:1868-1876` 已有先例）。

### 2c. UI（`SettingsPanel.cs`）

- 新标签页"比赛/场景"（`:210-221` 标签注册处）：后端 OptionButton（跟随场景/legacy/mujoco v1/mujoco v2，说明 tooltip 注明"v2 才支持小车页参数"）、场景文件 MakePathInput+浏览、时长/种子 SpinBox、"换种子重开"Button。
- 高级折叠区：仿真参数页底部"高级/开发者 ▾"Button + VBox 显隐切换（含 L1/L2/L3 三个 CheckButton + "改动影响碰撞判定，回放身份会失配"警示 note）。批4 打磨时若做全页重排，此折叠区随之迁移但语义不变。

## 批3：交互统一

| 修复点 | 设计 |
|---|---|
| R3.1 Vehicle 计入变更 | `MatchSettingsEqual`（`Main.cs:1340-1345`）补 Vehicle（含 SensorDisabled/Offsets）深度比较（参照文件内既有 `ValuesEqual` 类帮助函数；record 含 List/Dict 不能裸 `==`）；修 `Main.cs:1088-1091` 日志分支：matchChanged 打印"比赛相关设置已应用并重置"，否则"显示设置已应用"。 |
| R3.2 预设切换保留编辑 | `RebuildSensorChannelRows`（`:897-948`）回填源从 `_settings.Vehicle` 改为"先从当前控件收集 offsets/disabled → 换预设基底 → 同名通道保留编辑值，不同名通道取基底默认"→ 重建。 |
| R3.3 能量块冲突警告 | `Main` 暴露"当前场景是否带 layoutVersion"（Open 设置时传入 SettingsPanel）；能量块页自定义开关 `Toggled` 且场景带 layoutVersion → `ConfirmationDialog`："自定义能量块布局将覆盖布局编辑器的摆位"，取消则回弹开关。 |
| R3.4 错误文案中文化 | 显示层映射：`SettingsPanel` 加 `LocalizeValidationError(string)`（前缀/正则映射表覆盖 `DesktopSettings.Validate()` 已知消息模板，`:181-307`），未识别消息原文兜底；`Validate()` 本身不动（保持结构化、可测试）。 |
| R3.5 生效时机标注 | 每页底部 note 行：显示页="立即生效"；仿真/控制器/视觉/能量块/比赛场景页="应用后自动重开当前对局（非实况则下一场）"；小车页（R3.1 修后同左）。文案走常量。 |
| R3.6 恢复默认含外观 | `RestoreDefaults`（`:1230-1235`）同时把外观模型区控件重置为默认（路径空/scale 1/偏移 0）并标记 draft；实际落盘仍走"应用设置"。 |

## 批4：打磨

- **tooltip 中文说明**：`SimulationParameterCatalog`（`DesktopSettings.cs:627-650`）的 Definition 增加 `Description`（中文，含影响方向与单位），`MakeSpin` 把 `label.TooltipText` 从 `definition.Key`（`SettingsPanel.cs:498`）换成 Description；各页控件逐个补 TooltipText（B4 清单）。
- **单位重复**：删 `:504` 的 `AddLabel(row, definition.Unit)`，保留 SpinBox `Suffix`。
- **文件选择器**：`MakePathInput`（`:1292-1303`）加"浏览…"按钮 + 面板级共享 `FileDialog`（目录/文件两种模式参数化），替换视觉三路径与外观模型路径的手打输入。
- **自适应尺寸**：`_dialog`（`:169-176`）尺寸改为 `min(980, viewport*0.9/scale) × min(620, …)`，包一层外层 `ScrollContainer` 作溢出保护；uiScale 缩放逻辑（`SetUiScale :1362-1365`）不变。实机目检走 `--settings-smoke --capture` 对照审计 §D。
- **MakeOption id 化**：`MakeOption`（`:1305-1318`）把 `(label,id)` 存进控件 Meta（`index→id` 映射），新增 `GetSelectedId(OptionButton)`；读取端三处 switch（`:886-891,1122-1128,753-764`）改按 id 匹配，UI 顺序不再承担语义。
- **低优先级**：全屏模式联动禁用宽高输入（`:265-270`）；视觉源不相关路径框保留可见但加"（当前来源不使用）"灰字（比隐藏稳，避免布局跳动）；硬编码文案/示例路径集中到 `SettingsPanel` 顶部常量区（`:324,334-335,862,1033-1034,1106`）。

## 测试与验证策略

- 单测（`src/Sim.Tests`，跟随现有 `DesktopSettingsTests.cs` 风格）：bundle 序列化往返、导入版本校验拒绝、新设置字段默认值钉死（回归红线）、MatchSettingsEqual 含 Vehicle、训练配置 CLI 覆盖优先级（python 侧用 selftest 或轻量 pytest，若仓库无 python 测试设施则用 `--config`+dry-run 断言）。
- 桌面目检：真实 Godot exe（勿用 PATH 的 WinGet 符号链接），自定义参数放 `--` 分隔符之后：`--settings-smoke --settings-tab <页> --capture <png>`。
- 每批收尾跑 `trellis-check` 子代理 + 全量 `dotnet test`。

## 风险表

| 风险 | 等级 | 缓解 |
|---|---|---|
| 物理后端运行时热切不支持（装配在 Reset 路径之外） | 中 | 批2 首个 spike 验证；降级为"下一场/F5 生效"+UI 标注 |
| 传感器跨预设通道名不同，编辑保留语义模糊 | 低 | 明确规则：同名保留、异名取基底默认（PRD 已写） |
| 配置包导入写 `res://robot-models.json` 属主工程目录写文件 | 低 | 该文件已 gitignore 且 SaveRobotModels 已有先例 |
| MakeOption id 化触面广（4 处读取端） | 中 | 逐处替换+现有测试全绿为准；替换前先加 id 映射单测 |
| train.py 配置覆盖顺序写错导致静默失效 | 低 | 未知键报错 + 显式覆盖优先级单测/dry-run 断言 |
