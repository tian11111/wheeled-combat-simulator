# 技术设计：物理时基修复与电机扭矩级建模

需求已拍板（见 `prd.md`），本文不重开需求，只写落地取舍、数值来源、披露清单与批次组织。
按两批组织：**批 1 时基单一真值化**（先行）；**批 2 电机扭矩级建模**（在批 1 的 1:1 时基上才有意义）。

## 0. 现状与根因（写档时实测）

**时基失配是事实，不是猜测。**

| 事实 | 位置 | 说明 |
|---|---|---|
| MJCF 步长字面量 = 0.002 | `src/Sim.Mujoco/MujocoModel.cs:154` | 09-29 QACC 修复改的（commit `2dd61d9`），此后未再动 |
| C# 子步常量 = 0.005 × 10 | `src/Sim.Mujoco/MujocoModel.cs:14-15` | 自 `aad1dfb` 引入以来**从未改过**（`git log -G "SubstepsPerTick = "` 仅 1 条命中） |
| 每 tick 物理推进 = 10 × 0.002 = 0.02 s | `MujocoPhysicsBackend.cs:124-126` | 裁判 tick 强制 0.05 s（`:102-104,112-118`）⇒ 慢 2.5× |
| `contactTime = (i+1) × 0.005` = 0.005…0.05 | `MujocoPhysicsBackend.cs:127` | 标称到 0.05，但该 tick 只走到 0.02 ⇒ 接触时刻与推进量不自洽 |
| 执行器仍是工程值 kv=0.25 / ±3.0 N·m / ±80 rad/s | `MujocoModel.cs:22-28`，写进 MJCF 处 `:216-219` | 80 × 0.0325 ≈ 2.6 m/s，真车 0.408 m/s |

**副作用**：v2 行为基线（`tmp/match-v2-*.txt` 系列）全部是慢动作下的结果；09-29 因"物理轨迹全变"而挂起的断言（得分守卫 Skip、索敌补偿改记录模式）需要用 1:1 时基重测才能定性。

## 决策① 时基单一真值化（批 1 核心）

一处常量、两处推导、一处引用：

```csharp
/// <summary>MJCF option.timestep —— 物理时基唯一真值(真车 2342 应用下由 QACC 稳定性选定)。</summary>
internal const double MjcTimestep = 0.002;
/// <summary>每个物理子步推进的仿真秒数, 只能等于 MjcTimestep。</summary>
internal const double SubstepSeconds = MjcTimestep;
/// <summary>每裁判 tick 的子步数: MjcTickSeconds / MjcTimestep = 0.05 / 0.002。</summary>
internal const int SubstepsPerTick = 25;
```

- MJCF `Header`（`MujocoModel.cs:151-156`）必须写成 `N(MjcTimestep)`，删除字面量 `"0.002"`——**这是防复发的关键**：XML 与 C# 不再各写一份。
- 允许的实现变体：`SubstepsPerTick = (int)(MjcTickSeconds / MjcTimestep)`（写档实测：double 下商恰为 25.0、25 × 0.002 恰为 0.05，IEEE 精确）；也可保留 `25` 字面量并用测试钉住乘积。两种都行，**不变量必须被测试钉住**：`SubstepsPerTick * MjcTimestep == 0.05`（与 `MujocoPhysicsBackend.cs:102-104` 的强制 tick 时长同源）。
- **批 1 不碰 MJCF 字节**：`N()` 用 `"R"` 格式（`MujocoModel.cs:382`），写档实测（`tmp/fmtprobe`，.NET 8）：`(0.002).ToString("R") == "0.002"`、`25 * 0.002 == 0.05` 为 True ⇒ v1/v2 模型哈希不变 ⇒ `MujocoVehicleMeshTests.V1Scenario_KeepsTheRecordedModelHash`（`:138-144`）与既有 mujoco 回放身份不被打破。这是批 1 与批 2 的关键分界（批 2 必改哈希，见决策⑥）。
- 行为效果：每 tick 物理时长 0.02 → 0.05 s（×2.5），裁判时钟/FSM 时限/传感器节拍重新对齐。

