# 设计：MBri 比赛逻辑移植内置可选 FSM

> 侦察依据见 prd.md Confirmed Baseline；真车源码只读参考
> `D:/project/robocup/2026/MBri`（不入库）。

## 1. 架构：加法可选控制器

- 新增 `src/Sim.Core/MbriFsm.cs`：与 `Fsm.cs` 平级的内置控制器，实现既有
  控制器接口（与 Fsm 同签名风格：每 tick 输入快照/传感器，输出 RobotAction）。
- 选择接线：`ControllerModes` 加 `mbri`（协议加法）；场景 vehicles[].controller
  与桌面设置页控制器区各加一档；默认仍 `builtin`。
- 铁律：零 IO/零时钟/零随机——真车 wall-clock 时长全部换算为 tick 计数。

## 2. 移植映射表（真车 → 仿真）

| 真车概念 | 真车语义 | 仿真落地 |
|---|---|---|
| 轮速命令 | 0-1023（400≈0.358 m/s 实测锚点） | `MbriUnits.WheelToMs(unit)=unit·0.000896`；左右→v=(l+r)/2·k，w=(r−l)/(2·TrackWidth)·k |
| 灰度 | 4 路 ADC 0-10000，逐通道实测参考值 | `GrayCalibration.SimToAdc(channel, g)`：edge_ref+(g/1000)·(center_ref−edge_ref) 逐通道仿射；白边域同理 |
| GrayRiskModel | 3 点中值滤波 + zone/white 双层 | 原样移植（C# 纯函数，滑动窗口），阈值用真车原值 |
| 转向 | 开环查表 MOTOR_TURN_CALIBRATION（角度→速度+时长） | 时长→tick 数（÷0.05，四舍五入），执行期间按表内速度差速原地转 |
| 掉台判定 | 四路 zone 全<0 ×3 帧 | 同（zone 来自标定层） |
| 开局上台 | START_REVERSE 1000×1.8s 独占 | 同（tick 计数 36），期间忽略一切其他逻辑 |
| 视觉 | YOLO 8fps good/bad | P2：仿真 ObjectSet 真值投影为 hunt 输入（特权，披露）或 liveBridge 流 |
| 铲子红外 | 2 路 ADC | 仿真无此机构 → ShovelGuard 移植为恒 IDLE no-op（披露） |
| 敌人推动 | 六路数字红外+视觉仲裁 | P2 |

## 3. 状态机结构（MbriFsm 顶层仲裁）

```
START_REVERSE(独占36 tick) → [仲裁每 tick 按序]
  1 reentry 接管（fall 触发/进行中）
  2 patrol（兜底默认；巡台状态机内含 CRUISE/MEDIUM/EDGE_AVOID/EDGE_TURN/
    WHITE_ESCAPE/RECOVER_*/SENSOR_STOP/WARMUP）
  3 probe/hunt（P2，仅 patrol 处于 CRUISE 级时允许启动）
```

- 与内置 FSM 的关系：平行实现，不共享状态；`FsmState` 枚举不变——MbriFsm
  内部状态以自己的枚举/字符串承载，对外快照的 State 字段映射到既有枚举
  （SEARCH/MountRing/Recover/ScoreBlock 语义近似映射，保 HUD/事件流可用）。
- 事件流：状态迁移与理由逐条 Log（与内置 FSM 同格式），可观测性对齐。

## 4. 确定性与基线

- 移植代码不触碰 MatchEngine/物理/裁判/传感器采样——既有基线逐位不变。
- 中值滤波窗口在 reset 时清空（角色重启语义与真车 rearm 对齐）。
- 同输入逐位确定性由单测钉住（双跑快照指纹）。

## 5. 风险与披露

- TrackWidth 旧桥猜测 0.18 需按实车核对（影响 w 换算）；不符则在实现批内
  修正并披露。
- 灰度仿射标定是"结构忠实、数值近似"：真车传感器的非线性/噪声不建模
  （仿真灰度本身是确定性模型）——写进交付报告。
- 真车开环转向查表在仿真动力学下可能过转/欠转（动力学差异）——行为对照
  批如实呈现，不调参掩盖。
