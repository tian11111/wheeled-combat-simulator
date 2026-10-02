# 验收报告：MBri 比赛逻辑移植为内置可选 FSM

- 任务：`.trellis/tasks/10-01-mbri-fsm-port`（`10-01-mbri-fsm-port`）
- 分支：`test/score-block-ppo-checkpoint-round`；日期：2026-10-01
- 移植深度：批1（换算/标定/巡台）+ 批2（掉台回归/仲裁）+ 批3（选择接线/对照）；
  P2 hunt/probe 按 PRD Open Decision 留后续批次
- 纪律：全程未 `git add`/`commit`（主会话按批提交）；真车源码只读参考
  `D:/project/robocup/2026/MBri`，不入库

## 1. 结论

1. **R1/R2/R3 落地，默认路径逐位不变**：`MbriFsm` 成为场景可选内置控制器
   （`vehicles[].controller = "builtin" | "mbri"`）+ 桌面设置页三档；省略字段的既有
   路径位对位不变（replay-check seed-42 PASS、官方场景 vs 缺省副本逐行一致）。
2. **R4 对照数值条件达成**：官方 legacy 场景 11 seed，mbri 掉台中位数 **1** vs
   内置 FSM **43**（镜像双份）/ **39.5**（同场配对，双向角色互换）。
3. **但能力层结论不利且已如实入证**：mbri 每车只在开场上台一次，掉台后不再回台、
   得分 0——修复前修订版还叠加 SAFE_STOP 冻结吸收态；评审修复消除了吸收态
   （全场事件 19 → ~350/场），但"回台/巡台/得分"仍未证实。**不得据此宣称 MBri
   控制器可作比赛策略**；建议以"掉台后无法回台"为新 finding 另批修复并重跑对照。
4. **门禁全绿**（脚本终验）：全量测试 651 通过 / 1 跳过 / 652 总计、exit=0；
   seed-42 replay-check `4:49 / 752 事件` PASS；Godot 构建 0 错误。
5. **评审**：7 条发现，修复 2 条（high：预热窗误接管+SAFE_STOP 吸收态；medium：
   分派被方块/对手决定 + 矫正分支不可达），修复带 6 条单测
   （`src/Sim.Tests/MbriReviewFixTests.cs`）。

## 2. 交付清单

| 类别 | 文件 |
|---|---|
| 换算层 | `src/Sim.Core/MbriUnits.cs`（WheelToMs / DifferentialToVW(trackWidth=0.229) / SecondsToTicks） |
| 灰度标定层 | `src/Sim.Core/MbriGrayCalibration.cs`（SimToAdc / SimToAdcWhite / SimSampleToAdc / SimSampleToAdcFallDomain；Names/EdgeReference/CenterReference/WhiteReference） |
| 风险模型 | `src/Sim.Core/MbriRiskModel.cs`（中值滤波 + zone/white 双层） |
| 巡台/回归/仲裁 | `src/Sim.Core/MbriPatrol.cs`、`src/Sim.Core/MbriReentry.cs`、`src/Sim.Core/MbriFsm.cs` |
| 选择接线（批3） | `src/Sim.Protocol/Profiles.cs`、`src/Sim.Core/MatchEngine.cs`、`src/Sim.Cli/MatchRunner.cs`、`src/Sim.Cli/Program.cs`、`godot/src/{DesktopSettings,SettingsPanel,ControllerWiring,DesktopLiveDriver,HudPanel,Main}.cs`、`docs/CLI.md` |
| 单测（82 用例） | `src/Sim.Tests/MbriUnitsTests.cs`(10)、`MbriGrayCalibrationTests.cs`(8)、`MbriPatrolTests.cs`(19)、`MbriReentryTests.cs`(19)、`MbriReentrySmokeTests.cs`(1)、`MbriFsmTests.cs`(8)、`MbriReviewFixTests.cs`(6)、`MbriSelectionTests.cs`(11) |
| 证据 | `.trellis/tasks/10-01-mbri-fsm-port/evidence/comparison.md`（44 场逐 seed 表 + 中位数 + 冻结根因 + 修复后复测 §8） |
| 临时产物（不入库，`tmp/` 已忽略） | `tmp/mbri-comparison/`（4 场景副本、44 场修复前日志、postfix-* 修复后日志、summary/postfix-summary JSON、diag 逐 tick、脚本） |

