# 修复后 11-seed 三方对照复测（A1 重锚定 + P2 视觉追击移植后）

- 任务：`.trellis/tasks/10-01-mbri-fsm-port`（验证与目检者，2026-10-02）
- 前序：`evidence/comparison.md`（§1–§7 修复前口径；§8 第一轮修复复测）
- 场景：`scenarios/wushu-ring-2026.json`（legacy 物理，120 s = 2400 tick），
  场景副本直接引用入库复现件 `.trellis/tasks/10-01-mbri-fsm-port/evidence/repro/`（mirror-builtin / mirror-mbri / head-us-mbri）
- seed 集合：`1,2,3,4,5,6,7,8,9,10,42`
- 二进制：`src/Sim.Cli/bin/Debug/net8.0`（本次验证开始前 `dotnet build src/Sim.Cli/Sim.Cli.csproj` 0 警 0 错重建，含全部修复与 MbriHunt/MbriProbe）
- 原始日志/汇总：`tmp/mbri-postfix2/logs/*.txt`（33 场）、`tmp/mbri-postfix2/summary.json`、
  驱动脚本 `tmp/mbri-postfix2/run-postfix-comparison.py`（repro/run-comparison.py 同法，tmp/ 不入库）

## 0. 结论摘要（含不利结果）

1. **修复前"冻结"已消除：mbri 全场持续决策。** mirror-mbri 每场事件 262–391 条
   （修复前 19 条），最后非终局事件 ≈118.4–119.9 s（修复前 5.0 s 后全场零事件）。
2. **掉台中位保持低位且不再来自冻结：mirror 双份 mbri 掉台中位 2（n=22，范围 1–7）
   vs builtin 43（n=22，范围 3–56，逐位等于修复前基线）。**
3. **得分从恒 0 变为非零：mirror-mbri 我方得分中位 2.0（范围 0–12），对手 0.0（范围 0–8）；
   11 场全部至少一方 >0（我方 10/11、对手 5/11）。** 得分构成含掉台罚分、登台读秒、
   增益块推下（+3，见 §3.2 seed2 示例）。
4. **不利结果 ①：同场对 builtin 时 mbri 稳而不赢。** head-us-mbri 中 mbri 掉台中位 2
   vs builtin 11，但**得分中位 1 vs 13（11 场全负，我方得分 10/11 场恒为 1）**——
   mbri 掉台少、在台久，但推块/读秒得分能力显著弱于 builtin 对手。
5. **不利结果 ②：A2 REMOUNT 显式"回台成功"出口在 33 场中 0 次触发。** 物理再上台
   （falls 后重新 mount）确实发生（mirror-mbri 我方 5/11 seed 再上台 ≥1 次，中位 1），
   但经由 reentry 周期其它状态（IR_WAIT `!Fall`→WAIT 出口 `MbriReentry.cs:428-429`、
   REVERSE 结束分流 `MbriReentry.cs:347-363`）完成，REMOUNT 冲台分支的灰度恢复
   成功文案（`MbriReentry.cs:555`）从未打出——回台达成，但不是经设计主打的那条显式路径。
6. **判读：ask 给定的切换门槛字面达标**（掉台更低 ✓ 且得分非零 ✓），切换建议与差距
   见 §5（含同场得分劣势的如实披露）。

## 1. 实际命令

```bash
DOTNET="C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"
REPRO=".trellis/tasks/10-01-mbri-fsm-port/evidence/repro"
# 逐场（33 = 3 配置 × 11 seed）:
"$DOTNET" src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed <S> \
  --scenario $REPRO/<cfg>.json --events --stats
# 独立交叉核对:
"$DOTNET" src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll batch \
  --seeds 1,2,3,4,5,6,7,8,9,10,42 --scenario $REPRO/<cfg>.json \
  --out tmp/mbri-postfix2/batch-<cfg>.jsonl
```

驱动脚本：`python tmp/mbri-postfix2/run-postfix-comparison.py`（repro 同法：逐场解析 +
batch 交叉核对 + 汇总 JSON）。

## 2. 指标口径

- **掉台/上台次数**：`match --stats`（整场提交快照 OnPlatform true→false / false→true
  迁移，`MatchRunner.cs:44-93`，与裁判 physics.OnStage 同源）。
- **回台成功次数**（本报告新口径）：物理再上台 = `max(0, mounts − 1)`——首发在台外
  （开局走道），首次 mount 是开场登台，其后每次 mount 必然跟随一次掉台。
- **得分**：终局 `score 我方 X : Y 对手`。

## 3. 逐 seed 结果

### 3.1 mirror-builtin（双份 builtin 基线，n=22）

