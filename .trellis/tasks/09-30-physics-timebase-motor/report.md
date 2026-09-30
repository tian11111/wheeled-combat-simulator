# 物理时基修复与电机扭矩级建模 — 验收报告（2026-09-30）

## 结论

**两批全部落地，终验全绿；旧行为基线整体作废，新基线已按三轮同口径扫描重标。**

- **批 1（时基）**：MuJoCo 每裁判 tick 的物理积分从 0.02 s 回到 **0.05 s**（`SubstepsPerTick` 由 `MjcTimestep` 推导 = 25，MJCF `option timestep` 同源去重），接触时刻改用真实累计物理时间。全量单测 500 通过 + 1 跳过（新增 4 条时基测试），**变异验证可判红**（我把 `SubstepsPerTick` 改回 10，4 条中 3 条立刻红，含"每 tick 位移"行为判别式：实测 0.01424 m 落在旧慢动作值上）；6 个 tracked 回放逐位 PASS。
- **批 2（电机）**：执行器从工程值（kv=0.25 / ±3.0 N·m / ±80 rad/s）换成 2342 真值（kv=0.13687325105903 / ±1.72 N·m / ±12.566370614359172 rad/s）+ 按轮 duty 口径；哈希与守卫按纪律更新（`V1ModelSha256=3fc9685f…`，来源 `tmp/mujoco-seed42-recorded-0930.json`）；**全量 510 通过 / 0 失败 / 1 跳过**（本次重跑），6/6 回放 PASS。
- **行为扫描（11 seeds × v2 场景，同口径 `tmp/scan-metrics.py`）**：

| 轮次 | 翻覆 | 自动重启 | QACC | 跑满场 | 胜负（我方） |
|---|---|---|---|---|---|
| 批 0（0.4× 慢动作，12 s） | 11 | 9 | 0 | 10/11 | 6 胜 0 平 5 负 |
| 批 1（1:1 时基，24 s） | 10 | 6 | 0 | **11/11** | 8 胜 1 平 2 负 |
| 批 2（真车电机，27 s） | **1** | 0 | 0 | 10/11 | 4 胜 0 平 7 负 |

- **归因边界**：批 1−批 0 的差异只归因于时基（0.02→0.05 s/tick）；批 2−批 1 的差异只归因于执行器标定（两批时基均为 0.05 s/tick）。
- 两次扫描各自首跑与重跑**逐字节一致**（`cmp` 通过，另加本次第三次复跑），确定性复核通过。
- 全部改动**未提交**（工作区态），提交拆分见 §8。

## 1 验收证据总表（本次验收实际重跑）

| # | 检查 | 命令（本次实跑） | 实测结果 |
|---|---|---|---|
| 1 | 全量单测（终态） | `"C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1` | `已通过! - 失败: 0，通过: 510，已跳过: 1，总计: 511，持续时间: 9 s`；唯一 Skip = `MujocoScoreEdgeGuardTests.OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops` |
| 2 | legacy 回放逐位 | `for f in replays/*.json; do … replay-check "$f"; done` | 6/6 `PASS: replay reproduces the recorded match bit-for-bit.` |
| 3 | 性能门（不弱化） | `… test … --filter "FullyQualifiedName~TrainingResetPerformanceTests"` | `失败: 0，通过: 2`（85 ms）；门未改动，见 §6③ |
| 4 | 时基护栏 | `… test … --filter "FullyQualifiedName~MujocoTimebaseTests"` | 4/4 通过；变异实验见 §3.3 |
| 5 | 扫描指标（批 1） | `python tmp/scan-metrics.py tmp/timebase-motor-after-batch1-rerun.txt` | 翻覆 10 / 重启 6 / QACC 0 / 跑满 11/11 / 8 胜 1 平 2 负 |
| 6 | 扫描指标（批 2） | `python tmp/scan-metrics.py tmp/timebase-motor-after-batch2-rerun.txt` | 翻覆 1 / 重启 0 / QACC 0 / 跑满 10/11 / 4 胜 0 平 7 负 |
| 7 | 扫描确定性 | `cmp tmp/timebase-motor-after-batch{1,2}.txt …-rerun.txt` | 两批各自 `byte-identical` |
| 8 | 独立计时（批 2 态） | `… run --project src/Sim.Cli -- match --seeds 1,…,42 --scenario …v2.json --events > tmp/timebase-motor-report-timing.txt` | 墙钟 **26 952 ms**（`date +%s%N` 前后差），输出与批 2 重跑逐字节一致 |
| 9 | 旧 mujoco 回放门禁 | `… replay-check tmp/mujoco-seed42-replay.json` | 正确拒绝：`replay physics identity mismatch: recorded mujoco/3.14.0/1ad75271…/sim-core-1.0.2, current mujoco/3.14.0/3fc9685f…/sim-core-1.0.4.`（预期行为） |
| 10 | 新录回放身份 | 读 `tmp/mujoco-seed42-recorded-0930.json` | `seed 42 / mujoco / 3.14.0 / 3fc9685fc120…819 / core 1.0.4 / ticks 2400 / 1:2 / done=比赛时间结束 / 67 事件`，与 `MujocoVehicleMeshTests.cs:26` 常量一致 |

