# 技术设计：RL 策略接入桌面可选控制器

需求已拍板（见 `prd.md`），本文不重开需求，只写落地取舍。批组织：**批 1 共享缝与契约；
批 2 runner（CLI 无头 + Python 适配器）；批 3 桌面装配与文档**。所有 file:line 依据见
`research/facts.md`（本会话实测/读码）。

## 0. 数据流总览

```
                    ┌─ 训练/评测（既有，不改语义）：rl-env JSONL ←→ gym_env.py ←→ SB3
                    │
scenarios/wushu-ring-2026-mujoco.json（seed=42，mujoco 后端）
        │
        ▼
MatchEngineHost.Create(scenario, vision?)         ← 唯一物理后端装配入口（src/Sim.Hosting/MatchEngineHost.cs:33-50）
        │
        ▼
MatchEngine（Core，零 IO；RNG 只来自 scenario.Seed，MatchEngine.cs:106）
        │  Arm → FSM 预推进到「我方首次 SCORE_BLOCK」   ← 共享缝 ScoreBlockExhibition（批 1 新增，Sim.Hosting）
        │  目标锁定（Us.Fsm.ScoreTarget → 回退首个台上增益块）＋ 11 维观测投影（唯一实现）
        ▼
交接：此后我方动作 = 外部策略进程；对手仍 Tick(null,...) 走内置 FSM
        │
        ├─ CLI：MatchRunner（新旗标 --start-at score_block，默认关=逐字节不变）
        │        └─ ExternalControllerBridge（Sim.Controller，每角色一进程/一场）
        │               └─ py -3.12 controllers/score_block_rl/bridge_adapter.py --model <zip>
        │                        └─ PPO.load + predict(deterministic=True) → {v, w, requestId}
        └─ 桌面：DesktopLiveDriver（同一交接门控 + 同一桥）
                 └─ F10 设置页 usController=external（现有字段，不新增配置）
```

## 决策① 共享层与归属：桥不迁，新增 Sim.Hosting 纯展演缝

**事实核对（与任务前提的差异，必须披露）**：外部控制器桥的共享实现**已经在**
`src/Sim.Controller/ExternalControllerBridge.cs`，且 CLI（`Sim.Cli.csproj`）、桌面
（`godot/GodotSim.csproj`）、测试（`Sim.Tests.csproj`）三方都已引用；`src/Sim.Cli/PythonBridge.cs:7-12`
只是兼容壳（2026-09-01 commit `1053e8d` 下沉，见 `research/facts.md` §1/§6）。
`Sim.Hosting` 目前的职责是**物理后端装配**（spec `.trellis/spec/sim/index.md:39`），
不含进程 IO。

**决定（推荐 Q2 选"不迁"）**：批 1 只在 `Sim.Hosting` 新增**纯函数**展演缝
`src/Sim.Hosting/ScoreBlockExhibition.cs`（建议类名，实现时可微调）：

```csharp
namespace Sim.Hosting;

/// SCORE_BLOCK 展演共享缝：与 rl-env 同语义的入口预推进、目标锁定、11 维观测投影。
/// 纯函数：无 IO、无时钟、无 RNG；调用方（CLI/桌面/Python 侧）只提供引擎与显式参数。
public static class ScoreBlockExhibition
{
    public const int BaseObservationSize = 9;
    public const int ObservationSize = 11;
    public const int PrerollMaxTicks = 4800;   // RlEnvCommand.cs:245 的 guard

    public readonly record struct PrerollResult(
        bool NoScoreBlock, string? Reason, int EntryTick, int TargetIndex,
        Snapshot EntrySnapshot);

    public static PrerollResult ArmAndPreroll(MatchEngine engine, int maxTicks = PrerollMaxTicks);
    public static int LockTargetIndex(MatchEngine engine);
    public static double[] BuildObservation(MatchEngine engine, int targetIndex,
        Region platform, double matchDuration, bool usOnPlatform, double timer);
    public static double[] AppendOwnPositionObservation(double[] observation,
        double ownX, double ownY, Region platform);
}
```

- `ArmAndPreroll` 必须逐位复刻 `RlEnvCommand.Reset` 的既有序列（`RlEnvCommand.cs:220-253`）：
  `engine.Arm()` → `engine.CommitSnapshot()` → `while (!Done && guard < 4800) { if
  (Us.Fsm.State == ScoreBlock) break; snap = engine.Tick(); guard++; }`；完成后
  `NoScoreBlock = Done || Us.Fsm.State != ScoreBlock`；`EntryTick = (int)engine.TickIndex`。
  返回 `EntrySnapshot` 供调用方读 `Robots[Us].OnPlatform` 与 `Timer`（`RlEnvCommand.cs:287` 同源）。