## 决策② `contactTime` 语义修正

- 修正后：`contactTime = (i + 1) * MjcTimestep`，i=0..24 ⇒ 0.002 … 0.05，末子步正好是 tick 末尾。语义 = **该子步结束时在 tick 内的累计物理时间**。
- 归属语义不变：`PhysicsWorld.FinalizeBlockContacts` 只取同 tick 内 `maxT` 并按 `Math.Abs(c.T - maxT) <= 1e-6` 判同刻，再按不同角色数判 `simultaneous`（`src/Sim.Core/Physics.cs:650-663`）。新的 0.002 网格下同刻相等依然精确（同一表达式逐子步计算，值可复现；下游 `controllers/score_block_rl/diagnose.py:55-56,323-324` 用 1e-9 容差取 max 接触时刻分组，0.002 的子步间距远大于该容差，假设继续成立）。
- 数值变化是**有意的**：RL 遥测 `blocks[].contacts[].t`（`src/Sim.Cli/RlEnvCommand.cs:632-636`）与依赖它的诊断脚本会看到新值（0.005 网格 → 0.002 网格）。披露到 report，禁止为了"旧诊断脚本数字好看"回退网格。
- 附带清理：`MujocoPhysicsBackend.cs:16-25` 的原地转向补偿注释里"dt=0.005/0.002"的叙述与 `MujocoModel.cs:44-51` 的"2.5kg"均与现状不符，属注释级修复，随批 1/批 2 顺带改正（不改行为）。

## 决策③ 电机扭矩级建模：真值 → 仿真量

来源：`scenarios/wushu-ring-2026-mujoco-v2.json` 的真车参数与设置页默认值（`src/Sim.Tests/DesktopSettingsTests.cs:126-138`：mass 3.5 / 120 rpm / 1.72 N·m / 轮径 0.0325 / 极速 0.408）。

| 真值 | 推导 | 仿真常量 |
|---|---|---|
| 减速后 120 rpm | ω_noload = 120/60 × 2π = 4π | `MjcNoLoadSpeed = 12.566370614359172` rad/s（`ctrlrange = ±` 它） |
| 输出扭矩 1.72 N·m | τ_stall | `MjcStallTorque = 1.72` N·m（`forcerange = ±` 它） |
| — | kv = τ_stall / ω_noload | `MjcServoKv = 0.13687325105903` |
| 轮径 0.0325 m | ω_noload × r | 轮端极速 0.408407 m/s（场景写 0.408，差 1e-3 相对，属舍入） |
| 单轮驱动力 | τ_stall / r | 52.92 N（爬坡富余按 09-29 报告口径） |

替换关系（旧 → 新）：`WheelServoKv 0.25 → MjcServoKv`、`WheelForceLimit 3.0 → MjcStallTorque 1.72`、`WheelAngularSpeedLimit 80.0 → MjcNoLoadSpeed`（含 `SetControls` 的 clamp 处 `MujocoPhysicsBackend.cs:173-174` 与 MJCF 生成处 `MujocoModel.cs:216-219`）。

## 决策④ duty 口径：按轮计算（实现钉死）

**公式**（`SetControls`，`MujocoPhysicsBackend.cs:157-177`，原地转向补偿 `:164-167` 之后）：

```
halfTrack = robot.Vehicle.TrackWidth / 2
duty_left  = clamp((cmdV - cmdW * halfTrack) / MaxSpeed, -1, 1)
duty_right = clamp((cmdV + cmdW * halfTrack) / MaxSpeed, -1, 1)
ctrl[i]    = duty_i * MjcNoLoadSpeed          // 左前后轮同值、右前后轮同值(保持现结构)
```

