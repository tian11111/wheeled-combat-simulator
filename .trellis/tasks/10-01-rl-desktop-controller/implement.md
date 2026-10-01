# 执行计划（批 1-3）

前置阅读：`prd.md`（需求/验收/开放决策）、`design.md`（决策①-⑧）、`research/facts.md`
（本会话实测与 file:line 底稿）、`.trellis/spec/sim/index.md`（铁律与 RL/观测契约）。

## 环境（本机实测 2026-10-01）

```bash
# .NET SDK（系统 PATH 里的 dotnet 无 SDK，必须用项目本地安装）
export DOTNET_ROOT="C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet"   # SDK 8.0.425
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"
DOTNET="$DOTNET_ROOT/dotnet.exe"      # 本文命令里的 $DOTNET 即指它

# Python（实测：py -3.12 与 python 是同一解释器）
py -3.12 -V   # Python 3.12.10
py -3.12 -c "import torch, stable_baselines3; print(torch.__version__, stable_baselines3.__version__)"
# torch 2.13.0+cpu / stable_baselines3 2.9.0
```

## 基线（动手前先复跑存底；本目录写档时已实测一次）

```bash
"$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1
# 2026-10-01 本会话实测：已通过! - 失败: 0，通过: 525，已跳过: 1，总计: 526（38 s，exit 0）
"$DOTNET" build godot/GodotSim.csproj -m:1        # 期望 0 错误 0 警告
"$DOTNET" exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll replay-check replays/seed-42.json   # 本机 fixtures 在位
py -3.12 controllers/score_block_rl/selftest.py   # 既有 RL 自测（开工前重跑记录数字）
py -3.12 -X utf8 controllers/score_block_rl/bridge_adapter_selftest.py   # 批 2 新增后
```

`rl-env` 冒烟对照（本会话已实测的字节样本，批 1 改完必须复现）：

```bash
printf '%s\n' '{"op":"reset","seed":42}' '{"op":"step","v":0.5,"w":0.0}' '{"op":"close"}' \
  | "$DOTNET" exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll rl-env \
      --scenario scenarios/wushu-ring-2026-mujoco.json
# 期望：reset obs 长度 11、info.phase=score_block、target_name=增益块、entry_tick=281、
#       obs=[0.173,0.0865,0.5625,0.5625,0,-0.0003,1,1,0.8829,-0.7476,-0.7151]（±1e-4）
```

展演候选 checkpoint（开放决策 Q4 未拍板前默认用它做验证；记录 SHA-256）：

```
.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-20260929/checkpoints/rl_model_204800_steps.zip
# 实测：开发集 9 分/0 掉台（50 个候选中第一）；sb3 可加载，num_timesteps=204800
py -3.12 -c "import hashlib,sys;print(hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest())" <zip>
```

> 固定复现按 `policy.pth`/`policy.optimizer.pth`/`pytorch_variables.pth` 内容哈希判断，
> 不用整包 ZIP 哈希（`.trellis/spec/sim/index.md:70-72`）。

---

## 批 1：共享展演缝（Sim.Hosting）+ rl-env 委托

**目标**：SCORE_BLOCK 预推进、目标锁定、11 维观测投影成为唯一实现；rl-env 行为逐位不变。

1. 新增 `src/Sim.Hosting/ScoreBlockExhibition.cs`：类与方法签名见 `design.md` 决策①。
   - `ArmAndPreroll` 逐位复刻 `RlEnvCommand.cs:220-253` 的既有序列（Arm → CommitSnapshot →
     guard 4800 循环 → 停在我方 SCORE_BLOCK）；含 no_score_block 判定与 `EntrySnapshot` 返回。
   - `BuildObservation`/`AppendOwnPositionObservation`/`LockTargetIndex` 从
     `src/Sim.Cli/RlEnvCommand.cs:406-429,481-529` 平移（公式一字不改，参数显式化）。
   - 纪律：无 IO/时钟/RNG；不引用 Godot/Mujoco。
2. `src/Sim.Hosting/Sim.Hosting.csproj`：显式加 `<ProjectReference Include="..\Sim.Protocol\Sim.Protocol.csproj" />`（如编译需要）。
3. `src/Sim.Cli/RlEnvCommand.cs`：改委托（常量与三个方法转调共享缝），reset/step 响应字节不变；
   不改 reward/终止/事件口径。
4. `src/Sim.Tests/RlEnvCommandTests.cs:12-33` 断言迁移/保留；新增
   `src/Sim.Tests/ScoreBlockExhibitionTests.cs`：① seed 42 预推进 entry_tick=281、目标=增益块；
   ② 无 SCORE_BLOCK seed（构造一个 guard 内不进入的用例或最小场景）返回 NoScoreBlock 且不 Tick 策略；
   ③ `BuildObservation` 与 rl-env 实测样本一致（9+2 顺序/裁剪）；④ LockTargetIndex 回退分支。
