# RL 策略接入桌面可选控制器（SCORE_BLOCK PPO 展演）— 验收报告

- 任务目录：`.trellis/tasks/10-01-rl-desktop-controller/`（prd/design/implement/research 见同目录）
- 状态：三批实施与终验已完成，全部改动**未提交**（工作区 `git status` 可见）；本报告不 git add/commit
- 撰写：任务档案员，2026-10-01。§4 的门禁数字与 e2e 比分为**本会话实测**（命令+输出原文）；标注「ask 给定」的来自工作流收尾输入，无法在本会话复核的已如实注明

## 1. 结论

1. **三批落地**：①核心桥（`Sim.Hosting.ScoreBlockExhibition` 共享纯缝 + `Observation.rlObservation` 加性字段 + CLI `match --start-at score_block` 交接 runner）；②RL 运行器（`tools/rl-bridge/` 三件套，复用训练侧解析/映射/哈希）；③桌面装配（`godot/src/ControllerWiring.cs` 纯决策 + `DesktopLiveDriver` 预推进交接门控 + 设置/HUD + 拒绝矩阵）。
2. **本会话复跑四项门禁全绿**：全量测试 失败 0/通过 565/跳过 1/总计 566（16 s，exit 0）；`replay-check replays/seed-42.json` 逐位 PASS；`godot/GodotSim.csproj` 0 警 0 错；runner selftest 7 条腿全 PASS。**真权重 e2e**（v4 checkpoint，我方 RL 外部进程 vs 对手内置 FSM）exit=0、`faults(us/them)=0/0`、同 seed 连跑两遍摘要逐字一致：`score 我方 3 : 1 对手`、`handoff=true entryTick=281 target=增益块`。
3. **必须披露的历史红点**：ask 给定的自动化终验里 e2e 为 `exit=1`（含终验修复轮后仍 `exit=1`），本会话用同形命令复跑无法复现（见 §6.1）；因此**不得**把本轮结论写成"终验 e2e 全绿"，也不得宣称该红点已修复。
4. **交付诚实性**：评审 9 条发现（修 6、low 披露 8、未修 high/medium 1）为 ask 给定；本条明细在本会话不可取回（工作流记录不可读，工作区无落盘评审 JSON），§6 逐项披露**可独立核实**的残余与边界，不冒充原始清单。

## 2. 交付清单（逐批文件）

> 批次文件数（核心桥 15 / RL 运行器 3 / 桌面装配 15）为 ask 给定；下列文件均以本会话 `git status` / `git diff` / 读文件核实存在。批归属按工作流脚本的批范围（桥=C# 与 Sim.Tests；运行器=仅 `tools/rl-bridge/`；桌面=Godot 壳 + 设置/HUD + 无引擎测试）推定。

### 批 1 核心桥（15）

