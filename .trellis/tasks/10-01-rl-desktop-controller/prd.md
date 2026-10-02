# RL 策略接入桌面可选控制器（SCORE_BLOCK PPO 展演）

## Goal

把已训练的 SCORE_BLOCK PPO 策略（特权 11 维观测）作为**可选的外部控制器**接进桌面壳：
**我方 = RL 外部进程，对手 = 内置 FSM**。用途是**展演/目检**（exhibition）：证明"训练产物能
在桌面仿真里以真实协议驱动我方"，不产生任何门槛证据（`gate_evidence_eligible=false`），
不打开、不消耗 v4 盲集。

同一条链路提供 **CLI 无头入口**先做端到端验证（可脚本、可复跑、可留日志），桌面装配复用同一
共享实现，不为桌面复制一份 RL 推理/观测逻辑。

事实前提（详见 `research/facts.md`，含 file:line）：

- 外部控制器桥**已经共享**：`src/Sim.Controller/ExternalControllerBridge.cs:12-162`，
  CLI（`src/Sim.Cli/MatchRunner.cs:43-105`）与桌面（`godot/src/DesktopLiveDriver.cs:263-320`）
  共用同一实现；`src/Sim.Cli/PythonBridge.cs:7-12` 只是兼容壳。
- 桌面**已经能**接外部进程控制器（`godot/src/DesktopSettings.cs:47-55,137-139`、
  `godot/src/Main.cs:1178-1187`、`godot/src/ControllerPreflight.cs:26-47`），
  缺的是 RL 专属件：SCORE_BLOCK 入口交接、训练同语义观测、策略适配器进程、展演口径。
- 观测/动作契约唯一实现：构造在 `src/Sim.Cli/RlEnvCommand.cs:25-26,481-529`，
  消费/映射在 `controllers/score_block_rl/gym_env.py:84,123-130,162-168`；
  旧维度模型必须拒载（`controllers/score_block_rl/evaluate.py:70,387-393`）。

## Requirements

- **R1 共享展演缝（Sim.Hosting，纯函数）**：把 SCORE_BLOCK 入口与 RL 观测投影抽成
  `Sim.Hosting` 内的共享实现，rl-env / CLI 展演 runner / 桌面 driver 三方共用：
  ① `Arm + FSM 预推进至我方首次 SCORE_BLOCK`（与 `src/Sim.Cli/RlEnvCommand.cs:241-253`
  逐位同语义：guard 4800 ticks、进入即停）；② 目标锁定规则与 `:406-429` 相同
  （先 `Us.Fsm.ScoreTarget`，为空回退"第一个台上未出界增益块"）；③ 11 维观测投影
  （前 9 项 + 末尾平台中心归一化 x/y）**只有一份实现**，`RlEnvCommand` 改为委托，
  行为逐位不变（由既有 `RlEnvCommandTests` 钉住并扩展）。该缝不得含 IO/时钟/RNG。
- **R2 交接契约（外部策略拿到与训练同语义的观测）**：runner 只在我方进入 SCORE_BLOCK 后
  才把外部动作交给 `MatchEngine.Tick`；此前该角色必须以 `null` 动作预推进（否则
  `StepSimExt` 会把角色切 Manual，见 `src/Sim.Core/MatchEngine.cs:554-…`，预推进不再发生）。
  策略进程在交接时必须能确定"锁定的目标块"（训练口径 `state.TargetIndex`）；
  承载方式见开放决策 Q1，无论选哪种都必须有单一实现与等价测试。
- **R3 Python 策略适配器**：新增 `controllers/score_block_rl/bridge_adapter.py`：
  ① 启动即加载 SB3 模型（`PPO.load`），加载失败/维度不符以非零退出并写 stderr；
  ② 观测维度守卫与 `evaluate.py:70,387-393` 同口径（非 11 维拒载，绝不静默）；
  ③ 动作映射与 `gym_env.py:166-167` **同源**（`v=a0` m/s、`w=2*a1` rad/s，
  其余交给内核按车辆 profile 限幅）；④ `policy.predict(..., deterministic=True)`
  （先例 `evaluate.py:141`）；⑤ 非交接帧/残帧（如 `ControllerPreflight` 的
  `Observation{RequestId=1}`）必须零动作快速应答，不得崩溃或超时；⑥ 逐行 flush，
  诊断只写 stderr。
- **R4 CLI 展演 runner（无头先行）**：在既有 `match` 链路上提供显式入口
  （建议旗标 `--start-at score_block`，命名见开放决策 Q3；默认关闭时行为逐字节不变），
  使 `--controller-us "py -3.12 controllers/score_block_rl/bridge_adapter.py --model <zip>"` 能：
  预推进 → 交接 → 策略驱动 → 对手 FSM，并输出 ticks/比分/doneReason/双方 faults/
  交接信息（entry tick、目标块）。seed 直接取 `scenario.Seed`
  （展演语义，非门禁；依据见 `research/facts.md` §5）。
