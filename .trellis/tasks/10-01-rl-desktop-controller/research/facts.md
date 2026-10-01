# 事实底稿：RL 策略接入桌面可选控制器（2026-10-01 调研，逐条实测/读码）

> 本文件是 `prd.md`/`design.md`/`implement.md` 的证据附录；所有 file:line 以本工作树当前提交
> （HEAD=ca34ab9）为准，改动后需按 implement.md 的验证命令复核。

## 1. 控制器注入点（CLI 与桌面）

**协议（JSONL stdio，每帧一次 decide）**
- `docs/CONTROLLER_PROTOCOL.md:3-5`：外部策略独立进程，JSONL stdio，两端共享
  `src/Sim.Controller/ExternalControllerBridge` 的校验与故障语义。
- obs 字段表 `docs/CONTROLLER_PROTOCOL.md:9-24`；动作 `{v,w,requestId}` 与限幅
  `docs/CONTROLLER_PROTOCOL.md:25-38`；故障策略表 `:40-46`；batch 进程生命周期 `:48-65`。

**关于 `sim_bridge.py`（任务前提澄清）**
- 仓库根的 `rpi-yolo-pi4-int8-lto-8fps(1)/sim_bridge.py` 是**视觉流桥**（文件头声明
  `vision-stream-v1`：每行一帧 JSONL → `Sim.Cli vision live --process` / 桌面 `liveProcess`），
  **不是控制器桥**，不通过 `--controller-us` 调用；该目录被本地 `.git/info/exclude:7` 排除、未入库。
- `--controller-us "python ..."` 的既有先例是 `controllers/mbri_adapter.py`
  （`docs/CONTROLLER_PROTOCOL.md:131-141` 的 smoke/oracle/calibrated 三种口径）与
  `controllers/example_controller.py`（`docs/CLI.md:48-50`）。

**CLI 旗标与链路**
- 解析：`src/Sim.Cli/Program.cs:54`（`ControllerUs`）、`:76`（`RunnerOptions()` 映射）、
  `:104-106`（`--controller-us` / `--controller-them` / `--timeout-ms`，默认 100 ms）。
- `batch` 同类旗标：`src/Sim.Cli/BatchCommand.cs:292`、`:305`、`:396`；
  用法文本 `:444-451`。
- 装配与逐 tick 时序：`src/Sim.Cli/MatchRunner.cs:50-57`（每外部角色一个桥，先启动）、
  `:66-73`（`usBridge.Decide(engine.BuildObservation(engine.Us))`）、
  `:74`（`engine.Tick(usAction, themAction)`）、`:95-96`（faults 计数随结果返回）、
  `:100-104`（try/finally 释放全部桥）。
- CLI 的 `PythonBridge` 只是兼容壳：`src/Sim.Cli/PythonBridge.cs:7-12`，实现已下沉
  `src/Sim.Controller`（注释原文 "The implementation lives in Sim.Controller so Godot and CLI
  use the same JSONL, timeout and process-lifecycle behavior"）。

**桥实现（超时→零动作→fault；request-id 匹配）**
- `src/Sim.Controller/ExternalControllerBridge.cs:12`（一个实例一个子进程）、
  `:40-60`（`Start`，命令拆分、重定向 stdin/stdout）、
  `:62-77`（`Decide`：写 obs 行，写失败即 fault）、
  `:79`（`expectedId = observation.RequestId.ToString()`）、
  `:81-101`（截止时间内逐行取；`:87-93` 非法行动作行丢弃；`:97` requestId 为 null
  或缺省按当前帧接受、不匹配的丢弃）、
  `:102`（截止时间到 → 超时 fault）、
  `:105-110`（`Fault()`：计数 +1 并返回 `RobotAction.Zero`——超时/死管道/坏行统一零动作）、
  `:112-140`（后台读线程）、`:142-162`（Dispose 杀进程树）。
- 内核契约：`src/Sim.Core/MatchEngine.cs:5-16`（"Process lifetime, request-id matching,
  deadlines and zero-action fallback live in the adapter implementation … not in the core"）；
  `:546-552`（`Tick`，null=该角色走内置 FSM）；`:563-…`（`StepSimExt`，外部动作把角色切
  Manual，非有限动作退零）。

**桌面现状：外部控制器通道已存在（与任务前提不同，须披露）**
- `godot/src/DesktopSettings.cs:11-15`（`ControllerModes builtin|external`）、
  `:47-55`（`ControllerProfile { Mode, Command, TimeoutMs=100, IsExternal }`）、
  `:137-139`（`UsController`/`ThemController`）。
- `godot/src/Main.cs:1178-1187`（`StartLiveDriverIfConfigured`：任一路 external 即新建
  `DesktopLiveDriver`）。
- `godot/src/DesktopLiveDriver.cs:52-53`（us/them 桥）、`:176-177`（Run 内启动两桥）、
  `:263-280`（`StepOne`：Phase==Running 时对每个 external profile 调 `Decide` 再
  `engine.Tick(usAction, themAction)`）、`:282-296`（external 且未启动 ⇒ `RobotAction.Zero`，
  与 fault 同策略）、`:298-320`（启动失败记录 startupFault）、`:332-369`（状态回传
  faults/LastFault）。