| 文件 | 作用 |
| --- | --- |
| `src/Sim.Hosting/ScoreBlockExhibition.cs`（新增，183 行） | 唯一共享缝：`ArmAndPreroll`（4800 tick/目标锁定）、`LockTargetIndex`、11 维投影 + 追加归一化 x/y；纯函数零 IO/时钟/RNG（文件头注释 :14-15） |
| `src/Sim.Hosting/Sim.Hosting.csproj` | 显式 +Sim.Protocol 引用（供 Region/Snapshot） |
| `src/Sim.Protocol/Observation.cs` | 加性 `double[]? RlObservation`（:73-82，null 不序列化）+ 有限性校验（:113-124） |
| `src/Sim.Controller/ExternalControllerBridge.cs` | stdin 显式 UTF-8（防 cp936 乱码）；新增 `LastProtocolFault`（区分"坏应答/错帧"与"超时"）；超时/零动作/fault/request-id 语义不变 |
| `src/Sim.Cli/RlEnvCommand.cs` | 删除本地预推进/目标锁定/投影，全部委托共享缝（训练入口行为不变） |
| `src/Sim.Cli/MatchRunner.cs` | `StartAtScoreBlock` 展演分支：Arm→预推进→交接→每 tick 注入 `rlObservation`；无入口返回 no_score_block 摘要且不调策略；默认关 |
| `src/Sim.Cli/Program.cs` | `--start-at score_block` 解析/用法；摘要追加 `exhibition/gateEvidenceEligible/handoff/entryTick/target`；非法取值 exit 2 |
| `src/Sim.Tests/ScoreBlockExhibitionTests.cs`（新增，242 行） | 预推进 entry tick/目标锁定/guard/无目标 reason/obs 与 rl-env reset **逐位一致**/Manual 语义 |
| `src/Sim.Tests/MatchRunnerExhibitionTests.cs`（新增，130 行） | 展演 runner 端到端（交接+注入守卫非空转+无入口不调策略+默认无展演元数据） |
| `src/Sim.Tests/RlControllerBridgeTests.cs`（新增，144 行） | 桥契约：加性序列化、UTF-8、缺字段透传、超时/错 requestId/死进程 |
| `src/Sim.Tests/RlEnvCommandTests.cs` | 旧断言迁至共享缝测试（保留 reset/step 形状与容错） |
| `src/Sim.Tests/CliTests.cs` | `--start-at` 摘要/失败路径/非法取值 exit 2 |
| `src/Sim.Tests/EchoController/Program.cs` | 新增 fixture 模式 `utf8probe`、`rlguard`（守卫非空转的反证） |
| `src/Sim.Tests/Sim.Tests.csproj` | 为 `godot/src/ControllerWiring.cs` 增加 `<Compile Include>`（无 Godot 纯逻辑链入测试；属桌面批的测试接线） |
| `docs/CONTROLLER_PROTOCOL.md` | UTF-8 编码条款 + 桌面展演小节（交接/预检/拒绝矩阵/纪律） |

### 批 2 RL 运行器（3）

| 文件 | 作用 |
| --- | --- |
| `tools/rl-bridge/rl_desktop_runner.py`（新增，23 KB） | 控制器进程：stdin obs JSONL → `predict(deterministic=True)` → 动作 JSONL；`--stub`/真权重两模式；启动即加载、维度门 exit 3、stdout 只有动作、逐帧 flush、EOF/断管道 exit 0 |
| `tools/rl-bridge/selftest.py`（新增，22 KB） | 自测 7 腿：cli contract / contract unit / stub protocol stream / broken pipe shutdown / stub determinism / real mode（现场微型 11 维 PPO）/ rl-env cross-language |
| `tools/rl-bridge/README.md`（新增，10 KB） | 接入点、契约逐帧一比一、checkpoint 解析、退出码矩阵、exhibition 语义声明、已知边界 |

### 批 3 桌面装配（ask 给定 15；下列 12 个文件 + 4 张自动截图为可核验证据，计数口径以工作流回执为准）

