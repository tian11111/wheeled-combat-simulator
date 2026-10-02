# 验收报告：MBri 巡台行为重校——hunt 接洽与回台链修复（A3–A6）

- 任务：`.trellis/tasks/10-01-mbri-hunt-engagement`（2026-10-01）
- 前序：`10-01-mbri-fsm-port` 能力修复轮结论"稳而不赢"（head 11/11 全负，中位 1 vs 13）
- 代码面：`MbriPatrol.cs`（A3 常数+注释）/ `MbriReentry.cs`（A4/A4'/A5/A5b）/
  `MbriFsm.cs`（A6 + 头注释同步）/ 测试 4 文件适配 + 1 新增钉板用例
- 门禁：全量 `dotnet test` **696 通过 / 1 跳过 / 697，exit=0**；`replay-check seed-42`
  **4:49 / 752 事件 PASS**（默认路径逐位不变）；Godot 构建 0 错
- 纪律：未 git add/commit（主会话按批提交）；诊断产物 `tmp/mbri-hunt/` 不入库

## 1. 结论

1. **根因一（巡台）证实并修复**：真车场标定的 early-front 前路线（0.76，对应官方场探点
   距沿 0.46m）在官方场陡线性渐变上把整段 RECOVER（0.72m）划进"提前避边"区，1 帧确认
   形成沿带极限循环，CRUISE/MEDIUM_CRUISE 不可达 → hunt 门禁（main.py:42 逐字）永不打开。
   A3 重推为 0.35（官方场 danger 边界，与 near-edge 同线，↔0.24m）。修复后 mirror-mbri
   **11/11 场 hunt 接洽**（GOOD_PUSH latch 84 次，基线轮仅个位场次）。
2. **根因二（回台）证实并修复**：A2 固定倒车冲台在官方场被"f 对齐台沿→倒离"反转成走道
   死螺旋（seed42 实证 9 轮 REMOUNT/SAFE_STOP 到终场）；mirror 对局中对手在旁时 analog 桥
   （max(f,对角)）把对手当墙对齐，全链走道翻滚（seed1 双方 108s 不回台）。A4'/A5/A5b/A6
   修复后：mirror seed1 在台时间 0→1000+ tick；head 中位得分 **1→2**（seed8 出现 22:10）。
3. **"赢"未翻转，如实披露**：head BlockScore 0/11（推击出界前 ~1-2 帧被探点悬空打断，
   推击余量缺口）；mirror 我方得分中位 2.0→1.0（对手同步从 0→1，对局从单边变均势）。
   下批目标不变：推块得分率（需块-探点几何 instrumentation）与追击振荡（yolo-bridge 轮）。
4. **判定口径（本人亲跑 vs 转述）**：11-seed×2 对照、门禁三项、diag 逐 tick 机理证据均为
   本轮亲跑；postfix2 基线数字引自入库证据文件。A3 前的极限循环判读基于事件日志
   （head-us-mbri seed42，12/12 EDGE_AVOID 理由一致），未做修复前后同位姿 zone 采样对照，
   如实标注为机制+行为代理证据。

## 2. 关键事实表（详见 evidence/comparison-hunt.md）

| 面 | 基线（A2 轮） | 本轮（A3–A6） |
|---|---|---|
| head us:them 中位 | 1:13 | **2:13**（seed8 22:10） |
| head GOOD_PUSH 场次 | 个位（seed2 等） | 4/11（12 latch） |
| head mbri BlockScore | 1/11（seed2） | **0/11** |
| mirror us:them 中位 | 2:0 | 1:1 |
| mirror GOOD_PUSH 场次 | 稀少 | **11/11（84 latch）** |
| mirror 掉台中位 | 2 | 2 |
| 门禁 | 695/1skip、PASS | **696/1skip、PASS** |

## 3. 交付与测试

| 类别 | 文件 |
|---|---|
| A3 | `MbriPatrol.EarlyFrontAbs` 0.76→0.35（独立常数，与 ShovelPreheat 解耦披露） |
| A4/A4'/A5/A5b | `MbriReentry`：RemountCommand 方向裁决 + _reverseAlignedToFront + 分派灰度粗定向 + IR_WAIT 扫描 |
| A6 | `MbriFsm.ReadAnalogIr` Valid=f 亮 |
| 测试 | `MbriPatrolTests`（新增 A3 钉板：恢复中途 mid-front 不再被打断→释放巡航）；`MbriReentryTests`（REMOUNT 前向语义 3 处）；`MbriReentrySmokeTests`（重写为官方场自主回台全链：ADC_CORRECT→REVERSE→REMOUNT→回 WAIT→巡台交还，3 seed）；`MbriReviewFixTests`（IR_WAIT 重臂→转 90° 扫描）；`MbriHuntTests`（接线冒烟块位按开局朝向重摆：目标块放朝向正前方距沿 0.25m） |
| 证据 | `evidence/comparison-hunt.md`（逐 seed 表 + 机理 diag + 残余） |
| 临时（不入库） | `tmp/mbri-hunt/`（22 场日志、summary JSON、diag 工程+CSV） |

## 4. 残余与建议（下批输入）

1. **推击出界余量**（head 0/11 BlockScore 主因）：推击中探点悬空先于块出界 ~1-2 帧触发
   回归。需专项 instrumentation（块位-探点位逐 tick）；动 legacy14 探点 = 动冻结基线，禁。
2. **追击振荡**：BIG_TURN/ARC 在特权投影 8fps 帧时钟下左右摆动，收敛慢（63 tick 接洽 →
   327 tick 才 GOOD_PUSH）。yolo-bridge 保真轮一并处理（真实检测节奏）。
3. **角部落水**：REVERSE 3s 满超时（2.4m）超出 REMOUNT 预算可达范围；A5b 已有界化但仍
   可能整场不回台（head seed42 仍 0 分）。
4. REMOUNT 显式"回台成功"出口 22 场 0 触发（回台经 REVERSE 灰度恢复分流达成）——与
   postfix 轮残余同源。
5. MuJoCo 域未按 A1 流程校准（既有披露，本轮未触碰）；probe ±13.5° 锥、特权视觉→
   yolo-bridge、REMOUNT 出口诊断维持另立。

## 5. 默认切换决策包的影响

切换建议维持**不切换**且理由更新：本轮 mbri"稳"未退（mirror 掉台 2 持平）、接洽能力恢复，
但同场对 builtin 仍全负（2 vs 13）且 mirror 总分下降——"整体更强"依旧不成立；重录基线
成本不变。待推块得分率迭代达标后复评。
