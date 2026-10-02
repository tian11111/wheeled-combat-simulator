# 能力修复轮验收报告（A1 灰度重标 + A2 有界回台 + P2 视觉追击移植）

- 任务：`.trellis/tasks/10-01-mbri-fsm-port`（能力修复轮，2026-10-02）
- 前序：`report.md`（批1–3 移植验收，结论"掉台后无法回台、得分 0"）；本报告覆盖其 §6-1
  残余项（能力缺口）的修复验收
- 修复内容：A1 灰度重标（`MbriGrayCalibration.cs:14-45`，治 early-fire）+ A2 有界回台
  （`MbriReentry.cs` REMOUNT/重新武装， finding 1 后续）+ P2 视觉追击/近物推敌移植
  （新增 `MbriHunt.cs` 825 行 / `MbriProbe.cs` 611 行，特权观测双披露）+ reentry/仲裁修复
  （`MbriFsm.cs` 595 行 / `MbriReentry.cs` 588 行修订）
- 纪律：全程未 `git add`/`commit`；replays/seed-42.json 与 legacy fixtures 逐位未动
  （本轮亲跑 `replay-check` PASS，见 §1）

## 1. 终验门禁（主会话脚本执行，消息提供；本人未重复执行全量）

| 门禁 | 结果 |
|---|---|
| 全量 `dotnet test` | **exit=0；失败 0，通过 695，跳过 1（`MujocoScoreEdgeGuardTests.OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops`，1 ms），总计 696**，1 m 18 s（上一轮 651→695，净增 44 用例：MbriHunt/MbriProbe/重标语义等） |
| `replay-check replays/seed-42.json` | **exit=0；`scores 4:49 (expected 4:49) events 752/752` + `PASS: replay reproduces the recorded match bit-for-bit.`** |
| Godot 构建 | **exit=0，0 个错误**，00:00:00.93 |

跳过项说明：该守卫断言官方 seed42"吃真 buff 且我方不反复掉台"，属官方基线行为守卫；
按本轮硬约束（不重录基线、不动 builtin 行为）保持跳过，其恢复条件列入 §4 决策包步骤 5。

本人本轮亲跑的针对性验证（均在本工作流内执行，非转述）：
`replay-check replays/seed-42.json`（PASS 逐位）；33 场三方对照 + 3 条 batch（match vs batch
33/33）；mirror-mbri seed42 与 seed7 各双跑 `cmp` 逐字节（CLI/MuJoCo 确定性）；MuJoCo 5 场；
Godot 3 帧抓取判读；`dotnet build src/Sim.Cli/Sim.Cli.csproj` 与 `godot/GodotSim.csproj`
各 0 警 0 错。

## 2. 修复前后对照（官方 `scenarios/wushu-ring-2026.json`，legacy，11 seed）

数据源：`evidence/comparison.md`（修复前 §3 / 第一轮修复后 §8）→
`evidence/comparison-postfix.md`（本轮，33 场亲跑）；原始日志 `tmp/mbri-postfix2/`（不入库）。

### 2.1 early-fire 是否消除 → **已消除（机制+行为双证）**

- **缺陷机理（修复前）**：批1 把 zone anchor 钉成 g/1000（宣称"仿真灰度 0=台沿、1000=台心"），
  而官方场台面灰度只有 300（台沿黑带）→ 825（内环最亮带）、台心红区平顶 650
  （`FieldModel.cs:80-88`）→ 巡台 zone 恒 ≤0.825，低于真车 early-front 阈值（0.88/0.76）
  → **几乎全程 EDGE_AVOID、MEDIUM_CRUISE 不可达**（`MbriGrayCalibration.cs:14-19` 头注释逐字）。
- **修复**：对官方场 FieldGrayLocal 经真实 SensorSampler（legacy14 四路探点 ±0.11 m、
  光斑半径 0.025 m、灰度噪声 ±30、种子 42）离线采样，把 anchor 0/1 端点重锚到官方场
  实测台沿/台心灰度：E = front 329.5 / rear 328.5 / left 329.0 / right 327.5，
  C = front 650.9 / rear 650.4 / left 652.3 / right 652.1（`MbriGrayCalibration.cs:31-33,71,96`，
  采样件 `tmp/mbri-recal/` 不入库）。
