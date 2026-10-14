# Implement：基线 P0 修复

## 步骤

0. **前置**：build Release 确认 0 错（基线 HEAD）。
1. **诊断 P0-1**：写诊断脚本（tmp/ 下，不入库）复现 seed1 台沿抖动——打印上台瞬间双方位姿/四角坐标/速度，确认根因（残余动量越界？判据本身？计数重置？），结论写进执行记录。按诊断结论选修复方向（design 三选）。
2. **修复 P0-1**：最小改动实施；新增回归测试钉死新行为（登台后不掉/同 t 不 Mount+Drop）。
3. **修复 P0-2**：ATTACK 超时；新增测试（持续进攻超时转 SEARCH）。
4. **验证**：官方场景 8 种子 120s + seed1/2@300s 复跑，对齐 PRD 验收清单逐条打勾；全量 dotnet test；replay-check 旧基线→失配则重新录制并注明。
5. **收尾**：执行记录表（每步命令+结果）；提交分组（行为修复/测试更新/基线再生成）；不 push。

## 回滚点

- 每步一个逻辑单元；build+test 全绿后再进下一步；git 工作区随时可 diff 回滚。

## 验证命令

- `& "C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe" build RobotSimulator.sln -c Release --nologo -v q`
- `& "C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe" test src/Sim.Tests -c Release --nologo`
- `& ".../dotnet.exe" src/Sim.Cli/bin/Release/net8.0/Sim.Cli.dll match --seed 1 --scenario scenarios/wushu-ring-2026.json --duration 120 --events`

---

# 执行记录（10-14-baseline-p0-fix）

`$dotnet = C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe`

| 步 | 命令 | 结果 | 结论 |
| --- | --- | --- | --- |
| 0 | `& $dotnet build RobotSimulator.sln -c Release --nologo -v q` | 0 警告 / 0 错误, 4.82s, exit 0 | 基线 HEAD 可构建 |
| 1 | 诊断器 `tmp/p0diag/`（不入库）逐 tick 取证 | 见"根因结论" | 根因=判据本身, 非残余动量 |
| 2 | 修 P0-1（`Fsm.cs` / `MatchEngine.cs`） | build 0 错; 新增 5 个回归测试全绿 | 抖动死循环消除 |
| 3 | 修 P0-2（`Fsm.cs` / `RuntimeState.cs`） | build 0 错; 量纲核对 AttackT 峰值 5.0s | 僵持超时兜底生效 |
| 4 | 8 种子@120s + seed1/2@300s 复跑; 全量 `dotnet test`; `replay-check` | 见"验收结果" | PRD 逐条通过 |
| 5 | 本记录 + 改动/测试清单 | — | 待主会话分组提交（禁 push） |

## 步骤 1：P0-1 根因结论

诊断器 `tmp/p0diag/Program.cs`：`MatchEngineHost.Create(scenario)` → `Arm()` → 逐 tick `Tick()`，打印
`idx/Tick/SimT/State/Action/X/Y/Th/Vx/Vy/V/W/OnStage/wasOn/dp/RecPhase/RecCount/MountPhase/MountT/Edge/四角(相对台面中心)`；
参数 `--seed --scenario --duration --window --max-windows --role --dump-from --dump-to --timeline`。
证据文件：`tmp/p0diag/seed1.dump2.txt`、`tmp/p0diag/seed1.tl3.txt`（tmp/ 不入库）。

1. **真因 = 掉台判据（几何误判），不是残余动量。** 判据 `_physics.OnStage(r)` = footprint 四角全在台面内。
   贴沿原地旋转时（SEARCH「转向目标 / 旋转扫描」、RECOVER「spin」）角点会短暂扫出台沿，而车体并没有掉下去
   ⇒ 被判掉台 → 对方 +1 → RECOVER → 重新登台 → 再旋转再掉，形成 **0.7-1.0s 周期抖动死循环**（方向固定=台沿朝向固定）。
   实测 seed1@120s：对手 93 分里 90 分（96.8%）来自我方掉台；seed3 90.7% / seed4 82.4% / seed8 73.7%。
   逐 tick 证据：tick 350-362 `OnStage=1` 全程为真、四角最大 |x|=1.098 < 台面半宽 1.2、无 on-stage 迁移，掉台却仍被判出。
