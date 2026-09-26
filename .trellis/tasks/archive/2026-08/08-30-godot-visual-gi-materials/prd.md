# Godot 3D 视觉三阶: 全局光照与材质增强

## Goal

在一轮（取景/三点光/官方调色板）与二轮（方向校正/径向渐变）的基础上，用 Godot
Forward+ 的内置能力把画面从"程序化图元素模"推进到"半写实转播质感"：全局光照、
程序化材质细节、受控氛围后处理、机器人/能量块二次升级、场地标识。全部使用引擎
内置能力，**不引入第三方资产**；保持 HUD、仿真权威、输入语义与台面灰度契约不变。

## Confirmed Baseline

- 一轮/二轮已交付：取景占比断言（51.9%×53.9%）、三点光+Filmic+SSAO+UPDATE_ONCE
  反射探针、官方径向渐变台面（Unshaded 契约）、相机四方向反转契约、能量块贴纸
  立方体（六面 UV + 棱线 + 顺光接触阴影）、MSAA 4×。
- 被否决并记录的实验：glow（白心 ~1.0 亮度会泛光晕）、TAA（拖影）、
  SDFGI/VoxelGI（当时按"首轮不启用"搁置——本任务正式评估启用 SDFGI）。
- 用户对比 Unity 后反馈差距；诊断结论：差距在"内容与光照美术"而非引擎能力。
- 硬件：RTX 5070 Ti Laptop，Forward+ 全特性可用；30% 帧时间退化门禁沿用。

## Requirements

### R1. 全局光照 (SDFGI)

- 启用 `Environment.sdfgi`（Forward+ 专属），参数保守（默认级联、
  `use_occlusion=true`）；给擂台/围栏/机器人带来真实的相互反弹光。
- 台面 `Unshaded` 材质不受 GI 影响必须用 A/B capture 证明（逐像素对照，
  沿用二轮的纯色 A/B 方法）。
- gl_compatibility 下降级路径：SDFGI 自动无效，画面不得出现错误（用探针/环境
  光兜底），需 capture 验证。

### R2. 程序化材质细节 (零外部资产)

- 走道/围栏：`NoiseTexture2D`（FastNoiseLite，引擎程序生成）作为 roughness/
  albedo 微变化层，表达 PVC 哑光与磨损感；平台侧面金属拉丝感（各向异性噪声）。
- 能量块：`RoundedBoxMesh`（Godot 4 内置图元）替代硬边立方体，边缘高光；
  贴纸贴图与棱线保留。
- 机器人 primitive fallback 三次升级：斜切/倒角车体、带纹路轮面、天线+呼吸
  LED（自发光能量的逐帧确定函数——纯表现层）、推铲刃口高光。全部打
  `primitivePart` meta 保持 glTF 让位契约。

### R3. 受控氛围后处理

- 体积雾（Forward+ Volumetric Fog，低密度）营造赛场纵深；不得让台面灰度
  读数被雾污染（台面靠近相机，雾密度按 A/B 验证）。
- Glow 重新评估：二轮否决原因是白心 1.0 亮度泛光；本任务用 **HDR 阈值 >1.0**
  （如 1.2）+ 低强度，使只有 emission energy >1 的 LED/指示灯泛光、台面白心
  不泛光——必须 A/B capture 证明灰度契约不受污染，否则维持关闭并记录。
- 轻微 DoF（`CameraAttributesPractical`）+ 可选暗角；HUD CanvasLayer 不受
  3D 后处理影响。

### R4. 场地标识与转播层次

- 出发区边界线、中圈标线等用 Decal/薄面片（几何从 `Scenario.Field` 推导，
  渲染层装饰，不打 meta 进任何仿真数据）。
- 可选：场边标识文字（Label3D，队伍名/赛项名）。

### R5. 证据与性能门禁

- 双分辨率（1280×720 / 1920×1080）真实 renderer capture：改动前后、三镜头
  模式、SDFGI/雾/glow 逐项开关 A/B。
- 帧时间对照（同机同场景，±30% 门禁；超限项默认关闭并记录实验配方）。
- 灰度契约逐像素 A/B（台面官方渐变逐位不受影响）；HUD 清晰度检查。

## Out of Scope (Phase B — 资产轨, 另行决策)

- 真实机器人 GLB 模型、HDRI 环境贴图、PVC 扫描贴图等**第三方/自制资产**：
  需用户提供或选定来源并完成许可证/尺寸/法线审计后另立任务；
  `RobotModelLoader` 的导入链已就绪。
- 不重排 HUD、不改相机交互语义、不触碰 Sim.Core/Scenario/回放协议。

## Acceptance Criteria

- [ ] SDFGI 开启后真实 renderer capture 显示擂台/机器人反弹光明显改善；
  台面灰度逐像素 A/B 证明零污染；gl_compatibility 下降级无错误画面。
- [ ] 走道/围栏/平台侧面具备可辨识的程序化材质细节；能量块圆角 + 棱线 +
  贴纸完整；机器人分件在默认取景下可辨识出车体/轮/铲/灯带结构。
- [ ] 体积雾/glow/DoF 均通过灰度契约 A/B 与 HUD 清晰度检查；glow 若仍污染
  白心则默认关闭并记录。
- [ ] 场地标识渲染为纯装饰层，不影响选择/拖拽/规则。
- [ ] 帧时间退化 ≤30%（每项后处理有开关 A/B 与帧时间记录）。
- [ ] `dotnet test`（324+ 基线）、`--camera-smoke`（含取景断言）、`--edit-smoke`、
  Godot parity 全绿；`Scenario`/`Snapshot`/回放/fidelity.json 零改动。

## Key Decisions

- 全部使用 Godot 内置能力（NoiseTexture2D/RoundedBoxMesh/SDFGI/VolumetricFog/
  Decal/CameraAttributes 均为引擎特性，不属于"第三方资产"）。
- 第三方资产（机器人模型/HDRI/贴图扫描）为 Phase B，待用户提供或选定来源
  后另立任务；本任务不阻塞其前置（glTF 链已就绪）。
- 每项视觉效果必须可独立开关（A/B 证据 + 帧时间门禁），默认配置 = 全部
  通过门禁项的组合。