口径说明：批 1 的"500 通过 + 1 跳过"为实现记录，本次**未复跑该中间态**（工作区已推进到批 2，无法回退复现）；对账一致：写档基线 496 + 批 1 新增 4 条时基测试 = 500，再 + 批 2 新增 10 个电机用例 = 510（本次实测终态）。

未跑（如实声明）：RL 五 seed 吞吐套件、Godot 构建/桌面目检、真机对照（见 §6⑥）。

## 2 三轮行为扫描对比（含指标口径）

- 命令（三轮完全同参）：`… run --project src/Sim.Cli -- match --seeds 1,2,3,4,5,6,7,8,9,10,42 --scenario scenarios/wushu-ring-2026-mujoco-v2.json --events`，stdout+stderr 一并落 `tmp/timebase-motor-*.txt`。
- 指标口径（`tmp/scan-metrics.py`，仓库既有口径）：翻覆 = 含 `Incapacitated` 的事件行；自动重启 = 含 `真实重启` 的裁判行；QACC = 含 `QACC` 的告警行；跑满场 = `seed=` 行中 `done=比赛时间结束` 的场次；胜负按 `score 我方 X : Y 对手`。
- 逐 seed 比分：
  - 批 1：`4:1 / 5:3 / 6:4 / 4:3 / 1:0 / 1:0 / 5:3 / 1:11 / 0:1 / 2:2 / 4:1`（seed 42 修复后跑满全场——旧基线里它是"恢复次数超限→停车"）。
  - 批 2：`6:9 / 19:6 / 4:3 / 1:14 / 7:10 / 17:6 / 4:9 / 3:11 / 6:4 / 4:14 / 6:8`。
- 我方掉台行（`Drop … [我方] [fsm] 掉台`）：批 0 **28** → 批 1 **56** → 批 2 **38**（时基 1:1 后动作节奏真实化、我方尝试更多，掉台随之上升；电机标定后扭矩下降、冲劲变小，掉台回落）。计分掉台行（`[score] 我方掉台`）：批 0 **6** → 批 1 **6** → 批 2 **26**。
- 批 2 非满场唯一为 seed 8（`done=恢复次数超限 → 停车`，tick 已到 2400；若按 `ticks ≥ 2400` 计则 11/11）——与批 0 的 seed 42 同形态，故沿用"done 原因"口径计 10/11。
- 批 2 唯一翻覆 = seed 1 末段 t=1176（翻覆后无自动重启直到终场，该场仍 `done=比赛时间结束`）。

## 3 批 1：时基回归修复

### 3.1 根因（写档时已定位，实现按此修复）

- 09-29 的 QACC 修复只改了 MJCF 字面量：`MujocoModel.cs` 的 `option timestep="0.002"`；C# 侧 `SubstepsPerTick=10`、`SubstepSeconds=0.005` 未同步 ⇒ 每 tick 只 `mj_step` 10 次、只积分 **0.02 s**，而裁判 tick/FSM 时限/传感器/接触记账全是 0.05 s ⇒ 物理时间流速 0.4×（慢动作），`contactTime=(i+1)×0.005` 网格也与真实推进量脱节。
- 该失配自引入起就存在（`git log -G "SubstepsPerTick = "` 仅命中最初的引入提交）。

### 3.2 修复形态（单一真值）

