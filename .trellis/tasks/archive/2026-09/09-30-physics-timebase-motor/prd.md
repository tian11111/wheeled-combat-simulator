# 修复物理时基回归与电机扭矩级建模

## Goal

**一句话**：先把 MuJoCo 物理时基拉回 1:1（现在每 tick 只推进 0.02 s 仿真时间，应当是 0.05 s），再把轮驱动从"工程限幅速度伺服"换成真车 2342 电机的扭矩级模型（kv / forcerange / ctrlrange 全部由真值推导）。两批各自跑一次同一套行为扫描，作为时基归因与电机归因的证据。

**为什么先修时基**：09-29 的 QACC 修复把 MJCF 的 `option timestep` 改成 0.002（`src/Sim.Mujoco/MujocoModel.cs:154`），但 C# 侧的 `SubstepsPerTick` 仍是 10（`:15`）——每 tick 只走 10 × 0.002 = **0.02 s** 物理时间，而裁判 tick、传感器、FSM 时限、接触时刻全部按 0.05 s 记账（`MujocoPhysicsBackend.cs:102-104,112-118`），整个仿真变成 **0.4× 慢动作**；`contactTime = (i+1) * SubstepSeconds`（`MujocoPhysicsBackend.cs:127`）沿用的还是 0.005 网格的旧口径。当前所有 v2 行为基线（翻覆数/得分/索敌时长）都建立在慢动作上，不可信。

**为什么第二批不可省**：上一轮只把整车质量与 maxSpeed 换成真车值，扭矩字段"仅存档"（`.trellis/tasks/archive/2026-09/09-29-vehicle-settings-motor/report.md:30`）；执行器仍是 kv=0.25 / forcerange=±3.0 N·m / ctrlrange=±80 rad/s 的工程值（`MujocoModel.cs:22-28`），轮端极速 80 rad/s ≈ 2.6 m/s 与真车 0.408 m/s 差 6.4×，扭矩也不是真值。本轮让执行器只说真车电机的语言（12.566 rad/s / 1.72 N·m）。

## Requirements

### 批 1 — 时基修复与单一真值化

- **R1.1 单一真值**：`MjcTimestep = 0.002` 定义为**一处**常量（MuJoCo 模型侧），`SubstepSeconds` 与 `SubstepsPerTick` 只能从它推导（`SubstepsPerTick = 25`，即 0.05 / 0.002），MJCF 的 `option timestep` 也必须写这个常量而不是字面量。禁止再出现"XML 与 C# 两份步长"的第二次失配。
- **R1.2 不变量自检**：`SubstepsPerTick × MjcTimestep` 必须等于该后端强制的 tick 时长 0.05 s（`MujocoPhysicsBackend.cs:102-104`），用测试钉住这条关系（IEEE 下 25 × 0.002 == 0.05 精确成立）。
- **R1.3 `contactTime` 语义修正**：子步接触时刻 = 该子步结束时的**累计物理时间** `(i+1) × MjcTimestep`（0.002 … 0.05），与真实推进量一致。归属判定只比较同一 tick 内 max 接触时刻的相等性（`src/Sim.Core/Physics.cs:658-661`），归属语义不变；接触时刻数值会变（RL 遥测 `contacts[].t` 会看到新数值，`src/Sim.Cli/RlEnvCommand.cs:632-636`）。
- **R1.4 受影响断言因果重标定（以实测为准）**：`IncapacitatedTests`（v1 seed 19 翻覆基线，`src/Sim.Tests/IncapacitatedTests.cs:13-20`）、得分守卫（`MujocoScoreEdgeGuardTests.cs:93-120`，当前 Skip）、`SearchTurnCompensationTests`（记录模式参考值，`SearchTurnCompensationTests.cs:91-120`）、`MujocoVehicleMeshTests` 的模型哈希守卫、`TrainingResetPerformanceTests`（热 reset 门）逐一按批 1 后实测重标定；改断言必须写明因果，**不得为了过门反装或弱化断言**。
- **R1.5 身份门**：批 1 不得改 MJCF 字节（`N(0.002)` 仍须序列化为 `"0.002"`），因此 v1/v2 模型哈希不变、`replays/*` 与 `tmp/mujoco-seed42-replay.json` 身份不被打破。
- **R1.6 spec 教训回写**：把"时基单一真值"沉淀进 `.trellis/spec/sim/index.md` 的模型调校教训节（09-25 教训的追加条目）。