逐 seed 与修复前基线（comparison.md §3.1）**逐位一致**（falls/mounts/比分全同，
程序断言 `mirror-builtin vs 修复前基线逐位一致: True`）——修复未触碰 builtin 路径，
且确定性在修复后二进制上保持。

| 统计 | 掉台 | 回台成功(再上台) | 得分 |
|---|---|---|---|
| 中位 | **43** | 42 | 8.5 |
| 范围 | 3–56 | 2–55 | 3–51 |

### 3.2 mirror-mbri（双份 mbri，n=22）

| seed | falls(us/them) | 回台成功(us/them) | 比分 | 事件行数 | 最后事件(s) |
|---|---|---|---|---|---|
| 1 | 1/1 | 0/0 | 1:0 | — | 119.3 |
| 2 | 6/7 | 6/6 | 12:4 | 391 | 118.4 |
| 3 | 1/2 | 0/1 | 0:4 | — | 119.9 |
| 4 | 4/7 | 3/7 | 7:8 | — | 119.8 |
| 5 | 1/1 | 0/0 | 1:0 | — | 118.9 |
| 6 | 1/3 | 0/2 | 2:2 | — | 119.3 |
| 7 | 3/2 | 2/1 | 9:0 | — | 119.8 |
| 8 | 1/2 | 0/1 | 1:0 | — | 119.6 |
| 9 | 2/4 | 1/3 | 1:5 | — | 118.8 |
| 10 | 3/1 | 2/0 | 2:0 | — | 119.7 |
| 42 | 2/1 | 1/0 | 2:0 | — | 119.4 |

（事件行数区间 262–391，全表见 `tmp/mbri-postfix2/summary.json`。）

汇总：掉台中位 **2**（1–7）；回台成功中位 **1**（0–7，11 seed 中 5 个 seed 我方有再上台）；
得分中位 我方 **2.0** / 对手 **0.0**。

**得分构成示例（seed2，`logs/mirror-mbri-seed2.txt`）**：掉台罚分（对手掉台 +1 ×4）、
增益块被推下 `BlockScore 我方 +3`（t=502，追击 GOOD_PUSH 实际推块得分）、
登台/掉台读秒 `ScoreClock +1` ×4——hunt 追击链路（ARC_LEFT→GOOD_CONFIRM→GOOD_PUSH）
在完整对局口径下产出了真实得分。

### 3.3 head-us-mbri（我方 mbri vs 对手 builtin，11 场全负——如实呈现）

| seed | falls mbri/builtin | 回台成功 mbri/builtin | 比分 |
|---|---|---|---|
| 1 | 2/17 | 1/16 | 1:9 |
| 2 | 3/33 | 2/32 | 1:9 |
| 3 | 1/4 | 0/4 | 1:10 |
| 4 | 3/29 | 2/28 | 1:9 |
| 5 | 2/3 | 1/3 | 1:17 |
| 6 | 2/27 | 1/26 | 0:13 |
| 7 | 3/5 | 2/5 | 1:16 |
| 8 | 2/6 | 1/6 | 1:12 |
| 9 | 2/11 | 1/10 | 1:9 |
| 10 | 4/5 | 3/5 | 1:17 |
| 42 | 6/6 | 5/6 | 1:16 |

汇总：mbri 掉台中位 **2** vs builtin **11**；mbri 得分中位 **1** vs builtin **13**。
我方得分几乎恒为 1（多为登台读秒），faults/penalties 均 0（无消极比赛罚分）——
**mbri 在台上时间远长于对手，却几乎不推块、不吃掉台分**：对手掉台时我方常不在台上
（我方自己也在走道周期里）或双方不在台上不计分。

### 3.4 中位数对比（每场每角色一条观测）

| 数据面 | 控制器 | n | 掉台中位 | 回台成功中位 | 得分中位 |
|---|---|---|---|---|---|
| mirror 双份 | builtin | 22 | 43 | 42 | 8.5 |
| mirror 双份 | mbri | 22 | **2** | **1** | **1.0** |
| 同场 head | builtin(对手) | 11 | 11 | 10 | 13 |
| 同场 head | mbri(我方) | 11 | **2** | **1** | **1** |

### 3.5 与修复前基线对比（ask 口径：掉台 43 vs 1-冻结、得分 0）

| 面 | 修复前 | 修复后 | 变化 |
|---|---|---|---|
| mirror mbri 掉台中位 | 1（冻结常数：5 s 后全程零位移） | 2（全场活动，118–120 s 仍有事件） | 冻结消除，掉台仍低 |
| mirror mbri 得分 | 恒 0:1 | 我方中位 2.0，11 场 8 场有分 | **0 → 非零** |
| mirror builtin 掉台中位 | 43 | 43（逐位一致） | 不变（确定性） |
| head mbri(我方) | 1 掉台 / 0 分，全负 | 2 掉台 / 中位 1 分，全负 | 得分非零但仍全负 |