- 预检：`godot/src/ControllerPreflight.cs:26-47`（拉桥 → 单次 `Decide(new Observation{RequestId=1})`
  → 有 fault 即失败；注意该 obs 是**无 robot/objects 的残帧**）。
- 测试：`src/Sim.Tests/DesktopLiveDriverTests.cs:36-70`（external driver 端到端）、
  `src/Sim.Tests/BatchCommandTests.cs:400-494`（桥的 echo/wrongid/bad/die/hang 故障面）。
- 桌面设置页：`godot/src/SettingsPanel.cs:591-592`、`:661-662`（us/them 模式+命令+超时）；
  `docs/CLI.md:44-46`（F10 设置页与 CLI 同一协议，进程每场独立）。

**明确没有控制器通道的两处（澄清用）**
- `godot/src/MatchSession.cs:143-171`（`StepLive` 只 `Engine.Tick()`，无动作注入；该类是回放/
  无外部控制器时的会话壳）；`:61-66` 构造只传 scenario+vision 工厂。
- `src/Sim.Hosting/MatchEngineHost.cs:33-50`（只做后端选择与引擎装配，无控制器概念）。
  结论：桌面注入点是 `DesktopLiveDriver`，不是 MatchSession/MatchEngineHost。

## 2. RL 观测/动作契约的唯一实现

**C# 构造（训练环境 rl-env）**
- `src/Sim.Cli/RlEnvCommand.cs:25-26`（`BaseObservationSize=9`、`ObservationSize=11`）。
- 前 9 项：`:497-508`（relForward/relLeft = 目标块相对车体坐标 / 平台 X 边长；
  bx/side、by/side；V/MaxSpeed、Omega/MaxTurnRate；onPlatform；目标在台上；remaining=timer/duration，
  全部 clamp 到 [-1,1]）。
- 末 2 项：`:514-529`（`AppendOwnPositionObservation`：`halfSide=(MaxX-MinX)/2`，
  center=平台中心；`(ownX-centerX)/halfSide`、`(ownY-centerY)/halfSide`，clamp）。
- 入口 `:481-512`（`BuildObservation`，用 `state.TargetIndex` 锁定的目标块坐标）。
- 目标锁定规则：`:406-429`（`LockTargetIndex`：先 `engine.Us.Fsm.ScoreTarget`，为空时取
  第一个"台上未出界增益块"）；入口在 `:267-268`。
- SCORE_BLOCK 入口预推进：`:241-253`（Arm 后双方 FSM，`while !Done && guard<4800`，遇
  `Us.Fsm.State==ScoreBlock` 即停）；`:256-265`（无 SCORE_BLOCK 的 seed 走 no_score_block）。

**Python 侧（消费与动作映射）**
- `controllers/score_block_rl/gym_env.py:52-56`（11 维含真值块坐标=特权状态）、
  `:84`（`observation_space = Box(-1,1,(11,),float32)`）、
  `:86-92`（子进程 `dotnet … rl-env --scenario … --duration`）、
  `:123-130`（`_observation`：解析、形状/有限性校验，失败即 RuntimeError）、
  `:158-160`（reset）、`:162-168`（**动作映射**：`v=clip(a0,-1,1)` m/s，`w=clip(a1,-1,1)*2.0` rad/s）、
  `:170-193`（`step_fsm` 基线）。
- 评测驱动：`controllers/score_block_rl/evaluate.py:141`（`policy.predict(obs, deterministic=True)`）、
  `:609-610`（`evaluation_mode=PPO deterministic`）。
- 旧维度拒载：`controllers/score_block_rl/evaluate.py:70`（`EXPECTED_OBSERVATION_SHAPE=(11,)`）、
  `:387-393`（shape 不符 → ConfigError "Models trained with the previous 9-value observation
  must be retrained"）。
- 规格：`.trellis/spec/sim/index.md:50-51`（前 9 项顺序不变，末尾追加平台中心归一化 x/y；
  改维度后旧 PPO 模型必须重训，不得静默加载）。

**本会话实测（rl-env 冒烟）**
```bash
printf '%s\n' '{"op":"reset","seed":42}' '{"op":"step","v":0.5,"w":0.0}' '{"op":"close"}' \
  | dotnet exec src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll rl-env \
      --scenario scenarios/wushu-ring-2026-mujoco.json
```
输出（节选）：reset obs 长度 11，`info.phase=score_block`，`target_name=增益块`，
`entry_tick=281`，obs=[0.173, 0.0865, 0.5625, 0.5625, 0, -0.0003, 1, 1, 0.8829, -0.7476, -0.7151]；
step 返回 obs 长度 11、reward≈-1e-4、terminated=false。

## 3. v4 checkpoint 实位与运行身份

- 产物是 **SB3 zip**，`policy.pth` 在 zip 内部（无裸 `policy.pth` 落盘；仓库内
  `find . -name policy.pth` 无命中）。已用 Python 列包验证成员：
  `data`、`pytorch_variables.pth`、`policy.pth`、`policy.optimizer.pth`、
  `_stable_baselines3_version`、`system_info.txt`。
