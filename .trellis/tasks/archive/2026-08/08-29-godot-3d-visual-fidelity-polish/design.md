# Technical Design — Godot 3D 赛事视觉真实感优化

## 1. Scope and non-negotiable decisions

本设计只覆盖 Godot 桌面端的 render-only 视觉层。第一轮不引入第三方 GLB、纹理、HDRI、字体或音频，不修改 Sim.Core、Scenario、Snapshot、回放 schema、fidelity.json、传感器数据或规则；保留现有 `RobotModelLoader` glTF 配置/法线修复/fallback 接口，作为后续真实资产任务的扩展点。

既有深色赛事控制台 HUD、CanvasLayer 层级、按钮布局、快捷键和指针所有权保持不变。视觉改动必须从现有场景几何和 `Scenario`/`FieldTransform` 推导尺寸，不复制一套物理或规则几何。

## 2. Data flow and ownership

```text
Scenario / SnapshotView
        |
        v
MatchSession / ArenaVisualizer --------------------> CanvasLayer HUD
        |                                                   (2D only)
        v
render-only arena, robots, indicators
        |
        v
Main.tscn WorldEnvironment + lights + probe + MatchCamera
        |
        v
Godot Forward+ real renderer
```

- `Sim.Core` 和协议层继续拥有仿真状态、几何真值、计分和事件语义。
- `ArenaVisualizer` 只把状态转换为可见节点；新增装饰节点不能参与碰撞、规则或传感器计算。
- `MatchCamera` 只拥有表现层取景和输入响应；不在其中重建擂台几何。
- HUD 继续消费现有 `SnapshotView`/`MatchSession`，且处于独立的 `CanvasLayer`，不经过 3D 后处理。

## 3. File-level implementation contract

### `godot/scenes/Main.tscn`

- 将纯色 `Environment` 演进为克制的程序化赛事空间背景：优先使用 `ProceduralSkyMaterial`/天空渐变或等价内置设置，保持深色基调并抬高场地与机器人轮廓的背景分离度。
- 配置 tonemap、环境光和雾/泛光的最小基线；所有后处理先以关闭或低强度实验项存在，只有真实 renderer capture 与性能对照通过后才进入默认值。
- 建立主光、补光、轮廓光的明确关系。主光提供方向与阴影，补光只抬暗部，轮廓光只帮助区分机器人和围栏；避免用极端能量或颜色制造假真实感。
- 添加一个覆盖擂台但边界有限的 `ReflectionProbe`，默认一次更新而非每帧更新。探针不应覆盖无关的大范围空间，也不能成为动态仿真的依赖。
- 若启用 MSAA/TAA，优先通过项目设置做可回退的实验，并在相机移动和机器人运动 capture 中评估；不要为了静态截图牺牲动态清晰度。

### `godot/src/ArenaVisualizer.cs`

- 把地面、围栏、平台侧面、机器人、能量块和指示灯的材质参数收敛到可复用的创建方法或材质配置，避免每个节点各写一套互相漂移的参数。
- 普通不透明物体使用 `StandardMaterial3D` 的 albedo/roughness/metallic/specular/emission 等参数表达喷涂金属、橡胶、白色板材和发光标识的差异。
- 保持台面灰度纹理使用 unshaded 材质；不得把方向光、环境光或后处理写入灰度判读区域，也不得改变黑边到白中心、中央红区和白“武”字的官方颜色与轴向。
- 在 primitive robot fallback 内增加 render-only 分件：车体上盖/侧面层次、车头或推铲层次、轮/履带暗部、团队识别灯带和必要的接触阴影承载几何。尺寸、位置、朝向从既有机器人视觉尺寸/场地变换推导，不能改变碰撞半径、位置、朝向或状态。
- 适度增加平台边缘、围栏立柱、能量块外壳/发光层等深度信息；装饰节点统一挂在视觉树下，不添加物理节点。
- 材质和几何改动必须保留可选 glTF 路径、缺失法线修复、损坏/缺失模型 fallback 与团队状态指示。

### `godot/src/MatchCamera.cs`

- 只调整概览默认 framing 的距离、焦点或视场角，使完整擂台在 1280×720 与 1920×1080 达到 PRD 的占比目标；目标值应继续由场地尺寸推导。
- 保持 Overview/Follow/Top 三模式、左键旋转、右键平移、滚轮缩放、C 切换、F5 重置和 `PointerInputEnabled` 行为。
- 所有 framing 调整都要用 `--camera-smoke` 和真实 capture 检查：不能为了放大场地裁掉围栏、出发区、台面或状态指示。

### `godot/src/RobotModelLoader.cs` and HUD files

- `RobotModelLoader` 本轮只做必要的兼容回归，不接入新资产、不改变配置格式、不删除 normals/fallback 保护。
- HUD 文件不做布局重构；只有当新背景导致 3D 与文字对比不足时，才允许最小透明度/对比度调整，并单独记录前后截图。

## 4. Visual stack and rollout order

1. 先建立当前画面的双分辨率 baseline capture 和帧时间/启动日志。
2. 先改背景与灯光，再改材质与程序化细节，最后才试验反射探针和抗锯齿；每一步都能独立回退。
3. 以 `StandardMaterial3D` 为首轮 PBR 基线，不引入自定义全屏 shader、资产下载或重型实时 GI。
4. ReflectionProbe 使用有限 extents、一次更新；仅在真实 renderer 下确认机器人外壳和平台侧面得到稳定反射后保留。
5. SSAO/SSIL、glow、雾、TAA/MSAA 逐项 A/B。若运动画面出现明显拖影、闪烁、HUD 污染或帧时间相对 baseline 退化约 30% 以上，则关闭该项并记录为可选实验。
6. SDFGI/VoxelGI 只允许作为记录充分的候选实验，不进入本任务默认配置。

## 5. Determinism and compatibility guardrails

- 不新增仿真时钟、随机数、物理材质、碰撞体、规则判断或网络/进程依赖。
- 不修改 `SnapshotView`、`MatchSession`、回放、CLI batch 或 AI agent 无头入口。
- 所有视觉状态仍由现有快照驱动；发光颜色、团队条、登台指示只能反映已有状态。
- 真实视觉证据必须用 Forward+ renderer 生成；dummy renderer 的“成功”只能证明启动/流程，不代表画面质量。
- 临时 PNG、Godot `.import` 和性能日志放在临时目录或任务证据目录，不提交到产品资源目录。

## 6. Rollback and performance policy

每个视觉阶段应保持可独立回滚：环境/灯光、材质/几何、相机 framing、后处理/AA 分开提交或至少分开记录。若视觉质量与性能冲突，优先回退高成本后处理、实时 GI 或每帧反射；保留低成本 PBR、受控灯光、程序化细节和 framing 改进。

完成时必须同时报告双分辨率静态 capture、镜头移动/机器人运动检查、HUD 清晰度、灰度契约和前后性能对照，不能只报告“画面更亮”。