- **重标后语义**（`MbriGrayCalibration.cs:36-40`，单测钉住
  `MbriGrayCalibrationTests.OfficialField_ZoneSemantics`）：台心红区 ≈1.0、内环最亮带 ≈1.54、
  走道 ≈−1.03（掉台判定可达）、early-front 0.76/0.88 ↔ 前探点距台沿 0.46/0.55 m、
  FAST_ZONE 1.10 ↔ 内环带（可达，CRUISE 恢复）。
- **行为证据（本轮亲跑）**：巡台分级巡航恢复——mirror-mbri seed2 日志出现
  `MEDIUM_CRUISE: 按区域分级巡航` 与 `已达到避边释放线，恢复中速穿越渐变区`
  （t=303/341/1020，修复前不可达）；EDGE_AVOID 退到"近沿触发"的设计位置；
  掉台中位 43→2（§2.2）。**未做** early-fire 的专项逐 tick 对照实验（如台心 zone 值
  修复前后采样对比），"消除"判读基于上述机制证据+行为代理，如实标注。

### 2.2 回台成功率（掉台后物理再上台 = mounts−1，口径 `MatchRunner.cs:44-93`）

| 配置 | 侧 | 掉台总数 | 回台成功总数 | 回台率 |
|---|---|---|---|---|
| mirror-mbri | us | 25 | 15 | **60.0%** |
| mirror-mbri | them | 31 | 21 | **67.7%** |
| head-us-mbri | us (mbri) | 30 | 19 | **63.3%** |
| head-us-mbri | them (builtin) | 146 | 140 | 95.9% |

- 修复前（`report.md` §4.2）：每车 1 上台 1 掉台后**不再回台（0%）**；本轮 mbri 侧
  回台率 60–68%，"掉台即终局"已解决。
- 未回台残例（如 mirror-mbri seed1/3/5/6/8 的单次不回台）集中在追击推块后随块下台
  且剩余时间/位置不利时；A2 REMOUNT 显式"回台成功"出口（`MbriReentry.cs:555`）33 场
  0 次触发——回台实际经 REVERSE 结束灰度恢复分流（`MbriReentry.cs:347-363`）与
  IR_WAIT `!Fall` 出口（`MbriReentry.cs:428-429`）达成：**回台能力成立，但不是经
  设计主打的那条显式路径**（残余 R2）。

### 2.3 得分能力

| 面 | 修复前（comparison.md §3/§8） | 修复后（本轮 33 场亲跑） |
|---|---|---|
| mirror-mbri 比分 | 0:1（冻结期）/ 0:0（第一轮修复后） | **我方中位 2.0（0–12）、对手 0.0（0–8）；11 场全部至少一方 >0（我方 10/11、对手 5/11）** |
| head-us-mbri（mbri vs builtin） | 0:4…0:15 全负，我方恒 0 | 我方中位 **1**（10/11 场恰为 1，seed6=0），对手中位 **13**（9–17），**11 场全负** |
| 得分构成 | 无 | 掉台罚分、登台读秒（`ScoreClock`）、**增益块推下 `BlockScore +3`**（seed2 t=502，hunt GOOD_PUSH 实际推块） |
| 消极比赛罚分 | — | faults/penalties 全 0 |

**判读**：得分能力从"结构性为零"恢复到"非零、链路完整"（巡台→追击→推块→读秒全链
有得分事件），但同场对 builtin 仍 11/11 全负（中位 1 vs 13）——**推块/读秒收益显著弱于
builtin**（详见 §4 决策包）。

### 2.4 中位数总表（每场每角色 1 观测）

| 数据面 | 控制器 | n | 掉台中位 | 回台成功中位 | 得分中位 |
|---|---|---|---|---|---|
| mirror 双份 | builtin | 22 | 43（逐位=修复前基线） | 42 | 8.5 |
| mirror 双份 | mbri | 22 | **2**（1–7） | 1 | **1.0** |
| head 同场 | builtin(对手) | 11 | 11 | 10 | 13 |
| head 同场 | mbri(我方) | 11 | **2** | 1 | **1** |

确定性：mirror-builtin 11/11 seed 逐位等于修复前基线（builtin 路径未被本轮触碰）；
mirror-mbri seed42 双跑 `cmp` 逐字节一致；`--stats` vs 裁判 Drop 差全部 ≤1
（SimultaneousDrop 归并口径）。

## 3. mbri+MuJoCo 验证与 Godot 目检（本轮亲跑，摘自 comparison-postfix.md §6–§7）