- `BuildObservation` = `RlEnvCommand.cs:481-512` 的函数体（显式参数化 platform/duration/
  usOnPlatform/timer，去掉 `EpisodeState`）；`AppendOwnPositionObservation` = `:514-529` 原样。
- `src/Sim.Cli/RlEnvCommand.cs` 改为委托：常量与两个方法转调共享缝，其余（reset/step 状态机、
  reward、trace）不动；`src/Sim.Tests/RlEnvCommandTests.cs:12-33` 的断言迁移到新类型或经委托保留，
  行为逐位不变（对照本会话实测样本：seed 42 → entry_tick=281、obs len 11）。
- `Sim.Hosting.csproj` 增 `ProjectReference` 到 `Sim.Protocol`（显式，不用传递引用）。

**若 Q2 仍决定迁桥入 Sim.Hosting**，完整文件清单（工作量/风险都大，不建议）：
`src/Sim.Controller/ExternalControllerBridge.cs`（移动改命名空间）、删 `src/Sim.Controller/*`、
`RobotSimulator.sln:12`、`src/Sim.Hosting/Sim.Hosting.csproj`（+Sim.Protocol，删 Sim.Mujoco 引用与否另议）、
`src/Sim.Cli/Sim.Cli.csproj`、`src/Sim.Cli/PythonBridge.cs`、`godot/GodotSim.csproj`、
`godot/src/DesktopLiveDriver.cs`、`godot/src/ControllerPreflight.cs`、`src/Sim.Tests/Sim.Tests.csproj`、
`docs/ARCHITECTURE.md:30,173-174`、`docs/MIGRATION.md:18`、`.trellis/spec/sim/index.md:33`。
（`using Sim.Controller` 的消费文件全仓库只有 3 个，见 facts §1/§6。）

## 决策② SCORE_BLOCK 入口与交接语义（契约保真的核心）

- **为什么必须交接**：策略只在"我方 FSM 已进入 SCORE_BLOCK、目标已锁定"的状态分布上训练
  （`RlEnvCommand.cs:14,241-253`；`evaluate.py:67` 也把该入口写进协议描述）。桌面默认是从
  发令开局跑整场；直接让外部动作从 tick 0 生效会把角色切 Manual（`src/Sim.Core/MatchEngine.cs:554-…`：
  任何非 null 动作 ⇒ `st.Manual = true; st.State = Manual`），预推进（登台/索敌）永不发生。
- **门控规则（CLI 与桌面同一条）**：`Arm` 之后、`ScoreBlockExhibition.ArmAndPreroll` 完成之前，
  我方 `Tick` 参数必须为 `null`；完成之后每 tick 调桥 `Decide`，对手全程 `null`。
- **交接帧**：第一帧交给策略的观测 = 预推进结束时的 `BuildObservation(..., targetIndex, ...)`；
  与 rl-env `reset` 返回的 obs 同语义（同一 `entry_tick`，同一目标索引）。
- **Manual 语义沿用**：交接后我方 FSM 不再产生决策事件（`docs/CONTROLLER_PROTOCOL.md:115-123`），
  验收用"交接前无外部动作、交接后我方 FSM 事件停止"作为证据。
- **桥的生命周期**：保持现状（每角色一进程、一场一 Dispose，`MatchRunner.cs:50-57,100-104`；
  `DesktopLiveDriver.cs:176-177,220-230`）。预推进期间桥可以不 Decide，但进程可先启动
  （模型加载与仿真解耦，预检也能提前发现启动失败）。若 Q1 选 (b) argv 传目标，则改为
  "预推进完成后再 `Start` 桥"——两种时序都必须在 runner 与 driver 里保持一致。

## 决策③ 观测契约唯一实现：C# 投影 ＋ 加性协议字段，Python 适配器只做搬运

- **唯一实现**：11 维投影只保留 `Sim.Hosting.ScoreBlockExhibition.BuildObservation` 一份；
  `rl-env` 委托它。任何 Python 侧重算（bx/by、relForward/relLeft、归一化 x/y）都是第二实现，
  一律禁止。
- **承载（推荐 Q1 选 (a)）**：`Observation` 增一个可空加性字段承载该帧的 RL 观测/锁定目标，例如
  `public double[]? RlObservation { get; init; }`，或 `public EnergyBlockView? LockedTarget`。
  取舍：
  - 只加 `RlObservation`（11 维数组）：适配器最小（直接喂 SB3），契约保真"由构造保证"；
    代价是协议里出现 RL 专用字段（提名为 `rlObservation`，仅展演路径填充）。
  - 只加 `LockedTarget`（块坐标）：协议更通用，但 Python 要重算 11 维 ⇒ 第二实现风险，
    需额外等价测试（对同一引擎状态逐位比对）才敢用。
  - **推荐**：加 `RlObservation`（并在文档说明"由 SCORE_BLOCK 展演 runner 填充，普通
    match/rl-env 不带"）；字段为 null 时 `WhenWritingNull` 直接省略
    （`src/Sim.Protocol/ProtocolJson.cs:28`），旧消费者/旧测试字节不变（协议只加不改）。
  - 填充点**不放 `MatchEngine`（Core）**：由 runner/driver 在 `engine.BuildObservation(robot)`
    之后 `with { RlObservation = ScoreBlockExhibition.BuildObservation(...) }` 注入
    （`Observation` 是 record）。这样 Sim.Core 零改动，展演语义留在宿主层。
