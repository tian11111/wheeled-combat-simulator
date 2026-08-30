# Technical Design — Godot 3D 二轮视觉校正与相机拖拽方向修复

## 1. Boundary and ownership

本任务是 Godot 桌面壳的表现层修正，目标是让用户看到的画面和操作符合实机预期；不改变仿真内核的任何数值语义。

```text
Scenario / SnapshotView / MatchSession
              |
              +--> ArenaVisualizer --> render-only 3D nodes
              |                          (materials / surface texture)
              +--> MatchCamera -------> presentation pose + input
              |
              +--> CanvasLayer HUD ---> 2D controls and text

Sim.Core.FieldModel.FieldGrayLocal -- sensor/physics gray semantics only
official surface gradient ---------------- visual-only display mapping
```

- `Sim.Core.FieldGrayLocal` 仍是灰度传感器和规则所需的 0–1000 模型；不在本任务改成径向模型，不触碰旧回放。
- `FieldGrayTextureMap`/`ArenaVisualizer` 负责把平台局部坐标转换成官方效果图的显示颜色。该显示映射可以和传感器函数不同，但必须明确标记为 visual-only。
- `MatchCamera` 只处理 Godot 输入与镜头状态；`Main` 的 smoke 是输入行为的证据，不是仿真数据源。
- `CanvasLayer` 与 3D WorldEnvironment 保持隔离，任何后处理不得改变 HUD 像素。

## 2. Camera direction contract

以 `screenDelta = currentMousePosition - previousMousePosition` 定义输入，正 X 表示指针向右，正 Y 表示指针向下。用户要的是当前实现的反向修正，因此实现契约固定为：

- Overview/Follow：`yaw = yaw - screenDelta.X * YawPerPx`；`pitch = clamp(pitch + screenDelta.Y * PitchPerPx, MinPitch, MaxPitch)`。
- Top：`topYaw = wrap(topYaw - screenDelta.X * YawPerPx)`；俯仰仍固定为 `-90°`，纵向拖动不改变高度/俯仰。
- 右键 `PanBy` 的地面抓取语义不变，滚轮步进和各模式限幅不变。

只改 `RotateView` 的符号、注释和对应 smoke 期望值；不要通过反转鼠标坐标、改 `OrbitDir`、改相机节点 transform 或修改布局编辑器来间接实现，否则会同时影响 Top、Follow 或编辑器行为。

测试应显式注入四种小拖动：右、左、下、上，并分别检查 yaw/pitch 的增减；Top 只检查水平自旋。测试需等待相机阻尼收敛，但方向判断使用暴露的角度属性，不用截图猜测。

## 3. Official surface gradient design

### 3.1 Mathematical mapping

对平台局部点 `(x, y)` 和平台边界 `[minX,maxX] × [minY,maxY]`：

```text
centerX = (minX + maxX) / 2
centerY = (minY + maxY) / 2
halfX   = (maxX - minX) / 2
halfY   = (maxY - minY) / 2
nx      = (x - centerX) / halfX
ny      = (y - centerY) / halfY
radius  = sqrt(nx² + ny²) / sqrt(2)
luma    = clamp(1 - radius, 0, 1)
```

这会使中心为 1、四角为 0，并让同一欧氏半径的水平/垂直/对角样本相同；边中点约为 `1 - 1/sqrt(2)`，符合“从四角到中心”而不是“整条边都黑”的图纸观感。实际实现允许在 capture 证据支持时增加很小的曲线校正，但不能重新引入方向性 `max` 或 `abs(x)+abs(y)` 造成对角亮带。

### 3.2 API and data flow

