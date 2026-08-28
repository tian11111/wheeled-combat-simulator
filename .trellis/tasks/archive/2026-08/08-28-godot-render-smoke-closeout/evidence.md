# Godot 渲染冒烟收尾证据

执行器：Godot `4.7.2.stable.mono.official.ed1daf0bf`。

## 结果

- headless dummy renderer：22 项编辑逻辑断言和 applied-layout parity 全部通过，但 `Texture2D.GetImage()` 返回 null，未生成截图；该结果不作为视觉通过。
- 真实窗口 + OpenGL Compatibility renderer：退出码 `0`，22 项编辑断言全部通过，applied-layout parity 通过。
- 截图：[windowed-edit-smoke.png](./windowed-edit-smoke.png)，分辨率 `1152x648`，大小 `58819` bytes；像素统计 `us=2308 them=272 buff=2075 debuff=28 platform=13005 floor=709664 model=0`。画面包含场地、两台机器人和 HUD。
- 运行时日志摘要见 [windowed-run.log](./windowed-run.log)。

## 兼容性

窗口冒烟只验证渲染与编辑壳；规则权威仍在 `Sim.Core`。CLI seed-42 replay-check 和 Godot parity 仍需在最终总门禁中再次运行，且不允许通过修改截图门槛来掩盖 dummy renderer 能力差异。