- `MujocoModel.cs:23-32`：`MjcTimestep = 0.002`（唯一真值）→ `SubstepSeconds = MjcTimestep`（语义别名）→ `TickSeconds = 0.05` → `SubstepsPerTick = (int)(TickSeconds / MjcTimestep)`（=25）。
- MJCF `Header` 改为 `N(MjcTimestep)`（`MujocoModel.cs:191-197`），生成器里不再有第二份步长字面量；`N(0.002)` 仍输出 `"0.002"` ⇒ **批 1 不改 MJCF 字节/模型哈希**（v1 守卫自然保持绿）。
- `MujocoPhysicsBackend` 的 tick 校验收口到同一常量（`Validate`/`Step` 用 `MujocoModel.TickSeconds`），子步循环注释写明 25×0.002==0.05 的不变量。
- 接触时刻：`contactTime = (i + 1) * MujocoModel.MjcTimestep`（`MujocoPhysicsBackend.cs:147`），即 0.002…0.05 的真实累计物理时间；归属只比同 tick 内 max 接触时刻（`Physics.FinalizeBlockContacts`），语义不变、网格从 10 点变 25 点。

### 3.3 护栏与变异验证（本次实跑）

- `src/Sim.Tests/MujocoTimebaseTests.cs`（4 条）：常量同源 + 无容差乘积不变量；生成 MJCF 的 timestep 来自常量且无 0.005 残留；**行为判别式**"每 tick 实际位移" `[0.030, 0.042]`；接触时刻落在 `k×MjcTimestep` 网格且末子步=0.05。
- **变异验证**：我临时把 `MujocoModel.cs:32` 改回 `SubstepsPerTick = 10`（复现回归形态）→ `--filter MujocoTimebaseTests` 得 `失败: 3，通过: 1`：`TimebaseConstants…`（Expected 25 / Actual 10）、`MujocoTick_AdvancesOneRefereeTickOfPhysics`（实测位移 **0.01424 m**，正好落在慢动作值 ≈0.0142）、`BlockContactTimes…` 红；唯一通过的是 MJCF 同源测试（XML 未变，符合预期）。随后已按备份还原，`MujocoModel.cs` SHA-256 恢复为 `169e2b5f0f434c20e7f194b25b0c10cb99d509f2c3d07a46059b8c943dc47fa3`，工作区差异行数与变异前一致（86 行：65+/21−）。

## 4 批 2：2342 电机扭矩级标定

### 4.1 真值 → 仿真量（`MujocoModel.cs:36-57`）

| 真值 | 推导 | 常量/MJCF |
|---|---|---|
| 减速后 120 rpm | ω_noload = 4π | `MjcNoLoadSpeed = 12.566370614359172`，`ctrlrange = ±此值` |
| 输出轴堵转扭矩 1.72 N·m | τ_stall | `MjcStallTorque = 1.72`，`forcerange = ±此值` |
| — | kv = τ_stall/ω_noload | `MjcServoKv = 0.13687325105903` |
| 轮径 0.0325 m | ω_noload × r | 轮端极速 0.408407 m/s（真车 MaxSpeed 0.408） |

- 旧工程常量（kv=0.25 / ±3.0 N·m / ±80 rad/s）已删除，`grep` 无残留，避免第二份"电机真值"。
- **duty 口径**（`MujocoPhysicsBackend.cs:186-206`）：`duty = clamp((cmdV ∓ cmdW·halfTrack)/MaxSpeed, −1, 1)`，`ctrl = duty×ω_noload`，**按轮计算**（单标量 `|cmdV|/MaxSpeed` 会把原地转向压成 duty=0，与 SEARCH 闭环冲突）。
- 数学等价（已写进常量注释）：MuJoCo `<velocity>` 的 τ=kv×(ctrl−qvel) 截断到 ±τ_stall 后，在 [0, ω_noload] 上精确等价直流电机线性转速-扭矩曲线；起步扭矩 = duty×τ_stall、空载转速 = duty×ω_noload。

### 4.2 测试（`src/Sim.Tests/MujocoMotorModelTests.cs`，10 个用例，本次全绿）

- 常量推导（1.72 / 4π / kv=τ/ω，且 kv 由两真值推导）；
- MJCF 文本断言 `kv="0.13687325105903"`、`ctrlrange="-12.566370614359172 12.566370614359172"`、`forcerange="-1.72 1.72"`，且旧值 `kv="0.25"/±80/±3` 不残留；
- 转速-扭矩曲线恒等（duty=1/0.5/−0.25 三档：起步扭矩=duty×τ_stall，空载点 τ=0，中点线性）；
- 驱动路径实测（`ReadCtrl`）：直线 0.9 m/s ⇒ 两轮 ctrl=ω_noload（duty=1 饱和）；纯原地转向 ⇒ 两轮反号非零；
- 行车上界：v2 满档稳态车速 0.3554 m/s ∈ [0.30, 0.40] 且 ≤ ω_noload×0.0325=0.408407；
- 电池接口默认禁用且惰性（注入与否模型哈希相同）。

