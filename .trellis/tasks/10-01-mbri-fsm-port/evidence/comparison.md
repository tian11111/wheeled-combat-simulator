# 批3 行为对照：内置 FSM vs MbriFsm（官方场景 11 seed）

- 任务：`.trellis/tasks/10-01-mbri-fsm-port`
- 日期：2026-10-01
- 场景：`scenarios/wushu-ring-2026.json`（legacy 物理，3.8 m 官方布局，120 s = 2400 tick）
- seed 集合：`1,2,3,4,5,6,7,8,9,10,42`（沿用仓库既有 11-seed 扫描惯例）
- 修订说明：§1–§7 的 44 场数据采集于 **评审修复前修订版**（日志时间 2026-10-01
  20:24–20:35；`MbriFsm.cs` / `MbriReentry.cs` 随后因评审 finding 1/2 于 21:02–21:08
  变更）。修复后复测见 **§8**。
- 场景副本/日志/汇总/脚本（均不入库，`tmp/` 已被 `.gitignore:49` 忽略）：
  `tmp/mbri-comparison/`（4 份场景副本、`logs/` 44 场原始 stdout、`summary.json`、
  `analysis.md`、`run-comparison.py`、`diag/` 逐 tick 诊断程序、`diag-mirror-mbri-seed42.txt`）

## 0. 结论摘要（含不利结果）

1. **数字指标：mbri 掉台中位数显著低于内置 FSM。**
   - mirror 双份（双方同控制器）：builtin 掉台中位 **43**（n=22，范围 3–56）vs mbri **1**（n=22，全部恰为 1）。
   - 同场配对（双向角色互换，mbri vs builtin 同场）：builtin 中位 **39.5**（4–56）vs mbri **1**（全部 1）。
2. **但该低掉台来自"冻结"，不是"巡台能力"——本轮对照不支持 MbriFsm 行为可用的结论。**
   mbri 镜像份（`mirror-mbri`）每场每车签名完全相同：**1 次上台 + 1 次掉台 + 约 5 s 后全程零位移**，
   120 s 每场仅 19 条事件（同 seed builtin 镜像对照 470–811 条），比分恒 0:1；同场配对中
   我方 mbri 一分未得（0:5…0:15 全负）。"无上台后掉台死循环"仅以"机器人不再移动"的方式表面成立。
3. **根因定位（供后续批决策）：开局流程在"走道→台面"过渡帧被 reentry 误触发，
   随后 REVERSE 倒车穿越整个台面并从对侧跌落，最终停在 SAFE_STOP 不再复位。**
   见 §4 逐 tick 诊断；代码侧 `src/Sim.Core/MbriReentry.cs:368-381`（SAFE_STOP 在
   灰度未恢复时永久停车）与 `src/Sim.Core/MbriReentry.cs:455-469`（REVERSE 超时→SAFE_STOP）。
   这是批1/2 移植逻辑在官方场景完整对局口径下的问题，不是本批接线引入（接线本身见 §2 验收）。

## 1. 对照设计与口径

### 1.1 四份场景副本（`vehicles[].controller` 协议加法字段）

| 副本 | us.controller | them.controller | 用途 |
|---|---|---|---|
| `tmp/mbri-comparison/mirror-builtin.json` | 省略（builtin） | 省略（builtin） | 双份基线 |
| `tmp/mbri-comparison/mirror-mbri.json` | `mbri` | `mbri` | 双份处理 |
| `tmp/mbri-comparison/head-us-mbri.json` | `mbri` | 省略（builtin） | 同场配对（我方 mbri） |
| `tmp/mbri-comparison/head-them-mbri.json` | 省略（builtin） | `mbri` | 同场配对（对手 mbri，角色互换） |

副本由 `scenarios/wushu-ring-2026.json` 逐字段复制，仅改写 `vehicles`；官方原文件未改动。

### 1.2 指标定义

- **掉台次数** = 整场提交快照 `OnPlatform` true→false 迁移数（`match --stats`，与裁判
  `physics.OnStage` 判定同源，与控制器选择无关）。
