# 车端日志字段盘点与缺口报告

> 阶段 1 产物(R3):拿到真实日志样本后逐项填写。**结论只有三种:够(可归一化)/
> 缺(需最小采集辅助)/ 不适用**;每个"缺"必须列出受影响参数与建议补采方式。
> 禁止从不相关传感器数据推断物理参数。

## 样本信息

| 项 | 值 |
|---|---|
| 日志来源(程序/模块) | 待填 |
| 日志格式(CSV/JSON/二进制) | 待填 |
| 采样频率 | 待填 |
| 时间源(单调时钟/UTC/相对) | 待填 |
| 样本文件 SHA-256 | 待填 |

## 字段 → telemetry-v1 映射总表

| telemetry-v1 字段 | 车端日志字段 | 单位换算 | 符号/坐标系约定 | 结论 |
|---|---|---|---|---|
| `frames[].t` | 待盘点 | | 严格递增要求 | 待定 |
| `frames[].robot.x/y` | 待盘点 | | 场地坐标系与 Sim.Protocol 一致性 | 待定 |
| `frames[].robot.th` | 待盘点 | | 弧度/零向约定 | 待定 |
| `frames[].command.v/w` | 待盘点 | | 前向为正 | 待定 |
| `block_push.block.x/y` | 待盘点 | | | 待定 |
| `collision.normal` / 撞前撞后速度 | 待盘点 | | 法向分量 >0.05 且反向才有效 | 待定 |
| `stall.stalled` 标签 | 待盘点(或人工标注) | | | 待定 |
| `mount.vn/vt/outcome` | 待盘点(或人工测速) | | atan(vt/vn) 分桶 | 待定 |

## 缺口结论(待填)

| 试验类型 | 结论 | 受影响参数 | 建议补采方式(路径②) |
|---|---|---|---|
| lateral_coast | 待定 | latFrictionK | |
| angular_coast | 待定 | angDamping | |
| block_push | 待定 | BLOCK_MU_K | |
| collision | 待定 | COLLISION_RESTITUTION | |
| stall | 待定 | STALL_SPEED | |
| mount | 待定 | MOUNT_V_MIN / MOUNT_ANGLE_MAX(验证) | |