### 批 2 — 电机扭矩级建模

- **R2.1 真值推导的执行器参数**（2342 减速电机）：ω_noload = 120 rpm/60 × 2π = 4π = 12.566370614359172 rad/s；τ_stall = 1.72 N·m；kv = τ_stall / ω_noload = 0.13687325105903；`ctrlrange = ±ω_noload`；`forcerange = ±τ_stall`。真值语义写进常量注释（真值，非拟合）。
- **R2.2 SetControls 占空比口径**：先把 (cmdV, cmdW) 换算成各轮目标角速度，再按 `duty = clamp(该轮线速度 / MaxSpeed, −1, 1)` 缩放，`ctrl = duty × ω_noload`；cmdW 的差速项与纵向项同口径（同一分母 `MaxSpeed`）。轮端极速因此从工程 80 rad/s（≈2.6 m/s）收敛到真车 0.408 m/s（12.566 × 0.0325）。
- **R2.3 起步扭矩恒等**：ω = 0 时执行器扭矩 = kv × ctrl = duty × τ_stall（duty=1 时恰为 1.72 N·m/轮，轮面力 52.9 N），必须有单测断言这条恒等式，并断言生成 MJCF 里的 kv / ctrlrange / forcerange 三个值。
- **R2.4 披露语义变化**：FSM 内置速度档中 ≥ MaxSpeed 的档位（0.9 / 1.0 / 1.25 m/s，`src/Sim.Core/Fsm.cs:515,968`）在 MaxSpeed = 0.408 下饱和到 duty = 1，等价于真车开环全速（微操档 0.35 / 0.4 仍按比例，`Fsm.cs:775,1085`）；design 里写明，并评估 `TimeScale = 1.5 / maxSpeed`（`Fsm.cs:306-311`）在"执行器真极速 = 12.566 rad/s"下是否仍自洽（v2 自洽；v1 场景差距如实披露）。
- **R2.5 电池内阻/压降参数化接口默认禁用**：只留接口与文档钩子，默认关闭且默认路径逐位不变；**无实测数据不得发明压降数值**，不得启用。
- **R2.6 回放身份**：改 MJCF 后模型哈希会变（v1/v2 都变），按仓库纪律更新 `MujocoVehicleMeshTests.cs:21` 的 `V1ModelSha256` 并重录新模式回放；6 个 tracked legacy 回放（`replays/*.json`，均无 physics 字段）必须继续逐位 PASS。RL 链路的模型身份/旧 PPO 权重是否可用按既有门禁自然判定，本任务不重训。

### 顺序与归因

- **R3.1 批 1 先行**：批 1 落地并全量验证后，跑第一轮 11-seed 行为扫描（时基归因）。
- **R3.2 批 2 之后**：再跑第二轮同一 11-seed 扫描（电机归因）。
- **R3.3 两轮都进报告对比表**：表含 批 0（本轮写档实测基线）/ 批 1 后 / 批 2 后；指标 = 翻覆（Incapacitated 事件行）、自动重启（Restart 事件行）、QACC 警告数、跑满场数（`done=比赛时间结束`）、比分与胜负、墙钟耗时。
- **R3.4 扫描方式**：沿用仓库既有 seed 扫描惯例——`Sim.Cli match --seeds 1,2,3,4,5,6,7,8,9,10,42 --scenario scenarios/wushu-ring-2026-mujoco-v2.json --events`，stdout+stderr 一并落 `tmp/*.txt`，指标从日志抽取（命令与抽取脚本见 `implement.md`）。11-seed 集合与 09-29 行为扫描一致（`tmp/match-v2-*-10s.txt` 的 `summary: matches=11`），便于与旧记录对照。

