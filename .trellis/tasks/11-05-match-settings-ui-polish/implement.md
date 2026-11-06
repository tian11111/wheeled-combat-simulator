# 执行计划

- [x] 确认用户选择：比赛主界面与设置页、现有深色竞技风、现有分辨率范围。
- [x] 核对现有 HUD/设置源码、项目规范与历史截图；记录 Godot 初始化探测问题。
- [x] 恢复或明确定位 Godot 运行和截图环境；保护真实用户的设置与历史证据。
- [x] 分文件实现 HUD 安全锚点、信息层级和长提示可读性；检查比赛、回放和编辑三种模式。
- [x] 分文件实现设置页密度、组件视觉及安全可用的焦点反馈；不改变页面/业务行为。
- [x] 构建 Godot .NET 工程；运行相关最小自动验证及适当的 CLI/Godot 回放 parity。
- [x] 用真实渲染观察 1280×720、1920×1080、1280×720 @ 1.4 和 640×360 @ 1.4，检查设置滚动与 HUD/事件/回放/编辑不重叠；保留临时证据路径及可复现命令。
- [x] 审阅完整差异与既有功能影响，更新必要规范；明确未覆盖的环境和交互。

## 默认验证入口

- 使用本机可用 .NET 8 SDK：`$env:LOCALAPPDATA\Programs\robot-simulator-dotnet\dotnet.exe`；避免默认并行 MSBuild。
- `dotnet build godot/GodotSim.csproj --no-restore -m:1`
- `dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 --no-restore --filter "FullyQualifiedName~SettingsInteractionTests|FullyQualifiedName~EditorGateTests"`
- Godot `--headless --path godot -- --camera-smoke`、`--edit-smoke`、`--parity-check ../replays/godot-parity-seed42.json`。
- 用 Godot 原有 `--capture` 与 `--settings-smoke` 采集真实渲染；截图不提交，不能把无头截图或旧截图作为真实视觉验收。

## 回滚/风险

独立文件分工，保留原有数据契约与用户设置；如焦点改变会穿透到比赛输入，则缩小焦点改动。Git 提交、推送、归档或破坏性清理需用户另行确认。
