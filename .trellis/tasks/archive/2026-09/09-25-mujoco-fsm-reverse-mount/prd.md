# MuJoCo 内置 FSM 倒车登台机动

> 2026-09-25:本任务由本工作区 agent 接手(原派发的外部隔离工作树由用户负责停用,
> 其种子 PRD 的 Goal 文本原样沿用)。根因分析与方案见 [design.md](./design.md)。

## Goal

仅修复 MuJoCo 模式内置 FSM 从官方出生点倒车登台;旧模式与裁判回放合同保持,
参数仍为未标定工程值。

## Requirements

- R1:MuJoCo 模式下,内置 FSM 从官方出生点 (0.95, 0.3) 发令后能在比赛时间内完成
  倒车登台(四角全上台,进入 SEARCH),无穿透、无位置跳跃。
- R2:旧二维物理路径零改动——全部 legacy FSM/物理/回放测试逐位不变
  (MountGateParameterTests、MatchEngineTests.Arm_MountsPlatform、Restart/TransformedField、
  旧回放 replay-check)。
- R3:模型内可调项限于未标定工程值(轮驱动力上限、底盘离地),不改轮半径/整车质量/
  holdout 门槛/标定算法;模型哈希随 MJCF 内容变化,旧 mujoco 回放被身份校验拒绝属预期。
- R4:新模式确定性保持——batch 并行度 1/4 逐行一致;回放可同机复现。

## Acceptance Criteria

- [ ] `NativeMode_MountsTheSixCentimetreStageContinuously` 强化:直接驱动下四角全上台(FullOn),仍无 >0.15 m 跳跃。
- [ ] 新增 `NativeMode_FsmMountsFromOfficialSpawn`:官方出生点 Arm 后 ≤600 ticks FullOn 并进入 SEARCH(发 Mount 事件);若纯物理修复即达标,FSM 零改动。
- [ ] 全套 `dotnet test` 通过且 legacy 行为逐位不变;旧回放全量 replay-check PASS。
- [ ] 新模式 batch p1=p4 逐行一致;重录回放经真实 Godot `--parity-check` PASS;32×5s p32 32/32 completed。
- [ ] 修复报告(根因/推导/前后对比)落任务目录;ARCHITECTURE.md 中轮驱动力上限值同步。
