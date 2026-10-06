# Design — 能量块布局可配置

## 现状与卡点

- `Scenario.Blocks` 已是 `List<BlockSpec>`（kind buff/debuff + 可空 x/y），JSON 手编任意组合本就可行。
- `MatchEngine.CreateBlocks` 按列表创建 N 块，`RespawnBlock` 支持多块布点。
- 固定"2 增益 + 1 减益"的三个泄漏点：
  1. `MatchEngine.BuildObjectSet`：debuff 只留最后一个（`debuff = View(b)` 覆盖）；
  2. `PhysicsPoses`（Snapshot.cs）：只有单 `Debuff` 字段，`MujocoPhysicsBackend.BuildPhysicsPoses` 循环覆盖；
  3. `godot/SnapshotView.From`：只渲染单个 debuff。
- 桌面布局编辑器只有移动（`LayoutDraft.MoveBlock`），无添加/删除/换类型。
- `Scenario.Validate` 不校验块数量。

## 改动

### 1. 协议（src/Sim.Protocol）

- `ObjectSet`：新增 `List<EnergyBlockView> Debuffs = []`（JSON `debuffs`，additive）。
  `Debuff` 语义改为文档明确"第一个减益块（无减益为 null）"，wire 上保留。
- `PhysicsPoses`：新增 `List<PhysicsPose3> Debuffs = []`；`Debuff` 保持 = 第一个。
- `Scenario.Validate`：`Blocks.Count > 12` → 错误 "blocks: at most 12 blocks are supported"。
  0 块合法。12 上限是性能护栏（MuJoCo qpos/contact 随块数线性增长），文档写明。

### 2. 内核（src/Sim.Core/MatchEngine.cs）

- `BuildObjectSet`：收集 `debuffs` 列表（按 `_blocks` 索引顺序），`Debuff = debuffs.FirstOrDefault()`。
- 计分循环（buff 上台 +3 / debuff 上台 +6 对手）本就按块遍历，多减益自动独立计分——不改。

### 3. MuJoCo（src/Sim.Mujoco/MujocoPhysicsBackend.cs）

- `BuildPhysicsPoses`：按块索引填 `Debuffs` 列表；`Debuff = 第一个`。

### 4. 桌面编辑器（godot/）

- `LayoutDraft`：
  - `AddBlock(BlockKind kind)`：确定性找空位——从场地中心网格 (0.25m 步长) 螺旋搜索，
    距既有块 ≥0.35m、距边 ≥0.30m，找到即放（field-local，吸附后）；找不到放中心并提示。
  - `RemoveBlock(int index)`；`ToggleBlockKind(int index)`。
  - 三者走既有 `Apply(State with {...})` 撤销栈。
- `LayoutEditor`：
  - 新输入动作（project.godot）：`editor_block_add`=B(66)、`editor_block_kind`=K(75)、
    `editor_block_remove`=Delete(4194312)；检查无键位冲突（现只占 E/Ctrl+Z/Y/[/]/S/方向键）。
    <!-- 2026-10-06 修订：原文写作 4194322，那是 KEY_DOWN 而非 KEY_DELETE(4194312)；
         该笔误使删除功能实际绑在 ↓ 上（与 ui_down 微调冲突），已修复并由
         src/Sim.Tests/GodotInputMapTests.cs 守卫。 -->
  - `_Process` 里处理三个动作：add 默认增益；kind/remove 作用于选中块，未选中给提示。
  - `SelectedLabel` 带块类型（"能量块 #2 (增益)"）；操作后 StatusLine 提示数量。
- `Main.cs` 编辑器进入提示行(:1800)追加 "B 添加块 · K 切换类型 · Del 删除块"。
- `HudPanel` 编辑器条默认提示同步。

### 5. 渲染（godot/SnapshotView.cs）

- `From()`：debuff 段改为先渲染 `Debuffs` 列表（与 `PhysicsPoses.Debuffs` 按索引配对），
  列表为空但 `Debuff` 非空时回退单块（旧快照兼容）。ArenaVisualizer 按 `frame.Blocks` 渲染，无需改。

### 6. RL（controllers/score_block_rl）不改

- 观测 JSON additive（新 `debuffs` 字段），gym_env 不读它 → 策略 obs 空间不变，不重训。

## 兼容性

- 回放门禁（比分/事件指纹/结束原因/tick 数）不含 snapshot JSON → 既有回放不受影响（已核 ParityCheck.cs）。
- 官方默认布局 2+1 → `Debuffs`=[1个]，`Debuff` 不变 → 逐位等价。
- 旧外部控制器只认 `debuff` 字段 → 继续有效（= 第一个减益块）。

## 回滚

单分支单 PR；revert 即回。无数据迁移、无 fixture 重录（预期 parity fixture 不变）。