| 文件 | 作用 |
| --- | --- |
| `godot/src/ControllerWiring.cs`（新增，168 行） | 纯装配决策：mujoco 门控、预检 launch/protocol/timeout 三分类、来源描述；`Sim.Tests` 可无头回归 |
| `godot/src/DesktopLiveDriver.cs` | 展演交接门控：Arm 后 ArmAndPreroll→交接；未交接 Tick(null)（绝不零动作切 Manual）；进程早退回退 FSM；状态新增 `Handoff/HandoffReason` |
| `godot/src/ControllerPreflight.cs` | `ControllerPreflightKinds`（launch/protocol/timeout）+ 清理顺序（先取 LastProtocolFault 再判超时） |
| `godot/src/HudPanel.cs` | 常驻"控制器来源"行 + 未交接/退出红色告警（沿用既有 fault 行） |
| `godot/src/Main.cs` | 装配接线：`ControllerWiring.Resolve` 决定生效 profile/exhibition 标志，应用设置即预检，拒绝/告警进控制台与 HUD |
| `godot/src/SettingsPanel.cs` | 控制器区沿用既有 us/them 外部命令字段（无第二套配置），标题/提示对齐展演 |
| `godot/README.md` | 桌面展演上手（CWD=godot/、`../tools/...`、TimeoutMs ≥5000）、边界与纪律 |
| `docs/CLI.md` | `--start-at score_block` 小节（示例、摘要字段、默认不变、非门禁） |
| `.trellis/spec/sim/index.md` | 外部控制器/展演/桌面装配条款（本归档轮再校正两处，见 §8） |
| `src/Sim.Tests/ControllerWiringTests.cs`（新增，165 行） | 装配矩阵：legacy 拒绝、mujoco 保留、确定性失败回退、timeout 告警、来源命名 |
| `src/Sim.Tests/ControllerPreflightTests.cs` | 失败类别与真实 fixture 行为一致 |
| `src/Sim.Tests/DesktopLiveDriverTests.cs` | 新增"预推进→交接→Manual"与"legacy 拒绝且不拉起子进程"两条（含 rlguard 反证） |

自动截图证据（桌面批产出，位于 gitignored `godot/tmp/`）：`hud-controller.png`、`hud-legacy-reject.png`、`hud-exhibition.png`、`settings-controller.png`（均 2026-10-01 12:57–13:03）。**不替代**真窗口人工目检（§6.7）。

## 3. 验收逐条（对照 `prd.md` §验收清单）

| 验收条目 | 结果 | 证据 |
| --- | --- | --- |
| A1 共享投影后 obs 逐位不变 | ✅ | 本会话 rl-env 冒烟：`reset obs len 11 entry_tick 281 phase score_block target 增益块`，`obs=[0.173,0.0865,0.5625,0.5625,0,-0.0003,1,1,0.8829,-0.7476,-0.7151]`（与改动前基线一致）；`ScoreBlockExhibitionTests.HandoffObservation_IsBitIdenticalToRlEnvResetObs`（读码，含在全量绿） |
| A2 全量测试零回归 | ✅ | `"$D" test src/Sim.Tests/Sim.Tests.csproj -m:1 --nologo`：失败 0、通过 565、跳过 1、总计 566、16 s、exit 0 |
| A3 Godot 构建 + replay-check | ✅ | `build godot/GodotSim.csproj -m:1`：0 警告 0 错误 exit 0；`run --project src/Sim.Cli --no-build -- replay-check replays/seed-42.json`：`scores 4:49 (expected 4:49) events 752/752`、`PASS: replay reproduces the recorded match bit-for-bit.` exit 0 |
| B1 Python 适配器自测 | ✅ | `py -3.12 tools/rl-bridge/selftest.py`：7 腿全 PASS、`ALL RL-BRIDGE SELFTESTS PASSED`、exit 0（prd 中的 `controllers/score_block_rl/bridge_adapter_selftest.py` 路径已按批 2 范围改为 `tools/rl-bridge/selftest.py`，见 §5.1） |
| B2 v4 checkpoint 真权重可启动应答 | ✅ | e2e 内 runner stderr 身份行：`mode=real checkpoint=...rl_model_204800_steps.zip sha256=6d8256b242dbb45b6e9f7485d936ebcf5993a145fb43d0ddef08913dba0f12cd num_timesteps=204800 observation=(11,) action=(2,) deterministic=True` |
| C1 CLI e2e faults=0 且交接后进入 Manual | ✅（本会话） | 真权重 e2e exit 0、`faults(us/them)=0/0`、`handoff=true entryTick=281 target=增益块`、`exhibition=true gateEvidenceEligible=false`；测试侧 `ExhibitionRunner_PrerollsThenHandsOffToTheExternalPolicy`（rlguard 只在携带 11 维 rlObservation 时应答 ⇒ faults==0 钉住注入非空转） |
| C2 同命令两跑摘要一致 | ✅ | 连跑两遍真权重：两次均 `seed=42 ticks=2119 score 我方 3 : 1 对手 done=比赛时间结束(手动模式) faults(us/them)=0/0 ... handoff=true entryTick=281 target=增益块`，逐字一致，exit 0/0 |
| C3 未带新旗标的历史命令不回归 | ✅ | 全量测试（含 `MatchRunnerExhibitionTests.DefaultRunner_ReportsNoExhibitionMetadata`）+ `replay-check` 逐位 PASS；`--start-at` 默认关时摘要不追加展演字段（Program.cs:146-160） |
| D1 桌面 driver 测试 + 预检不回归 | ✅（含在全量） | 新增 `ExhibitionDriver_PrerollsToScoreBlockThenHandsOffToTheExternalPolicy`、`ExhibitionDriver_RejectsLegacyScenarioAndKeepsTheBuiltInFsm`（读码）；本会话**未单独**跑 `--filter DesktopLiveDriver`，结论来自全量 565 通过 |
| D2 真窗口人工目检 | ❌ 未执行 | 需用户在真窗口操作（设置切控制器、预检报错弹窗、HUD 告警/回退）；仅有 4 张自动截图（§2 批 3） |
| D3 非门禁标识与盲集纪律 | ✅ | 摘要打印 `gateEvidenceEligible=false`；`git status` 中 `controllers/score_block_rl/` 无任何改动；`.sim_runs/score-block-final-holdout-v4-run.json` 不存在（`test -f` → NO） |

