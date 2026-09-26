# Godot 实体点选与拖拽布局编辑实施计划

## Implementation order

1. **基线与边界确认**
   - 确认当前任务为 `planning`，实现前由用户单独批准 `task.py start`。
   - 保留工作区已有的视觉修复、遥测任务和旧视觉任务改动，不重置或覆盖。
   - 再次确认 `LayoutEditor`、`LayoutDraft`、`Main` 的当前行号与现有
     `--edit-smoke` 断言，避免在已有拖动逻辑上重复造轮子。
   - **基线刷新 (2026-08-30)**：本任务创建后工作区已合入一轮视觉任务与二轮
     校正（分支 `feat/godot-3d-visual`，测试基线 319/319）。实现需注意：
     1) 相机拖拽方向契约已反转（camera-smoke 42 项含四方向断言），编辑器
        自身的拖动不经过相机，不受影响，但不得"顺手"改动相机符号；
     2) 能量块节点现在带有 `Edges`/`ContactShadow` 渲染子节点——实体命中
        代理以 `_previewFrame.Blocks` 解析坐标，不遍历视觉子节点，故不受
        影响，但 smoke 拖动能量块时断言的是 draft 数值而非视觉子节点；
     3) 布局编辑器进入时的块坐标是 spawn-resolved（`ScenarioWithResolvedBlocks`），
        与 `--edit-smoke` 现有断言一致。

2. **纯布局模型扩展**
   - 在 `godot/src/LayoutDraft.cs` 增加按角色移动起始位置的方法。
   - 只改变 `Pose2.X/Y`，保留 `Pose2.Th`，不触碰对应 `StartZones`。
   - 通过现有 `Apply`/`BeginGroup`/`EndGroup` 接入撤销重做和拖动分组。
   - 在 `src/Sim.Tests/LayoutDraftTests.cs` 增加：位置变化、朝向保留、
     出发区不变、undo/redo、`BuildScenario` 输出验证。

3. **实体命中模型**
   - 在 `godot/src/LayoutEditor.cs` 扩展机器人选择状态，并定义 typed hit
     result（实体类型、角色/块索引、射线距离）。
   - 用 `_previewFrame` 的世界位置和 `FieldTransform` 生成能量块 AABB 与
     车辆容差代理，使用相机射线求交；不要新增 Godot 物理碰撞体。
   - 按最近射线命中选择实体；没有实体命中时保留出发区/场地地面投影
     fallback。
   - 暴露仅供 smoke 使用的屏幕点选入口，确保测试走与鼠标相同的 picker。

4. **选中显示与拖动接线**
   - 为我方/对手小车补充选中标签和高亮，切换选择时清理旧块索引。
   - 在 `NudgeSelected` 中接入车辆 `MoveStart`，保持现有 block/zone/field
     分支不变。
   - 左键按下先实体命中，成功后复用现有地面投影计算拖动增量；释放时只
     结束一次 group。
   - 保持编辑器激活时相机指针禁用、事件 handled、吸附与无跳变行为。

5. **端到端 smoke**
   - 扩展 `godot/src/Main.cs` 的 `--edit-smoke`：从屏幕投影点选择我方或
     对手小车，拖动并断言只改对应 `Starts` 的 X/Y；再选择能量块拖动。
   - 断言车辆 yaw 和 start zone 不变，撤销/重做按整次拖动工作，Apply 后
     新 `MatchSession` 的出生位置与预览一致。
   - 增加低视角或实体中心屏幕点覆盖，防止回归到“只投射 y=0 猜目标”。

6. **文档与可操作性**
   - 更新 `godot/README.md` 的布局编辑操作说明，明确 `E`、实体点选、
     左键拖动、吸附、Enter 应用，以及只能编辑起始布局。
   - 不把渲染偏好、命中代理或编辑状态写入 `Scenario`/回放协议。

7. **质量门禁**
   - 运行 `dotnet build godot/GodotSim.csproj --no-restore -m:1
     -p:UseSharedCompilation=false --verbosity minimal`。
   - 运行 `dotnet test src/Sim.Tests/Sim.Tests.csproj --no-restore -m:1
     -p:UseSharedCompilation=false --verbosity minimal`（当前基线 319/319，
     本任务新增 LayoutDraft/命中测试后应 ≥ 319 且全绿）。
   - 运行 Godot `--headless --path godot -- --camera-smoke`（42 项）、`--edit-smoke`
     （含本任务新增的实体点选拖动断言）。
   - 运行 `dotnet run --project src/Sim.Cli -- replay-check
     replays/seed-42.json`，确认布局编辑未改动回放语义。
   - 如交互高亮有视觉变化，使用 `--rendering-method gl_compatibility` 在
     1280×720 与 1920×1080 各做一次真实渲染 capture；截图只放临时目录。
   - 最后运行 `git diff --check`，检查无 debug 输出、无临时场景、无误改
     的既有任务文件。

## Risk and rollback points

- 命中代理误判：回滚 picker 接线，保留现有地面选择路径；不引入物理层
  碰撞体。
- 车辆拖动影响出发区：以 `LayoutDraft.MoveStart` 的独立变更和测试锁定
  语义，回滚只撤销模型/编辑器新增分支。
- Apply 后坐标不一致：优先回滚 `Main` 的 smoke/接线变更，保留纯模型测试
  以便定位 `FieldTransform` 或 preview 数据流问题。
- 所有回滚都使用补丁或恢复本任务新增行，不使用 `git reset --hard`，不
  覆盖工作区已有用户改动。

## Completion gate

Only after the user approves the final planning summary should the task be
started and product code changed. The implementation is complete only when all
acceptance criteria in `prd.md` and the quality commands above pass.