### 验收清单

**批 1**

- [ ] 全量通过：`dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1`（写档基线：失败 0，通过 496，跳过 1，总计 497；批 1 后 Skip 数只减不增——得分守卫若仍不满足，保留 Skip 并写明实测原因）。
- [ ] `replay-check replays/seed-42.json` 逐位 PASS（legacy 身份与行为不变）；另 5 个 tracked 回放同样 PASS。
- [ ] 时基不变量单测：`SubstepsPerTick × MjcTimestep == 0.05`；生成 MJCF 的 `option timestep` 来自同一常量，并断言批 1 后 v1 模型哈希不变。
- [ ] `contactTime` 断言：末子步 = 0.05、步长 = MjcTimestep；归属语义（同 maxT 不同角色 = simultaneous）不变。
- [ ] 11-seed 扫描（第一轮）产出 `tmp/` 日志：QACC 警告 **0**；翻覆/重启/跑满场/比分新基线记录（写档基线见 `implement.md`）。
- [ ] 重标定后的断言逐条可解释：每条改动在 `report.md` 写明"旧值 → 新实测值 + 因果"。

**批 2**

- [ ] 全量通过，含新增电机特性单测：kv / ctrlrange / forcerange 断言 + `ω=0` 起步扭矩 = duty × τ_stall。
- [ ] 占空比口径单测：直线 duty=1 时 ctrl = ω_noload；原地转向（cmdV=0, cmdW>0）两轮 duty 反号且非零（不得被 duty 清零）。
- [ ] 电池接口默认禁用：默认路径生成物与批 1 逐位一致（同哈希）；接口开启后才可能改变行为（接口不改协议/场景，只留代码内钩子）。
- [ ] `replay-check` 全部 tracked 回放 PASS；新模式回放重录 + `V1ModelSha256` 更新（写明新值来源文件）。
- [ ] 11-seed 扫描（第二轮）产出 `tmp/` 日志：QACC、翻覆、跑满场、比分；与批 1 行并列进对比表。
- [ ] 披露成文：FSM 速度档饱和 → 开环全速；TimeScale 自洽性结论；v1 场景（maxSpeed 1.5 / r 0.065）真实极速被 ω_noload 压到 0.817 m/s；`TrainingResetPerformanceTests` 性能门**未弱化**且实测通过。

### 边界（明确不做）

- 坡道几何（需真车台沿决策）；FSM 特权收敛；RL 观测特权（重训级）；电池压降启用（无数据）。
- 不做：改 FSM 速度档/时限取值（TimeScale 只评估不改）、改场景 `maxSpeed`、改官方布局、改 legacy 物理、旧 fixture/旧回放/基线数字"修绿"、`fidelity.json` 晋升。

### 已知风险（design 有对应处置）

- 每 tick `mj_step` ×2.5（10 → 25 步）⇒ 训练吞吐下降；`TrainingResetPerformanceTests` 性能门不可弱化，若不达标按实测如实报告并单独定位（不得改门）。
- 行为基线全面重标：批 1 与批 2 各一轮扫描 + 断言重标定，report 必须给出两轮对比表，不得只写结论。
- 批 2 的 RL 侧影响（模型哈希变化导致旧 PPO 权重身份门禁失效）按既有门禁如实披露，不在本任务重训。

## Notes

- 证据纪律：结论先行，每条断言改动的因果与实测命令进 `report.md`；做不到的检查如实写"未跑"，不填猜测值。
- 环境：`C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe`（8.0.425）；`py -3.12`；扫描日志与临时产物落 `tmp/`（gitignored）。
- 技术取舍、批次划分与披露清单见 `design.md`；步骤、命令与验证矩阵见 `implement.md`。
- 本任务不回写 `.trellis/spec/` 以外的全局文件；`AGENTS.md` 不动。