**MuJoCo（PASS-不崩溃）**：`wushu-ring-2026-mujoco-v2.json` + controller=mbri 共 5 场
（mirror-mbri seed 1/7/42、head-us-mbri seed42、mirror-builtin seed42 参照），全部 exit=0、
事件流完整（builtin 参照场 tick 2017 规则性"恢复次数超限→停车"，非崩溃）；mirror-mbri
seed7 双跑 `cmp` 逐字节。差异如实记录：①传感器档 wheeledCombat11 后向红外恒 0；②台沿
物理不同——seed42 双方约 5 s 巡台下台（EDGE_AVOID 在 MuJoCo 台沿几何下不总防得住）；
③回台能力 seed 相关：seed1 物理回台 5 次（比分 8:0），seed42/7 掉台后 120 s 零物理回台
（走道 IR_WAIT 循环）；④head-us-mbri seed42 我方 1:12（mbri 卡走道致 hunt 门禁未达成，
builtin SCORE_BLOCK×14）；⑤A1 重标按 legacy 采样链路校准，MuJoCo 场地灰度域未单独校准。

**Godot（PASS）**：真实 exe + `--scenario-path`(mirror-mbri 副本) + `--auto-arm` +
`--capture` + `--capture-frames`，抓 60/300/900 帧（`godot/docs/qa-10-02/`）三帧均亲自
Read 判读：HUD 绿字"控制器 我方 内置 MBri / 对手 内置 MBri"正确；开局后退上台→双车
登台环→[RECOVER] 避边巡台与事件流一致，无异常。设置隔离：原设置文件（external RL 档）
stash 后逐字节还原，QA 日志已删。

## 4. 默认切换决策包（独立章节）

### 4.1 建议：**不切换（此刻）**——门槛字面达标，但"整体更强"不成立

- 达标项：掉台更低 ✓（mirror 中位 2 vs 43；同场 2 vs 11）、得分非零 ✓（mirror 我方中位
  2.0，11 场 10 场我方有分）。
- 反对项：**同场对 builtin 11/11 全负（得分中位 1 vs 13）**——mbri 稳而不赢，推块/读秒
  收益显著弱于 builtin；mbri 内战总得分亦远低于 builtin 内战（中位 1.0/侧 vs 8.5/侧）；
  回台率 60–68% vs builtin 95.9%。
- 结论：当前证据支持"mbri 更稳、能得分"，**不支持"mbri 整体优于 builtin"**。切换默认
  需重录全部基线（不可逆、断证据链），收益不成比例。**先迭代同场得分能力
  （推块得分率、对手掉台时我方保持在台率），复跑本对照达标后再评估**。
- **执行切换需用户确认**（PRD 独立决策；本工作流未执行下列任何一步）。

### 4.2 切换执行步骤清单（届时使用）

前置确认：切换语义 = 引擎级默认（场景省略 `vehicles[].controller` 时由 builtin → mbri），
涉及 `MatchEngine` 构造缺省与 `Profiles` 校验默认；**不是**仅改桌面默认档（后者只影响
桌面未显式选择的场景，不动 CLI 基线）。

1. **重录 legacy fixtures**：`node tools/legacy-baseline.js [旧原型 wushu_ring_sim.html 路径]`
   （产出写 `src/Sim.Tests/fixtures/`；现状 `legacy-fsm-seed21.json` / `legacy-pushoff-seed42.json` /
   `legacy-referee-seed7.json` 仅生成器引用、无测试直接读取——已搜 `src/Sim.Tests/*.cs` 确认）。
   **风险**：旧原型无 mbri 控制器，legacy 基线语义是"原型 builtin 行为"，**不可重录出
   mbri 行为**——切默认后凡依赖"省略字段=builtin"的测试必须显式钉
   `controller: "builtin"`，否则对照语义错位。
2. **重录 replays/seed-42.json**：`dotnet src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll replay-record
   --seed 42 --out replays/seed-42.json`（官方场景，预期值由 4:49 变为 mbri 默认行为新值；
   记录后同步更新 spec/文档中所有 4:49 引用）。
3. **重录 parity 基线**：`replay-record --seed 42 --out replays/godot-parity-seed42.json`
   后复制为 `src/Sim.Tests/fixtures/godot-parity-seed42.json`（`CrossEndTests.cs:13-17` 的
   既有流程），并重录 `src/Sim.Tests/fixtures/restart-replay-seed42.json`
   （`RestartRobotTests.cs:19-20` 消费）。
