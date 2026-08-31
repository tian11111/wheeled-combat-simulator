# Godot 实体点选与拖拽布局编辑

## Goal

在 Godot 的布局编辑模式中，让用户可以直接点击并拖动能量块和小车，直观看到布局变化，并在应用后由共享 `Scenario` 驱动新的比赛。该能力只编辑比赛开始前的布局，不向运行中的 `Sim.Core.MatchEngine` 注入传送或物理外力。

## Confirmed Current Behavior

- `godot/src/LayoutEditor.cs` 当前可选择场地、黄色/蓝色出发区和能量块；能量块已有基于地面投影点的选择与拖动路径。
- `godot/src/LayoutDraft.cs` 已经以场局部米制保存 `Pose`、`StartZones`、车辆 `Starts` 和 `Blocks`，并提供撤销/重做、验证、保存和应用所需的纯 C# 操作。
- 拖动出发区会同步移动对应车辆的出生位姿；当前没有“直接点击小车模型并单独移动其出生位姿”的选择类型或命中路径。
- `Main.TryToggleEditor` 只允许在比赛准备阶段进入编辑器；编辑期间相机让出鼠标，应用后由 `Main` 重建 `MatchSession`。
- 场局部与仿真世界之间必须继续使用现有 `FieldTransform`，不能在 Godot 交互层复制坐标旋转公式。

## Requirements

- R1. 在布局编辑模式中，鼠标点击可命中能量块或任一方小车，并显示唯一的选中高亮与当前对象名称。
- R2. 左键拖动能量块时，复用现有 `LayoutDraft.MoveBlock` 修改其场局部固定坐标；保持网格吸附、连续拖动合并为一次撤销记录，并保留现有布局合法性校验。
- R3. 左键拖动小车时，按确认的产品语义修改对应车辆的场局部起始位姿；不改变运行中快照、不修改 `Sim.Core` 物理规则。
- R4. 选取与拖动必须使用实体命中结果，而不是只把鼠标点投射到 `y=0` 后猜测目标，确保低视角点击可命中视觉上看到的方块/小车。
- R5. 编辑器激活或拖动实体时，相机不得同时处理同一鼠标事件；退出编辑、撤销/重做、恢复官方布局、保存和应用的既有行为保持不变。
- R6. 应用后由 `Scenario.Field.Starts`/`Scenario.Blocks` 作为唯一布局来源，新的会话出生位置与编辑预览一致，旧回放与确定性契约不改变。

## Acceptance Criteria

- [ ] 布局编辑模式下，点击每个能量块都能选中对应对象，选中对象有明确高亮，点击空白处可取消选择。
- [ ] 布局编辑模式下，点击任一小车都能选中对应角色，选中标签能区分我方/对手。
- [ ] 能量块和小车均可通过左键拖动到合法场地位置；拖动过程中对象不会出现跳变、翻转或跟随相机 billboard 的现象。
- [ ] 连续拖动每个对象后，撤销/重做以一次完整拖动为单位恢复/重放，吸附开关行为与现有编辑器一致。
- [ ] `Enter` 应用后重新建立会话，能量块位置和车辆起始位姿与编辑预览一致；编辑期间不改变正在运行的权威引擎。
- [ ] `dotnet test src/Sim.Tests/Sim.Tests.csproj`、Godot `--camera-smoke`、`--edit-smoke` 和实体拖动专项 smoke 全部通过；`git diff --check` 通过。

## Out of Scope

- 比赛进行中直接拖动或瞬移车辆/能量块。
- 改动 `Sim.Core` 的物理、碰撞、计分、传感器、回放协议或随机数逻辑。
- 改变车辆朝向、能量块图案、场地几何尺寸或官方布局常量；本任务只处理点选与位置编辑。

## Key Decisions

- 车辆拖动只修改被选角色的 `Field.Starts[role]` 的 `X/Y`，保留 `Th` 朝向，出发区保持不动。
- 车辆与能量块只能在准备阶段的布局编辑模式中移动；应用后通过新 `Scenario` 重建会话，不向运行中的 `MatchEngine` 注入瞬移或外力。
- 选择实体时使用实际视觉对象的世界空间命中代理；地面投影只继续用于无实体命中后的场地/区域选择和拖动位移计算。
- 现有 `Scenario.Validate()` 的边界保持不变：只沿用有限值与场地内校验，不新增“出生点必须位于出发区”的规则。

## Risks and Deferred Items

- 外部 glTF 车辆的可见尺寸可能与碰撞半径不同；点选使用带少量容差的渲染命中代理，不能把模型节点当作物理碰撞体。
- 本任务不改变车辆朝向编辑；车辆被拖动时只更新位置，方向继续由原始 `Pose2.Th` 保持。
- 低视角下的真实鼠标点选需要专项 smoke 覆盖，避免只验证 `LayoutDraft` 数值而漏掉屏幕坐标命中偏差。

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- Lightweight tasks can remain PRD-only.
- For complex tasks, add `design.md` for technical design and `implement.md` for execution planning before `task.py start`.