## 4. 交叉核对与确定性

| 核对 | 方法 | 结果 |
|---|---|---|
| match vs batch | 33 行比分/ticks/doneReason 逐行比对 sim-batch-result-v1 | **33/33 一致** |
| 确定性 | mirror-mbri seed42 双跑 `cmp` 逐字节 | **byte-identical** |
| mirror-builtin vs 修复前基线 | 逐 seed 断言 falls/mounts/比分 | **11/11 逐位一致** |
| --stats vs 裁判 Drop 事件 | 逐场逐角色比对（分母允许 SimultaneousDrop ±1） | 全部差 ≤1，口径差内 |
| replay 基线 | `replay-check replays/seed-42.json` | **PASS（scores 4:49, events 752/752 逐位）**——legacy 基线未动 |

## 5. 切默认建议（defaultSwitchPackage）

**门槛判定：字面达标**——mbri 掉台显著更低（mirror 中位 2 vs 43；同场 2 vs 11）
且得分非零（mirror 我方中位 2.0；11 场 8 场有分）。

**但"显著优于"须打折扣**（不利结果如实呈现）：
- 同场对 builtin **11/11 全负**（得分中位 1 vs 13）：mbri 稳定性换不来得分，
  推块/读秒收益远低于 builtin；
- mbri 内战总得分（中位 2:0，多数场 1–12 分）远低于 builtin 内战（中位 8.5/侧）——
  mbri 巡台策略整体更保守，比赛观赏性/得分节奏下降；
- A2 REMOUNT 显式回台成功出口 0 次触发（回台经由其它 reentry 状态达成）。

**建议：不建议此刻切换默认控制器。** 理由：切换默认意味着重录全部 legacy 基线
（replays/seed-42 + fixtures），成本高且不可逆，而当前证据只支持"mbri 更稳、能得分"，
不支持"mbri 整体更强"。建议先在同一批修复上迭代同场得分能力（推块得分率、
对手掉台时保持在台率）后再评估切换。

若未来达标需切换，精确步骤清单（供届时执行，本轮**未执行**任何一步）：
1. `node tools/legacy-baseline.js` 重录 legacy fixtures（确认工具脚本与输出清单）；
2. 重录 `replays/seed-42.json`（官方场景 4:49 → 新预期值，命令行与录制备份流程）；
3. 11-seed 重扫（`batch --seeds 1..10,42` × 官方场景 + 本对照 3 配置）；
4. 需用户确认的基线文件：`replays/seed-42.json`、`tests/fixtures/`（legacy）全部、
   桌面默认控制器档位（`godot/src/DesktopSettings.cs` 默认值）；
5. 更新 `.trellis/spec/` Mbri 控制器条款与本证据链。

## 6. mbri + MuJoCo 验证（wushu-ring-2026-mujoco-v2.json）

- 场景副本（仅加 `controller: "mbri"`，tmp/ 不入库）：
  `tmp/mbri-postfix2/mujoco/mirror-mbri-mujoco-v2.json`（双方）、
  `head-us-mbri-mujoco-v2.json`（我方 mbri）、`mirror-builtin-mujoco-v2.json`（builtin 参照）。
- 命令：`dotnet Sim.Cli.dll match --seed <S> --scenario <副本> --events --stats`。

| 场次 | 结果 | 行为判读 |
|---|---|---|
| mirror-mbri seed1 | exit=0，2400 tick，**8:0**，falls 6/2，再上台 5/1 | 全场活跃（EDGE_AVOID×100、probe 事件 30 条）；回台经 REVERSE 结束灰度恢复分流（SAFE_STOP「倒车完成（前头红外无值）」）达成 |
| mirror-mbri seed7 | exit=0，2400 tick，1:0，falls 1/1，再上台 0/0 | 掉台后走道 IR_WAIT 循环（rearm 2 s 周期），120 s 无物理回台 |
| mirror-mbri seed42 | exit=0，2400 tick，0:1，falls 1/1，再上台 0/0 | 同上；双方约 5 s 时 EDGE_AVOID 巡台下台（MuJoCo 台沿物理下「提前离边」未防住），此后 REMOUNT 冲台 3 次重试均「未检测到上台」 |
| head-us-mbri seed42 | exit=0，2400 tick，**1:12**，mbri falls 1 再上台 0 / builtin falls 1 再上台 1 | mbri 卡走道循环（hunt 门禁需巡台 CRUISE，未达成 → hunt/probe 0 事件）；builtin 正常推块得分（SCORE_BLOCK×14） |
| mirror-builtin seed42（参照） | exit=0，**tick 2017 提前终局**「恢复次数超限 → 停车」，1:0，falls 1/1 | MuJoCo 物理下内置 FSM 自身也触发恢复超限停车——MuJoCo 对双方控制器都更难 |

