# Design — Godot 3D 视觉三阶 (全局光照与材质增强)

## 0. 边界重申

所有改动限于表现层：`godot/scenes/Main.tscn`（Environment/灯光/雾/probe）、
`godot/project.godot`（渲染设置）、`godot/src/ArenaVisualizer.cs`（材质工厂/
装饰节点）、`godot/src/Main.cs`（仅 QA 旗标/capture 证据）、`godot/src/MatchCamera.cs`
（如 DoF 相机属性）。Sim.Core/Scenario/Snapshot/回放零 diff；HUD 文件零 diff。
台面灰度 `Unshaded` 官方调色板契约是每一步的 A/B 红线。

## 1. SDFGI 全局光照 (R1)

- `Main.tscn` WorldEnvironment：`sdfgi_enabled=true`，`use_occlusion=true`，
  级联/密度用默认起步；`sdfgi_y_scale` 视场景高度调至 75%。
- 仅 Forward+ 生效；gl_compatibility 自动忽略 → 该渲染器下依靠既有
  ReflectionProbe + 环境光，capture 验证降级画面无错误。
- **灰度契约 A/B**：沿用二轮方法——同一机位 SDFGI on/off 各 capture，
  台面像素分桶逐像素对照（Unshaded 不参与 GI，理论上零差异，必须证明）。
- 帧时间记录；若 1080p 退化 >30% → 默认关闭并记录配方（RTX 5070 Ti 预期远低）。

## 2. 程序化材质 (R2)

- `NoiseTexture2D` + `FastNoiseLite`（引擎程序生成，非资产）：
  - 走道/围栏哑光橡胶：roughness 纹理（噪声 0.55–0.95）+ albedo 微噪声
    （±4% 亮度），seamless 平铺。
  - 平台侧面白色板材：轻度各向异性噪声模拟拉丝。
- 实现集中在 `ArenaVisualizer` 材质工厂（`MakeMatte`/`MakePaintedMetal`/
  `MakeWhiteBoard` 增加可选噪声层），避免逐节点漂移。
- 能量块：Godot 4.7 API 未提供 `RoundedBoxMesh`，因此采用方案 B 的自定义倒角
  `ArrayMesh`（约 8% 边长）：六个内缩主面保持完整同图案 UV，倒角面用同色材质，
  另保留 12 条低厚度边缘辅助线强化轮廓。这样不改变碰撞几何，也不牺牲官方贴纸完整性；
  capture 已确认没有透视空洞或悬浮感。
- 机器人分件三次升级（全部 `primitivePart` meta）：
  - 车体上盖改斜切棱台（SurfaceTool 构建），前缘下压。
  - 轮面加径向刻线（每轮 8 条薄暗盒）。
  - 天线（细圆柱 + 端点 emissive 小球，随呼吸灯同相）。
  - 团队灯带改呼吸发光：emission energy = 基线 + 0.4·sin(2π·t/2.0 + role 相位)
    ——表现层时间函数，逐帧确定，不进仿真。

## 3. 受控氛围 (R3)

- Volumetric Fog：`volumetric_fog_enabled=true`，density = 0.003，
  GI/雾同时开的组合帧时间必须记录。台面贴近相机、雾对灰度的影响用 A/B 证明。
- Glow 复评：`glow_enabled=true` + `glow_hdr_threshold=1.2`（台面白心
  unshaded ≈1.0 不越阈）+ `glow_intensity=0.35`；LED 灯带 emission energy 提到 2–3
  使其成为唯一泛光源。A/B capture 证明台面无晕染，否则默认关闭（沿用二轮
  结论并更新实验配方）。
- DoF：`CameraAttributesPractical`，far blur = 0.045、distance = 7.0 m，
  HUD 为 CanvasLayer 天然不受影响；暗角仅 CameraAttributes 内置（无全屏 shader）。

## 4. 场地标识 (R4)

- 出发区描边：4 条薄面片（黄色/蓝色区边界 +0.002 抬升，几何从
  `Field.StartZones` 推导）。
- 中圈：环形薄面片（半径 = 红区外接 ~0.45，中心同武字）。
- 场边文字：Label3D"武术擂台 · 轮式格斗"（可选，放围栏外侧上方）。
- 全部装饰不参与拾取/规则（与实体拾取代理无交集——拾取读 `_previewFrame`）。

## 5. 证据矩阵 (R5)

每个效果独立开关（场景属性或 QA 旗标），证据按效果记录：

| 效果 | A/B 对照 | 灰度契约检查 | 帧时间 |
|---|---|---|---|
| SDFGI | on/off 同机位 | 逐像素台面对照 | 720p/1080p |
| 材质噪声 | 新旧材质 | 不触及台面 | 合并计 |
| 体积雾 | on/off | 台面近相机区 | 1080p |
| Glow(1.2) | on/off | 白心晕染检查 | 1080p |
| DoF | on/off | HUD/边缘 | 合并计 |

## 6. 测试与回归

- `dotnet test` 324+ 基线全绿（Sim.Core 零 diff）。
- `--camera-smoke`（含 R1 取景断言 51.9%×53.9% 与四方向拖拽）——灯光/雾不得
  影响几何断言。
- `--edit-smoke` 全绿（编辑器拾取/拖拽/应用不回归）。
- Godot parity：视觉改动必须保持 `--parity-check` 逐位 PASS。
- 既有 capture 像素分桶（us/them/buff/debuff/platform/floor）在新材质下
  仍全层命中（贴纸/棱线/噪声不得破坏分桶颜色识别）。

## 7. 回滚

- 每个效果一个独立提交（SDFGI / 材质 / 氛围 / 标识 / 机器人 / 相机），
  单独 revert。
- 场景级总开关：全部新效果默认配置收在 `Main.tscn` Environment 单节点，
  revert 场景文件即可整体回退。