### 4.3 身份与回放

- `MujocoVehicleMeshTests.cs:20-26`：`V1ModelSha256` 更新为 `3fc9685fc120d354427e8cbf3d527d6c21551f69143aa86b1bb1d74884448919`，来源 `tmp/mujoco-seed42-recorded-0930.json`（重录，seed 42 / core 1.0.4 / 2400 tick / 1:2 / 67 事件）——本次已读文件核对一致。
- 6 个 tracked legacy 回放全部 PASS（§1#2）；旧 mujoco 回放被身份门正确拒绝（§1#9）。

### 4.4 电池压降接口（默认禁用）

- `src/Sim.Mujoco/MotorDriveOptions.cs`：`Enabled=false` 时 `VoltageScale` 恒 1；未标定（参数非正）也恒 1；不改场景 JSON/协议，仅内部构造器注入点。启用需实测 V0/R/I_stall 三者。
- 待核项（§7.2）：注释里"2342 台架实测 1.4 A"在仓库内查不到出处。

## 5 断言因果重标定（旧 → 新 + 因果）

| 断言 | 旧值/形态 | 新值/形态 | 因果 |
|---|---|---|---|
| `IncapacitatedTests` v1 翻覆 seed | 19（2026-09-29 慢动作时基下选定） | 批 1 → 13；批 2 → **155**（`IncapacitatedTests.cs:27`） | 时基 1:1 后 seed 19 最大 roll 仅 11.4°（1..40 复扫无翻覆）；批 2 电机扭矩 −43%、v1 极速被压到 0.817 m/s，seed 13 也不再翻覆，1..170 复扫选定 155（803 tick 翻覆 / 809 tick 宣告，Δ6 ≤ 30，事件 2） |
| 得分守卫 `MujocoScoreEdgeGuardTests` | Skip：09-29"FSM 登台语义" | 仍 Skip，理由更新为**实测**：批 1 = 我方掉台 15 次、0 次得分；批 2 = 掉台 0/翻覆 0/重启 0、2291/2400 tick 在台上，但两增益块均未推出台（位移 0.25/1.28 m）、全场 0 次 BlockScore | 批 2 显示"掉台"问题已解，缺口变成真车电机扭矩下推块冲量不足——属 FSM 推块策略重校，非本任务范围；未反装断言 |
| `SearchTurnCompensationTests` 参考值 | dt=0.005 时代 9.45 s/0.032 rad；09-29 慢动作 6.60 s/0.543 | 记录模式不变，参考值更新：批 1 comp=4 → 2.65 s/0.427、comp=1 → 14.10 s/0.579；批 2 候选重扫 comp=1..8 → 31.75/17.00/7.55/3.40/2.05 s | 旧数字含慢动作因子 2.5×与工程扭矩；真车电机下原地转向可达偏航率下降 ⇒ 硬门重立属 FSM 重校，本批留档 |
| `MujocoVehicleMeshTests.V1ModelSha256` | `7160897c…` | `3fc9685f…`（重录取证） | 批 2 改 MJCF 字节 ⇒ 哈希必变（批 1 刻意保持字节不变，故批 1 未触发） |

## 6 强制披露

① **旧行为基线全面作废、不可比**：批 0 的 11/11 翻覆等数字出自 0.4× 慢动作时基（每 tick 只积分 0.02 s），与修复后的数字不是同一物理问题；09-29 因"物理轨迹全变"挂起的守卫/索敌结论也已由批 1/批 2 重测覆盖（§5）。任何引用 `tmp/match-v2-*.txt` 旧日志的结论必须重新出数。

② **吞吐代价**：每 tick `mj_step` ×2.5（10 → 25 步）。同一 11-seed 扫描：批 0 12 s（该日志产出时记录）→ 批 1 24 s（实现记录）→ 批 2 **27 s**（本次独立实测，`date +%s%N` 差值 26 952 ms）。RL 五 seed 吞吐门（`.trellis/spec/sim/index.md:67-72`，≥500k transitions / 三套中位 ≤60 min）**本任务未跑**——任何训练线推进前必须重测。

③ **`TrainingResetPerformanceTests`**：门未改动（热 reset p95 ≤ 冷编译 p95 的一半），本次重跑 `通过: 2 / 失败: 0`（85 ms）；性能门的原始输出仍需在正式 CI/性能轮次复测。