- **上台次数** = false→true 迁移数。
- **比分** = 终局 `score 我方 X : Y 对手`。
- 日志里的 `t=` 显示存在既有 ×10 怪癖（`Js.ToFixed(simT,1)` 先把值乘 10^digits 再格式化，
  `src/Sim.Core/Js.cs:37-52`；旧日志 `tmp/match-v2-final.txt` 同一现象，120 s 场显示 t=1200），
  本文引用"最后事件时刻"时按 log 值 ÷10 换算为秒。

### 1.3 实际命令（每配置 × 每 seed 各一条）

```bash
DOTNET="C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"
# 详细运行（掉台/上台/比分；events 用于与裁判 Drop 事件交叉核对）:
"$DOTNET" src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed <S> \
  --scenario tmp/mbri-comparison/<cfg>.json --events --stats
# 独立交叉核对（sim-batch-result-v1 JSONL）:
"$DOTNET" src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll batch \
  --seeds 1,2,3,4,5,6,7,8,9,10,42 --scenario tmp/mbri-comparison/<cfg>.json \
  --out tmp/mbri-comparison/batch-<cfg>.jsonl
```

驱动脚本：`python tmp/mbri-comparison/run-comparison.py`（44 场 match + 4 条 batch），
聚合脚本：`python tmp/mbri-comparison/analyze.py`。`--stats` 为本批新增的追加式 CLI 能力
（`src/Sim.Cli/Program.cs:178-183`，`src/Sim.Cli/MatchRunner.cs:48-93`），不传 `--stats`
时输出与既有完全一致（`CliTests.MatchSeeds_StillSequentialOneLinePerSeed_PlusSummary` 仍绿）。

## 2. 接线验收（选择机制）

| 项 | 证据 | 结果 |
|---|---|---|
| 协议加法 | `VehicleProfile.Controller`（`src/Sim.Protocol/Profiles.cs:245`，null 省略不加宽既有 wire）；取值 `builtin`/`mbri`（`Profiles.cs:224-232`），非法值被 `Validate` 拒绝（`Profiles.cs:320-328`） | ✅ |
| 场景选择生效 | `MatchEngine` 构造按 `vehicles[role].controller` 建 Mbri 控制器（`src/Sim.Core/MatchEngine.cs:135-136`），每 tick 派发 `mbri.TickFor(r, SimStepIndex)`（`MatchEngine.cs:687-692`），发令走 mbri 路径（`MatchEngine.cs:367-372`），裁判重启换新实例 rearm（`MatchEngine.cs:527-537`） | ✅ |
| 省略=builtin 逐位不变 | 单测 `MbriSelectionTests.ExplicitBuiltin_IsBitIdentical_ToOmittedController`（事件指纹+比分相等）；官方场景 vs `mirror-builtin` 副本 20 s 输出逐行 `diff --strip-trailing-cr` 一致（仅多一行 `scenario:` 路径诊断，stderr）；`replay-check replays/seed-42.json`：`scores 4:49 (expected 4:49) events 752/752 PASS` | ✅ |
| 桌面设置页 | `ControllerModes.Mbri`（`godot/src/DesktopSettings.cs:16`）；档位三选一"内置 FSM / 内置 MBri / 外部命令"（`godot/src/SettingsPanel.cs:359-362`）；写场景 `ApplyControllerSelection`（`DesktopSettings.cs:287-312`，无选择时返回同一引用）；HUD 来源名 `ControllerWiring.cs:105-111`；Main 链路 `Main.cs:1174-1179` | ✅（代码+构建+UI 冒烟） |
| 针对性单测 | `dotnet test --filter "FullyQualifiedName~MbriSelectionTests|~ControllerWiringTests|~DesktopSettingsTests|~CliTests"`：55/55 通过；补齐相关面 `MatchEngineTests/MatchRunnerExhibitionTests/DesktopLiveDriverTests/BatchCommandTests/BatchMatchResultTests/RoundTripTests/MalformedJsonTests/MbriFsmTests/MbriReentry*` 后 129/129 通过 | ✅ |
| Godot 构建/冒烟 | `dotnet build godot/GodotSim.csproj` 0 警 0 错；`godot --path godot -- --settings-smoke --settings-tab 2` 输出 `[settings-smoke] UI 构建冒烟通过`；截图 `godot/tmp/mbri-comparison/settings-controller.png`（下拉展开项未注入输入验证，设置页静态渲染正常） | ✅（展开交互未验） |

## 3. 逐 seed 结果

### 3.1 主对照·双份镜像（双方同一控制器）

