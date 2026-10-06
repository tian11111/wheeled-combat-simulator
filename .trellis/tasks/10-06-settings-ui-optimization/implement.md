# 执行计划：设置界面优化（四批）

> 顺序：批1 复现 → 批2 补参数 → 批3 交互 → 批4 打磨。每批独立提交（回滚点=批边界）。
> 通用验证命令见 §0；每批收尾走 `trellis-check` → 提交（Trellis 3.4 批量确认流程）。

## 0. 通用验证命令

```bash
# dotnet SDK 固定路径（勿用旧 /tmp 路径）：
C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe test
# 桌面目检：真实 Godot exe（PATH 上的 godot 是 WinGet 符号链接，会崩）；
# 自定义参数必须放在 -- 分隔符之后，否则被静默忽略：
<real-godot-exe> --path godot -- --settings-smoke --settings-tab <tab> --capture <png>
```

## 批1：复现基座

- [x] 1.1 `train.py` 加 `--config`（design §1c）：snake_case schema、显式 CLI 覆盖、未知键报错；无 `--config` 时逐位等价现状
- [x] 1.2 新建 `godot/src/SettingsBundle.cs`（bundle schema + ProtocolJson 序列化/反序列化 + 原子写）
- [x] 1.3 `Main` 增加 `ExportSettingsBundle/ImportSettingsBundle`（版本校验拒绝 + 确认弹窗 + 四类内容落点，design §1b）；IO 走 `ResolveUserPath` 语义
- [x] 1.4 `SettingsPanel` 页脚两按钮 + 共享 `FileDialog`（参照 `LayoutEditor.cs:101-118`）；导出时可选挂载 train-config 文件
- [x] 1.5 单测：bundle 往返、版本不匹配拒绝、无 `--config` 等价性断言
- [x] 1.6 验证：`dotnet test` 全绿；实机导出→删改→导入还原 手工用例；`--settings-smoke --capture` 截图确认按钮不溢出页脚
- [x] 1.7 提交（回滚点：批1）

## 批2：比赛/场景页 + 高级折叠区

- [x] 2.1 **Spike 结论（2026-10-06，实施代理）：支持热切 —— backend 每次会话构造时按 `scenario.physics` 重新装配，不存在启动期一次性选定。**
  - 证据链（行号为实施当日代码）：`Main.ResetLiveSession`（Main.cs:1273-1282）→ `BuildLiveScenarioFromTemplate`（:1255-1259，`ApplyDesktopSettings` 链重建场景值）→ `ReplaceSession`（:1266-1271，`new MatchSession(scenario, _visionFactory)`）；`MatchSession` ctor（MatchSession.cs:61-66）与 `ResetToLive`（:174-188）都调 `MatchEngineHost.Create`。
  - `MatchEngineHost.Create`（src/Sim.Hosting/MatchEngineHost.cs:35-43）**每次调用**按 `scenario.Physics?.Backend` 二选一分派（mujoco → 注入 `MujocoPhysicsBackendFactory.Instance`，否则 legacy）；`MatchEngine` ctor（src/Sim.Core/MatchEngine.cs:146-165）据此构造 `_physics`。
  - `MujocoPhysicsBackendFactory.Create`（src/Sim.Mujoco/MujocoPhysicsBackendFactory.cs:22-34）无缓存/无进程级模型池，每次 `new MujocoPhysicsBackend`；`ReplaceSession` 先建新会话再 `previous.Dispose()`（旧 backend 原生句柄随引擎释放）。
  - 结论：走"覆盖字段 → 应用后自动重开当前对局生效"（不做"下一场/F5 生效"降级）；UI tooltip 按热切语义写。唯一残留启动期方是"场景文件 JSON 解析"（`--scenario-path`/覆盖路径），与 backend 选择无关。
- [x] 2.2 `DesktopSettings` 加 `MatchOverrides` + `DevContact`（design §2a）+ `Validate()` 增补 + 默认值回归测试（老配置无新字段行为不变）
- [x] 2.3 `Main` 装配链插入 `ApplyMatchOverrides`（最先）；`Seed` 运行时可写 + "换种子重开"；`DevContact` 注入 `MatchEngine` 构造点
- [x] 2.4 运行时场景重载入口（导入/场景选择共用；参考 `ApplyLayoutScenario` `Main.cs:1868-1876`）
- [x] 2.5 `SettingsPanel` 新"比赛/场景"页（design §2c）+ 仿真参数页"高级/开发者"折叠区（L1/L2/L3）
- [x] 2.6 单测：backendOverride 覆盖语义、seed 重开一致性（同 seed 同布局轨迹一致）、默认值钉死
- [x] 2.7 验证：`dotnet test`；实机切 legacy↔mujoco v2 观察下一场生效与提示；`--settings-smoke --settings-tab match --capture`（tab 名以实现为准）
- [x] 2.8 提交（回滚点：批2）

## 批3：交互统一

- [x] 3.1 `MatchSettingsEqual` 补 Vehicle 深比较 + 日志分支修复（design 批3 表 R3.1）
- [x] 3.2 `RebuildSensorChannelRows` 改"当前控件回填"（同名保留/异名取基底）+ 单测
- [x] 3.3 能量块自定义开启检测 layoutVersion → 确认弹窗（Main 传入场景状态）
- [x] 3.4 `LocalizeValidationError` 中文映射表 + 覆盖已知消息模板的单测
- [x] 3.5 每页页脚生效时机 note（文案常量）；`RestoreDefaults` 含外观区
- [x] 3.6 验证：`dotnet test`；手工用例：仅改小车参数→自动重置且日志正确；切预设编辑不丢；开启自定义块→弹窗→取消→布局编辑器结果保持
- [x] 3.7 提交（回滚点：批3）

## 批4：打磨

- [ ] 4.1 `SimulationParameterCatalog` 加中文 `Description`；tooltip 全面替换（B3/B4 清单）；删重复单位 `SettingsPanel.cs:504`
- [ ] 4.2 `MakePathInput` + "浏览…"（目录/文件两种模式），替换视觉三路径与外观路径
- [ ] 4.3 `_dialog` 自适应尺寸 + 外层 ScrollContainer；`--settings-smoke --capture` 全 6 页 + uiScale 1.4 目检（对照 `research/settings-ui-audit.md` §D 清单逐项销项）
- [ ] 4.4 `MakeOption` id 化（Meta 映射 + 4 处读取端替换 + 单测先行）
- [ ] 4.5 低优先级项：全屏禁宽高、视觉路径框灰字说明、硬编码文案集中常量区
- [ ] 4.6 验证：`dotnet test`；目检销项表全过；trellis-check 全量终检（含 `get_context.py --mode packages` 各包 Quality Check）
- [ ] 4.7 提交（回滚点：批4）

## 收尾

- [ ] `trellis-update-spec`：若沉淀出新约定（如"程序化构建控件必须带 id/说明"、"设置新增字段必须钉默认值测试"）写回 `.trellis/spec/frontend/`
- [ ] Phase 3.4 批量提交计划 → 用户确认 → `/trellis:finish-work`

## 回滚策略

- 批边界即回滚点：`git revert` 对应批提交即可，批间无共享中间态。
- 批2 spike 失败的降级路径已在 design §2b 写死，不阻塞批3/批4。
- 配置包 bundle schema 若中途变更：`bundleSchemaVersion` 升位并同步拒绝逻辑（导入只认当前版本）。
