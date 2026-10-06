# 配置面 × 设置 UI 缺口审计（只读代码审计，2026-10-06）

> 来源：Explore 代理全量审计。锚点以当日代码为准。设置 UI 指 Godot 桌面壳的 `SettingsPanel`（F10 六页）与 `LayoutEditor`。

## 0. 配置载体总览

| 载体 | 位置 |
| --- | --- |
| 桌面设置 schema | `godot/src/DesktopSettings.cs`（`DesktopSettings` 记录，JSON 存 `user://wushu-ring-settings.json`） |
| 仿真参数白名单 | `godot/src/DesktopSettings.cs:623` `SimulationParameterCatalog` |
| 场景/协议 schema | `src/Sim.Protocol/Scenario.cs`、`Profiles.cs` |
| CLI 旗标 | `src/Sim.Cli/Program.cs` 及各子命令 |
| Godot 启动旗标 | `godot/src/Main.cs`（`OS.GetCmdlineUserArgs()`） |
| 外观模型配置 | `godot/src/RobotModelLoader.cs`（`robot-models.json`） |
| 物理注入选项 | `src/Sim.Core/Physics.cs:15`、`src/Sim.Mujoco/MotorDriveOptions.cs` |
| 环境变量 | 仅 `src/Sim.Tests/TrainingResetPerformanceTests.cs:115` 与 `tools/rl-bridge/selftest.py:418`（均测试/自测，非产品面） |

## A. 配置 schema 字段全表（`DesktopSettings` + 场景/外观）

标注：UI=设置页或编辑器是否暴露。

| 字段 | 类型 | 默认值 | 锚点 | UI? | 受众 |
| --- | --- | --- | --- | --- | --- |
| `schemaVersion` | int | 1 | DesktopSettings.cs:154-156 | 无（内部） | — |
| `window.width` | int | 1280 | :45 | 完整（显示页）SettingsPanel.cs:260 | 演示 |
| `window.height` | int | 720 | :47 | 完整 :263 | 演示 |
| `window.mode` | string | `windowed` | :49 | 完整 :266 | 演示 |
| `uiScale` | double | 1.0 | :160 | 完整 :269 | 演示 |
| `simulationParameters.*` | Dict<string,double> | 见 §A2 | :162 | 完整（仿真页）SettingsPanel.cs:280-301,491 | 演示/开发 |
| `vehicle.mass` | double | 3.5 | :74 | 完整 :792 | 演示/开发 |
| `vehicle.motorRpm` | double | 120 | :77 | 完整 :796 | 开发 |
| `vehicle.motorTorque` | double | 1.72 | :80 | 完整（仅存档）:800 | 开发 |
| `vehicle.wheelRadius` | double | 0.0325 | :83 | 完整 :804 | 开发 |
| `vehicle.maxSpeed`（派生，JsonIgnore） | double | rpm 推导 | :86-87 | 只读推导 | — |
| `vehicle.sensorProfileId` | string? | null=跟随场景 | :93 | 完整 :827 | 开发 |
| `vehicle.sensorDisabled[]` | List<string> | [] | :96 | 完整（通道勾选）:930 | 开发 |
| `vehicle.sensorOffsets{id:{dx,dy,dz,yaw}}` | Dict | {} | :99,103 | 完整（dx/dy/dz/dyaw 框）:942-945 | 开发 |
| `blockLayout.buffCount` | int? | null=跟随场景 | :139 | 完整 :1047 | 演示 |
| `blockLayout.debuffCount` | int? | null=跟随场景 | :142 | 完整 :1050 | 演示 |
| `blockLayout.randomPositions` | bool | false | :145 | 完整 :1054 | 演示 |
| `vision.source` | string | `classifyRate` | :114 | 完整 :1129 | 演示/开发 |
| `vision.evidencePath` | string | "" | :117 | 完整（按源启用）:1137 | 开发 |
| `vision.csvPath` | string | "" | :120 | 完整 :1141 | 开发 |
| `vision.processCommand` | string | "" | :123 | 完整 :1145 | 演示（RL） |
| `vision.maxAgeMs` | double | 500 | :126（`LiveVisionBridge.DefaultMaxAgeMs`, LiveVisionBridge.cs:32） | 完整 :1150 | 开发 |
| `usController.mode` | string | `builtin` | DesktopSettings.cs:54 | 完整 :366 | 演示 |
| `usController.command` | string | "" | :56 | 完整 :376 | 演示（RL 展演） |
| `usController.timeoutMs` | double | 100 | :58 | 完整 :372 | 演示 |
| `themController.*` | 同上 | 同上 | :54-58 | 完整 :343-421 | 演示 |
| `RobotModelConfig.path` | string | "" | RobotModelLoader.cs:16 | 完整 :862 | 演示 |
| `RobotModelConfig.scale` | double | 1.0 | :19 | 完整 :869 | 演示 |
| `RobotModelConfig.yawOffset` | double | 0 | :22 | 完整 :873 | 演示 |
| `RobotModelConfig.heightOffset` | double | 0 | :25 | 完整 :877 | 演示 |