`mirror-builtin`（基线份）：

| seed | us_falls | them_falls | us_mounts | them_mounts | 比分 us:them | done | ticks | 最后事件 t(÷10 s) |
|---|---|---|---|---|---|---|---|---|
| 1 | 50 | 40 | 50 | 40 | 5:12 | 比赛时间结束 | 2400 | 119.7 |
| 2 | 50 | 7 | 50 | 8 | 4:40 | 比赛时间结束 | 2400 | 119.9 |
| 3 | 56 | 7 | 56 | 7 | 4:49 | 比赛时间结束 | 2400 | 119.6 |
| 4 | 50 | 9 | 50 | 10 | 5:26 | 比赛时间结束 | 2400 | 119.9 |
| 5 | 46 | 7 | 46 | 7 | 3:31 | 比赛时间结束 | 2400 | 119.5 |
| 6 | 46 | 7 | 46 | 8 | 4:42 | 比赛时间结束 | 2400 | 119.6 |
| 7 | 49 | 4 | 49 | 5 | 4:38 | 比赛时间结束 | 2400 | 119.9 |
| 8 | 50 | 3 | 50 | 4 | 3:51 | 比赛时间结束 | 2400 | 119.8 |
| 9 | 47 | 30 | 47 | 30 | 24:5 | 比赛时间结束 | 2400 | 119.9 |
| 10 | 50 | 4 | 50 | 5 | 4:43 | 比赛时间结束 | 2400 | 119.9 |
| 42 | 55 | 3 | 55 | 4 | 4:49 | 比赛时间结束 | 2400 | 119.9 |

`mirror-mbri`（处理份）：**11/11 seed 完全相同** —— us/them 各 1 掉台、1 上台，比分 0:1，
最后一条非 End 事件 t=50（即 **5.0 s 后全场无事件**）。

| seed | us_falls | them_falls | us_mounts | them_mounts | 比分 us:them | 最后事件 t(÷10 s) |
|---|---|---|---|---|---|---|
| 1–10, 42（每 seed 同值） | 1 | 1 | 1 | 1 | 0:1 | 5.0 |

### 3.2 同场配对·双向角色互换（同 match 内 mbri vs builtin）

`head-us-mbri`（我方 mbri）：

| seed | us_falls(mbri) | them_falls(builtin) | us_mounts | them_mounts | 比分 us:them |
|---|---|---|---|---|---|
| 1 | 1 | 7 | 1 | 8 | 0:10 |
| 2 | 1 | 6 | 1 | 7 | 0:12 |
| 3 | 1 | 45 | 1 | 45 | 0:5 |
| 4 | 1 | 7 | 1 | 8 | 0:10 |
| 5 | 1 | 34 | 1 | 34 | 0:11 |
| 6 | 1 | 4 | 1 | 5 | 0:7 |
| 7 | 1 | 5 | 1 | 6 | 0:14 |
| 8 | 1 | 26 | 1 | 26 | 0:6 |
| 9 | 1 | 20 | 1 | 20 | 0:7 |
| 10 | 1 | 18 | 1 | 18 | 0:15 |
| 42 | 1 | 6 | 1 | 7 | 0:13 |

`head-them-mbri`（对手 mbri，角色互换）：

| seed | us_falls(builtin) | them_falls(mbri) | us_mounts | them_mounts | 比分 us:them |
|---|---|---|---|---|---|
| 1 | 50 | 1 | 50 | 1 | 4:0 |
| 2 | 50 | 1 | 50 | 1 | 4:0 |
| 3 | 56 | 1 | 56 | 1 | 4:0 |
| 4 | 50 | 1 | 50 | 1 | 4:0 |
| 5 | 46 | 1 | 46 | 1 | 4:0 |
| 6 | 46 | 1 | 46 | 1 | 4:0 |
| 7 | 48 | 1 | 48 | 1 | 4:0 |
| 8 | 50 | 1 | 50 | 1 | 4:0 |
| 9 | 5 | 1 | 6 | 1 | 11:0 |
| 10 | 50 | 1 | 50 | 1 | 4:0 |
| 42 | 50 | 1 | 50 | 1 | 4:0 |

### 3.3 中位数对比（每场每角色 = 一条观测）

