# Godot 3D 赛事视觉真实感优化

## Goal

优化 Godot 桌面端 3D 赛事画面的取景、环境、灯光、材质和显示层次，使擂台与机器人更好看、更接近真实转播效果；保持既有 HUD 布局、仿真权威和输入语义不变。

## Confirmed Baseline

- `godot/project.godot` 已声明 Godot 4.7 / C# / `Forward Plus`；本任务可以使用 Forward+ 的 3D 渲染能力，但不能把渲染能力写进 `Scenario`、`Snapshot` 或回放数据。
- `godot/scenes/Main.tscn:7-29` 当前只有纯色环境和一盏带阴影的 `DirectionalLight3D`；没有天空、反射探针、环境后处理或专门的补光层。
- `godot/src/MatchCamera.cs:38-53,125-153` 当前概览取景距离按场地尺寸约为 `fieldSize * 2.5`，现有 `godot/docs/desktop-1080.png` 与 `desktop-720.png` 显示擂台在窗口中央占比偏小，四周有大面积深色空区。
- `godot/src/ArenaVisualizer.cs:112-195` 通过程序化 Box/Plane 构建外场、擂台、围栏和出发区；`235-272` 的默认机器人是圆柱 + 盒子图元；`330-345` 的普通材质只有基础颜色、粗糙度和金属度。
- `godot/src/ArenaVisualizer.cs:132-145,348-360` 台面灰度纹理必须保持 `Unshaded`，这是为了保证官方调色板和灰度区域不会被方向光制造假梯度；真实感增强不能破坏这条显示契约。
- `godot/src/RobotModelLoader.cs:12-41,160-190` 已有可选 glTF 外观导入、坏模型回退和缺失法线修复，但仓库当前只有品红测试立方体，不应把测试模型当成真实机器人资产。
- `godot/src/Main.cs` 已提供 `--capture`、`--camera-smoke`、`--edit-smoke`；截图是视觉验收证据，dummy renderer 下截图可能跳过，不能用无纹理 dummy 输出宣称真实画面质量。
- HUD 已按“深色赛事控制台”方向实现；本任务不重新设计控件层级，不移动已有按钮，不改变镜头/编辑/重启快捷键语义。

## Research Findings

- Godot 官方文档确认 `StandardMaterial3D`/`ORMMaterial3D` 已覆盖 PBR 常用的 albedo、metallic、roughness、emission 和 AO 参数，不需要首轮就引入自定义 shader。
- Godot 官方文档确认 Forward+ 支持 SSAO、SSIL、SDFGI、屏幕空间反射、反射探针、TAA、MSAA、雾和 glow；但这些能力有明显的 GPU/动态物体限制，必须逐项用真实 renderer capture 验证。
- 反射探针官方建议使用覆盖场景的有限范围，`Update Mode=Once` 比每帧更新便宜，适合这个小型固定擂台；不默认启用昂贵的全场实时 GI。
- VoxelGI 需要静态几何和烘焙数据，SDFGI 虽然配置少但成本高且动态物体不能贡献完整 GI；二者先作为候选实验，不进入第一版默认画面。
- 抗锯齿要在真实窗口和移动镜头下评估：Forward+ 支持 TAA/MSAA，但 TAA 可能在动态机器人后产生拖影，不能只凭静态截图选择。

## Requirements

### R1. 赛事画面构图与可读性

- 默认概览镜头应让完整场地成为画面主体：在支持的 1280×720 和 1920×1080 窗口中，场地包围盒目标占视口宽度约 45%–65%、高度约 45%–75%，四边留出安全边距，不裁掉围栏、出发区和台面。
- 通过取景距离/焦点/相机视场角解决“场地太小”，不得在渲染层重新抄写场地尺寸、中心或坐标变换；继续使用 `Scenario`/`FieldTransform` 的唯一几何来源。
- 概览、跟随、俯视三种镜头仍可用；左键旋转、右键平移、滚轮缩放、C 切换、F5 重置和布局编辑器的指针所有权必须保持现有语义。
- 场地、双方机器人、能量块和关键边界在默认视角下要有清晰层次，不能因为背景、阴影、泛光或曝光变成一团黑/白。

