# Implementation Plan — Godot 3D 赛事视觉真实感优化

## Phase 0 — Baseline and workspace safety

1. 阅读当前 `prd.md`、`design.md`、Godot frontend/sim specs，并确认工作树中已有 AI batch/headless 改动属于其他任务；只在本任务范围内修改 Godot 视觉文件。
2. 用当前代码在真实 Godot Mono Forward+ renderer 生成 1280×720 和 1920×1080 baseline capture，记录默认概览、俯视、跟随至少一张画面，以及 `--camera-smoke`/`--edit-smoke` 结果。
3. 记录相同机器、场景、窗口尺寸和运行时长下的启动耗时、平均/峰值帧时间（能可靠采集时）及 renderer 设置。截图和日志放临时目录或任务证据目录，不放入 Git。

## Phase 1 — Environment and lighting

1. 在 `godot/scenes/Main.tscn` 中把纯色环境升级为内置程序化天空/渐变背景，控制亮度和饱和度，确保背景服务于擂台轮廓而不是抢主体。
2. 配置主光、弱补光、轮廓光或等价方案，分别验证阴影方向、机器人暗面可读性、围栏/擂台边缘分离和双方颜色保真度。
3. 添加有限范围、一次更新的 `ReflectionProbe`；如果真实 renderer 下反射不稳定、覆盖错误或成本不合适，回退到无探针的稳定基线并记录原因。
4. 每次修改后先编译启动，再做双分辨率 capture；不得修改台面灰度 unshaded 材质。

## Phase 2 — Materials and render-only geometry

1. 在 `godot/src/ArenaVisualizer.cs` 中建立/复用材质创建策略：喷涂金属、橡胶/深色结构、白色板材、能量块和团队发光标识分别设置合理 roughness/metallic/specular/emission。
2. 保留台面灰度纹理的 unshaded 路径、官方黑边到白中心、中央红区和白“武”字；用针对性 capture 检查灰度阶梯没有被灯光或后处理污染。
3. 为 primitive robot fallback 加入车体分层、车头/推铲、轮/履带暗部和团队灯带等视觉细节。所有节点必须是 render-only，并由现有 robot/field 尺寸推导。
4. 为围栏、平台边缘、能量块增加有限的深度/倒角/发光层次；禁止引入第二套物理几何或影响碰撞/传感器。
5. 回归 glTF 测试立方体、缺失法线修复、坏/缺失模型 fallback 和团队状态显示；本轮不新增第三方模型或贴图。

## Phase 3 — Camera framing

1. 在 `godot/src/MatchCamera.cs` 调整默认概览 framing，使完整场地在两种分辨率中达到 PRD 占比目标，同时保持安全边距。
2. 运行 `--camera-smoke`，逐项检查 Overview/Follow/Top、左键旋转、右键平移、滚轮缩放、C 切换、F5 重置和编辑器指针所有权。
3. 确认相机仍从 `Scenario`/`FieldTransform` 取得场地尺寸和中心，不新增硬编码 arena 常量。

## Phase 4 — Optional post-processing and anti-aliasing A/B

1. 逐项试验 tonemap、轻量 SSAO/SSIL、受控 glow/雾和 MSAA/TAA；先保留可回退开关，避免一次加入多个无法归因的效果。
2. 在静态画面、相机拖动和机器人运动中检查锯齿、拖影、闪烁、曝光、颜色和 HUD 污染。
3. 以 baseline 为参照；任何明显卡顿或约 30% 以上帧时间退化的选项默认关闭，保留配置说明和实验结果。
4. 不把 SDFGI/VoxelGI、Lightmap 烘焙、自定义全屏 shader 或第三方资产纳入第一轮默认实现。

## Phase 5 — Verification and documentation

按项目现有脚本和工具链执行以下检查，命令中的 Godot 路径按机器实际安装位置替换：

```powershell
dotnet test .\src\Sim.Tests\Sim.Tests.csproj --no-restore
python .\tools\smoke_test.py --help
python .\tools\smoke_test.py --parity
python .\tools\smoke_test.py --camera-smoke
python .\tools\smoke_test.py --edit-smoke
```

使用真实 Godot Mono Forward+ renderer 执行项目既有 `--capture` 流程，在 1280×720、1920×1080 和运动镜头下留存对照证据；不要用 dummy renderer 代替视觉验收。若项目脚本的参数名或窗口参数与上述示例不同，以 `godot/README.md` 和 `Main.cs` 的现有定义为准，不要自行发明第二套启动入口。

更新 `godot/README.md` 或相关架构文档，说明：第一轮无第三方资产；保留 glTF 外观接口；默认视觉栈、关闭的高成本实验、真实 renderer capture 方式和性能回退策略。不得把临时截图、`.import` 或本机绝对路径写入文档。

## Definition of done

- PRD 中 R1–R5 和验收项逐项有实现或证据映射。
- 双分辨率真实 renderer capture 显示完整擂台占比改善、材质/灯光层次提升、灰度契约和 HUD 保持正确。
- 三种相机、鼠标交互、快捷键、编辑器指针所有权和现有 smoke 通过。
- 运行时/回放/协议/Sim.Core/无头 batch 无非渲染改动；可选 glTF、法线修复和 fallback 回归通过。
- 性能对照记录完整；高成本效果若未达标则默认关闭且有原因。
- `trellis-check` 通过；完成后再使用 `trellis-finish-work` 收尾，不在规划阶段启动任务或修改产品代码。

## Risk points and rollback

- `Main.tscn` 的环境、灯光、探针和后处理是第一处回退点；出现过曝、黑块、闪烁或成本异常时先回到上一个渲染配置。
- `ArenaVisualizer.cs` 的材质/装饰节点最容易误触灰度契约或产生额外节点开销；任何回归都优先删除新增装饰并保留材质基线。
- `MatchCamera.cs` 只接受 framing 调整；如果相机 smoke 或编辑指针失败，回退 framing 参数，不修改输入状态机。
- 当前工作树已有其他任务的未提交文件；实现时不得用全量格式化、重置或清理命令覆盖它们。