## 4. 关键量化事实（门禁结果 + 对抗比分）

**本会话实测**（命令见 `§3`；原始输出存 `.sim_runs/rl-desktop-controller-final-gates.log`、`.sim_runs/rl-desktop-controller-e2e-repro.log`、`.sim_runs/rl-desktop-controller-e2e-repeat.log`）：

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 全量测试 | `dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 --nologo` | exit=0；失败 0 / 通过 565 / 跳过 1 / 总计 566；16 s |
| 回放逐位 | `dotnet run --project src/Sim.Cli --no-build -- replay-check replays/seed-42.json` | exit=0；scores 4:49 (expected 4:49)、events 752/752、PASS bit-for-bit |
| Godot 构建 | `dotnet build godot/GodotSim.csproj -m:1 --nologo -v q` | exit=0；0 警告 0 错误 |
| runner selftest | `py -3.12 tools/rl-bridge/selftest.py` | exit=0；7 腿 PASS |
| e2e 真权重 ×2 | `match --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json --start-at score_block --timeout-ms 5000 --controller-us "py -3.12 tools/rl-bridge/rl_desktop_runner.py --checkpoint .sim_runs/.../seed-20260929/checkpoints/rl_model_204800_steps.zip"` | 两次 exit=0；`ticks=2119 score 我方 3 : 1 对手 ... faults(us/them)=0/0 ... handoff=true entryTick=281 target=增益块`，逐字一致 |
| e2e stub（附加） | 同上但 `--stub`（无 checkpoint） | exit=0；`ticks=2119 score 我方 7 : 7 对手 ... faults(us/them)=0/0 handoff=true entryTick=281` |

**ask 给定的终验记录（本会话无法复核）**：全量测试 exit=0（565/1/566）、replay-check exit=0、Godot 构建 exit=0、runner selftest exit=0；**RL vs FSM 对抗 e2e exit=1**（第一遍未过，跳过第二遍；终验修复轮后仍 exit=1）。

## 5. 与设计的偏差

