# PRD：MBri 巡台行为重校——恢复 hunt 接洽（A3 early-front 线重推）

- 任务：`10-01-mbri-hunt-engagement`（2026-10-01）
- 前序：`10-01-mbri-fsm-port`（移植 + A1 灰度重标 + A2 有界回台 + P2 视觉追击）；
  能力修复轮结论"稳而不赢"（head-us-mbri 11/11 全负，得分中位 1 vs 13）

## 1. 根因（已定位，事件日志实证）

head-us-mbri seed42（tmp/mbri-postfix2/logs）显示 mbri 巡台自开局起被锁死在沿带极限循环：

```
EDGE_AVOID(0.6s) → EDGE_TURN(1.0s) → RECOVER_FORWARD(仅 ~0.1s) → EDGE_AVOID …
每轮 EDGE_AVOID 理由全部是"前向灰度趋势变暗，提前离边"（early-front，confirm=1）
```

- main.py:42 `HUNT_ALLOWED_PATROL_STATES=("CRUISE","MEDIUM_CRUISE")` 逐字移植无误；
  hunt 对任意距离 good 可启动（hunt.py:24）。门禁永远打不开是唯一卡点。
- 机理：A1 重标后官方场 zone(d) ≈ 1.81·d − 0.09（d=探点距沿距离，线性渐变场
  `g=300+700·(1−t)`，FieldModel.cs:80-88）。真车 early-front 前路线 0.76 是真车场
  "内环安全最小 0.817 / 出界最大 0.714"的中点（config.py:110-113 注释），对应本场
  d≈0.46m——而 RECOVER_FORWARD 全程 2s×0.4m/s≈0.72m，即**整段恢复行程都在
  "提前避边"区内**，1 帧确认每轮恢复刚起步就打断。真车中点法成立前提（安全/出界
  两分布不重叠）在本场陡线性渐变下不存在。
- 推论：这不是逻辑错误，是**场地标定常数错配**，按 A1 同纪律修（常数重推、逻辑逐字不动）。

## 2. 修复（A3：单常数重推）

`MbriPatrol.EarlyFrontAbs`（PATROL_EARLY_FRONT_ABS）：0.76 → **0.35**。

- 依据：本场 danger 边界取 A1 锚下 near-edge 线 zone=0.35（↔ 探点距沿 0.24m，
  与 `NearEdgeEnter` 同源同值）；early-front 语义从"真车内环保护线"变为
  "官方场台沿危险线"（front 单通道过线 + zone_score<0.88 双确认）。
- 防掉台裕度核算：巡航 450→0.40m/s，触发于探点距沿 0.24m，1 帧确认+1 帧反应
  ≈2cm ≪ 0.24m 裕度；`near-edge`（median<0.35，3 帧）与 `release`（0.55）不动。
- 与真车 ALIAS 的偏离披露：真车 PATROL_EARLY_FRONT_ABS ≡ PATROL_SHOVEL_PREHEAT_FRONT_ZONE
  （config.py:113，双用途 0.76）。仿真铲子为披露的 no-op，`ShovelPreheatFrontZone`
  保持 0.76（真车透传），`EarlyFrontAbs` 独立为 0.35——两常数在仿真域解耦，
  头注释披露。

## 3. 验收标准

| # | 标准 | 判定口径 |
|---|---|---|
| R1 | hunt 接洽恢复：head-us-mbri 11 seed 每场 ≥1 次 hunt 进入 GOOD_ACQUIRE/GOOD_PUSH（事件流） | 11/11 |
| R2 | 同场得分脱离"恒 1"：head-us-mbri mbri 得分中位 > 1，且 ≥5/11 场 mbri 有 BlockScore/+读秒增量 | 逐 seed 表 |
| R3 | 稳定性不回退：mirror-mbri 掉台中位 ≤ 4（基线 2）；mirror-mbri 我方得分中位 ≥ 基线 1.0 | 中位数表 |
| R4 | 默认路径逐位不变：builtin 路径零改动；`replay-check replays/seed-42.json` PASS；全量 dotnet test exit=0；Godot 构建 0 错 | 门禁 |

## 6. 方法与产物（执行记录）

1. 常数落地 + 注释链（MbriPatrol.cs / MbriGrayCalibration.cs 头注释同步）。
2. 单测：`OfficialField_ThresholdGrays` 改钉新常数（0.35 ↔ 0.24m 语义）；
   craft 注释更新；行为钉板用例（early 触发/不触发边界）适配。
3. 行为 sweep（如 0.35 不达标）：EarlyFrontAbs ∈ {0.30, 0.45} 备选，11-seed 快扫后定值。
4. 对照复测：head-us-mbri + mirror-mbri × 11 seed（复用 evidence/repro 场景副本），
   逐 seed 掉台/上台/比分/hunt 事件计数 → `evidence/comparison-hunt.md`；
   mirror-builtin 引用 postfix2 基线（builtin 未触碰）。
5. MuJoCo smoke：mirror-mbri + head-us-mbri seed42 各 1 场（不崩溃 + 行为摘注；
   MuJoCo 灰度域未校准为既有披露，不在本轮放大）。

## 3.5 范围修订（执行中依据实证追加，2026-10-01）

R2 对照执行发现"稳而不赢"的第二层根因在回台链，追加四项（全部在 mbri 仿真补全/桥接层，
与 §2 同纪律，逐项披露见代码头注释与 evidence/comparison-hunt.md §1）：

- **A4/A4'** REMOUNT 方向裁决：A2 固定倒车在官方场形成走道死螺旋（seed42 实证 9 轮
  重试到终场）——f=edge_target 只见台沿不见围栏，矫正把台沿当墙对齐后"倒车冲台"背台。
  A4' 决定性依据：REVERSE 必然来自 f 对齐 ⇒ 倒车后台在正后方，原路前向冲回。
- **A5** 分派灰度粗定向 + **A5b** IR_WAIT 重臂转 90° 扫描：红外全暗/朝向背离时
  官方场实证滞留（IR_WAIT 1757 tick/场），f 2.2m 束扫描 ≤4 周期必捕台沿。
- **A6** 模拟红外对 Valid=f 亮：mirror 对局中对手在旁 → 对角(target 模式)驱动矫正把
  对手当墙（mirror seed1 实证双方 108s 不回台）；f 是唯一墙感，门在 f 上。

## 4. 验收结果对账（终版，evidence/comparison-hunt.md §2.3）

R1 mirror 11/11 ✓（head 4/11 部分）；R2 中位 2>1 ✓ 但 BlockScore 0/11 ✗（推击余量缺口）；
R3 ✓（含 mirror 得分 2.0→1.0 退步的如实披露）；R4 全绿。**总体判定：行为重校方向性达成
（接洽与回台能力恢复），"赢"未翻转——推击余量/追击振荡列为下一轮。**

## 5. 范围外（本轮不做，如实披露）

- probe 正前 ±13.5° 锥不可达（传感器模型级，另立）。
- A2 REMOUNT 显式出口 0 触发的诊断（残余 R2，另立）。
- 特权视觉 → yolo-bridge 检测流替换（保真对照轮，另立；依赖视觉链路已就绪）。
- MuJoCo 场地灰度域按 A1 流程校准（既有局限，不因本轮恶化即视为通过）。