## 3. 验收标准逐条（prd.md Acceptance Criteria，:76-83）

| # | 标准 | 判定 | 证据 |
|---|---|---|---|
| 1 | MbriFsm 可被场景/设置选中，既有 FSM 默认路径逐位不变 | ✅ | 场景字段+桌面档位（`Profiles.cs:224-245,320-328`；`DesktopSettings.cs:16,287-312`）；`MbriSelectionTests.ExplicitBuiltin_IsBitIdentical_ToOmittedController`；`replay-check replays/seed-42.json` PASS（scores 4:49 events 752/752）；官方场景 vs `mirror-builtin` 副本 20 s 输出 `diff --strip-trailing-cr` 一致 |
| 2 | 换算层/灰度标定层纯函数单测全绿（端点+单调+确定性） | ✅ | `dotnet test --filter "FullyQualifiedName~Mbri"` → **84/84 通过**（含 Units 10 + GrayCalibration 8 及 Theory 展开）；全量套件覆盖见 §4.3（脚本） |
| 3 | 巡台+掉台回归状态机迁移矩阵单测全绿 | ✅ | 同上过滤组（Patrol 19 + Reentry 19 + Smoke 1 + Fsm 8 + ReviewFix 6 + Selection 11）84/84 通过；全量套件覆盖见 §4.3（脚本） |
| 4 | 官方场景 11-seed 扫描：mbri 掉台中位数显著低于内置 FSM（目标：无"上台后掉台死循环"），结果入证据 | ⚠️ 数值达成、目标未证实 | 中位 1 vs 43/39.5（§4.1）；但 mbri 掉台后无法回台、得分 0（修复前为冻结）——"无死循环"仅以不巡台方式成立，见 §4.2 与残余 R1 |
| 5 | 全量 dotnet test + seed-42 replay-check + Godot parity 逐位不变 | ✅ | 全量套件 exit=0（651 通过/1 跳过/652）；replay-check PASS；Godot 构建 0 错误；Godot parity 由 `CrossEndTests.Parity_VerifyCliRecordedBaseline_PassesBitForBit`（fixture `godot-parity-seed42.json`）在全量套件内覆盖 |

## 4. 关键量化事实

### 4.1 行为对照（官方 `scenarios/wushu-ring-2026.json`，legacy，120 s；修复前修订版）

跑法：4 份场景副本 × 11 seed（`1..10,42`）= 44 场
`dotnet Sim.Cli.dll match --seed <S> --scenario <cfg> --events --stats`，
另以 `batch` 交叉核对比分（44/44 一致）。完整逐 seed（含上台）见
`evidence/comparison.md` §3；此处摘掉台与比分：

| seed | mirror-builtin 掉台 us/them | mirror-mbri 掉台 | head-us-mbri us=mbri / them=builtin | head-them-mbri us / them=mbri |
|---|---|---|---|---|
| 1 | 50/40 | 1/1 | 1/7 | 50/1 |
| 2 | 50/7 | 1/1 | 1/6 | 50/1 |
| 3 | 56/7 | 1/1 | 1/45 | 56/1 |
| 4 | 50/9 | 1/1 | 1/7 | 50/1 |
| 5 | 46/7 | 1/1 | 1/34 | 46/1 |
| 6 | 46/7 | 1/1 | 1/4 | 46/1 |
| 7 | 49/4 | 1/1 | 1/5 | 48/1 |
| 8 | 50/3 | 1/1 | 1/26 | 50/1 |
| 9 | 47/30 | 1/1 | 1/20 | 5/1 |
| 10 | 50/4 | 1/1 | 1/18 | 50/1 |
| 42 | 55/3 | 1/1 | 1/6 | 50/1 |