1. **运行器位置/组成**：`design.md` 建议 `controllers/score_block_rl/bridge_adapter.py` + `policy_contract.py` + selftest；落地为 `tools/rl-bridge/{rl_desktop_runner.py,selftest.py,README.md}`（工作流批 2 范围即如此指定），并**直接 import** `controllers/score_block_rl/gym_env.py` 的解析与动作映射、`train_artifacts.py` 的哈希，未另建 `policy_contract.py`。PRD 验收 B 的路径随之改名。
2. **共享层归属（开放决策 Q2 落地为"不迁桥"）**：进程桥仍在 `Sim.Controller.ExternalControllerBridge`（CLI/桌面/Sim.Tests 同一实现），`Sim.Hosting` 只承载纯缝 `ScoreBlockExhibition`。因此 spec/报告按事实写成"**桥在 Sim.Controller + 展演共享装配入口在 Sim.Hosting**"——不是"桥搬进 Sim.Hosting"；两者都满足"CLI/桌面同一入口"的意图，但字面归属不同（见 §8）。
3. **协议字段**：按 design 决策③推荐落地 `Observation.RlObservation`（加性、null 不序列化），并额外加了有限性校验（`Observation.cs:113-124`）。
4. **桌面新增设计外纪律**：legacy 场景（`physics.backend` 未写）选我方外部进程**被拒绝并回退内置 FSM**（`ControllerWiring.cs:46-93`、`DesktopLiveDriver.cs` 最后防线）；预检失败三分类，只有 `timeout` 保留外部控制器并告警。design/prd 均未含该矩阵（为本轮桌面批新增的落地决策）。
5. **e2e 绑定口径**：design 的"确定性"= 同 seed 两跑摘要一致（本会话满足）；工作流把它与 e2e ok 绑定为"确定性绿"，历史 e2e 红点见 §6.1。
6. **rl-env 边界口径微调**：进入 SCORE_BLOCK 但无有效增益块时，`entry_tick` 记录当时 tick 并显式给 `reason`（`RlEnvCommand.cs` 注释），由 `ScoreBlockExhibitionTests.Preroll_ScoreBlockWithoutValidTarget_ReportsReasonAndReachedTick` 钉住。

## 6. 残余与缺口

**6.0 评审明细不可取回（先说结论）**：本会话无法读取工作流运行记录（`GetWorkflowRun` 返回 `workflow_introspection_unavailable`），工作区也没有落盘的评审 JSON/清单；因此**无法逐条转载**那 8 条 low 与 1 条未修 high/medium 的原文。ask 给定：9 条发现、已修 6 处、low 披露 8 条、未修 high/medium 1 条。以下为可独立核实的残余/披露项（按严重度直觉排序，非原文顺序）：

