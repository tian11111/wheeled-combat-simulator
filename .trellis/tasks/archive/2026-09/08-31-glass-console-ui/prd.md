# 玻璃赛事控制台视觉重构

## Goal

把现有 Godot 深色赛事 HUD 改为半透明、层次清晰、可读性稳定的玻璃控制台，覆盖实况、回放和布局编辑模式，不改变状态来源、快捷键或仿真边界。

## Confirmed Baseline

- `godot/src/HudPanel.cs` 以程序化 `StyleBoxFlat` 创建状态卡、事件卡、帮助卡、回放条和编辑栏；当前 `CardColor`/`CardColorRaised` 为高不透明度深色。
- `godot/project.godot` 已将 Canvas UI 设计基准固定为 1280×720，并通过 `canvas_items`、`expand`、`fractional` 处理窗口拉伸；新视觉不得另建一套缩放坐标系。
- HUD 数据继续来自 `RenderFrame`、`SessionMode` 和编辑器状态；`Main`/`MatchSession`/`Sim.Core` 不应被视觉重构改变。

## Requirements

- R1：统一玻璃卡片材质语言（半透明填充、圆角、细边框、轻微层次）并保留蓝/红/黄/绿状态语义和文字冗余。
- R2：实况、回放、编辑栏显示/隐藏状态保持现有行为；动态文本不重叠、不挤压关键值，玻璃层不吞掉 3D 场地和可操作区域。
- R3：全屏、窗口化、1280×720 与 1920×1080 下按同一设计视口缩放；兼容渲染器没有真正背景模糊时仍有稳定的半透明回退。
- R4：不引入第三方资产/依赖，不改变相机、编辑器指针所有权、快捷键、回调顺序、回放和核心数据。

## Acceptance Criteria

- [ ] 三模式真实 renderer 截图均能识别玻璃卡片层级，背景不再是大块纯黑，文本和按钮清晰。
- [ ] 双分辨率及全屏窗口没有关键文本截断、卡片重叠或 3D 关键对象被遮挡。
- [ ] 原有 `UpdateFrame`、`UpdateEditor`、回放时间轴和布局编辑 smoke/parity 全部通过。

## Out Of Scope

- 不重做场地/机器人美术，不改变 Sim.Core，不增加第三方主题包或远程资源。
