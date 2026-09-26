# Implementation Plan — Godot 3D 视觉三阶

## Phase 0 — 基线
1. 当前 HEAD 生成双分辨率基线 capture（默认/俯视/运行中）+ 帧时间记录
   （临时目录）。
2. 记录现有 Environment/灯光配置快照（回滚锚点）。

## Phase 1 — SDFGI（已完成）
1. `Main.tscn` WorldEnvironment 启用 SDFGI（默认参数 + occlusion）。
2. A/B capture：同机位 on/off；台面逐像素对照（灰度契约红线）。
3. gl_compatibility 降级 capture 验证。
4. 帧时间记录（720p/1080p），>30% 则默认关闭。

## Phase 2 — 程序化材质（已完成）
1. 材质工厂加噪声层（走道/围栏 roughness+albedo 微噪声；平台侧面拉丝）。
2. 能量块自定义倒角 ArrayMesh 评估（Godot 4.7 无 RoundedBoxMesh；UV 完整性 capture 定案）。
3. 机器人分件三次升级（斜切上盖/轮刻线/天线+呼吸 LED）。
4. capture 验证 + 像素分桶回归（分桶颜色识别不破坏）。

## Phase 3 — 受控氛围（已完成）
1. Volumetric Fog 低密度 + A/B（灰度污染检查）。
2. Glow HDR 阈值 1.2 复评（白心无晕染才默认开）。
3. DoF + 可选暗角（CameraAttributesPractical）。
4. 逐项帧时间 A/B。

## Phase 4 — 场地标识（已完成）
1. 出发区描边 + 中圈环（Scenario.Field 推导，装饰层）。
2. 可选场边 Label3D 文字。
3. 拾取/拖拽/规则回归（edit-smoke）。

## Phase 5 — 质量门禁与文档（已完成）
1. `dotnet build godot/GodotSim.csproj`（0 警告）；`dotnet test`（324+ 全绿）。
2. `--camera-smoke`（R1 断言 + 四方向）、`--edit-smoke`、Godot parity 全绿。
3. 双分辨率最终 capture（默认/俯视/跟随/拖动后 + 运行中），效果逐项
   A/B 证据与帧时间表整理进 `research/evidence.md`。
4. `godot/README.md` 视觉栈表更新（SDFGI/雾/glow/材质/标识各行 + 关闭策略）；
   `.trellis/spec/frontend/component-guidelines.md` 视觉栈约定同步。
5. `git diff --check`；Sim.Core/协议/HUD 零 diff 复核。

## Validation

```bash
dotnet build godot/GodotSim.csproj --no-restore
dotnet test --no-restore        # 324+ 全绿
GODOT="--headless --path godot --"
$GODOT --camera-smoke           # 42+ 全 ok
$GODOT --edit-smoke             # 全 ok
$GODOT --parity-check .../godot-parity-seed42.json   # PASS
# 真实 renderer: forward_plus + gl_compatibility 双 capture（证据见 research/evidence.md）
```

## Risky files / 回滚

- `godot/scenes/Main.tscn`（环境/灯光/雾——场景整体 revert 即总回滚）
- `godot/src/ArenaVisualizer.cs`（材质工厂/装饰——按效果分提交）
- `godot/project.godot`（MSAA/渲染设置——加性）
- 每效果独立提交，单独 revert；不做 `git reset --hard`，不覆盖外来改动。