### R2. 环境与灯光的半写实基线

- 将当前纯色背景升级为克制的赛事空间背景（程序化天空/渐变背景或等价方案），保持深色赛事控制台气质，同时消除大面积“空黑”对主体的吞没；背景不能抢过场地和 HUD。
- 建立可解释的三点或等价灯光关系：主光负责方向和接触阴影，弱补光避免机器人暗面死黑，轮廓/边缘光负责分离双方机器人和擂台边界。颜色与能量必须是渲染层参数，不进入物理场景。
- 普通不透明几何使用真实 renderer 可验证的阴影、环境光和 PBR 材质；阴影要有接触感但不能在场地灰度纹理上制造规则误读。
- 可加入一个范围严格受控、默认一次更新的 `ReflectionProbe` 或同等低成本反射方案，让机器人外壳/平台侧面有合理的环境反射；不得默认开启需要烘焙资产或每帧重渲染的重型方案。
- 后处理只允许使用能改善层次的轻量设置（如 tonemap、轻微 SSAO/SSIL、受控 glow/雾）；不得用过度 bloom、景深、色偏或 vignette 掩盖几何/材质问题。

### R3. 场地、机器人和能量块的材质层次

- 为地面、围栏、平台侧面、机器人、能量块和发光指示分别定义可复用的渲染材质策略，使用 roughness/metallic/specular/emission 等参数表达橡胶、喷涂金属、白色板材和指示灯差异。
- 默认机器人图元不能继续只表现为无细节色块；在不改变 `RobotVisual`、碰撞半径、位置、朝向和仿真结果的前提下，增加可见的车体分件、车头/推铲层次、轮/履带暗部或团队识别灯带等纯渲染细节。
- 保留现有 `RobotModelLoader` 的 glTF 配置、缺失法线修复、坏模型回退和团队识别接口作为后续升级入口；本轮不新增或下载第三方 GLB、纹理、HDRI 资产，仍要让 primitive fallback 达到可接受的视觉层次。测试立方体只能用于导入链回归，不能作为最终真实感展示。
- 台面官方灰度区域、中央红区和白“武”字的颜色/轴向/Unshaded 契约保持不变；任何材质增强都只能作用于不破坏该契约的侧面、边缘和额外装饰层。
- 额外线条、边缘、灯带或装饰必须是视觉层对象，尺寸/位置从既有场景几何推导，不新增一套会被误认为物理规则的几何真值。

### R4. HUD 与交互兼容

- 保留当前深色 HUD 的节点层级、布局锚点、按钮文本、按钮顺序、快捷键、事件栏和回放控制条；本任务只允许为保证 3D 可读性做透明度/对比度等最小配合调整，并须单独记录。
- HUD 继续处于 `CanvasLayer`，不被 3D glow、雾、景深或反射处理污染；3D 画面优化不能让中文字体、分数和事件状态难以阅读。
- 运行中比赛仍只由 `SnapshotView`/`MatchSession` 驱动显示；视觉增强不得在 Godot 节点内计算得分、碰撞、登台或传感器状态。

### R5. 性能与质量证据

- 所有视觉选择必须在 Godot Forward+ 真实 renderer 下通过 1280×720 和 1920×1080 capture 检查；截图存为本地/任务证据，不把临时截图和 `.import` 文件混入提交。
- `--capture`、`--camera-smoke`、`--edit-smoke` 在视觉改动后仍成功；相机三模式的完整场地覆盖、交互收敛和编辑器指针接管不能回归。
- 在相同机器、场景、窗口尺寸和运行时长下记录改动前后渲染性能；如某项后处理造成明显卡顿或超过基线约 30% 的帧时间退化，默认配置必须关闭该项并记录为可选实验。
- 视觉质量验收关注主体占比、阴影/反射稳定性、边缘锯齿、颜色保真度、HUD 清晰度和镜头移动无明显拖影；不能用“看起来更亮”替代这些证据。

## Out of Scope

