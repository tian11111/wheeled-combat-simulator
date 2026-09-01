# 实施计划：玻璃控制台、运行设置与自定义小车控制器

## Ordered Checklist

1. 记录当前工作区基线，确认只在新任务文件中增加规划产物；不得覆盖中央圆环移除、README/spec 或其他既有 dirty path。
2. 在不改产品代码行为的前提下，建立 `DesktopSettings`、参数元数据和 `SettingsValidator` 的纯 C# 模型；为全量已登记 `SimParameters` 键补齐默认值、单位、范围、自动值语义和错误信息测试。
3. 建立 `SettingsStore` 的版本化 `user://` JSON 读写与损坏/旧版本回退测试；实现设置 draft 与应用结果分流，显示设置即时应用，仿真/控制器设置只进入 pending。
4. 在 `HudPanel` 样式工厂上改造玻璃 token，并新增 `SettingsPanel`；接入打开/关闭、取消、恢复默认、应用、字段校验和统一 UI scale，保持现有 HUD 更新、快捷键、回放条和编辑栏回调顺序。
5. 增加分辨率、窗口模式和 UI scale 的 Godot 壳应用；在 1280×720、1920×1080、全屏和窗口化下验证 CanvasItem stretch、锚点、文字和模态输入所有权。
6. 抽取 `PythonBridge` 到 `Sim.Controller` 共享类库（或等价的最小共享边界），让 CLI 继续使用同一协议和生命周期；先通过既有 CLI controller/batch/replay 测试再接桌面。
7. 实现 `DesktopLiveDriver` 的有界 snapshot/command 队列、单场 bridge 生命周期、发令/暂停/重启/重置/关闭回收和角色 fault 状态；Main 只在配置 external controller 的 live session 启用它。
8. 将设置中的 controller profile 接到下一场 session：发令前显示内置/外部来源和预检结果；启动失败、坏行、request-id 错配、超时、退出均安全回退并显示角色诊断。
9. 更新 `godot/README.md`、`docs/CLI.md`、`docs/CONTROLLER_PROTOCOL.md` 或新增桌面设置说明，明确配置文件位置、参数分组、下场生效规则、外部命令风险和启动示例。
10. 生成真实 renderer 的玻璃 HUD、设置界面、全屏/双分辨率、实况/回放/编辑模式证据；移除临时截图和日志，不把 `.import` 产物入库。
11. 运行完整质量门禁；确认任务 PRD 的 acceptance 全部有证据后，再由用户明确批准进入 `task.py start`/实现阶段。

## Validation Commands

```powershell
dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 -p:UseSharedCompilation=false
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json
godot --headless --path godot -- --camera-smoke
godot --headless --path godot -- --edit-smoke
godot --path godot --rendering-method gl_compatibility -- --capture <glass-720.png>
godot --path godot --rendering-method gl_compatibility -- --capture <glass-1080.png>
git diff --check
```

Settings/driver unit tests must also cover: round-trip, corrupted JSON fallback, unknown key rejection, range errors, pending-vs-immediate application, no controller process in replay, independent process cleanup, and UI status transition. 真实画面验收不能用 headless dummy screenshot 代替。

## Risky Files / Rollback Points

- `godot/src/HudPanel.cs`, `godot/src/SettingsPanel.cs`: 视觉和输入层，可独立回滚。
- `godot/src/Main.cs`, `godot/src/MatchSession.cs`: session/命令集成，改动前后必须做 parity、camera/editor smoke。
- `src/Sim.Controller/*`, `src/Sim.Cli/PythonBridge.cs`: 共享 bridge 抽取点；CLI controller tests 是先行门禁。
- `godot/project.godot`: 只允许必要的 input/window 默认配置，不改变设计视口和渲染契约。

任何核心结果、回放指纹或旧 CLI 行为变化都停止 UI 继续扩展，先回退 session/bridge 集成到内置 FSM 路径。