### A2. `simulationParameters` 24 项白名单（全部有 UI，无缺口）

锚点 `DesktopSettings.cs:623-651`（定义）+ `SimParameters.cs:42-98`（消费，键名字符串）。

常用组：`EDGE_THRESHOLD`(400)、`FALL_THRESHOLD`(150)、`ON_STAGE_THRESHOLD`(500)、`grayNoise`(30)、`irNoise`(0.02)、`IR_TRIGGER`(0.35)、`MOUNT_SPEED`(780)、`classifyRate`(100)、`RECOVER_LIMIT`(3)。
高级组：`STALL_TIME`(0.4)、`STALL_SPEED`(0.03)、`STALL_RELEASE`(0.06)、`STALL_DISPLACEMENT`(0.006)、`cmdLatencyFrames`(0)、`IR_HYST_BAND`(0.10)、`graySpotRadius`(0.025)、`BLOCK_STICK_SPEED`(0.02)、`BLOCK_MU_K`(0.5)、`COLLISION_RESTITUTION`(null)、`MOUNT_V_MIN`(0.3)、`MOUNT_ANGLE_MAX`(0.26)、`antiStallBladeAmp`(0.006)、`antiStallBladePeriodUs`(2.1)、`antiStallBladePeriodThem`(2.7)。

结论：24/24 全部由 `SettingsPanel.BuildSimulationPage` 渲染（含"自动"复选框跳过持久化）。

### A3. 场景/协议字段（可配置但不在 DesktopSettings）

| 字段 | 默认 | 锚点 | UI? | 受众 |
| --- | --- | --- | --- | --- |
| `scenario.seed` | 42（CLI）；Main `[Export]` 42 | Scenario.cs:298；Main.cs:26 | 无（仅 `--seed`） | 演示 |
| `scenario.id` | `wushu-ring-2026` | Scenario.cs:280 | 无 | 开发 |
| `field.fieldSize` | 3.8 | Scenario.cs:44 | 只读（布局编辑器可平移/旋转，不可缩放） | 开发 |
| `field.matchDuration` | 120 | Scenario.cs:65 | 无（仅 `--duration`） | 演示 |
| `field.tickSeconds` | 0.05 | Scenario.cs:68 | 无 | 开发（回放身份） |
| `field.pose` | identity | Scenario.cs:76 | 布局编辑器（旋转/平移） | 演示 |
| `field.platform/blockSize/blockRadius/startZones/starts` | 官方 2026 | Scenario.cs:56-93 | 布局编辑器部分（出发区/块），几何不可缩放 | 开发 |
| `physics.backend` | `legacy` | Scenario.cs:242 | **无** | 演示/比赛 |
| `physics.modelVersion` | 需 mujoco 必填；v1/v2 | Scenario.cs:245,230-240 | **无** | 演示/比赛 |
| `vehicles[].controller` | null=`builtin` | Profiles.cs:245 | **部分**：控制器页写入仅我方/对手内置 MBri；无 per-vehicle 细项 | 开发 |
| `vehicles[].length/width/height/frontExtent/.../latFrictionK/angDamping/shovelHeight` | 见 Profiles.cs:248-302 | Profiles.cs | **无** | 开发（标定） |
| `vehicles[].sensors.channels[].forward/lateral/height/angle/range/fov/mode/min/max/noise/activeHigh` | 内置 profile | Profiles.cs:23-101 | **部分**：UI 只暴露 offset/禁用，不暴露几何字段 | 开发 |
| `blocks[].kind/x/y/radius` | 官方 3 块 | Scenario.cs:178-206 | 布局编辑器 + 能量块页 | 演示 |
| `scenario.parameters`（原始键值） | 场景文件 | Scenario.cs:324 | 部分（经白名单） | 开发 |
| `layoutVersion` | null/`arena-layout-v1` | Scenario.cs:289 | 布局编辑器写入 | 开发 |