2. **「同 t Mount+Drop」的放大器 = 陈旧 `DropPending`。** `MatchEngine.ObjFallCheckAll` 在任意状态都置
   `r.DropPending = true`，而 `Fsm.CrisisGateFor` 在 `MountRing/Recover/Finished` 期间直接 `return`，既不消费也不清除该标志。
   登台途中 footprint 在台沿来回翻转即置位，登台成功转 SEARCH 后**下一 tick 立刻消费这枚陈旧标志** → 假掉台 → 同 t 的 Mount+Drop 对。
3. **`WasOn` 判据不同源**：`MatchEngine.cs:530`（摆位/重启路径）用 `_field.OnPlatform(x,y)`（中心），
   而 tick 末尾与 `ObjFallCheckAll` 用 `_physics.OnStage`（四角）⇒ 中心在台而四角悬出时 `WasOn=true, OnStage=false`，同样造出幽灵掉台。
4. **推翻 design 默认方向（b 消惯性 + c Rec.Count 跨轮累计），按证据走 a（放宽判据）**：
   - 消惯性不成立：抖动轮里速度并不越界（见证据 1）；且没有「跨轮累计」可救，每一轮都是独立的几何误判。
   - 中途试过「禁止贴沿原地旋转 + 离沿回中」并留下两个反例：①把旋转安全并入登台判据
     （`DistToNearestEdge >= SpinClearance(0.2808)`）⇒ 登台车道距台沿仅 0.25m，判据**永不可满足**，
     机器人倒车穿过整个台面从对侧掉下去（首次登台推迟到 38.7s）；②判据回退后又因「贴沿且车头平行台沿时
     只能沿 ±y 往复」**死锁成 0:0**（120s 内除 t=13s 登台外再无事件）。两处代码已全部撤销。
   - 落地：判据换成有物理语义的「**车体中心越界才算掉台**」（中心=车体质量中心），而非简单放宽阈值。

## 步骤 2：P0-1 改动（`src/Sim.Core/`）

- `MatchEngine.ObjFallCheckAll`：`nowUs/nowThem` 由 `_physics.OnStage(r)` 改为 `_field.OnPlatform(r.X, r.Y)`。
- `MatchEngine` tick 末尾 `_us.WasOn/_them.WasOn` 同改为 `_field.OnPlatform`；`ScoreClockPhase()` 四处同改（判据全局统一，杜绝不同源）。
- `Fsm.CrisisGateFor`：掉台触发条件改 `r.DropPending || (r.WasOn && !_field.OnPlatform(r.X, r.Y))`；
  早退分支（MountRing/Recover/Finished）增加 `r.DropPending = false;`（清除陈旧标志，消除同 t Mount+Drop 对）。
- `Fsm.MountTick`：**保持** `_physics.FullOn(r)`（登台 = 整车四角在台，与掉台判据不同源，注释已写明）。
- 未改动：`_physics.OnStage/FullOn/HangOn` 语义、协议 DTO、渲染、`oracle/`、`tools/`。

回归测试（新增 `src/Sim.Tests/BaselineP0RegressionTests.cs`，5 个用例）：
1. `DropCriterion_UsesBodyCenter_NotFootprintCorners`：中心 (3.0, 1.9, th=0) 时前角 x=3.22 悬出（`OnStage=false`）但中心在台 ⇒ 不判掉台；中心移到 3.2 ⇒ 判掉台且对方 +1。
2. `OfficialSeed1_HasNoSameTickMountDropPair_AndNoSelfDropPoints`：无同 tick 的 `Mount ∩ Drop`；我方计分掉台 0；真实掉台 ≤5（个位数）；首次上台后 40 tick(2s) 内不再掉台。
3. `OfficialSeed2_AttackStalemateIsCappedByTimeout`：出现「连续进攻 … 超时兜底」宣告；最大静默 ≤60s；末 60s 仍有实质事件。
4. `OfficialStalemateSeeds_KeepProducingEventsAtTheEnd`（seed 5/6，Theory）：同上静默/末段约束。

## 步骤 3：P0-2 改动与依据