5. `.trellis/spec/sim/index.md:33` 修正为 `Sim.Controller.ExternalControllerBridge`
   （CLI 侧 `PythonBridge` 为兼容包装），补一句"展演 runner 走 Sim.Hosting 纯缝 + 既有桥"。

**验证**

```bash
"$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1 --filter "FullyQualifiedName~RlEnvCommand|FullyQualifiedName~ScoreBlockExhibition"
"$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1        # 全量，期望 ≥525 通过 / 1 跳过，零失败
# 上面的 rl-env 冒烟对照（逐值）
"$DOTNET" exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll replay-check replays/seed-42.json
```

**回滚点**：本批只动 `src/Sim.Hosting/Sim.Hosting.csproj`、新增 `ScoreBlockExhibition.cs`、
`src/Sim.Cli/RlEnvCommand.cs`、`src/Sim.Tests/RlEnvCommandTests.cs`、新增
`ScoreBlockExhibitionTests.cs`、`.trellis/spec/sim/index.md`。回滚 = 还原这些路径
（新文件删除）；对训练/rl-env 无数据副作用。

---

## 批 2：runner（协议加性字段 + Python 适配器 + CLI 入口）

**目标**：`match --start-at score_block --controller-us "py -3.12 …/bridge_adapter.py --model <zip>"`
端到端跑通，faults=0；同 seed 两跑摘要一致。

1. 协议（若 Q1 选 (a)）：`src/Sim.Protocol/Observation.cs` 增可空加性字段
   （推荐 `public double[]? RlObservation { get; init; }`，camelCase `rlObservation`；
   文档注释写明"仅 SCORE_BLOCK 展演 runner 填充，普通 match/rl-env 省略"）。
   `DefaultIgnoreCondition=WhenWritingNull`（`ProtocolJson.cs:28`）保证旧消费者字节不变。
2. `src/Sim.Cli/Program.cs`：`Options` + 用法文本加旗标（Q3 定名，暂定 `--start-at score_block`）；
   `ParseOptions` 解析；`RunnerOptions` 透传。
3. `src/Sim.Cli/MatchRunner.cs`：`Options` 加 `StartAtScoreBlock`；`Run` 在 Arm 后按
   `design.md` 决策④ 预推进/交接；无 SCORE_BLOCK 直接返回 no_score_block 摘要；
   交接后每 tick 调桥（`Decide`）前给 obs 注入 `RlObservation`；
   摘要字段加 `exhibition=true`、`gateEvidenceEligible=false`、`entryTick`、`target`、`handoff`。
   默认关：所有既有调用（match/replay-record/batch）输出不变。
4. `controllers/score_block_rl/policy_contract.py`（新）：`OBSERVATION_SIZE=11`、
   `EXPECTED_OBSERVATION_SHAPE`、`action_to_vw`（与 `gym_env.py:166-167` 同源）、`load_policy`
   （PPO.load + 维度守卫，错误信息含"旧 9 维模型必须重训"）。
5. `controllers/score_block_rl/bridge_adapter.py`（新）：argv `--model`（必需）、可选
   `--timeout-ms` 提示；启动即 `load_policy`；主循环按
   `mbri_adapter.py:259-273` 先例（stdin 逐行、stdout 只写动作、stderr 诊断、flush）；
   缺 `rlObservation`/残帧 → 零动作；`predict(deterministic=True)`。
6. `controllers/score_block_rl/gym_env.py`：`step` 改用 `policy_contract.action_to_vw`（消重，
   不改变 v/w 数值）。
7. `controllers/score_block_rl/bridge_adapter_selftest.py`（新）：伪造 obs 行（含 11 维数组、
   残帧、坏行、EOF）驱动适配器（用临时小模型或 `--model` 指向由 selftest 现场保存的
   11 维 PPO 策略），断言映射/维度拒载/零动作/退出码。
8. 文档：`docs/CLI.md` match 选项表 + 示例；`docs/CONTROLLER_PROTOCOL.md` 增 "RL 策略适配器"
   小节（用法、交接、超时建议、非门禁）；`controllers/score_block_rl/README.md` 展演一节。

**验证**

