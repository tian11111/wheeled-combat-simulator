# 证据：hunt 接洽行为重校（A3–A6）11-seed 对照

- 日期：2026-10-01；分支 `test/score-block-ppo-checkpoint-round`；诊断/日志 `tmp/mbri-hunt/`（不入库）
- 场景副本：`evidence/repro/{head-us-mbri,mirror-mbri}.json`（与 postfix 轮同一批）；
  跑法 `match --seed S --scenario <cfg> --events --stats`，11 seed = 1..10,42
- 基线：`10-01-mbri-fsm-port/evidence/comparison-postfix.md`（A2 能力修复轮，33 场）

## 1. 修复清单（全在 mbri 桥接/标定层，builtin 与默认路径零触碰）

| 项 | 内容 | 位置 |
|---|---|---|
| A3 | early-front 前路线 0.76→0.35（官方场 danger 边界，与 near-edge 同线；治"恢复刚起步即被打断"的沿带极限循环） | `MbriPatrol.EarlyFrontAbs` |
| A4/A4' | REMOUNT 方向裁决：f 对齐 ⇒ 原路前向冲台（A4' 决定性）；f 前亮/前灰度最亮 ⇒ 前向；rear 最亮 ⇒ 倒车（真车尾先登台）；侧向/全暗 ⇒ 倒车兜底 | `MbriReentry.RemountCommand` |
| A5 | 掉台分派 IR 全暗时以 fall-domain 灰度 argmax 四向粗定向（前→矫正/后→180/侧→90） | `MbriReentry.StartFromTrigger` |
| A5b | IR_WAIT 重臂超时 → 原地转 90° 搜索台沿（f 2.2m 束扫描，≤4 周期必捕），替代静默重臂 | `MbriReentry` IR_WAIT 分支 |
| A6 | 模拟红外对 Valid=f 亮（对角 target 模式不得在 f 暗时驱动矫正；治 mirror 对局把对手当墙对齐） | `MbriFsm.ReadAnalogIr` |

## 2. 11-seed 对照（本轮终版 = A3+A4'+A5+A5b+A6）

### 2.1 head-us-mbri（us=mbri vs them=builtin）

| seed | 比分 us:them | us_falls | GOOD_PUSH latch |
|---|---|---|---|
| 1 | 2:9 | 2 | 0 |
| 2 | 2:17 | 3 | 0 |
| 3 | 0:5 | 1 | 1 |
| 4 | 0:19 | 4 | 0 |
| 5 | 1:13 | 1 | 0 |
| 6 | 3:17 | 6 | 1 |
| 7 | 1:15 | 3 | 0 |
| 8 | **22:10** | 3 | 9 |
| 9 | 2:7 | 2 | 1 |
| 10 | 2:10 | 6 | 0 |
| 42 | 0:14 | 3 | 0 |
| **中位** | **2:13**（基线 1:13） | 3 | — |

### 2.2 mirror-mbri（双方 mbri）

| seed | 比分 us:them | us_falls | GOOD_PUSH latch |
|---|---|---|---|
| 1 | 6:0 | 6 | 4 |
| 2 | 0:3 | 2 | 5 |
| 3 | 3:2 | 2 | 3 |
| 4 | 0:2 | 2 | 2 |
| 5 | 1:1 | 1 | 2 |
| 6 | 1:0 | 1 | 1 |
| 7 | 1:0 | 1 | 1 |
| 8 | 0:7 | 1 | 1 |
| 9 | 4:0 | 4 | 1 |
| 10 | 1:4 | 1 | 1 |
| 42 | 4:0 | 2 | 4 |
| **中位** | **1:1**（基线 2:0） | 2（基线 2） | **11/11 场有 latch** |

### 2.3 验收口径对账（prd.md §3）

| # | 标准 | 结果 |
|---|---|---|
| R1 | hunt 接洽恢复 | **mirror 11/11 场 GOOD_PUSH latch（84 次）**；head 4/11 场（12 次，seed8 独占 9）。判定：mirror 全达、head 部分达 |
| R2 | 得分脱离恒 1：head mbri 中位 >1 且 ≥5/11 场有 BlockScore | 中位 2>1 ✓；BlockScore **0/11** ✗（mirror 3/11）——推击余量缺口（§3） |
| R3 | 稳定性：mirror 掉台中位 ≤4、我方得分 ≥基线 1.0 | 2≤4 ✓；1.0≥1.0 ✓（但基线 2.0→1.0 为退步，如实记） |
| R4 | 默认路径逐位不变 | 全量 696 通过/1 跳过 exit=0；`replay-check seed-42` 4:49/752 PASS；Godot 构建 0 错 ✓ |

## 3. 机理层证据（diag 逐 tick，`tmp/mbri-hunt/diag*.csv`）

- **A3 前**（postfix2 日志）：`EDGE_AVOID(0.6s)→EDGE_TURN(1.0s)→RECOVER_FORWARD(~0.1s)` 极限循环，
  每轮 EDGE_AVOID 理由全部"前向灰度趋势变暗"（12/12），CRUISE 状态 0 次。
- **A3 后**：RECOVER 跑满 71/56 tick、出现 MEDIUM_CRUISE/CRUISE；mirror 11/11 场 hunt 接洽。
- **A4'/A6 的必要性**（mirror seed1 逐 tick）：掉台时对手在旁 → analog 桥（max(f,对角)）把对手
  当墙"正对确认" → REVERSE/A4' 全链沿错误方向走道翻滚 108s（A6 前）；A6 后同场在台时间
  0→1000+ tick（RECOVER 571 + MEDIUM_CRUISE 161）。
- **官方 seed42**：9 轮 REMOUNT/SAFE_STOP 死螺旋（A2 固定倒车）→ A5b 后 IR_WAIT 1757→360、
  恢复尝试全程有界活跃（REMOUNT/冲台/扫描），仍未回台得分（角部落水几何，残余）。

## 4. 残余（下一轮输入，按优先级）

1. **推击出界余量**（head BlockScore 0/11 的直接原因）：GOOD_PUSH 推击中 legacy14 探点
   悬空触发回归早于块出界 ~1-2 帧（接线冒烟构造两次实测：块心距沿 ~0.10m 时车探点已全暗）。
   需块-探点几何的专项 instrumentation；动 legacy14 探点 = 动冻结基线，不可取。
2. **追击振荡**：tracker BIG_TURN/ARC 在特权投影帧时钟（8fps→3 tick）下左右摆动，收敛慢
  （接线场景 63 tick 接洽 → 327 tick 才 GOOD_PUSH）；yolo-bridge 保真轮应一并处理。
3. **角部落水几何**：REVERSE 3s 满超时（2.4m）可超出 REMOUNT 3×1.8s 预算的可达范围，
   且倒离后方向信息丢失——A5b 扫描已有界化但仍可能整场不回台（head seed42）。
4. REMOUNT 显式"回台成功"出口 22 场 0 触发（回台实际经 REVERSE 灰度恢复分流达成）——
   与 postfix 轮残余 R2 同源，未恶化。