- **Python 侧**：
  - 新 `controllers/score_block_rl/policy_contract.py`：`OBSERVATION_SIZE=11`、
    `EXPECTED_OBSERVATION_SHAPE=(11,)`（与 `evaluate.py:70` 同源或直接 import）、
    `action_to_vw(action) -> (v, w)`（`v=clip(a0,-1,1)`、`w=clip(a1,-1,1)*2.0`，与
    `gym_env.py:166-167` 同源——建议 `gym_env.py` 改为 import 这里，消重）、
    `load_policy(path)`（`PPO.load` + shape 守卫，非 11 维抛错并列出重训指引）。
  - 新 `controllers/score_block_rl/bridge_adapter.py`：stdin 逐行 JSON → `obs["rlObservation"]`
    → `policy.predict(..., deterministic=True)` → `action_to_vw` → stdout
    `{"v":…, "w":…, "requestId": obs["requestId"]}`（`flush=True`）；诊断只写 stderr；
    **残帧/缺 `rlObservation` ⇒ 立即零动作**（覆盖 `ControllerPreflight` 的
    `Observation{RequestId=1}` 与预推进前的任何帧）；坏行计 fault、回零动作（照
    `mbri_adapter.py:259-273` 先例）。
  - 新 `controllers/score_block_rl/bridge_adapter_selftest.py`：不依赖 mujoco 的最小自测
    （伪造 JSONL obs 序列 + 小型 11 维 PPO 模型或权重桩），命令 `py -3.12 …/bridge_adapter_selftest.py`。

## 决策④ CLI runner（批 2）：`match` 复用 + 新旗标，默认行为不变

- `src/Sim.Cli/Program.cs`：`Options` 增 `StartAtScoreBlock`（解析见 `Program.cs:54-106` 现式），
  传入 `MatchRunner.Options`。
- `src/Sim.Cli/MatchRunner.cs:43-105` 增分支：`StartAtScoreBlock == true` 时
  ① 先 `engine.Arm()`；② 调 `ScoreBlockExhibition.ArmAndPreroll`；③ 无 SCORE_BLOCK 的 seed
  直接返回 `no_score_block` 摘要（沿用 rl-env 口径，别静默跑成整场）；④ 再进入正常
  `while (!Done)` 循环（交接后每 tick Decide）；桥启动时机按 Q1 决定（默认维持 `:50-57` 先启动）。
- **默认关**：不加旗标时 `MatchRunner` 逐行不变 ⇒ `match`/`replay-record`/`batch` 三者行为不变
  （`BatchCommand.cs:292,305,396` 的选项不受影响）。
- 输出：沿用现有每 seed 一行摘要，追加 `entryTick`/`target`/`handoff`（是否完成交接）字段；
  `--events` 打印的 Manual 事件可作验收证据。
- 摘要里显式带 `exhibition=true`、`gateEvidenceEligible=false`（R6）。

## 决策⑤ 桌面装配（批 3）

- **driver 门控**：`godot/src/DesktopLiveDriver.cs`
  - 新增"我方 RL 交接"状态：仅当 `_usProfile` 为 external 且场景启用展演时，
    `Run()` 在 `Arm` 命令之后先跑 `ArmAndPreroll`（在 worker 线程内），期间 `StepOne` 对 us
    传 `null`；交接完成后按 `:270-277` 现状逐 tick `Decide`。
  - 对手 unchanged（external them 仍照旧从 Running 起决定；RL 展演默认 them=builtin）。
  - 无 SCORE_BLOCK ⇒ 状态里给明确 reason（不静默），HUD/`DesktopLiveStatus` 追加一个可选字段
    （`Handoff`/`HandoffReason`），不破坏既有记录形状（C# record 加可选参数即可）。
  - 预检：`ControllerPreflight.Run`（`ControllerPreflight.cs:26-47`）用残帧 obs；适配器零动作
    快速应答即可通过；预检**不覆盖**首帧模型加载时间（`Start()` 内加载），文档写"预检通过 ≠
    单帧预算通过"，建议 TimeoutMs ≥ 500 ms（Q6）。
- **设置页**：不新增字段（`SettingsPanel.cs:591-592,661-662` 现有 mode/command/timeout 足够）；
  文档给出推荐命令与超时。
