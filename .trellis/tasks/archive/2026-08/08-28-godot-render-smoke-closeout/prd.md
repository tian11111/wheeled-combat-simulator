# 补齐 Godot 窗口渲染冒烟门禁

## Goal

完成 renderer-backed 的 Godot 编辑器截图冒烟，确认 22 项编辑断言与视觉输出均通过。

## Requirements

- R1：确认 Godot 4.7.2 Mono 项目可加载、构建并启动真实窗口渲染。
- R2：执行布局编辑的选择、拖动、吸附、旋转、撤销/重做、恢复官方、应用流程，保留 22 项逻辑断言结果。
- R3：在真实 renderer 下生成至少一张编辑后场景截图，并检查截图非空、分辨率正确且包含场地/机器人/HUD 可见像素。
- R4：确认截图 QA 不改变 `Sim.Core` 结果；CLI replay-check 与 Godot parity 继续通过。
- R5：若环境仍阻断窗口启动，必须把阻断原因、已通过的 headless/logic 证据和待执行命令写入报告，不能把逻辑通过冒充视觉通过。

## Acceptance Criteria

- [x] 真实窗口版编辑冒烟退出码为 0，22 项断言通过。
- [x] 产生可复核的非空截图和运行日志。
- [x] seed-42 parity、布局回放和现有 .NET 测试不回退（最终总门禁再次复核）。

## Out Of Scope

- 不重做场地编辑器交互，不制作最终美术资产。
- 不将 Godot/Jolt 物理变成规则或判分来源。
