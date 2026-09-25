# 实施清单：玻璃赛事控制台

1. 以当前 `HudPanel` 布局和既有未提交视觉改动为基线，保留动态文本和回调顺序。
2. 提取玻璃颜色/边框/圆角/高光/阴影 token，修改卡片和按钮的 normal/hover/pressed/disabled 状态。
3. 新增设置模态层所需的可复用卡片/标题/错误提示样式，不把设置业务逻辑塞进 HUD 数据更新。
4. 增加 UI scale 的统一入口，检查设计视口拉伸、全屏和窗口化下的锚点。
5. 生成实况/回放/编辑/设置双分辨率真实 renderer 截图并检查遮挡和文字。
6. 跑 `dotnet test`、Godot parity、camera/edit smoke、`git diff --check`，视觉失败只回滚 UI 层。

## Files / Gates

- 重点文件：`godot/src/HudPanel.cs`、`godot/src/SettingsPanel.cs`、必要时 `godot/scenes/Main.tscn`。
- 不得修改 `RenderFrame`、`Scenario`、回放、相机交互或核心规则。