- **不崩溃**：5/5 场 exit=0、事件流完整（唯一提前终局是 builtin 参照场的既有规则性停车，非崩溃）。
- **确定性**：mirror-mbri seed7 双跑 `cmp` 逐字节一致。
- **与 legacy 的差异（如实记录）**：
  1. 传感器档不同：mujoco-v2 车辆 `sensors.id=wheeledCombat11`（后向红外未映射恒 0，
     无 legacy14 围栏位）——reentry 分派只靠前/侧红外（`MbriFsm.cs:56-59` 头注释披露项）；
  2. 台沿物理不同：mirror seed42 双方约 5 s 巡台下台（legacy 同 seed 首次掉台在 14 s+），
     EDGE_AVOID 的灰度趋势避让在 MuJoCo 台沿几何下不总能防掉台；
  3. 回台能力 seed 相关：seed1 物理回台 5 次（REVERSE 结束灰度恢复分流，设计内路径），
     seed42/7 掉台后 120 s 零物理回台（走道 IR_WAIT 循环；legacy 下 REMOUNT/再分派
     周期能物理回台）——MuJoCo 台沿爬行几何对 −1000 反向冲台更苛刻；
  4. A1 灰度重锚定按官方场 legacy 采样链路校准，MuJoCo 场地灰度域未单独校准
     （本轮未做 MuJoCo 灰度采样分布测量，属后续工作）。

## 7. Godot 无人值守目检（真实 exe + QA 参数）

- exe：`Godot_v4.7.2-stable_mono_win64_console.exe`（WinGet 安装真实包），
  `dotnet build godot/GodotSim.csproj` 0 警 0 错后运行。
- 命令（自定义参数在 `--` 之后，三次运行取不同时刻帧）：
  `godot_console --path godot -- --scenario-path <mirror-mbri 场景副本绝对路径>
  --auto-arm --capture <godot/docs/qa-10-02/qa-frame-NNN.png 绝对路径> --capture-frames N`
- 设置隔离：预置桌面设置档 `usController/themController = mbri`（HUD 控制器指示来源，
  `ControllerWiring.cs:105-111` 只反映桌面档，不读场景字段）；运行前原设置文件
  （external RL 桥档）移出 stash，判读后**原样还原（cmp 逐字节一致）**，QA 运行产生的
  Godot 日志已删除。三次运行 `[controller] 我方 内置 MBri, 对手 内置 MBri` 全部打出。

| 帧 | 判读（亲自 Read 过截图） |
|---|---|
| qa-frame-060.png（~0.5 s） | HUD 绿字"控制器 我方 内置 MBri / 对手 内置 MBri"✓；双方状态 [MOUNT_RING]"开局后退上台"，比分 0:0、剩余 119.5 s；双车在台南走道起步位尚未上台 |
| qa-frame-300.png（~1.5 s） | 双车已完成开局后退上台（青/红车分别位于台面两个登台环，环高亮），比分 0:0，控制器指示不变 |
| qa-frame-900.png（~4.3 s） | 双方状态 [RECOVER]"直线退离完成，开始转向"（EDGE_TURN→Recover 快照映射 ✓）；事件流滚动 `[mbri] EDGE_AVOID: 前向灰度趋势变暗，提前离边` / `EDGE_TURN` / `RECOVER_FORWARD`——mbri 巡台避边循环在跑；双车在台面内沿边重定位 |

- 结论：HUD 控制器指示正确、车辆行为与所显示 FSM 状态及事件流一致、无渲染/逻辑异常；
  截图存 `godot/docs/qa-10-02/`（3 帧，1280×720）。

## 8. 局限

- 未跑全量测试套件（按 ask 纪律，主会话后续跑门禁）；本轮针对性验证为
  replay-check + 33 场对照 + 交叉核对 + MuJoCo 5 场 + Godot 3 帧目检。
- RL 三方仍未参与（R4 口径同 comparison.md §6）。
- "回台成功"统计基于物理迁移（mounts−1）；reentry 内部状态的逐 tick 归因
  （哪条出口达成的回台）为代码阅读+事件流推断，未做逐 tick 位置采样验证。
- MuJoCo 灰度域未按 A1 流程单独校准/测量（见 §6 差异第 4 条）。
- Godot 目检仅静态帧判读：未注入输入逐项检查设置页，未验证长时运行内存/帧率。
