# 设计：玻璃赛事控制台

## Boundary

范围集中在 `godot/src/HudPanel.cs`、新增 `SettingsPanel` 的共用样式入口和必要的场景/UI 配置。状态仍由 `RenderFrame`、`SessionMode`、编辑器状态和 Main 回调提供；不改 `Sim.Core`、回放、布局编辑和相机输入契约。

## Visual System

- 以统一 token 生成半透明冷色玻璃填充、细边框、圆角、顶部高光和轻阴影；不使用第三方主题或必须存在的背景模糊。
- 状态卡、事件卡、帮助卡、回放条和编辑栏沿用现有层级/锚点，仅改变样式密度和可读性。
- 设置模态层复用同一 token，打开时拦截 UI 输入；关闭后恢复之前模式，不吞掉编辑器/相机状态。
- UI scale 只通过一个设计根或统一样式入口应用，禁止每个控件手写不同缩放。

## Acceptance

真实 renderer 下实况、回放、编辑、设置各有截图；1280×720、1920×1080 和全屏无重叠/截断；既有快捷键、回放条、编辑器、parity、camera/edit smoke 全绿。