| 数据面 | 控制器 | n | 掉台中位 | 掉台均值 | 范围 | 上台中位 | 得分中位 |
|---|---|---|---|---|---|---|---|
| mirror 双份 | builtin | 22 | 43 | 30.45 | 3–56 | 43 | 8.5 |
| mirror 双份 | mbri | 22 | 1 | 1.00 | 1–1 | 1 | 0.5 |
| 同场配对（双向） | builtin | 22 | 39.5 | 30.86 | 4–56 | 39.5 | 5.5 |
| 同场配对（双向） | mbri | 22 | 1 | 1.00 | 1–1 | 1 | 0 |

内置 FSM 侧另有明显的角色不对称（us 中位 50–56 vs them 3–40），mbri 侧 22 条观测全等于 1，
其"低掉台"不是分布上的改善而是**冻结造成的常数**。

## 4. 不利结果：mbri 冻结诊断（如实呈现）

`tmp/mbri-comparison/diag-mirror-mbri-seed42.txt`（逐 tick 采样；THEM 与本方镜像对称）：

| tick | US 位置 | v | 在台 | 状态 | 动作 |
|---|---|---|---|---|---|
| 1 | (0.950, 0.314) | -0.896 | False | MOUNT_RING | 开局后退上台 |
| 20 | (0.950, 1.129) | -0.896 | True | MOUNT_RING | 开局后退上台（已跨上台面） |
| 40 | (0.950, 2.013) | -0.806 | True | RECOVER | ADC 矫正完成，倒车（reentry 已接管） |
| 60 | (0.950, 2.820) | -0.806 | True | RECOVER | ADC 矫正完成，倒车 |
| 80 | (0.950, 3.626) | -0.806 | False | RECOVER | ADC 矫正完成，倒车（越过台面北缘跌落） |
| 100 | (0.950, 3.680) | 0 | False | RECOVER | 倒车超时（SAFE_STOP） |
| 400/800/1200/2000/2399 | (0.950, 3.680) | 0 | False | RECOVER | 倒车超时（**不再移动**） |
| 2400 | — | — | — | FINISHED | 停车, 比赛结束（终局 0:1） |

机理（代码引用）：

1. `START_REVERSE` 1.8 s × 0.896 m/s ≈ 1.61 m 的后退把车从出发点一路送上台面
   （`src/Sim.Core/MbriFsm.cs:42-46` 常量、`MbriFsm.cs:112-138` 独占窗口）；
2. 走道→台面过渡帧（灰度黑→亮）期间 reentry 的"四路 zone 全 <0 ×3 帧"触发条件成立
   （判定 `src/Sim.Core/MbriReentry.cs:350-351`）→ `StartFromTrigger`（`:247-270`，
   本场桥接的前向红外有值 → ADC 矫正 → REVERSE），START_REVERSE 结束后 reentry 状态
   ≠ WAIT 即以接管（`src/Sim.Core/MbriFsm.cs:152-167`）；
3. 其 `REVERSE` 无条件倒车直到前向 IR 无值或超时（`MbriReentry.cs:455-469`），在仿真几何下
   没有墙可停 → 倒车横穿台面并从对侧跌落到走道；
4. 超时进 `SAFE_STOP`，而 SAFE_STOP 只有"灰度恢复（重新上台）"才回 WAIT
   （`MbriReentry.cs:368-381`，设计注释 `:188-189` 亦写明"灰度恢复（人工/后续上台）后回 WAIT"）——
   机器人停在黑色走道（zone 恒 <0），永远不会恢复，reentry 永久接管，patrol 兜底失效。
   真车口径下需要人工/裁判把车放回台面；无裁判的仿真对局里这就是终态。

旁证：`mirror-mbri` 每场事件行数恒 19 条（`mirror-builtin` 470–811 条）；终局比分只有
掉台罚分（0:1 / 0:X），无读秒分、无推块分。

## 5. 交叉核对与确定性