- 不调整 `Sim.Core` 物理、碰撞、摩擦、堵转、登台、计分、规则或随机数；不把真实感渲染误写成物理保真度晋升。
- 不重排、重命名或重做 HUD 按钮、布局、快捷键和交互流程；不把本任务变成赛事控制台 UI 重构。
- 不修改官方场景、`Scenario`/`Snapshot`/回放 schema、`fidelity.json` 或视觉/传感器数据语义。
- 不默认引入 SDFGI/VoxelGI、Lightmap 烘焙、复杂自定义全屏 shader、动态纹理管线或会显著增加运行时依赖的特效。
- 本任务不引入第三方 GLB、纹理、HDRI、字体或音频资产；后续若要接入真实机器人模型，必须另开升级任务并记录来源、许可证、大小、尺度/法线校准和回退方案。
- 不把 Godot 桌面端改造成无头仿真入口；AI agent 的无头 batch 任务与本视觉任务保持分离。

## Acceptance Criteria

- [ ] 默认 1280×720 / 1920×1080 真实 renderer 截图中，完整擂台占据目标画面比例，主体不再缩在中央小区域；围栏、出发区、台面和双方机器人均清晰可辨。
- [ ] 场景拥有经过说明的背景、主光/补光/轮廓光和阴影策略；普通几何具备可见材质层次，机器人和能量块不再像无光照色块，同时没有大面积死黑、过曝或明显闪烁。
- [ ] 台面灰度官方调色板、中央红区、白“武”字、场地几何轴向和 Unshaded 契约逐项回归通过，视觉增强没有改变传感器/规则含义。
- [ ] 默认机器人 fallback 和可选 glTF 导入均能正确显示法线、团队识别和登台指示；损坏/缺失模型仍安全回退，仿真结果不变。
- [ ] HUD 的布局、按钮、快捷键、事件栏和回放条保持可用且清晰；3D 后处理不会污染 CanvasLayer 文本。
- [ ] 概览/跟随/俯视、左键旋转、右键平移、滚轮缩放、F5 重置、布局编辑器指针所有权和现有 smoke 断言全部通过。
- [ ] 真实 renderer 的双分辨率 capture、性能对照和视觉检查记录在任务证据中；任何被关闭的高成本效果都有原因和可复现配置。
- [ ] `dotnet test`、基线 `replay-check`、Godot parity/edit-smoke/camera-smoke 通过；`Scenario`、`Snapshot`、回放、`fidelity.json` 和 Sim.Core 行为无非渲染改动。

## Key Decisions

- 第一轮不引入第三方真实机器人 GLB、纹理或 HDRI；视觉提升只使用仓库现有代码、程序化几何、Godot 内置渲染能力和可复现的材质/灯光参数。
- 保留 `RobotModelLoader` 的现有 glTF 外观导入接口、法线修复与 fallback 行为，不删除、不改成只支持 primitive；后续真实资产接入必须作为独立任务完成来源与许可证审计。
- 第一轮优先验证取景、环境、三点灯光、PBR 材质、程序化机器人细节、低成本反射和必要的抗锯齿；SDFGI/VoxelGI、第三方资产和重型后处理不进入默认方案。

## Research References

- Godot `StandardMaterial3D` / ORM 材质：<https://docs.godotengine.org/en/stable/tutorials/3d/standard_material_3d.html>
- Godot 3D 灯光与阴影：<https://docs.godotengine.org/en/stable/tutorials/3d/lights_and_shadows.html>
- Godot 环境与后处理：<https://docs.godotengine.org/en/latest/tutorials/3d/environment_and_post_processing.html>
- Godot 反射探针：<https://docs.godotengine.org/en/latest/tutorials/3d/global_illumination/reflection_probes.html>
- Godot 渲染器能力矩阵：<https://docs.godotengine.org/en/stable/tutorials/rendering/renderers.html>
- Godot GI 方案比较：<https://docs.godotengine.org/en/latest/tutorials/3d/global_illumination/introduction_to_global_illumination.html>

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- 第三方资产决策已确认；`design.md` 与 `implement.md` 已按“第一轮无第三方资产、保留 glTF 接口”收敛，可交给实现工程师执行。
- 当前任务保持 `planning`；创建任务和写入 PRD 不等于授权修改 Godot 产品代码。