- **"同口径"的含义**：cmdW 的差速项与纵向项用同一个分母 `MaxSpeed`、同一个 `clamp(…, −1, 1)`，即 cmdW·halfTrack 先折成轮面线速度再与 cmdV 同口径合成。这样 duty 是"每轮各自的开环占空比"。
- **等价引理**（便于理解与测试，不作为实现依据）：当 `MaxSpeed == MjcNoLoadSpeed × r` 时（v2：0.408 与 0.408407，差 1e-3 相对），本式等价于旧式"先算 (cmdV ∓ cmdW·h)/r，再截到 ±ω_noload"。差别只在 MaxSpeed 与真极速不一致的场景（v1，见决策⑦）。
- **反例排除（为何不能写成单标量 duty）**：若把 `duty = clamp(|cmdV|/MaxSpeed, 0, 1)` 单标量乘到两轮，纯原地转向（cmdV = 0）的 duty 会归零——FSM 的原地转向点就是 `DriveToward(r, pos, 0, 2.0)`（`src/Sim.Core/Fsm.cs:825,1079`）与只发 W 的 `RotateTo`（`Fsm.cs:239-244`），SEARCH 闭环与 `DefaultInPlaceTurnCompensation = 4.0`（`MujocoPhysicsBackend.cs:25`）会整体失效。故 duty 必须按轮计算。**若主会话的方案原文与此读法冲突，以主会话为准并回头改本条**；本决策给出的是唯一能保住既有原地转向闭环的读法。
- **起步扭矩恒等**（验收单测）：MuJoCo `velocity` 执行器 τ = kv × (ctrl − ω)，ω = 0 时 τ = kv × ctrl = duty × (kv × ω_noload) = **duty × τ_stall**；duty = 1 时为 1.72 N·m/轮。
- 反冲/制动方向由 duty 符号自然给出（负 duty = 反向驱动，同样是开环占空比语义）。

## 决策⑤ 电池内阻/压降：只留接口，默认禁用

- 形态：MuJoCo 侧一个小 options 值类型（如 `MotorDriveOptions { Enabled = false, BatteryInternalResistanceOhm, BatteryNoLoadVoltageVolts }`），只被 `SetControls` 的 duty 折算消费；默认 `Enabled = false` ⇒ 计算路径与决策④逐位一致。
- 纪律：**不新增场景字段、不改协议**（无实测数据就不进公开接口）；注释写明"启用需要实测的电压-电流曲线，未标定前禁止填数"。
- 测试：默认禁用下生成的 MJCF 与批 2 常量路径一致（同哈希/同字符串），确保接口是惰性的。

## 决策⑥ 重放身份与守卫更新（批 2 必做）

- 批 2 改 MJCF 字节（kv/ctrlrange/forcerange 三个数变了）⇒ v1/v2 模型哈希都变 ⇒ 按仓库纪律（`.trellis/spec/sim/index.md:92-94`）**重录新模式回放**（`replay-record --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json --out tmp/...`）并更新 `MujocoVehicleMeshTests.cs:21` 的 `V1ModelSha256`，注释写新值来源文件与日期。
- 6 个 tracked 回放（`replays/*.json`）实测**全部是 legacy 身份**（header 无 `physicsBackend`，scenario 无 `physics` 字段）⇒ 批 1/批 2 都必须继续逐位 PASS；`tmp/mujoco-seed42-replay.json`（core 1.0.2 时代的 mujoco）身份失效属门禁预期，不作为回归。
- RL 侧：模型哈希变化使旧 PPO 权重/episode 身份按既有门禁自然失效；本任务不重训、不动 `controllers/score_block_rl/`，披露即可。

## 决策⑦ 披露：FSM 速度档饱和与 TimeScale 自洽性