- **seed**：取场景文件的 `seed`（`scenarios/wushu-ring-2026-mujoco.json` = 42）；桌面无 seed 覆盖
  UI（`Main.cs:26,1103-1105`），展演不需要（exhibition 非门禁）。
- **HUD**：`HudPanel.cs:216-226` 已在双方 external 时显示 faults/LastFault；RL 策略 fault 可见，
  不新增 UI；如需交接状态可并入同一行。
- **桌面上手方式**：`--scenario-path scenarios/wushu-ring-2026-mujoco.json`（`godot/src/Main.cs:89-93`），
  F10 设置 us=external + adapter 命令，Arm 前预检。

## 决策⑥ 确定性、RNG 与证据口径

- **seed**：`scenario.Seed`（`MatchEngine.cs:106`）；展演只允许"同场景 + 同 seed + 同 checkpoint"
  的重复，不新增 seed 参数。
- **RNG 禁止面**：C# runner/driver 不得 `new Random()`、不得按墙钟抽样；Python 适配器不得 import
  `random`/`np.random`，`predict(deterministic=True)`。模拟侧唯一 RNG 仍是引擎的 Mulberry32 与
  FSM 的 `_rng()`（`MatchEngine.cs:106,148`）。
- **视觉源**：默认 `classifyRate` 桩（`SimParameters.cs:17`），保持与训练一致；注意
  `ClassifyRateVision.Classify` **无条件消费一次 RNG**（`Fsm.cs:74-75`），换视觉源会移位对手
  FSM 的随机流 ⇒ 只能标"不同条件探索"（Q5）。
- **可复现边界**：CLI 无头路径声明为"同 seed 两跑摘要一致（faults=0）"；桌面实时驱动按墙钟
  （`DesktopLiveDriver.cs:180-213`）声明为不可位对位复现的展演，**不写 replay**、
  不晋升 `fidelity.json`、不产出 `gate_evidence_eligible=true` 的任何东西。
- **盲集纪律**：不触碰 `.sim_runs/score-block-final-holdout-v4-run.json`（当前不存在）、
  `splits.py` 零改动（`.trellis/spec/sim/rl-split-contract.md:1-6`）。

## 决策⑦ 失败与边界矩阵（实现时逐条钉测试）

| 情况 | 行为 |
| --- | --- |
| 模型文件缺失/加载失败 | 适配器启动即非零退出 + stderr 指名路径；桥记启动失败（CLI `ControllerStartException`，桌面 startupFault） |
| 模型维度 ≠ 11 | 适配器拒载并提示重训（口径同 `evaluate.py:387-393`）；绝不静默 |
| seed 预推进 4800 tick 未进 SCORE_BLOCK | runner 返回 `no_score_block` 摘要、不跑策略（口径同 `RlEnvCommand.cs:256-265`） |
| 交接前收到 obs（含预检残帧） | 适配器零动作立即应答；不崩溃、不超时 |
| 策略单帧超时（CPU 抖动） | 桥零动作 + fault 计数（`ExternalControllerBridge.cs:102-110`）；HUD/摘要可见 |
| 适配器 stdout 混入日志 | 非动作行被桥丢弃（`ExternalControllerBridge.cs:87-93`）但会占缓冲；适配器必须"动作只写 stdout、诊断只写 stderr" |
| 桌面无 mujoco 场景/无 SCORE_BLOCK | 状态显示 handoff 失败原因；不静默用内置 FSM 假装展演成功 |
| 同 seed 两跑摘要不一致且 faults=0 | 视为 runner 引入 RNG/时钟的缺陷，必须查（R7 验收） |

## 决策⑧ 测试与验证布局

- C#：`src/Sim.Tests/RlEnvCommandTests.cs` 迁移/扩展（9+2、裁剪、目标回退、preroll guard）；
  新增 `ScoreBlockExhibitionTests`（预推进含已知 seed 的 entry tick、无 SCORE_BLOCK 路径、
  obs 与 rl-env 对照）；`DesktopLiveDriverTests`（预推进→交接门控：交接前 us 无外部动作）；
  `ControllerPreflightTests` 不回归。
- Python：`bridge_adapter_selftest.py`（stub 模型/权重，不依赖 mujoco；覆盖映射边界、残帧、
  坏行、维度拒载）。
- e2e：CLI 命令两次运行摘要对比；桌面手动目检一次（见 implement.md 批 3 命令）。
- 回归门：全量 `dotnet test`、`dotnet build godot/GodotSim.csproj`、
  `replay-check replays/seed-42.json`、`py -3.12 controllers/score_block_rl/selftest.py`
  （既有 44 passed / 0 failed / 8 skipped 口径参照 09-29 任务记录，开工前重跑存底）。
