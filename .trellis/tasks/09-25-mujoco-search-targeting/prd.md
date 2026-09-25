# MuJoCo 模式 SEARCH 索敌闭环

## Goal

修复 MuJoCo 模式下内置 FSM 登台成功后 SEARCH 索敌不收敛的问题:让 SEARCH 能
稳定锁定并接近目标,进入 ATTACK/推块流程,使新模式全场对抗产生真实比分。
不改变 legacy 模式行为(legacy 同 seed 比分 4:49,链路在旧模式是通的)。

## 发现证据(2026-09-25,登台修复任务 09-25-mujoco-fsm-reverse-mount/report.md)

- 双方车 ~3.2 s 登台进入 SEARCH 后,事件流反复循环:
  "对角红外发现目标[增益块] (4.0m, 左后)" → 3 s 后"目标丢失 → 继续扫描" → 再发现,
  从未进入 ATTACK/推块;60 s 全场与 32 场 batch 全部 0:0。
- 疑点①:报告的目标距离 4.0 m 超出对角红外 1.6 m 量程——疑似感知距离单位、
  目标选择来源(perception ObjectSet 真值 vs 传感器)或坐标变换不一致。
- 疑点②:即使锁定,车辆在 MuJoCo 模式的运动(惯性/打滑/转向速率)可能导致
  3 s 目标锁超时前无法收敛到攻击条件(legacy 无惯性,转向瞬时)。

## Requirements

- TBD(盘点 SEARCH 实现与感知链路后固化;先诊断,后修)。

## Acceptance Criteria

- [ ] TBD:新模式全场(官方场景、内置 FSM)产生非零比分;legacy 逐位不变;
  全套测试与 parity 通过。

## Notes

- 诊断入口:`match --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json --events`
  的 SEARCH 事件循环;对照 legacy 同 seed 事件流。
- 登台修复本身见归档任务 `09-25-mujoco-fsm-reverse-mount`;本任务与登台验收任务
  `09-25-mujoco-reverse-mount-acceptance` 互不阻塞。