## B. CLI 旗标全表

### B1. `Sim.Cli` 共享（match / replay-record）

锚点 `Program.cs:96-125`（解析）、`Program.cs:86-93`（RunnerOptions）。

| 旗标 | 默认 | 解析锚点 | UI? | 受众 |
| --- | --- | --- | --- | --- |
| `--seed N` / `--seeds a,b,c` | 42 | Program.cs:103-114 | 无（Main 有同义 `Seed` export，无控件） | 演示 |
| `--scenario <path>` | 官方布局 | :115 | 无（等价桌面 `--scenario-path`） | 演示 |
| `--duration <s>` | null（用场景 120） | :116 | 无 | 演示 |
| `--controller-us <cmd>` | 内置 FSM | :117 | 完整（控制器页 command） | 演示 |
| `--controller-them <cmd>` | 内置 FSM | :118 | 完整 | 演示 |
| `--timeout-ms <ms>` | 100 | :119 | 完整（per-role 超时框） | 演示 |
| `--events` | 关 | :120 | 无（HUD 事件栏只读） | 开发 |
| `--out <path>` | — | :121 | 无 | 开发 |
| `--start-at score_block` | 关 | :122 | 无（桌面我方 external 自动等价展演） | 演示（RL） |
| `--stats` | 关 | :123 | 无（HUD 无掉台统计） | 开发 |

### B2. `batch`（`BatchCommand.cs:303-402`）

`--seed/--seeds`、`--scenario`、`--duration`、`--controller-us/--controller-them`、`--timeout-ms`、`--parallelism`（min(CPU,8)，上限 32）、`--out`——全部无 UI，开发用。`--events` 被拒绝（`BatchCommand.cs:315-318`）。

### B3. `rl-env`（`RlEnvCommand.cs:55-77`）

`--scenario`（默认 `scenarios/wushu-ring-2026-mujoco.json`）、`--reward`（默认 `v4`，可选 aggression-v1/2/3）、`--duration`、JSONL 请求字段 `op/seed/v/w/trace/timing`——全部无 UI，开发用。

### B4. `calibrate` / `sensor-calibration` / `vision`（离线工具，全部无 UI）

- `CalibrateCommand.cs:213-220`：`--input --out --vehicle-id --base-scenario --emit-scenario --fidelity --update-fidelity --force`。
- `SensorCalibrationCommand.cs:29-33`：`--data-dir --manifest --out --config --force`。
- `VisionCommand.cs:95-99`（import）与 `:197-214`（evaluate，`--max-age-ms(500)`）与 `:433-475`（live，`--realtime(1x)`）。

### B5. Godot 桌面启动旗标（`OS.GetCmdlineUserArgs()`，全部**无 UI**）

`Main.cs`：`--scenario-path`(:93)、`--replay-path`(:137)、`--replay-tick`(:144)、`--auto-arm`(:161)、`--robot-models`(:1465)、`--settings-smoke`+`--settings-tab`(:152,237)、`--capture`+`--capture-frames`(:167,188)、`--edit-smoke`(:253)、`--camera-smoke`(:259)、`--visual-baseline/--visual-no-sdfgi/--visual-no-fog/--visual-no-glow/--visual-no-dof/--visual-no-material-noise`(:275-302)、`--visual-frame-stats`(:310)、`--camera-cycle`(:200)、`--camera-orbit`(:214)、`--parity-check`(:2028)。

注：`--replay-path`/打开回放有 UI 等价操作（L 键文件对话框、HUD 时间轴）；`--auto-arm` 有 HUD 发令按钮等价；其余无。

### B6. Python 桥/训练（开发专用，全部无 UI）

- `tools/rl-bridge/rl_desktop_runner.py:475,479`：`--checkpoint --stub`（我方展演入口，由控制器页命令框填）。
- `tools/yolo-bridge/mbri_yolo_bridge.py:283-291`：`--stub --model-dir --lead-ms(200) --limit(0) --input`。
- `controllers/mbri_adapter.py:243-248`：`--mode --mbri-path --k --track-width --ir-threshold`。
- `rpi-yolo-pi4-int8-lto-8fps(1)/sim_bridge.py:340-358`：`--model-dir --input --loop --fps --limit --conf(0.25) --nms-iou(0.45) --bgr --raw-u8 --threads(4)`。
- `controllers/score_block_rl/train.py:201-211`：`--steps --out --train-seed --n-envs --dotnet --cli-dll --scenario --reward --checkpoint-interval`。
- `evaluate.py`/`benchmark.py`/`profile.py`/`run_throughput_suite.py`/`selftest.py`/`diagnose.py`：各自种子/split/门禁参数。