- **速度档饱和**：FSM 的命令是 m/s 模板（0.9 冲/登台、1.25 追人、1.0 前冲、−0.6/−0.5 倒车；`Fsm.cs:462,488,515,530,586,608,968` 等），core 已按 `MaxSpeed` 截断（`src/Sim.Core/MatchEngine.cs:613`、`src/Sim.Core/Physics.cs:928`）。MaxSpeed = 0.408 时所有 ≥ 0.408 的档位（0.9/1.0/1.25 与倒车档）**全部**截到 0.408 ⇒ duty = 1 ⇒ 执行器给 ω_noload 全额目标，**等价于真车开环全速**；微操档 0.35 / 0.4（`Fsm.cs:775,1085`）低于极速，duty 保持比例（0.86 / 0.98），不属"全速"。这不是本任务引入的，但批 2 让它第一次成为"真实可达极速"的显式后果（旧 80 rad/s 上限下执行器还留着 2.6 m/s 的余量假象）。写进 report 的"语义变化"节。
- **TimeScale 评估**（`Fsm.cs:306-311`，`Math.Max(1.0, 1.5 / maxSpeed)`）：
  - **v2 自洽**：场景 maxSpeed = 0.408 ≈ ω_noload × 0.0325 = 0.408407，执行器真极速 = 车辆声明极速 ⇒ 时限缩放与实际行驶速度同源，缩放仍成立。
  - **v1 不自洽（披露）**：v1 场景 maxSpeed = 1.5、轮径 0.065（`MujocoModel.cs:16,72`）需要 23.08 rad/s，但执行器上限 12.566 rad/s ⇒ 真实极速只有 **0.817 m/s**，而 TimeScale 仍按 1.5 算（缩放 = 1.0）⇒ v1 的登台/恢复时限相对偏紧。处置：**不改**（FSM/场景都不动），在 report 披露 + 在 spec 教训节注明 v1 mujoco 场景此后属"工程验证"定位。
- 同样披露：执行器极速从 80 rad/s 收敛到 12.566 rad/s 后，**v1 与 v2 的所有 mujoco 行为基线都变了**（v1 幅度更大），这是批 2 扫描要给出的数。

## 决策⑧ 断言因果重标定纪律（两批都适用）

1. **先跑后改**：受影响断言必须实地跑过再决定改法。候选清单（`prd.md` R1.4）：`IncapacitatedTests`（翻覆 seed 由 `tmp/flipscan` 式扫描重选，历史先例：seed 42 → 19）、`MujocoScoreEdgeGuardTests.OfficialSeed42_*`（Skip 保留或解除，取决于实测；`Skip` 理由必须更新为批 1/批 2 后的真实原因）、`SearchTurnCompensationTests`（记录模式参考值重标；候选空间重扫属可选，门重立不在本任务硬性范围）、`MujocoVehicleMeshTests`（哈希守卫，批 2 更新）、`TrainingResetPerformanceTests`（**门不改**，只重测）。
2. **改断言=改因果**：每处改动在 `report.md` 写"旧值 → 新实测值 + 为什么这是新物理的必然结果"，禁止 `Skip`/放宽阈值来"修绿"，禁止改旧 fixture/旧回放/官方布局。
3. `dotnet test` 全量 + `replay-check` 每个批次各跑一次；批 1 与批 2 之间必须各留一份全量结果与扫描日志。

## 决策⑨ 扫描与归因方法（沿用既有惯例）

- 工具：`Sim.Cli match`（`src/Sim.Cli/Program.cs:141-157`），`--seeds` 逗号列表、`--scenario`、`--events`（事件逐行打印，含 `Incapacitated`/`Restart` 前缀与 `[referee] 真实重启` 行）；stdout+stderr 一并重定向（MuJoCo 的 QACC 警告走 stderr）。
- 集合：**1,2,3,4,5,6,7,8,9,10,42**（与 09-29 行为扫描同集合，日志里 `summary: matches=11` 即此）。
- 指标口径（写档实测基线，`tmp/timebase-motor-baseline-scan.txt`，墙钟 12 s）：
  - 翻覆 = 含 `Incapacitated` 的事件行数（**11**）；
  - 自动重启 = 含 `真实重启` 的裁判事件行数（**9**）；
  - QACC = 含 `QACC` 的警告行数（**0**）；
  - 跑满场 = `seed=` 行中 `done=比赛时间结束` 的场次数（**10/11**，seed 42 为"恢复次数超限 → 停车"）；
  - 比分/胜负 = `score 我方 X : Y 对手` 行与 `summary:`（**我方胜 6 : 对手胜 5**）。