| seed | mirror-builtin 比分 | mirror-mbri 比分 | head-us-mbri 比分（mbri:builtin） | head-them-mbri 比分（builtin:mbri） |
|---|---|---|---|---|
| 1 | 5:12 | 0:1 | 0:10 | 4:0 |
| 2 | 4:40 | 0:1 | 0:12 | 4:0 |
| 3 | 4:49 | 0:1 | 0:5 | 4:0 |
| 4 | 5:26 | 0:1 | 0:10 | 4:0 |
| 5 | 3:31 | 0:1 | 0:11 | 4:0 |
| 6 | 4:42 | 0:1 | 0:7 | 4:0 |
| 7 | 4:38 | 0:1 | 0:14 | 4:0 |
| 8 | 3:51 | 0:1 | 0:6 | 4:0 |
| 9 | 24:5 | 0:1 | 0:7 | 11:0 |
| 10 | 4:43 | 0:1 | 0:15 | 4:0 |
| 42 | 4:49 | 0:1 | 0:13 | 4:0 |

中位数（每场每角色 = 1 观测）：内置 FSM 掉台中位 43（均值 30.45，3–56）/ mbri 1；
同场配对内置 39.5（4–56）/ mbri 1。上台次数与掉台同数（每次跌落前均有一次上台）。
不利事实：mbri 镜像份 11/11 seed 完全相同（1 上台 1 掉台、比分 0:1），
每场仅 19 条事件（内置 470–811），最后非 End 事件 t≈5 s——冻结诊断见
`evidence/comparison.md` §4（`tmp/mbri-comparison/diag-mirror-mbri-seed42.txt`）。

### 4.2 评审修复后复测（当前修订版，2026-10-01 21:2x 本人执行）

命令与 §4.1 相同，scenario 用 `tmp/mbri-comparison/mirror-mbri.json` /
`head-us-mbri.json`，日志 `tmp/mbri-comparison/postfix-*/`：

| 配置 | 掉台 us/them | 上台 us/them | 比分 us:them | 每场事件 | 最后非 End 事件 |
|---|---|---|---|---|---|
| mirror-mbri（双方 mbri） | 1/1（11/11 seed） | 1/1 | 0:0 | 347–352 | t≈118.8–119.9 s |
| head-us-mbri（us=mbri） | 1 / 5–45（对手 builtin） | 1 / 6–45 | 0:4…0:15 全负 | 305–501 | ~119 s |

结论修订：SAFE_STOP 吸收态已消除（事件 19→~350/场，全场持续决策），但每车掉台后
再未回到台面、无读秒/推块得分——**"低掉台"由"早期冻结"变为"掉台后无法回台"**，
巡台能力仍未证实（`evidence/comparison.md` §8）。

### 4.3 门禁（脚本终验，消息提供；本人未重复执行）

| 门禁 | 结果 |
|---|---|
| 全量 `dotnet test` | exit=0；**651 通过**，1 跳过（`MujocoScoreEdgeGuardTests.OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops`），652 总计，1 m 24 s |
| `replay-check replays/seed-42.json` | exit=0；`scores 4:49 (expected 4:49) events 752/752` + `PASS: replay reproduces the recorded match bit-for-bit.` |
| Godot 构建 | exit=0，0 错误 |

本工作流自验（本人执行）：`dotnet build` 三处 0 警 0 错；针对性 `dotnet test`
过滤器 55/55、129/129、98/98、`MbriSelectionTests` 11/11、本轮
`--filter "FullyQualifiedName~Mbri"` **84/84**；44 场对照 + 4 条 batch
（match vs batch 44/44 一致，`--stats` 掉台 vs 裁判 Drop 事件 43/44，1 例为
双方同帧 SimultaneousDrop 归并口径差）；Godot `--settings-smoke` UI 构建冒烟通过；
修复后 22 场复测（§4.2）。门禁曾报 `TrainingResetPerformanceTests.HotResetP95`
计时失败，隔离/并行复跑 7/7 通过（ratio 0.031–0.133，门限 0.5），非本任务改动回归。

## 5. 与设计的偏差与披露（design.md §5）

1. **TrackWidth 核对（设计风险项）**：真车实测 **0.229 m** 落地
   （`src/Sim.Core/MbriUnits.cs:14-17`；来源 `src/Sim.Mujoco/MujocoModel.cs:96-97`
   轮心 y 实测与 `scenarios/wushu-ring-2026-mujoco-v2.json`），旧桥猜测 0.18 弃用，
   偏差 21.4% < 30% 停止阈值。差速公式按标定表经验对齐用 `w=(r−l)/(2·TrackWidth)·k`
   （`MbriUnits.cs:9-13`，135° 定点 ≈0.96 s vs 表值 1.0 s）。