## C. 环境变量与硬编码常量

### C1. 环境变量（产品面无）

| 变量 | 用途 | 锚点 |
| --- | --- | --- |
| `ROBOT_SIM_RL_PERF_OUTPUT` | 训练 reset 性能测试输出 | `src/Sim.Tests/TrainingResetPerformanceTests.cs:115` |
| `RL_BRIDGE_DOTNET` | rl-bridge 自测 dotnet 路径覆盖 | `tools/rl-bridge/selftest.py:418` |

结论：**没有产品级环境变量**；C# 运行时不读任何 env。

### C2. 硬编码常量（"常被调"）

| 常量 | 值 | 锚点 | 注入面 | UI? | 受众 |
| --- | --- | --- | --- | --- | --- |
| legacy/v2 物理与模型版本 | `legacy`/`wushu-mjcf-v1/-v2` | `Scenario.cs:242-240` | 场景 JSON 字段 | **无** | 演示/比赛 |
| L1 车车 OBB 分离 | **true** | `Physics.cs:22` `ContactResolveOptions` | 仅 `MatchEngine` 构造参数（代码） | 无 | 开发 |
| L2 车块 OBB + 推块镜像 | **true** | `Physics.cs:29` | 同上 | 无 | 开发 |
| L3 块-台壁阻挡 | **true** | `Physics.cs:36` | 同上 | 无 | 开发 |
| 轮-地摩擦 v1=5.0 / v2=6.0，spin .02 / roll .002，condim 3 | 标定默认 | `MotorDriveOptions.cs:100-103` | `WheelContactOptions`（internal） | 无 | 开发（标定） |
| 轮 geom solref timeconst | 0.02 | `MujocoModel.cs:370` | `WheelContactOptions.SolRefTimeconst` | 无 | 开发（标定） |
| 电池压降模型 Enabled | **false** | `MotorDriveOptions.cs:13-45` | internal，默认禁用 | 无 | 开发（需实测） |
| 原地转向补偿 | 10.0 | `MujocoPhysicsBackend.cs:37` | `MujocoPhysicsBackendFactory` ctor 参数 | 无 | 开发 |
| 增益块得分 +3 / 减益块 +6 | 3 / 6 | `MatchEngine.cs:770,776` | 无 | 无 | 比赛规则（固定） |
| 重启判罚 | 同意 +3 / 未同意 +4 | `MatchEngine.cs:474,428` | 无 | 无 | 比赛规则（固定） |
| 块数上限 / 并行上限 / 种子上限 | 12 / 32 / 4096 | `Scenario.cs:321`,`BatchCommand.cs:28,31` | 无 | 无 | 性能护栏 |
| 展演预推进上限 / 单场 tick 上限 / 策略 tick 上限 | 4800 / 10000 / 2400 | `ScoreBlockExhibition.cs:29`,`MatchRunner.cs:16`,`RlEnvCommand.cs:26` | 无 | 无 | 开发 |
| 奖励常量（v4 与 aggression 变体） | 见 `RlEnvCommand.cs:19-50` | `RlEnvCommand.cs:19-50` | 仅变体选择 `--reward` | 无 | 开发（RL） |
| 比赛时长 / tick | 120s / 0.05s | `Scenario.cs:65,68` | 场景/`--duration` | 无 | 演示 |
| MBri 决策常量（hunt/patrol/reentry/gray） | 数十项 | `MbriHunt.cs:70-327`、`MbriPatrol.cs:51-96`、`MbriReentry.cs:50-55`、`MbriFsm.cs:88-97`、`MbriGrayCalibration.cs:82,127` | 无 | 无 | 开发 |
| MBri 传感器标定外部常量 | `GRAY_NEAR_EDGE_ENTER` 等 5 项 | `src/Sim.Calibration/ConfigSnapshot.cs:26-31` | `--config config.py` 导入 | 无 | 开发 |
| 视觉默认 maxAge | 500ms | `LiveVisionBridge.cs:32` | `vision.maxAgeMs` | 完整 | 开发 |
| 路面/渲染 QA 开关 | SDFGI/fog/glow/DOF/material noise | `Main.cs:275-302` | 启动旗标 | 无 | 开发 |
| 机器人模型导入上限 | 32MB / 5000 节点 | `RobotModelLoader.cs:33-34` | 无 | 无 | 护栏 |

