# Implementation Plan — Godot 3D 二轮视觉校正与相机拖拽方向修复

## Phase 0 — Baseline and safe workspace

1. 读取本任务 `prd.md`/`design.md`、`trellis-before-dev` 要求和 Godot frontend/sim 规范；确认当前工作树中已有视觉任务、遥测任务和 AI batch 变更，禁止清理或重置无关文件。
2. 保留当前实机截图作为用户反馈证据；使用规则 PDF `D:/project/robocup/1779761830740288.pdf` 第 10 页作为官方表面外观基准，不把该 PDF 或临时提取图片复制进产品资源。
3. 在未改代码前用真实 Godot 4.7.2 Mono Forward+ 生成 1280×720、1920×1080 的 `--capture` baseline，并记录 Overview/Top/运动中的画面、renderer 设置和同条件帧时间。输出放临时目录。

## Phase 1 — Camera direction correction

1. 修改 `godot/src/MatchCamera.cs::RotateView`：Overview/Follow 的 yaw 与 pitch 符号、Top 的自旋符号按设计契约反转；不动 `GroundPoint`、`PanBy`、缩放、阻尼和 pointer ownership。
2. 更新注释、`godot/README.md` 的镜头操作表和 `.trellis/spec/frontend/component-guidelines.md` 中的可执行相机契约，明确正 X/正 Y 的拖拽方向。
3. 更新 `godot/src/Main.cs` 的 `--camera-smoke` 期望值，新增/保留右、左、下、上四个小拖动断言；Top 只验证水平自旋，编辑器模式验证相机不抢鼠标。
4. 编译并运行 camera/edit smoke；若 smoke 失败，只回退符号/断言，不改输入架构。

## Phase 2 — Pure official gradient correction

1. 在 `godot/src/FieldGrayTextureMap.cs` 添加平台局部坐标到官方表面亮度的纯函数，按 `design.md` 的归一化欧氏距离实现中心白/四角黑；保留现有像素轴映射函数。
2. 增加 `src/Sim.Tests/FieldGrayDisplayTests.cs` focused tests：中心/四角/边中点、等半径轴向/对角向、红区覆盖、旋转/轴向映射和边界 clamp。测试传感器 `FieldModel.FieldGrayLocal` 仍使用原公式，明确两种语义不互相覆盖。
3. 让 `ArenaVisualizer.MakeFieldGrayTexture` 使用 visual-only gradient 生成 RGB8，同时保持红区/“武”字、Unshaded、平台边界和场局部 transform；不修改 `src/Sim.Core/FieldModel.cs`。
4. 用纯色/单轴测试纹理和关闭 SSAO/ReflectionProbe 的临时 A/B 场景验证：如果对角亮带仍在，按设计顺序排查 PlaneMesh UV、纹理过滤、mipmap、深度偏移和三角面接缝。
5. 仅在必要时调整顶面偏移或材质采样属性；任何调整都要有 capture 证据，不能改变平台物理高度或碰撞。

## Phase 3 — Targeted visual second pass

1. 以修正后的台面为基线，针对当前截图逐项微调 `Main.tscn` 的环境/灯光/ReflectionProbe 和 `ArenaVisualizer.cs` 的材质/程序化分件。
2. 优先解决中心过曝感、平台边缘缺少层次、机器人图元可读性和能量块/接触阴影分离；不引入第三方资产，不启用重型 GI、glow/bloom 或复杂全屏 shader。
3. 在默认概览、俯视、跟随、左键拖动后的镜头和运行中机器人状态下 capture；确认 HUD 仍位于独立 CanvasLayer 且关键内容不被遮挡。
4. 记录每项后处理/抗锯齿 A/B 的画质与帧时间。超过约 30% 退化、出现拖影、闪烁或污染灰度时默认关闭并在 README 记录配方。

## Phase 4 — Full regression and evidence

按项目现有入口执行；若本机 `godot` 不在 PATH，使用已安装的 Godot Mono 4.7.2 可执行文件替换 `$godot`，不要新增另一套启动脚本：

```powershell
dotnet test .\src\Sim.Tests\Sim.Tests.csproj --no-restore
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/rotated-seed42.json
$godot = 'C:\path\to\Godot_v4.7.2-stable_mono_win64_console.exe'
& $godot --headless --path godot -- --camera-smoke
& $godot --headless --path godot -- --edit-smoke
& $godot --headless --path godot -- --parity-check ..\replays\godot-parity-seed42.json
```

真实窗口 capture 使用 `--capture <temp-output.png>`、`--capture-frames` 和 `--camera-cycle` 的既有参数，在 1280×720/1920×1080 与运动状态下留存证据；具体窗口启动方式以 `godot/README.md`/`godot/src/Main.cs` 当前定义为准。dummy renderer 只可用于流程 smoke，不可用于视觉结论。

完成后更新 `godot/README.md`：说明相机拖拽正负方向、传感器灰度与官方显示渐变的分离、四角黑/中心白的依据、默认关闭的高成本效果和 capture 验收方法。若视觉规范中的“显示直接来自 FieldGrayLocal”表述不再准确，同步更新 `.trellis/spec/frontend` 或相关视觉契约。

## Definition of done

- 相机四方向、Top/Follow/Overview、右键平移、滚轮缩放和编辑器 pointer ownership 均有通过证据。
- 台面显示与规则图纸一致：中心白、四角黑、平滑径向渐变，无白色对角亮带/斜缝；传感器灰度和旧回放逐位不变。
- 双分辨率真实 renderer capture、运动检查、HUD 可读性和性能对照齐全。
- primitive fallback、可选 glTF、法线修复、坏模型回退、团队/登台指示回归通过；无第三方资产。
- `dotnet test`、replay-check、parity、camera-smoke、edit-smoke 和 `trellis-check` 通过。
- 任务完成后再执行 `trellis-finish-work`；规划阶段不要 `task.py start`。

## Risk points and rollback

- `MatchCamera.cs`：符号与 smoke 期望必须成对修改；失败时只回退方向修正。
- `FieldGrayTextureMap.cs`/`ArenaVisualizer.cs`：显示函数与传感器函数边界最容易被误合并；失败时保留核心模型，回退 visual-only 调用。
- `Main.tscn`：SSAO/反射/AA 可能引入新的斜向伪影或性能退化；按 A/B 逐项关闭，不用整体调亮掩盖问题。
- 当前工作树包含其他任务的用户变更；禁止 `git reset --hard`、全量格式化、删除临时以外文件或清理无关未跟踪文件。