- 两次扫描（批 1 后、批 2 后）+ 本基线行（批 0）三行并列进 `report.md` 对比表；**归因边界**：批 1 行与批 0 行的差异只归因于时基（0.02 → 0.05 s/tick），批 2 行与批 1 行的差异只归因于执行器（kv/τ/ω 与 duty），不得混说。
- 真实耗时参考：批 0 一轮 11 seeds 墙钟 12 s（含 dotnet run 编译缓存命中）；批 2 后每 tick 25 步（批 0 为 10 步），预计 2.5× 量级，实现时如实记录。

## 批 1 范围（文件级）

- `src/Sim.Mujoco/MujocoModel.cs`：常量三件套（`MjcTimestep/SubstepSeconds/SubstepsPerTick`）、`Header` 去掉字面量、注释修正。
- `src/Sim.Mujoco/MujocoPhysicsBackend.cs`：`contactTime` 表达式；原地转向补偿注释修正。
- `src/Sim.Tests/`：时基不变量测试（新，建议挂 `MujocoIntegrationTests` 或新文件 `MujocoTimebaseTests.cs`）＋ 断言重标定（`IncapacitatedTests` 等按实测）。
- `.trellis/spec/sim/index.md`：教训节追加"时基单一真值"。
- 不动：FSM、场景、协议、`Sim.Core` 物理、legacy 路径。

## 批 2 范围（文件级）

- `src/Sim.Mujoco/MujocoModel.cs`：电机常量三件套 + act 生成；电池 options 接口（默认禁用）。
- `src/Sim.Mujoco/MujocoPhysicsBackend.cs`：`SetControls` duty 口径 + clamp 换常量 + （可选）options 注入点。
- `src/Sim.Tests/`：电机特性单测（新文件，如 `MujocoMotorModelTests.cs`）：kv/ctrlrange/forcerange 断言、duty 恒等、原地转向 duty 非零、v2 极速 ≈0.408 m/s 的行为检查；`MujocoVehicleMeshTests` 哈希更新；其余按实测重标定。
- `tmp/`：重录的 mujoco 回放 + 第二轮扫描日志（gitignored）。
- 不动：`controllers/score_block_rl/`（RL 重训级）、场景 maxSpeed/FSM 取值、协议与 legacy。

## 兼容与回滚

- 硬门：`replays/seed-42.json` 等 6 个 tracked legacy 回放逐位 PASS；`dotnet test` 全绿；`TrainingResetPerformanceTests` 门未弱化且通过。
- 回滚粒度：批 1 是修 bug，只能整批 revert（逐半 revert 会把"XML 与 C# 两份步长"再拆开，等于重新引入失配）；批 2 可独立 revert（旧工程常量 kv=0.25 / ±3.0 / ±80 有完整历史），revert 后模型哈希回到批 1 值，回放按门禁重录。
- 明确不做（PRD 边界）：坡道几何、FSM 特权收敛、RL 观测特权、电池压降启用。

## 风险与披露

1. **训练吞吐下降**：每 tick 25 步（+150% 物理计算）。`TrainingResetPerformanceTests`（`src/Sim.Tests/TrainingResetPerformanceTests.cs:78-112`，热 reset p95 ≤ 冷编译 p95 的一半）**不许改门**；RL 五 seed 吞吐门（`.trellis/spec/sim/index.md:67-72`，≥500k transitions、中位 ≤60 min）本任务**不跑**，如实披露为未覆盖，任何训练线推进前必须先重测。
2. **行为基线全面重标**：已有基线（`tmp/match-v2-*.txt`、09-29 报告数字）在批 1 后即作废，禁止直接复用结论。
3. **v1 mujoco 场景的真实极速被压到 0.817 m/s**（TimeScale 不自洽，决策⑦）——披露，不修。
4. **RL 遥测接触时刻数值变化**（0.005 → 0.002 网格）——语义不变，数值变化需在 report 明示，避免诊断脚本误判。
5. **文档陈旧**：`MujocoModel.cs:44-51`（"2.5kg"）与 `MujocoPhysicsBackend.cs:16-25`（dt 叙述）与新状态不符，随批次修正。
6. **未跑即未覆盖**：RL 吞吐套件、桌面 Godot 目检不属本任务验收；若实现中跑不了（如 RL 套件过重）必须在 report 写"未跑"与原因。
