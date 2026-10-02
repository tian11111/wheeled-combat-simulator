# MBri 比赛逻辑移植内置可选 FSM

## Goal

把真车比赛决策代码（`D:/project/robocup/2026/MBri`，2026-08 系列实测标定）的
巡台/掉台回归/开局上台/仲裁结构移植为仿真内核的**内置可选控制器**（MbriFsm），
让仿真机器人具备真车同源的"台沿意识"，治理内置 FSM 的掉台死循环；既有
FSM、协议、回放基线零破坏。

## Confirmed Baseline

- 真车代码全景（2026-10-01 侦察）：`main.py`（550 行，优先级仲裁）+
  `ring_patrol.py`（400，灰度闭环巡台）+ `reentry.py`（276，掉台回归）+
  `hunt.py`（479，视觉追击）+ `config.py`（实测标定参数）+ `gray.py`
  （GrayRiskModel zone/white 双层风险模型）+ `shovel_guard.py`。
- 单位换算锚点（config 注释自带实测）：`PATROL_RECOVER_STEP_CM=21.5` 注明
  "400×0.6s 实测" ⇒ 车端轮速 400 ≈ 0.358 m/s（k=0.000896 m/s/unit）；
  `START_REVERSE_SPEED=1000`≈0.896 m/s（用户所称"速度拉满冲上台"）。
- 仿真侧现状：`Fsm.cs` 1142 行、9 状态、24 参数；传感器为 FieldGrayLocal
  0-1000（官方语义）+ 合成数字红外；控制器选择 plumbing 已有（builtin/external）。
- 09-25 先例（archive）：真车代码曾作为外部控制器接入（oracle smoke 0:5 落败），
  量纲映射表在 `docs/CONTROLLER_PROTOCOL.md`；本次是**原生移植**，不是外接。
- 内置 FSM 已知缺陷（本次动机）：SEARCH 无台沿意识 → 上台后掉台死循环
  （seed-42 legacy 基线 115 次掉台）；RECOVER 计数回台即重置。

## Requirements

### R1. 加法不替换（兼容性硬门禁）

- 新增 `Sim.Core/MbriFsm.cs`（可选内置控制器），**既有 `Fsm.cs` 保持默认**；
  `replays/seed-42.json`、Godot parity、11-seed 扫描等全部既有基线逐位不变。
- 控制器选择走既有 plumbing 加法扩展（builtin 默认 | mbri | external），
  协议字段只加不改。
- 验证达标后是否把默认切到 MbriFsm 是**独立决策**（另立任务或本任务收尾选项），
  切换即重录全部基线。

### R2. 单位换算层（可单测纯函数）

- 轮速：车端单位（0-1023）→ m/s，k=0.000896（锚点见上）；左右差速 →
  v=(l+r)/2·k，w=(r−l)/(2·TrackWidth)·k；TrackWidth 取真车实测（config/实车
  尺寸核对，旧桥猜测值 0.18 需复核）。
- 灰度：仿真 FieldGrayLocal(0-1000) → 真车 ADC 域逐通道仿射
  （edge_ref + (g/1000)·(center_ref−edge_ref)，白边域同理）——真车 GrayRiskModel
  阈值原样生效；标定层为纯函数并附端点/单调性单测。
- 时间：真车 wall-clock 时长 → tick 计数（×TickSeconds 0.05），开环转向查表
  （MOTOR_TURN_CALIBRATION 角度→速度+时长）按时长×tick 数执行——确定性保持
  （无时钟、无随机）。

### R3. 行为移植（按真车仲裁优先级）

- P0：开局后退上台（START_REVERSE 1000×1.8s 独占）→ 巡台（RingPatrol 全状态机：
  zone 巡航分级/EDGE_AVOID/EDGE_TURN 查表/WHITE_ESCAPE/RECOVER 双向/确认帧
  防抖/对角风险）——治理掉台死循环的核心。
- P1：掉台回归（Reentry：fall 判定 all-zone<0×3 帧 → 数字红外分派查表转向 →
  大力前冲贴墙 → 倒车回台）+ 仲裁骨架（unhealthy→reentry 接管语义映射到
  仿真传感器失效语义）。
- P2：视觉追击（hunt：仿真 ObjectSet 真值=特权观测，或 liveBridge 检测流；
  good 追击/bad 避让/推敌）+ 铲子守卫占位（仿真无铲子红外 → no-op 并披露）。
- 全部移植代码零 IO、零时钟、零随机（铁律 1）；状态/理由进事件流可观测。

### R4. 验证与对照

- 单测：换算层端点/单调性、巡台状态机迁移矩阵、reentry 迁移矩阵、
  确定性（同输入逐位）。
- 行为对照：同场景同 seed 三方对比（内置 FSM vs MbriFsm vs RL）——
  掉台次数、上台成功率、得分；MbriFsm 掉台应显著低于内置 FSM。
- 全量回归：既有基线逐位不变（R1）。

## Out of Scope

- 切换默认 FSM / 重录 legacy 基线（独立决策）。
- 真车运动映射的真机再标定（k/b 已用 config 实测锚点，真机复核另做）。
- 铲子物理（仿真无此机构）。
- MBri 仓库入库（只读参考，路径约定同 09-25）。

## Acceptance Criteria

- [ ] MbriFsm 作为可选控制器可被场景/设置选中，既有 FSM 默认路径逐位不变。
- [ ] 换算层/灰度标定层纯函数单测全绿（端点+单调+确定性）。
- [ ] 巡台+掉台回归状态机迁移矩阵单测全绿。
- [ ] 行为对照：官方场景 11-seed 扫描，MbriFsm 掉台次数中位数显著低于内置 FSM
      （目标：无"上台后掉台死循环"），结果入任务证据。
- [ ] 全量 dotnet test + seed-42 replay-check + Godot parity 逐位不变。

## Open Decision

- 移植深度：P0+P1 先行（本任务核心），hunt/probe(P2) 是否入本任务还是后续批次，
  视 P0/P1 验证结果定。
