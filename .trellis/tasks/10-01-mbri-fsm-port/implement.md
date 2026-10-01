# 实施清单：MBri 比赛逻辑移植

## 批1：换算层 + 巡台（P0 核心）

- [ ] `src/Sim.Core/MbriUnits.cs`：WheelToMs/差速→v/w/时间→tick 换算（纯函数）+ 端点单测
- [ ] `src/Sim.Core/MbriGrayCalibration.cs`：仿真灰度→真车 ADC 域逐通道仿射 + 端点/单调单测
- [ ] `src/Sim.Core/MbriRiskModel.cs`：GrayRiskModel 移植（中值滤波/zone/white/near_edge 滞回）
- [ ] `src/Sim.Core/MbriPatrol.cs`：RingPatrol 全状态机移植（含确认帧/对角风险/查表转向）
- [ ] `src/Sim.Core/MbriFsm.cs` 骨架：仲裁入口 + START_REVERSE + patrol 接线 + 快照状态映射
- [ ] 迁移矩阵单测（巡台全状态）+ 双跑确定性单测
- 验证：`dotnet test` 全绿；seed-42 replay-check 逐位不变

## 批2：掉台回归 + 仲裁（P1）

- [ ] `src/Sim.Core/MbriReentry.cs`：Reentry 状态机移植（fall×3 → 红外分派查表转向 → 冲墙 → 倒车）
- [ ] 仲裁链：unhealthy→reentry 接管、patrol 兜底、事件流接线
- [ ] 迁移矩阵单测 + 行为冒烟（官方场景 3-seed：掉台后能回台）
- 验证：同批1

## 批3：选择接线 + 行为对照

- [ ] ControllerModes 加 `mbri`（协议加法）；场景/桌面设置可选；默认 builtin
- [ ] 11-seed 行为对照扫描：内置 FSM vs MbriFsm（掉台次数/上台/得分）→ evidence/
- [ ] Godot 目检（设置页选择 + 对局）
- [ ] 全量回归 + replay-check + parity
- 验证：R4 验收标准

## 批4（可选，P2）：hunt/probe 视觉追击移植

- [ ] 视觉输入源决策（真值特权 vs liveBridge）后另批实施

## 回滚点

- 批1-2：纯加法新文件，回退=删文件
- 批3：ControllerModes 加法（协议只加不改），回退=还原枚举与接线
- 全程不触碰 Fsm.cs/MatchEngine/物理/裁判/既有基线

## Stop Conditions

- 灰度仿射标定无法让巡台状态机在仿真灰度上稳定工作（zone 全程饱和/失真）
  → 停，改走"仿真灰度几何直读 zone"方案重新设计（仍不调内核）
- TrackWidth 实测核对与旧桥差异 >30% → 停下向用户披露后再选值