2. **灰度标定层是"结构忠实、数值近似"**：`SimToAdc` 逐通道仿射使真车 zone 公式恒等于
   g/1000、阈值原样生效；真车非线性/噪声不建模；仿真灰度上限 1000 ⇒ ADC 上限
   = center_ref < white_enter，`white_hits`/`WHITE_ESCAPE` 结构性不可达（默认手绘场）；
   掉台判定另走 fall-domain 映射（走道 g<150 → ADC 0，`MbriGrayCalibration.cs:70-97`）。
3. **传感器桥接差异**（`MbriFsm.cs` 头注释逐项披露）：六路数字红外中四路对角在仿真
   是 target 模式（只探机器人/方块，不探墙），评审修复后不再进入分派；模拟红外对以
   `max(f, 对角)` 桥接使偏差矫正分支可执行；铲子守卫为恒 IDLE no-op（仿真无此机构）。
4. **评审修复改变批2初版行为**（已落地，`MbriReviewFixTests.cs:6-14`）：
   finding 1（high）= START_REVERSE 预热窗只喂滤波不推进状态机 + SAFE_STOP/IR_WAIT
   在仍掉台 2 s 后有界重新武装；finding 2（medium）= 分派只认墙感 + 模拟红外对
   由 f 保证信号、对角不对称量驱动 diff。
5. **提交纪律**：未 git add/commit；新文件为未跟踪状态，主会话按批提交。

## 6. 残余与建议

1. **能力缺口（最重要）**：修复后 22 场复测显示 mbri 掉台后不再回台、全场 0 分；
   建议以"掉台后回台/巡台"为新 finding 另立批次修复（reentry REVERSE 终止条件、
   回台成功判定），修复后重跑 11-seed 对照再决定默认 FSM 是否切换（PRD 独立决策）。
2. **对照口径**：`evidence/comparison.md` §3 表格为修复前修订版；§8 为修复后复测
   （逐 seed 表在 `tmp/mbri-comparison/postfix-summary-*.json`），未回写 §3 以免混淆。
3. **P2 未移植**：hunt/probe 视觉追击与铲子红外（no-op 披露）留后续批次。
4. **组合未验证**：mbri + MuJoCo 后端（本批接线不跑倾覆门控）；R4 三方对照中的 RL
   未参与（无 checkpoint/入口输入）。
5. **评审其余 5 条发现**：任务目录未留评审报告文件，仅有已修复 2 条的记录
   （`MbriReviewFixTests.cs` 文档注释）；其余状态未知、未修复项未记录在此。
6. **既有显示怪癖**：`Js.ToFixed(digits>0)` ×10（`src/Sim.Core/Js.cs:37-52`，
   旧日志可复现）；为保基线逐位不变未修，事件表 t 值需 ÷10 读秒。
7. **Godot 目检**：设置页仅构建冒烟 + 静态截图（`godot/tmp/mbri-comparison/settings-controller.png`），
   下拉展开项未做输入注入逐项目检。

## 附：证据索引

- 对照证据：`.trellis/tasks/10-01-mbri-fsm-port/evidence/comparison.md`（§3 逐 seed、
  §4 冻结诊断、§5 交叉核对、§7 建议、§8 修复后复测）
- 临时产物：`tmp/mbri-comparison/`（不入库）：`summary.json`、`postfix-summary-*.json`、
  `diag-mirror-mbri-seed42.txt`、`logs/`、`postfix-*/`、`run-comparison.py`、`postfix-scan.py`
- 关键代码：`src/Sim.Core/MbriFsm.cs`、`MbriReentry.cs`、`MbriGrayCalibration.cs`、
  `MbriUnits.cs`；`src/Sim.Core/MatchEngine.cs:135-136,687-692`；
  `src/Sim.Protocol/Profiles.cs:224-245`；`godot/src/DesktopSettings.cs:287-312`