- **R5 桌面装配**：`DesktopLiveDriver` 对配置为 external 的 RL 角色执行同一预推进/交接门控
  （Arm 后先用 FSM 预推进，交接后再逐 tick `Decide`）；HUD 复用既有
  `DesktopControllerStatus`（faults/LastFault，`godot/src/HudPanel.cs:216-226`）。
  设置页沿用既有 external 控制器字段（`godot/src/SettingsPanel.cs:591-592,661-662`），
  不新增第二套配置。桌面启动方式与 seed 沿用现状（`--scenario-path
  scenarios/wushu-ring-2026-mujoco.json`；seed 来自场景文件）。
- **R6 展演口径与非门禁纪律**：本任务所有产出（CLI 摘要、桌面屏幕/日志、可选报告 JSON）
  标 `exhibition` / `gate_evidence_eligible=false`；不得改
  `controllers/score_block_rl/splits.py`、不得创建或消费
  `.sim_runs/score-block-final-holdout-v4-run.json`（实测当前不存在）、不得运行
  `evaluate.py` 的 v4 盲验/冻结路径。展演选用的 checkpoint 与哈希须写入记录。
- **R7 确定性与可复现边界**：
  - C# runner/driver 不得引入 RNG/时钟抽样；模拟随机只来自 `scenario.Seed`
    （`src/Sim.Core/MatchEngine.cs:106`）。
  - Python 适配器不得用随机源；`deterministic=True`。
  - CLI 无头模式：同 seed、同 checkpoint 两跑，faults=0 时摘要（ticks/比分/doneReason/
    目标块/交接 tick）必须一致，作为"runner 未引入 RNG"的验收证据。
  - 桌面实时模式按墙钟推进（`DesktopLiveDriver.cs:180-213`），声明为**不可位对位复现的展演**，
    不作为回放/门禁证据；文档必须写清该边界。
- **R8 文档与 spec 同步**：`docs/CONTROLLER_PROTOCOL.md` 增 RL 适配器一节（用法、模型加载、
  超时建议、交接语义、非门禁标注）；`docs/CLI.md` 的 match 选项表加新旗标；
  `controllers/score_block_rl/README.md` 增展演入口与边界（不替换默认 FSM、特权观测、
  不可当真机部署）。`.trellis/spec/sim/index.md:33` 的过时表述（"一律走 Sim.Cli.PythonBridge"）
  按现实修正为 `Sim.Controller.ExternalControllerBridge`，并补展演纪律条目。

### 验收清单

**A. 共享实现与回归**
- [ ] `RlEnvCommand` 委托共享投影后，obs 逐位不变：本会话实测样本
      （seed 42 → entry_tick=281、obs len 11、前 9 项与末 2 项数值）可作对照；
      新增单测覆盖 9+2 顺序/裁剪/回退目标规则与预推进 guard。
- [ ] `dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1` 全量通过；相对写档前基线
      （implement.md 记录实测数字）零回归。
- [ ] `dotnet build godot/GodotSim.csproj -m:1` 0 错误 0 警告；`replay-check replays/seed-42.json` PASS。

**B. Python 适配器**
- [ ] `py -3.12 controllers/score_block_rl/bridge_adapter_selftest.py` 全过：模型加载、
      11 维守卫（9 维模型拒载非零退出）、动作映射边界（a=±1 → v=±1、w=±2）、
      残帧零动作、坏行/EOF 行为。
- [ ] 用 v4 checkpoint 实测（`research/facts.md` §3/§4 的路径与解释器）可启动并应答。

**C. CLI e2e**
- [ ] `match --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json --start-at score_block
      --controller-us "py -3.12 controllers/score_block_rl/bridge_adapter.py --model <v4 zip>"`
      输出 faults=0，且事件/`info` 证明我方在 SCORE_BLOCK 交接后才进入
      Manual（先例：`docs/CONTROLLER_PROTOCOL.md:115-123` 的 Manual 语义）。
- [ ] 同命令连跑两次摘要一致（R7）；`--events` 日志中交接前无我方外部动作写入。
- [ ] 未带新旗标的历史命令（如 `match --controller-us mbri_adapter smoke`）输出与改动前一致。

**D. 桌面**
- [ ] `dotnet test --filter "FullyQualifiedName~DesktopLiveDriver"` 覆盖"预推进→交接"新用例；
      `ControllerPreflightTests` 不回归（RL 适配器的残帧零动作可被预检通过）。
- [ ] 手动目检一次：`--scenario-path scenarios/wushu-ring-2026-mujoco.json`，F10 设 us external =
      `py -3.12 controllers/score_block_rl/bridge_adapter.py --model <v4 zip>`，对手 builtin；
      HUD 显示策略 faults=0，比赛可见我方推块/掉台等裁判事件。
- [ ] 桌面展演记录标 `gate_evidence_eligible=false`；`.sim_runs/score-block-final-holdout-v4-run.json`
      仍不存在；`git status` 中 `controllers/score_block_rl/splits.py` 无改动。