- 五 seed 训练产物（v4 吞吐 3 套，`suite-01` 全 seed 完成；`run-config.json` 记录
  `status=completed`、`actual_global_transitions=501760`、`model_zip_sha256`）：
  `.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-<20260927|20260928|20260929|20260930|20261001>/ppo_score_block.zip`
  以及同目录 `checkpoints/rl_model_<steps>_steps.zip`（每 51,200 步一个，末点 460800）。
  另有 `suite-02`、`suite-03` 与 `-rerun/suite-01|02` 的同构副本。
- 开发集评测（10 个候选/seed，共 50 行）：
  `.sim_runs/score-block-v4-split-multiseed-20260928/dev-sweep-seed-<seed>.json`；
  实测全部 `gate_passed=false`；按"锁定目标得分多、掉台少"排序第一为
  `.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-20260929/checkpoints/rl_model_204800_steps.zip`
  （9 分、0 掉台、204800 步）。
- 已实测 SB3 可加载（见 §4）。盲验索引 `.sim_runs/score-block-final-holdout-v4-run.json`
  **当前不存在**（`test -f` 为 NO）——展演不得创建/消费它。

## 4. Python 环境（本机实测）

```bash
py -3.12 -c "import torch, stable_baselines3, gymnasium, numpy; print(...)"
# torch 2.13.0+cpu / stable_baselines3 2.9.0 / gymnasium 1.3.0 / numpy 2.5.0
python -c "import sys; print(sys.executable)"
# C:\Users\Neco\AppData\Local\Programs\Python\Python312\python.exe（与 py -3.12 同一解释器）
```
模型加载实测：
```bash
py -3.12 -c "from stable_baselines3 import PPO; m=PPO.load(r'.sim_runs/.../seed-20260928/checkpoints/rl_model_51200_steps.zip', device='cpu'); print(m.num_timesteps, m.observation_space, m.action_space)"
# 51200 Box(-1.0, 1.0, (11,), float32) Box(-1.0, 1.0, (2,), float32)
```

## 5. 场景与种子/确定性纪律

- RL 训练/评测场景：`scenarios/wushu-ring-2026-mujoco.json`（`rl-env` 默认
  `RlEnvCommand.cs:30`；`train.py:206` 默认；v4 run-config 内为绝对路径指向该文件）。
  实测 `seed=42`、`field.platform={0.7,0.7,3.1,3.1}`（side=2.4、halfSide=1.2、
  center=1.9,1.9）、`matchDuration=120`、`tickSeconds=0.05`、`physics.backend=mujoco`。
- 引擎 RNG 唯一来源：`src/Sim.Core/MatchEngine.cs:106`（`Mulberry32(scenario.Seed)`）；
  FSM 经 `() => _rng.Next()` 消费（`:148`）。注意 `ClassifyRateVision` 即使成功率 100%
  也消费一次 RNG（`src/Sim.Core/Fsm.cs:74-75`），所以"同条件对比"必须锁定视觉源。
- 展演语义：非门禁证据（用户给定，等价 `gate_evidence_eligible=false`，先例
  `controllers/score_block_rl/evaluate.py:327`、`controllers/score_block_rl/README.md:42`、
  `.trellis/spec/sim/index.md:60`）；桌面 seed 直接取 `scenario.Seed`
  （`godot/src/Main.cs:1103-1105` 无场景文件时 `new Scenario{Seed=Seed}`，默认 42）。
- 分线纪律：不得改 `controllers/score_block_rl/splits.py` 与盲集注册
  （`.trellis/spec/sim/rl-split-contract.md:1-6`）。

## 6. 分层与共享归属现状

- `src/Sim.Hosting/Sim.Hosting.csproj` 当前只引 `Sim.Core` + `Sim.Mujoco`；
  `MatchEngineHost.Create`（`src/Sim.Hosting/MatchEngineHost.cs:33-50`）是 CLI/Godot 的
  物理后端装配入口（`godot/GodotSim.csproj` 与 `src/Sim.Cli/Sim.Cli.csproj` 均引用）。
- `src/Sim.Controller`（`Sim.Controller.csproj` 引 Sim.Core+Sim.Protocol）已被
  `src/Sim.Cli`、`src/Sim.Tests`、`godot/GodotSim.csproj` 共同引用——进程 IO 已共享，
  不需要为桌面重做；spec `.trellis/spec/sim/index.md:33` 的 "一律走 Sim.Cli.PythonBridge"
  是过时措辞（2026-09-01 起实现已下沉，commit 1053e8d）。
- Sim.Core 边界：铁律 `.trellis/spec/sim/index.md:8-9`；现实中的单文件例外是视觉进程源
  `src/Sim.Core/ExternalProcessStreamSource.cs`（契约 `.trellis/spec/sim/vision-replay-contract.md:40-42,65,104`）。
  RL 展演不得再往 Sim.Core 加进程/时钟/IO。