④ **语义变化：FSM 速度档饱和 = 真车开环全速**。核心已按 `MaxSpeed`（0.408）截断 FSM 档位，批 2 的 duty 口径让 ≥MaxSpeed 的档（0.9/1.0/1.25 与倒车档）落到 duty=1、ctrl=ω_noload；微操档 0.35/0.4 保持比例（0.86/0.98）。`Fsm.TimeScale`（1.5/maxSpeed）在 v2 自洽（执行器真极速 0.408407 ≈ 场景 0.408）；**v1 历史场景不自洽**（maxSpeed=1.5、轮径 0.065 实际极速只有 0.817 m/s，时限偏紧）——披露不改，v1 mujoco 场景此后定位"工程验证"（已写入 spec 教训节 8）。

⑤ **电池压降接口默认禁用**：`MotorDriveOptions` 默认 `Enabled=false`、参数为 0，`VoltageScale` 恒 1；不改场景/协议。**无实测电压-电流曲线不得填数、不得启用**。

⑥ **未做事项与残余**（边界内，明确不做）：坡道几何（需真车台沿决策）、FSM 特权收敛、RL 观测特权（重训级）；由此遗留的残余——得分守卫仍 Skip（推块冲量/登台语义待 FSM 重校）、索敌补偿硬门未重立（候选 1..8 数据已留档）、v1 场景时限偏紧、RL 侧旧 PPO 权重/回放身份按门禁自然失效（本任务不重训）。另：本任务未跑 Godot 构建与桌面目检、未跑 RL 吞吐套件。

⑦ **提交边界（主会话按 2-3 个提交拆分）**：本报告写在工作区未提交态上（终态 = §1 实测），建议拆分为 ①批 1 时基（`MujocoModel.cs` 时基段 + `MujocoPhysicsBackend.cs` contactTime/tick 校验 + `MujocoTimebaseTests.cs` + `IncapacitatedTests.cs` 批 1 段 + spec 教训 6）与 ②批 2 电机（电机常量/act 生成/`SetControls` duty + `MotorDriveOptions.cs` + `MujocoMotorModelTests.cs` + 哈希/断言重标定 + spec 教训 7/8）。注意工作区里的 `AGENTS.md`（"+思考链也要用中文"，+2 行）**非本任务产物**（会话开始前已存在），提交时不要混入。`tmp/*` 全部 gitignored（扫描日志、`timescan`/`flipscan` 工具、重录回放、计时证据），需要审计留档时再 `git add -f`。

## 7 本次验收发现的问题 / 待核项（未修，交后续处理）

1. **陈旧断言文案（低危）**：`src/Sim.Tests/IncapacitatedTests.cs:83` 失败消息仍写 `seed 13`，而场景已改为 `Seed = 155`（`:27`）；仅失败时可见，建议随提交顺手改成 155 或去掉硬编码 seed。
2. **来源待核（低危，默认禁用无运行时影响）**：`MotorDriveOptions.cs:25` 注释称"2342 台架实测 1.4 A"，仓库内 grep 不到该实测出处（仅测试夹具里出现过 1.4 作占位）。若出自主会话手里的台架数据，请在提交信息/报告中补出处；否则应改为"未标定的占位示例值"。
3. **测试专用访问器（风格提示）**：`MujocoPhysicsBackend.cs:638-639` 新增 `ExposedModel/ExposedData` 仅供测试读 ctrl；`internal` + InternalsVisibleTo 与仓库既有风格一致，保留或改为 `MujocoNative` 只读助手均可。

## 8 工作区状态

- 变更清单（`git status --porcelain`，全部未提交）：`MujocoModel.cs`、`MujocoPhysicsBackend.cs`、4 个测试文件、`.trellis/spec/sim/index.md`、`AGENTS.md`（非本任务）；新增 `MotorDriveOptions.cs`、`MujocoMotorModelTests.cs`、`MujocoTimebaseTests.cs`。
- 本轮验收新增的临时证据（gitignored）：`tmp/timebase-motor-report-timing.txt`（26 952 ms 独立计时 + 与批 2 重跑逐字节一致的第三份复现）。
- 结论：**批 1 与批 2 的验收门（全量单测 / replay-check / QACC=0 / 两轮行为扫描 / 断言重标定）均已通过并留证**；未覆盖项已在 §6⑥ 如实声明。