1. **历史 e2e 红点未复核（最重要）**：ask 给定自动化 e2e `exit=1` 且终验修复轮后仍红；本会话以同形命令复跑 stub 一次、真权重三次（含连跑两遍），全部 exit=0 且摘要逐字一致。失败原因不可复核（工作流记录不可读），**不得写成"已修复"**；提交前需人工再跑一次 `--start-at score_block` e2e 并留原始输出。
2. **预检不覆盖首帧模型加载窗口**：runner 冷启动实测约 1–2 s（`tools/rl-bridge/README.md:36-39`），桥 `--timeout-ms` 是单帧截止；桌面默认 `TimeoutMs=100`，文档建议 ≥5000。慢机器首帧仍可能 fault（计数而非崩溃）；`ControllerWiring` 把该项归为 warning 并保留外部控制器。
3. **桌面控制器子进程 CWD=godot/**：相对脚本路径必须写成 `../tools/rl-bridge/...`（`docs/CONTROLLER_PROTOCOL.md` 桌面节、`godot/README.md`）；写错时应用设置预检当场失败并回退（响亮，不静默）。
4. **Ctrl-C/控制台控制事件收尾未逐步验证**：非交互环境 OS 会直接终止进程（实测 0xC000013A，与 runner 无关）；常规收尾路径是桥 Dispose 杀进程树（`tools/rl-bridge/README.md:160-163`）。
5. **桌面实时驱动不可位对位复现**（墙钟）：属语义边界，不是缺陷；确定性证据只在 CLI 无头路径（同 seed 两跑）。
6. **推理延迟/超时余量未压测**：本轮只在 5000 ms 下观察到两次 e2e 无 fault；慢机/负载下的 fault 行为未测（ask 也把该项列入未覆盖）。
7. **真窗口人工目检未执行**：设置切换、预检报错弹窗、HUD 告警与回退的鼠标操作未验证；仅有自动截图与测试断言（§2 批 3）。
8. **策略质量/与训练评测的等价性未验证**：本任务只证明链路、交接与确定性；未跑 `evaluate.py` 门槛、未评策略质量（exhibition 定义如此）。
9. **展演仅支持我方 RL**：对手沿用既有 external 语义、不参与展演（`ControllerWiring` 注释明确）；RL 对手未训练也未接入。
10. **两处 gate 断言依赖 Windows/EchoController fixture**：`MatchRunnerExhibitionTests`/`DesktopLiveDriverTests` 在非 Windows 直接 return（读码），跨平台 CI 下这两条不会真正执行。

## 7. 语义边界声明

- **展演 = 非门禁证据**：CLI 摘要恒 `exhibition=true gateEvidenceEligible=false`；不写 replay、不晋升 `fidelity.json`、不改 `controllers/score_block_rl/splits.py`（git status 未见改动）、不创建/消费 `.sim_runs/score-block-final-holdout-v4-run.json`（实测不存在）。
- **特权观测**：11 维含仿真真值块坐标，属特权状态，不可宣称真机可部署；本任务不做识别/策略质量宣称。
- **确定性范围**：模拟 RNG 唯一来源 `scenario.Seed`（`MatchEngine.cs:106`）；CLI 展演确定性=同 seed 两跑摘要一致（本会话实测）；桌面实时驱动按墙钟，**不可位对位复现**。
- **Sim.Core 零 IO**：Core 未新增 IO；进程/时钟只在 Sim.Cli、桌面壳、`Sim.Controller` 桥与 `tools/rl-bridge/` Python 进程；`Sim.Hosting.ScoreBlockExhibition` 是纯函数（`ScoreBlockExhibition.cs:14-15`）。
- **CI/工具边界**：`tools/rl-bridge/` 的 Python 侧门禁只有 `py -3.12 tools/rl-bridge/selftest.py`，未接入 dotnet test；其覆盖范围见 §3 B1。

## 8. spec 更新（本归档轮）

`git diff .trellis/spec/sim/index.md`（本轮校正两处，其余条款为实施轮已写、事实核对后保留）：

1. **外部控制器条款**（index.md:33-35）：补"（CLI/桌面/Sim.Tests 同一实现）"——"一律走共享桥 `Sim.Controller.ExternalControllerBridge`"原文保留（事实如此）。
2. **展演条款**（index.md:36-43）：明确**共享装配入口**是 `Sim.Hosting.ScoreBlockExhibition`（`match --start-at score_block` / `rl-env` / 桌面 driver 三方同一实现）；补加性字段 `Observation.rlObservation`（null 不序列化、只在交接后填充）；补 **IO 例外边界**："进程/时钟编排只在 Sim.Cli、桌面壳与 `Sim.Controller` 桥，RL 控制器进程本体是 `tools/rl-bridge/rl_desktop_runner.py`（Python，UTF-8、逐帧 flush）；`Sim.Hosting` 缝本身零 IO/时钟/RNG，`Sim.Core` 不得新增 IO"。
3. **未新增 spec 文件**：改动最小化，落在既有 index.md 条款内；`vision-replay-contract.md` 等既有 IO 纪律文件未动。
4. **与指令字面的差异披露**：收尾指令要求"外部控制器条款改为共享桥在 Sim.Hosting"，但落地代码的**进程桥**仍在 `Sim.Controller`（Sim.Hosting 只有纯缝）；按"只写已落地的事实"写成两层归属，避免把未发生的迁移写进 spec。
