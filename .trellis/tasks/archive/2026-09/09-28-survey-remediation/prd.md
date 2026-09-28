# 体检确认项修复（10 条）

## Goal

落地「优化机会体检」确认的全部 10 条发现（四区域并行体检 → 分诊 10 条 → 独立确认 10/10 通过），分三批提交。来源：`.trellis/tasks/archive/2026-09/09-28-rl-v5-edge-risk` 之后的仓库体检工作流（确认记录含逐条代码证据）。

## Requirements

- R1 **rank 1（缺陷）**：`controllers/mbri_adapter.py:202` 视觉坐标读大写 `X`/`Y`，线上为 camelCase `x`/`y` → 改两行 + `mbri_adapter_selftest.py` 夹具改小写 + 删除 `:176-177` 不可达 smoke 分支；`mbri_adapter_selftest.py` 必须全绿。
- R2 **rank 3（缺陷）**：`evaluate.py` 的 `--freeze requires --select-candidate` 守卫从 sweep 之后（:634）上移到前置校验块（:481-496）；验证：错误在秒级返回且不写任何文件。
- R3 **rank 4（缺陷）**：`RobotModelLoader.Apply` 换路径加载失败的 return 前补 `ShowPrimitive(robotRoot, true)`，闭环"任一失败即回退 primitive"契约。
- R4 **rank 5（缺陷）**：回放播放从"每渲染帧 +1 tick、插值 +0.02"改为固定步长累加时钟（复用 StepLive 的 accumulator 模式），累加器数学放进纯类 `MatchSession` 并新增单测（delta=X 秒 → 前进 round(X/tickSeconds) tick；插值增量按 delta 折算）；保留 step 后 alpha 重置语义。
- R5 **rank 6**：`requirements.txt` 补 `psutil==7.2.2`。
- R6 **rank 7（性能）**：登台指示环材质缓存（`_ringOn/_ringOff` 一次性创建，ApplyRobot 只切换 MaterialOverride 指向），消除每秒约 120 次材质分配；队伍呼吸灯语义不变。
- R7 **rank 8（卫生）**：删除 `nul`（Windows 保留名，需特殊语法）与 `tmp38sgy5pc/`（失败则留档）；`.gitignore` 补 `.zcode/`、`.zcodeignore`、`.dsh/`。诱因不追（预注册决策）。
- R8 **rank 9**：`godot/robot-models.json` untrack（`git rm --cached` + `.gitignore`），本机文件保留；README 示例配置已存在不重复。
- R9 **rank 10（性能）**：`RlEnvCommand.JsonOptions()` 改 static readonly 单例（不改序列化选项本身，输出字节不变）。
- R10 **rank 2（回退）**：移除归因门控——Step() 恢复 v2 判据、删除 `EdgeShapingApplies`/`TargetContacts` 及其测试、删除死字段 `PrevEdgeDistance`（确认零读取）及其赋值；reward 回到 v2 语义。

## Acceptance Criteria

- [ ] `mbri_adapter_selftest.py` 全绿（夹具已改小写）。
- [ ] `evaluate.py --freeze` 缺 `--select-candidate` 时秒级拒绝、不产出文件。
- [ ] Sim.Tests 全绿（含回放累加器新测试；归因门控测试随 R10 移除）；`replay-check` 逐位 PASS。
- [ ] `git diff --check` 干净；`requirements.txt` 含 psutil。
- [ ] `nul`/`tmp38sgy5pc` 已删除或留档说明；`.gitignore` 含三个新条目且 `git status` 不再显示 `.zcode/`/`.dsh/`。
- [ ] `robot-models.json` 从索引移除、本机保留；回放播放按固定步长推进（单测断言）。
- [ ] 每批一个提交（批 1 修复 / 批 2 回退 / 批 3 回放时钟），完成后推送一次。