## D. 缺口清单（按受众分组 + 建议暴露方式）

### D1. 演示/比赛常用

1. **物理后端 legacy vs MuJoCo v1/v2（+ 场景选择）**——仅 `physics.backend`/`physics.modelVersion` 场景字段 + `--scenario`/`--scenario-path`。UI **完全无**（布局编辑器只改块/场地位姿，不碰 physics）。建议：设置页新增"场景/物理"页（legacy / mujoco-v1 / mujoco-v2 + 场景文件选择）。锚点 `Scenario.cs:242-240`，`Main.cs:1198`。
2. **比赛时长 `field.matchDuration`**——仅 `--duration`。建议：比赛面板/设置页加"比赛时长"（120 默认）。
3. **随机种子 `seed`**——仅 `--seed`；`Main.Seed` 是 `[Export]`（改场景文件才行），HUD 无输入。建议：比赛面板提供 seed 输入 + "换种子重开"（F5 已重置同 seed，缺"换 seed"）。
4. **能量块布局**——已完整暴露（能量块页 + 布局编辑器），**无缺口**。注意：`BlockLayout` 是数量/落位，不是逐块 debuff 列表（项目记忆里"Debuffs 列表"实为 `ObjectSet.Debuffs` 观测输出字段，`MatchEngine.cs:1033-1035`，非配置）。
5. **控制器 mode/command/timeout**——已完整暴露，`--timeout-ms` 语义已被 per-role 超时框覆盖，**无缺口**。

### D2. 开发/调试专用

6. **L1/L2/L3 legacy 接触扩展开关**——硬编码默认全 true，仅代码构造参数可关（`Physics.cs:22,29,36`）。建议：进高级折叠区。
7. **轮-地摩擦（v1=5/v2=6）与 solref timeconst（0.02）**——硬编码标定值，`WheelContactOptions` internal。建议：保持代码注入/标定脚本；若暴露须标注"会改模型哈希、旧回放失配"。
8. **电池压降模型（MotorDriveOptions）**——刻意禁用且不进协议。建议：维持不暴露。
9. **原地转向补偿（10.0）**——仅 factory ctor 注入。建议：保持代码注入。
10. **奖励变体 `--reward`（v4/aggression-v1~3）**——仅 `rl-env`；桌面"我方 external 展演"命令框可自带 rl_desktop_runner 参数但无 reward 选择 UI。建议：进训练配置文件（本任务批1）。
11. **视觉 QA / capture / smoke / parity 旗标**——全部仅启动旗标。建议：保持 CLI（CI/证据留存专用）。
12. **`--robot-models` 路径覆盖**——仅 CLI；UI 只能编辑固定 `res://robot-models.json`。建议：保持 CLI。
13. **场景 `vehicles[]` 几何/物理字段、`field` 几何、`tickSeconds`**——无 UI。建议：保持场景 JSON（改这些会破坏回放身份/fidelity），不做 UI。
14. **MBri 决策常量、patch 门限、fidelity.json**——无 UI，纯代码/离线工具。建议：保持现状。
15. **`--start-at score_block` / `--stats` / `--events`**——无 UI。建议：保持 CLI。
16. **`schemaVersion`**——无 UI（内部校验，DesktopSettings.cs:177-182）。无需暴露。

## E. 关键结论

- **有设置文件字段但无 UI 的仅 1 个**：`schemaVersion`（内部，无需暴露）。其余 `DesktopSettings` 字段全部有 UI，仿真参数 24 项白名单 100% 覆盖。
- **有 CLI 旗标但无 UI 的**：`--seed/--seeds`、`--scenario`、`--duration`、`--events`、`--out`、`--start-at`、`--stats`、`batch --parallelism`、`rl-env --reward`、全部 Godot 启动旗标、全部 calibrate/vision/sensor 旗标、全部 Python 桥旗标。
- **完全硬编码但"用户常想调"的**：物理后端 v1/v2 选择（最突出）、比赛时长、种子、L1/L2/L3、轮摩擦/solref、奖励常量。
- **最值得补 UI 的两项**：物理后端/场景选择 与 比赛时长+种子（均为演示/比赛高频，且当前唯一入口是命令行）。