1. 在无 Godot 依赖的 `godot/src/FieldGrayTextureMap.cs` 增加纯函数（名称可采用 `OfficialSurfaceLuminance`）和针对官方平台的 RGB8 构建入口；函数参数必须包含平台边界/中心，不写死 2.4m、1.2m 或世界坐标。
2. 保留通用纹理测试入口时，用明确参数区分 `grayAt`（传感器值）和 `displayAt`（视觉亮度）；不要让调用者误以为显示亮度仍等于 `FieldGrayLocal`。
3. `ArenaVisualizer.MakeFieldGrayTexture` 从 `FieldParams.Platform` 取得边界，把平台局部坐标送入 visual-only 函数。红区判定先于渐变着色；`WalkwayRgb` 只作为平台外/其他材质的视觉颜色，不改变传感器值。
4. 平台顶面继续使用 `StandardMaterial3D` 的 `Unshaded` 路径。保持图像轴约定（列西到东、行南到北），并关闭 mipmap；采用稳定线性过滤以避免 128×128 纹理的硬阶梯。具体 Godot 属性名按 4.7.2 C# API 核对后再写入。

### 3.3 Artifact isolation

按以下顺序排查，不一次改变多个变量：

1. 用纯白/纯黑或线性单轴测试纹理验证 PlaneMesh UV 与像素方向。
2. 用新的径向函数生成纹理，在关闭 SSAO/ReflectionProbe 的 A/B 场景中 capture；确认对角亮带在纯纹理阶段已经消失。
3. 若纯纹理正确而完整场景仍出现斜缝，检查平台 BoxMesh 顶面与顶面 PlaneMesh 的 1mm 高差、深度精度和两个三角面接缝；优先提高安全偏移或避免重叠表面，但不得制造可见台阶或改变平台碰撞几何。
4. 再逐项恢复 SSAO、反射探针和抗锯齿；任何效果重新制造灰度梯度或运动闪烁时，保持默认关闭并记录原因。

## 4. Second-pass visual tuning

- 以当前 `Main.tscn` 的程序化天空、三点灯光、一次更新 ReflectionProbe、Filmic tonemap 和 MSAA 为基线，只调一个参数/一组同职责参数后 capture。
- 优先改善平台边缘与底座层次、机器人 fallback 的车体/推铲/轮暗部/团队灯带、能量块的形体分离和接触阴影；不增加物理节点、不改变 robot position/yaw/radius。
- 背景和灯光的亮度必须服务于擂台主体；不使用 glow/bloom、景深、vignette 或颜色滤镜掩盖渐变问题。
- 相机 framing 只在当前截图仍存在遮挡或主体比例异常时小幅调整，并继续从场景几何推导尺寸；不复制场地常量。

## 5. Compatibility and rollback

- 预期产品文件范围：`godot/src/MatchCamera.cs`、`godot/src/Main.cs`、`godot/src/FieldGrayTextureMap.cs`、`godot/src/ArenaVisualizer.cs`、必要时 `godot/scenes/Main.tscn`、`godot/project.godot`、`godot/README.md`、`.trellis/spec/frontend/*` 和 `src/Sim.Tests/FieldGrayDisplayTests.cs`。
- 不得修改 `src/Sim.Core/FieldModel.cs` 的运行时灰度公式；若实现者认为必须改核心，必须先回到规划阶段，不得在本任务内偷偷扩大范围。
- 相机符号、显示渐变、渲染参数和几何细节应保持可独立回退；优先回退后处理/材质细节，再回退到旧视觉基线。
- 如果新视觉映射使旧的“显示来自 FieldGrayLocal”文档失真，更新前端/视觉规范，明确区分传感器灰度和官方效果图显示，不修改协议字段。

## 6. Evidence contract

真实 renderer capture 必须覆盖：默认概览、俯视、拖动后概览、运行中机器人，以及 1280×720/1920×1080 两个窗口尺寸。每组证据记录：

- 场地四角/边中点/中心的亮度关系和对角亮度是否一致；
- HUD 是否清晰且未被 3D 后处理影响；
- 相机方向、完整场地覆盖和编辑器指针所有权；
- 帧时间相对 baseline 的变化及被关闭效果的原因。

临时截图与日志放到系统临时目录或任务证据目录，不覆盖仓库内历史“before”截图，也不提交 `.import` 文件。