| 核对 | 方法 | 结果 |
|---|---|---|
| match vs batch 比分/ticks/done | 44 行逐行比对 `match` 摘要与 `sim-batch-result-v1` | 44/44 一致 |
| `--stats` 掉台 vs 裁判 Drop 事件（分角色） | 解析 `--events` 中 `[score]` 掉台行（Msg 前缀 `[我方]/[对手]`） | 43/44 一致；1 例（mirror-builtin seed=1：stats 50/40 vs 事件 49/39）差 1，原因是该场出现 1 次双方同帧掉台，裁判归并为中性 `SimultaneousDrop`（`src/Sim.Core/MatchEngine.cs:749-754`），`--stats` 按物理迁移对两侧各计 1 —— 口径差异，已核实 |
| 确定性 | `mirror-builtin` seed 42 双跑逐字节一致；`MbriSelectionTests.MbriSelected_IsDeterministicAcrossRuns` 双跑事件指纹/比分一致 | ✅ |

## 6. 局限与未验证项

- 只测了官方 legacy 场景；**mbri + MuJoCo 后端未测**（该组合下本批接线不跑倾覆门控，
  属已知披露项）。
- R4 的三方（内置 FSM vs MbriFsm vs RL）中 **RL 未参与**：本批 ask 明确定义为
  "内置 FSM vs MbriFsm 同场同 seed 对比"，RL checkpoint 不在本批输入内。
- Godot 设置页只做了 UI 构建冒烟 + 静态截图；下拉展开项未注入输入逐项目检。
- `Js.ToFixed` 的 ×10 显示怪癖为既有问题（旧日志可复现），本轮**故意未修**以保基线逐位不变。

## 7. 给主会话的建议

1. **不要把"掉台中位数 43→1"当作 R4 通过证据**：mbri 的 1 次掉台来自 5 s 后永久停车。
   R4 的"无上台后掉台死循环"目标需在 reentry 触发/SAFE_STOP 复位修复后重跑本对照。
2. 修复候选（另立批次，涉及批1/2 逻辑）：① reentry 触发窗口需排除"START_REVERSE 上台过渡帧"
   （或上台后强制清回 WAIT）；② SAFE_STOP 的复活语义需要仿真口径（无人工复位时定时重试，
   例如退避后重走 reentry 或交还 patrol）；③ `REVERSE` 需有台沿/围栏终止条件，避免倒穿台面。
3. `match --stats` 为追加式能力，已随本批测试；对照脚本与 44 场原始日志保留在
   `tmp/mbri-comparison/` 供复盘（不入库）。

## 8. 修复后复测（2026-10-01 21:2x，评审 finding 1/2 修复修订版）

评审修复落地后（`src/Sim.Core/MbriFsm.cs`：START_REVERSE 预热窗只喂灰度滤波不推进
reentry 状态机；`src/Sim.Core/MbriReentry.cs`：SAFE_STOP/IR_WAIT 在仍掉台且无进展
2 s 后重新武装），用当前二进制（`src/Sim.Cli/bin/Debug/net8.0`，Sim.Core.dll 构建于
21:08:30）复测两个配置，区分 §4 冻结是否仍成立：

- 命令：`dotnet Sim.Cli.dll match --seed <S> --scenario tmp/mbri-comparison/<cfg>.json --events --stats`
  （seeds 1–10,42；日志 `tmp/mbri-comparison/postfix-mirror-mbri/` 与
  `tmp/mbri-comparison/postfix-head-us-mbri/`，汇总 `postfix-summary-*.json`；
  驱动脚本 `tmp/mbri-comparison/postfix-scan.py`）。
- `mirror-mbri`（双方 mbri）11/11 seed：掉台 us/them=1/1、上台=1/1、比分 **0:0**；
  每场事件 **347–352** 条（修复前 19），最后非 End 事件 t≈118.8–119.9（即 ~119 s）
  → **SAFE_STOP 吸收态消失，控制器全场持续决策**。但每车仍只在开场上台一次，
  掉台后再未回到台面、无读秒/推块得分。
- `head-us-mbri`（我方 mbri vs 对手 builtin）11/11 seed：我方恒 1 掉台 / 1 上台 /
  0 分；对手内置 FSM 掉台 5–45、得分 4–15（我方全负）。

结论修订：§0-2/§4 的"低掉台来自早期冻结"在修复版应表述为
**"低掉台来自掉台后无法回台（吸收态已除、能力缺口仍在）"**——修复消除了
finding 1 的吸收态并恢复全场活动，但"回台/巡台/得分"能力在官方 legacy 场景仍未证实。
修复后的完整逐 seed 表见本节汇总 JSON 与 `tmp/` 日志（本文件 §3 表格仍为修复前口径）。