```bash
py -3.12 -X utf8 controllers/score_block_rl/bridge_adapter_selftest.py     # 期望全过
py -3.12 -X utf8 controllers/score_block_rl/selftest.py                    # 既有 RL 自测零回归
MODEL=".sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-20260929/checkpoints/rl_model_204800_steps.zip"
"$DOTNET" exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed 42 \
  --scenario scenarios/wushu-ring-2026-mujoco.json --start-at score_block --events \
  --controller-us "py -3.12 -X utf8 controllers/score_block_rl/bridge_adapter.py --model $MODEL"
# 期望：exit 0；faults=0；事件里可见 SCORE_BLOCK 交接 + 之后我方无 FSM 决策事件（Manual 语义）
# 同命令再跑一次：摘要（ticks/比分/doneReason/entryTick/target）一致
# 历史路径不回归（无新旗标）：
"$DOTNET" exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed 42 --duration 3 \
  --controller-us "python controllers/example_controller.py"
```

**回滚点**：本批新增 `bridge_adapter.py`/`policy_contract.py`/`bridge_adapter_selftest.py`，
改动 `Observation.cs`、`Program.cs`、`MatchRunner.cs`、`gym_env.py` 与三份文档。回滚 = 还原这些路径；
模型/场景只读，不产生需要清理的运行时数据（**不要**跑 `evaluate.py`，避免 `.sim_runs` 盲验索引）。

---

## 批 3：桌面装配（driver 交接门控 + 测试 + 目检）

**目标**：F10 里把 us 设成 external + 上面的命令，Arm 后预推进→交接→策略驱动，HUD faults=0。

1. `godot/src/DesktopLiveDriver.cs`：
   - 新增交接状态字段（如 `_usHandoffPending/_usHandoffResult`）；`Run()` 在收到 `Arm` 命令后、
     `_engine.Phase==Running` 且 us 为 external（且启用展演）时，先 `ScoreBlockExhibition.ArmAndPreroll`；
     交接前 `StepOne` 对我方传 `null`，交接后按现状 `Decide`。
   - `DesktopLiveStatus` 追加可选 `Handoff`/`HandoffReason`（record 尾参默认值，保持既有构造兼容）。
   - 无 SCORE_BLOCK/交接失败：状态可见、不静默。
2. `src/Sim.Tests/DesktopLiveDriverTests.cs`：新增"预推进→交接"用例（可用 EchoController fixture +
   短场景；断言交接前 us 桥未被调用/无外部动作，交接后 status.UsController.Faults 正常）；
   `src/Sim.Tests/ControllerPreflightTests.cs` 保持通过（适配器残帧零动作可被预检）。
3. `godot/src/HudPanel.cs:216-226`：如需，状态行加交接描述（可选，不新增 UI 控件）。
4. 文档/spec：`docs/CONTROLLER_PROTOCOL.md`（桌面 RL 展演段）、`docs/CLI.md:44-46`（F10 推荐命令与
   TimeoutMs ≥ 500ms 建议）、`godot/README.md`（如需要）、`.trellis/spec/sim/index.md` 增展演纪律：
   "桌面 RL 展演为非门禁证据（`gate_evidence_eligible=false`）、seed 取 `scenario.Seed`、
   runner 不得引入 RNG、不得消费 v4 盲集"。

**验证**

```bash
"$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1 --filter "FullyQualifiedName~DesktopLiveDriver|FullyQualifiedName~ControllerPreflight"
"$DOTNET" build godot/GodotSim.csproj -m:1     # 0 错误 0 警告
"$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1   # 全量零回归
```

手动目检（一次，无人值守可留 capture）：

```bash
# 在仓库根启动桌面（用户参数必须在 `--` 之后）：
godot --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json
# F10 → 控制器：us = external，命令：
#   py -3.12 -X utf8 controllers/score_block_rl/bridge_adapter.py --model <v4 zip>
#   （TimeoutMs ≥ 500）；them = builtin → 预检 → Arm → 观察 HUD 策略行 faults=0、
#   我方推块/掉台等裁判事件可见。
```

**回滚点**：本批只动 `godot/src/DesktopLiveDriver.cs`、`src/Sim.Tests/DesktopLiveDriverTests.cs`、
可选 `godot/src/HudPanel.cs` 与文档/spec。回滚 = 还原这些路径；桌面无持久化状态
（设置存用户目录，改回 builtin 即恢复）。

---

## 收尾与记录

- 展演记录（写清、可审计，不进门禁）：checkpoint 路径 + `policy.pth` 三件套哈希、
  `scenarios/wushu-ring-2026-mujoco.json` 哈希、Sim.Cli.dll 哈希、seed、视觉源、命令全文、
  两次 CLI 摘要、桌面目检结论；标注 `exhibition=true` / `gate_evidence_eligible=false`。
- 不做：`git add`/`git commit`（由主会话按批提交）；不改 `splits.py`；不跑 `evaluate.py`
  盲验/冻结路径；不创建 `.sim_runs/score-block-final-holdout-v4-run.json`。
- 批间门：每批结束跑该批验证 + 全量 `dotnet test`；批 2 的 Python 自测与 CLI 双跑是本任务
  唯一"确定性"证据来源，必须留原始控制台输出。