- `Fsm.cs`：新增 `private const double AttackTimeoutSeconds = 5.0;`、`EnterAttackFor(r)`（进入 ATTACK 时 `AttackT=0` 并清零 V/W）；
  `AttackTick` 增加 `r.Fsm.AttackT += dt;` 与 `if (AttackT > AttackTimeoutSeconds) → Log("[fsm] ATTACK: 连续进攻 …s 未分出胜负 → SEARCH(超时兜底)") + EnterSearchFor(r)`；
  两处 ATTACK 进入点（视觉分类命中对手 / 识别不到+前向持续）改走 `EnterAttackFor(r)`。
- `RuntimeState.cs`：`FsmRuntime` 新增 `public double AttackT;`（不进协议 DTO）。
- **超时值依据**：`docs/` 与全仓无官方进攻时长规则材料（grep `进攻|ATTACK|超时` 只命中控制器超时语义），按 implement.md 取 **5s 量级**，理由写在代码注释：正常一次追击推挤（1.25 m/s 追击+对准）远短于 5s，只有互推僵持才会触顶。
- 量纲核对（`tmp/p0attack/`，不入库）：seed2@300s 双方各 45 段 ATTACK、每段 **101 tick = 5.05s**、`AttackT` 峰值 **5.0** ⇒ 阈值与单位正确（日志文本"50.0s"见下方登记项）。

## 步骤 4：验收结果

命令：`python tmp/p0verify/verify.py 1,2,3,4,5,6,7,8 --duration 120` 与 `... 1,2 --duration 300`（`tmp/p0verify/`、报告 `tmp/p0verify/report.txt`，均不入库）。
事件 `t=` 为显示值（=10×秒，即 oracle 口径 0.1s 单位）；下表单位已换算成秒。真实掉台按 `cls=fsm` 计（一次掉台产出 `[score]` + `[fsm]` 两条 Drop 事件，oracle.py:75 的 `" Drop "` 子串判定会双计）。

| seed | 时长 | 事件数 | 比分 | 送分(占对手%) | 我方掉台 | 对手掉台 | 同时掉台 | 登台 | 同t Mount∩Drop | 最大静默 | 末实质事件 | 末60s事件 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 120s | 560 | 9:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 4.9s | 119.7s | 337 |
| 2 | 120s | 201 | 3:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 5.1s | 118.1s | 100 |
| 3 | 120s | 219 | 3:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 4.6s | 119.9s | 102 |
| 4 | 120s | 262 | 10:3 | 0 (0.0%) | 0 | 1 | 0 | 6 | 0 | 4.4s | 119.8s | 109 |
| 5 | 120s | 214 | 9:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 5.0s | 116.0s | 105 |
| 6 | 120s | 199 | 3:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 5.0s | 115.2s | 99 |
| 7 | 120s | 222 | 9:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 4.7s | 119.1s | 100 |
| 8 | 120s | 222 | 3:10 | 1 (10.0%) | 1 | 0 | 0 | 6 | 0 | 4.7s | 119.9s | 94 |
| 1 | 300s | 1614 | 9:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 4.9s | 300.0s | 350 |
| 2 | 300s | 496 | 3:3 | 0 (0.0%) | 0 | 0 | 0 | 4 | 0 | 5.1s | 299.8s | 100 |

PRD 逐条：
- **R1-1 送分占对手总得分 <30%**：seed1 **0.0%**（修复前 96.8%）；8 种子最差 seed8 **10.0%**。✓
- **R1-2 不存在同 t Mount+Drop 事件对**：10 次复跑全 0（修复前 seed1 多处）。✓
- **R1-3 抖动型种子 120s 掉台次数降到个位数量级**：seed1/3/4/7/8 = 0/0/1/0/1（修复前 seed1 约 105 次）。✓
- **R2-1 seed2@300s 无 >60s 连续静默段**：最大 **5.1s**（修复前 264.8s，末实质事件从 35.2s 推到 299.8s）。✓
- **R2-2 8 种子每场最后 60s 内仍有实质事件**：94-337 条（末实质事件 115.2-119.9s）。✓
- **跨种子双稳态分裂**：比分收敛到 3:3 / 9:3 / 10:3 / 3:10，事件量 199-1614（修复前 63↔1024、9:93↔12:3）。✓
- **build**：`& $dotnet build RobotSimulator.sln -c Release --nologo -v q` → **0 警告 0 错误**。✓
- **全量测试**：`& $dotnet test src/Sim.Tests -c Release --nologo` → **失败 0 / 通过 817 / 跳过 1 / 总计 818，28s，exit 0**（修复前基线 812+1；新增 5 个回归测试）。✓
- **replay-check**：`replays/seed-42.json` 旧基线 **失配**（`scores 3:9 (expected 5:10) events 211/302`，首个分歧 `replay: 24|183|Fsm|them` vs `record: 24|163|Drop|score`）⇒ 已重新录制并注明"**行为修复，基线再生成**"：
  `replay-record --seed 42 --out replays/seed-42.json` → `ticks=2400 score 我方 3 : 9 对手 (211 events)`；`replay-check` → `events 211/211` + **PASS**（exit 0）。✓