4. **11-seed 重扫**：`batch --seeds 1,2,3,4,5,6,7,8,9,10,42` × 官方场景 + 本轮 3 对照配置
   （repro/ 副本），中位数对比表回写 evidence 并与 comparison-postfix.md 基线对齐。
5. **恢复守卫测试**：取消跳过 `MujocoScoreEdgeGuardTests.OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops`
   （本轮全量套件唯一跳过项），按新基线验收。
6. **桌面默认档**：`godot/src/DesktopSettings.cs` 默认 `ControllerModes` 改 mbri
   （含设置页三档默认值与 HUD 来源显示验证）。
7. **spec 回写**：`.trellis/spec/sim/index.md` Mbri 条目与选择契约默认值、
   `docs/CLI.md` 场景字段默认说明。

### 4.3 切换风险（决策输入）

- **RL 训练线分布漂移（最重）**：rl-env 基线对照步 `engine.Tick(null, null)` 与策略步的
  对手侧都按引擎默认控制器驱动（`RlEnvCommand.cs:315-317`；`scenarios/wushu-ring-2026-mujoco.json`
  无 vehicles 字段 = 省略默认）——切默认后 **RL 训练/评测的对手从 builtin 变为 mbri**，
  已训练 PPO checkpoint 的对手分布失配，需重训或重评。
- **证据链断裂**：`4:49`/`752 events` 及历次对照引用（comparison.md、report.md、spec）
  全部失配；不可逆（除非保留双基线）。
- **行为面**：mbri 得分弱（1 vs 13），切默认会降低官方演示/桌面对局的可看性与对抗强度；
  MuJoCo 下 mbri seed 相关的走道滞留（120 s 零回台）会进入默认路径。
- **守卫面**：官方 seed42 基线守卫（跳过中的 score-edge guard）与 restart/parity 基线
  须同步重录，否则门禁红。

## 5. 评审与修复

- 本轮评审：**4 条发现，无 high/medium**（消息提供；任务目录未留评审文件，逐条内容
  未在本次输入中给出，如实记录不展开）。
- 前序评审遗留：批3 评审 2 条（high 预热窗误接管+SAFE_STOP 吸收态；medium 分派被
  方块/对手决定+矫正分支不可达）已于上轮修复（`report.md` §5-4、`MbriReviewFixTests.cs`）。

## 6. 残余与后续

1. **同场得分能力**（本轮最重要缺口）：head-us-mbri 11/11 全负（1 vs 13）——推块得分率、
   对手掉台时我方保持在台率是下批迭代目标。
2. **A2 REMOUNT 显式出口 0 触发**：回台经由 REVERSE 结束灰度恢复分流达成，REMOUNT
   冲台分支未记录一次成功——路径冗余或灰度恢复判定过严，值得诊断。
3. **MuJoCo 场地灰度域未按 A1 流程校准**；MuJoCo 回台 seed 相关（seed42/7 走道滞留 120 s）。
4. **RL 三方未参与**（R4 口径同前，无 checkpoint 输入）。
5. **显示怪癖**：`Js.ToFixed` ×10（`src/Sim.Core/Js.cs:37-52`）仍故意未修（保基线逐位），
   事件 t 值需 ÷10 读秒。
6. **early-fire 专项对照未做**：判读基于机制+行为代理（§2.1），未做修复前后台心 zone
   逐 tick 采样对比。

## 附：证据索引

- 本轮对照：`.trellis/tasks/10-01-mbri-fsm-port/evidence/comparison-postfix.md`
  （§0 摘要、§3 逐 seed、§4 交叉核对、§5 切换建议、§6 MuJoCo、§7 Godot、§8 局限）
- 前序对照：`evidence/comparison.md`（修复前 §3/§4、第一轮修复后 §8）
- 临时产物（不入库）：`tmp/mbri-postfix2/`（33 场日志、summary.json、驱动脚本、MuJoCo
  5 场、godot-qa）、`tmp/mbri-recal/`（A1 采样）、`tmp/mbri-comparison/`（前轮）
- Godot 截图：`godot/docs/qa-10-02/qa-frame-{060,300,900}.png`
- 关键代码：`src/Sim.Core/MbriGrayCalibration.cs:14-45`、`MbriReentry.cs:347-363,428-459,548-576`、
  `MbriFsm.cs:52-83`（桥接/特权观测披露）、`MbriHunt.cs`、`MbriProbe.cs`；
  `src/Sim.Cli/RlEnvCommand.cs:315-317`（§4.3 风险依据）