## Out of Scope

- 不改训练链路：`train.py`/`evaluate.py`/`splits.py` 的语义、split 注册与门槛口径一律不动；
  不跑 v4 盲验、不冻结候选、不追认 v4 结果。
- 不做 v5/新奖励/新观测维度；不重训、不微调。
- 不把 RL 策略提升为默认控制器（默认仍是内置 FSM，`docs/CLI.md:12`、`:52` 语义不变）。
- 不把特权观测（真值块坐标）宣称为真机可部署能力（`controllers/score_block_rl/README.md:1-3`）。
- 不改 `Sim.Core` 内核行为、既有协议字段形状、回放身份；不引入新 RNG/时钟源。
- 不为桌面新增视觉源语义：展演默认沿用与训练一致的视觉配置，混用 live 源只作探索且必须
  标注"非同条件"（开放决策 Q5）。

## 开放决策

1. **Q1 锁定目标（bx,by）的交接承载**：(a) `Observation` 加性字段
   （如 `lockedTarget:{x,y,onPlatform,out}` 或 RL 投影数组，协议铁律允许新字段、
   `WhenWritingNull` 保证旧消费者字节不变，`src/Sim.Protocol/ProtocolJson.cs:28`）；
   (b) 预推进完成后再启动控制器进程，用 argv 传目标；(c) 适配器按 FSM 几何规则重建
   （**不推荐**：重复 FSM 目标规则，且占用让让/无进展切换不可从单帧观测重建）。
   推荐 (a)：桌面/CLI 共用、交接前不启动进程也能工作、便于审计；方案 (b) 需改桥生命周期。
2. **Q2 共享层归属**：桥已在 `Sim.Controller`（CLI+桌面+测试共同引用），是否仍按 spec 措辞
   迁入 `Sim.Hosting`？推荐**不迁**：Sim.Hosting 现在只做物理后端装配；迁移要动
   `Sim.Controller.csproj`/`Sim.Hosting.csproj`/`Sim.Cli`/`Sim.Tests`/`godot`/solution/文档 8 处以上，
   收益仅是项目数。若坚持迁移，见 `design.md` 决策①的文件清单。两种选择都要修
   `.trellis/spec/sim/index.md:33`。
3. **Q3 CLI 旗标命名**：`--start-at score_block` 还是 `--preroll-score-block`？
   影响 CLI.md 与用法文本；语义相同（默认关、开启才预推进交接）。
4. **Q4 checkpoint 选型**：展演用哪个 v4 产物？开发集 50 个候选全部 `gate_passed=false`；
   按"锁定目标得分多、掉台少"实测第一为
   `…/suite-01/seed-20260929/checkpoints/rl_model_204800_steps.zip`（9 分、0 掉台）。
   备选：五 seed 最终模型（501,760 步，含 `policy.pth`）。需主人/主会话拍板并把 SHA-256 写进记录。
5. **Q5 视觉源与"同条件"**：展演是否强制默认 `classifyRate` 视觉桩（与训练一致）？
   若允许 `visionReplay`/`liveBridge`，对手 FSM 轨迹不再与训练条件可比，结果只能作"不同条件探索"。
   推荐：默认 classifyRate，并在报告/日志写明视觉源与口径。
6. **Q6 超时与首帧**：桌面 `ControllerProfile.TimeoutMs` 默认 100 ms
   （`godot/src/DesktopSettings.cs:53`），SB3 首帧推理在 CPU 上可能超；适配器启动即加载模型可把
   首帧压到单次 forward，但展演建议 TimeoutMs ≥ 500 ms。是否在文档给推荐值/是否自动预检放大。
7. **Q7 展演产物**：CLI 侧要不要写 sidecar/报告 JSON（目录、schema、是否入 `.sim_runs/`）？
   桌面侧是否只需屏幕目检 + 控制台日志？影响 implement.md 批 3 的工件量。
8. **Q8 双方角色**：只支持 RL 控制我方，还是也允许 RL 控制对手（当前策略只在"我方"口径训练）？
   推荐 v1 只支持我方，明确拒绝或标注"未训练口径"。

## Notes

- 事实底稿（含全部 file:line 与实测命令输出）见 `research/facts.md`。
- 控制器用法先例澄清：`--controller-us "python ..."` 的仓库先例是 `controllers/mbri_adapter.py`
  与 `controllers/example_controller.py`；`rpi-yolo-pi4-int8-lto-8fps(1)/sim_bridge.py` 是
  视觉流桥（`vision live --process` 口径），不是控制器桥（见 facts §1）。
- 技术取舍与三批组织见 `design.md`；步骤、验证命令与回滚点见 `implement.md`。
- 2026-10-01 本会话实测：`rl-env` 11 维冒烟通过；`py -3.12` 可加载 v4 checkpoint；
  v4 盲验索引不存在；全量测试基线在实现开工前重跑存底（本机 SDK 路径见 implement.md 环境节）。