## 步骤 5：改动与测试清单

行为修复（3 文件，86+/30-）：
- `src/Sim.Core/Fsm.cs`：`CrisisGateFor` 清陈旧 `DropPending` + 掉台触发改用中心判据；`MountTick` 判据注释；`AttackTimeoutSeconds` / `EnterAttackFor` / `AttackTick` 超时兜底。
- `src/Sim.Core/MatchEngine.cs`：`ObjFallCheckAll` 与 tick 末尾 `WasOn`、`ScoreClockPhase` 统一为 `_field.OnPlatform`（车体中心判据）。
- `src/Sim.Core/RuntimeState.cs`：`FsmRuntime.AttackT`。

回归测试（新增 1 文件）：
- `src/Sim.Tests/BaselineP0RegressionTests.cs`（5 用例，见步骤 2/3）。

既有测试期望更新（4 文件，全部为"行为修复导致基线失效"，非为绿而绿）：
- `src/Sim.Tests/ScoreBlockExhibitionTests.cs`：`EntryTick` 369 → **97**（注释写明"10-14 基线 P0 修复重录: 掉台判据由 footprint 四角改为车体中心越界后 SEARCH/登台轨迹变化, 281→369→97"）；`documented` obs 样本改为新轨迹实测 `[0.1423, -0.1782, 0.5625, 0.5625, 0.0, 0.0275, 1.0, 1.0, 0.9596, -0.7779, -0.7837]`（旧样本 0.1273… 属旧轨迹钉死值）。
- `src/Sim.Tests/MatchRunnerExhibitionTests.cs`：`Exhibition.EntryTick` 369 → **97**（同一轨迹依赖）。
- `src/Sim.Tests/DesktopLiveDriverTests.cs`：状态串断言 `"tick=369"` → `"tick=97"`（同上）。
- `src/Sim.Tests/IncapacitatedTests.cs`：原 `events >= 1 && events <= 2` 是旧轨迹钉死值，改为 `incapEntries >= 1` + `Assert.Equal(incapEntries, events)`；`incapEntries` 按双方各自"进入 INCAPACITATED"去重统计（教训：`engine.Events` 的 Incapacitated 事件含双方，只采样 `engine.Us` 会漏算）。

回放基线再生成（2 文件）：
- `src/Sim.Tests/fixtures/godot-parity-seed42.json`（由 `replay-record --seed 42` 重录后覆盖，2400 ticks / 211 events）。
- `src/Sim.Tests/fixtures/restart-replay-seed42.json`（删除后由 `EnsureFixture()` 用新行为重新生成，禁止手改）。

登记项（**不改**，超出本次 P0 范围）：
- `src/Sim.Core/Js.cs:37-52` `Js.ToFixed(v, digits)` 先乘 10^digits 又按 `F{digits}` 格式化 ⇒ `digits>0` 时输出比真值大 10^digits 倍（`5.0→"50.0"`、`120.0→"1200.0"`、`0.6→"1"` 正确）。它同时是事件行 `t=` 显示值 = 10×秒（oracle.py:94 注释的"0.1s 单位"口径）的成因。修它会改动全部事件文本与 `t` 字段、波及 `oracle/`、`tools/` 解析与所有回放基线，故仅登记；显示值 ÷10 与墙钟时间等价，PRD 指标不受影响。
- 未提交：按任务铁律**不得 git commit/push**，提交分组建议 → ①行为修复（`src/Sim.Core/*`）②测试更新+新增回归测试（`src/Sim.Tests/*.cs`）③回放基线再生成（`src/Sim.Tests/fixtures/*.json`）。`replays/*.json` 被 .gitignore 忽略，不入库。
