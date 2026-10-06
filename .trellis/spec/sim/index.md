# Sim 层开发规范（.NET 确定性内核 + 桌面壳）

> 本项目实际是 Godot 4 .NET + .NET 8 仿真栈；`backend/`、`frontend/` 为模板占位，
> 写仿真相关代码前先读本目录。

## 铁律（违反即破坏验收标准）

1. **Sim.Core 不依赖引擎与 IO**：不得出现 Godot/文件系统/网络/进程/时钟调用。
   随机只能来自 `DeterministicRandom`（种子派生），时间只能来自固定步长累加。
2. **确定性契约**：同种子 + 同被接受动作序列 ⇒ 逐位一致的事件与比分。
   改任何物理/裁判/传感器逻辑后必须跑：
   ```bash
   dotnet test
   dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
   ```
3. **协议演进只加不改**：`Sim.Protocol` 现有字段的 JSON 形状（camelCase、枚举拼写）
   永远不变；新能力走新枚举成员/新字段/新 `ProtocolVersion`。EventKind 只允许增量追加。
4. **官方布局单一来源**：块坐标等布局常量用 `Sim.Protocol.OfficialLayout`，
   禁止在 CLI/桌面壳/测试里再抄一份数字。磁盘规范形态是 `scenarios/wushu-ring-2026.json`。
5. **渲染不复刻规则**：`godot/` 只消费 `Snapshot`（经 `SnapshotView` 投影）并发裁判指令；
   任何本地计分/物理判分都是缺陷。

## 层次与依赖方向

```
godot/ ─┐
Sim.Cli ─┼─→ Sim.Core ─→ Sim.Protocol
Sim.Tests(链接 godot/src/SnapshotView.cs 做无 Godot 回归)
```

- 无 Godot 环境的可测逻辑放纯文件（如 `godot/src/SnapshotView.cs`），
  用 `<Compile Include>` 链接进 `Sim.Tests`，不要为它新建工程。
- 外部控制器一律走共享桥 `Sim.Controller.ExternalControllerBridge`（JSONL、UTF-8 行、
  request-id 匹配、超时→零动作、计 fault；CLI/桌面/Sim.Tests 同一实现）；CLI 的
  `Sim.Cli.PythonBridge` 只是兼容包装（2026-09-01 起实现下沉，见 `docs/CONTROLLER_PROTOCOL.md`）。
- SCORE_BLOCK 展演（RL 策略试跑，非门禁）的**共享装配入口**是 `Sim.Hosting.ScoreBlockExhibition`
  纯缝（CLI `match --start-at score_block` / `rl-env` 训练入口 / 桌面 driver 三方同一实现：
  预推进 4800 tick / 目标锁定 / 11 维观测投影）；观测经 `Observation.rlObservation` 加性字段
  下发（null 不序列化、旧消费者字节不变），只在交接后填充。
  **IO 例外边界**：进程/时钟编排只在 Sim.Cli、桌面壳与 `Sim.Controller` 桥，RL 控制器进程本体是
  `tools/rl-bridge/rl_desktop_runner.py`（Python，UTF-8、逐帧 flush）；`Sim.Hosting` 缝本身
  零 IO/时钟/RNG，`Sim.Core` 不得新增 IO。
- 桌面展演装配纪律（`godot/src/ControllerWiring.cs` 纯决策 + `DesktopLiveDriver` 交接门控）：
  我方外部控制器**只在 mujoco 场景**启用（legacy 场景明确拒绝并回退内置 FSM，设置仍保存）；
  应用设置时预检（启动→握手→释放），确定性坏命令回退、应答超时只告警；发起令后由共享缝
  预推进到 SCORE_BLOCK 才交接，未交接不喂外部动作。展演恒 `gate_evidence_eligible=false`、
  seed 取 `scenario.Seed`、不写回放/不晋升 fidelity/不触碰 v4 盲集，runner/driver 不得引入
  RNG（桌面实时驱动按墙钟，属不可位对位复现的展演，不是训练/门禁证据）。

## 内置 MBri 控制器（可选档 `vehicles[].controller`，10-01 落地）

- **选择契约（协议加法）**：场景 `vehicles[us|them].controller` 取值 `builtin`（省略默认）
  或 `mbri`（`src/Sim.Protocol/Profiles.cs:224-245`，null 不序列化、既有 wire 不加宽）；
  桌面设置页"来源"同档位（内置 FSM / 内置 MBri / 外部命令），仅显式选择 MBri 时把字段
  写进本场场景（`godot/src/DesktopSettings.cs:318-341`）。外部进程控制器继续走
  `--controller-us/--controller-them` 与桌面进程桥，不占用该字段，外部动作优先于场景选择。
  省略/显式 builtin 的场景行为逐位不变（`src/Sim.Tests/MbriSelectionTests.cs` 指纹相等 +
  replay-check seed-42 + 官方场景 vs 缺省副本 diff）。
- **实现边界**：`Sim.Core.MbriFsmController`（+ `MbriPatrol` / `MbriReentry`）是纯内置控制器，
  零 IO/时钟/随机（真车 wall-clock 统一经 `MbriUnits.SecondsToTicks` 换算为 tick）；
  `MatchEngine` 只按场景选择构造与逐 tick 派发（`src/Sim.Core/MatchEngine.cs:142-143,706`），
  不触碰物理/裁判/传感器采样。新桥接/新偏差必须在 `MbriFsm.cs` 头注释逐项披露。
- **单位与标定层（数值合同）**：轮速 `WheelToMs = unit×0.000896`（真车 400×0.6 s=21.5 cm
  实测锚点）；差速 `w=(r−l)/(2·TrackWidth)·k` 的 TrackWidth 取**真车实测 0.229 m**
  （`src/Sim.Core/MbriUnits.cs:27`；来源 `src/Sim.Mujoco/MujocoModel.cs:94-99` 轮心实测与
  v2 场景同值；旧桥猜测 0.18 已弃用，偏差 21.4% < 30% 停线阈值）。灰度 0–1000 → 真车 ADC
  的逐通道仿射（`SimToAdc`/`SimToAdcWhite`）是"结构忠实、数值近似"层：真车非线性/噪声
  不建模；**A1 重标（2026-10-02）**：巡台 zone 的 anchor 0/1 端点已从 g=0/1000 重锚到
  官方场实测台沿/台心灰度（E≈329 / C≈651 逐通道，`src/Sim.Core/MbriGrayCalibration.cs:31-33,71,96`，
  采样件 tmp/mbri-recal/ 不入库）——重标后 zone：台心红区 ≈1.0、内环最亮带 ≈1.54、
  走道 ≈−1.03，early-front/near-edge/FAST_ZONE 阈值距台沿语义见
  `MbriGrayCalibrationTests.OfficialField_ZoneSemantics`（纯常量断言，改锚必跑）；
  白域仍结构性不可达（`WhiteEnter_UnreachableOnOfficialField`）。掉台判定另走 fall-domain
  采样（走道 g<150 → ADC 0，`src/Sim.Core/MbriGrayCalibration.cs:123-149`）。
  改这些常量/公式会改变 mbri 轨迹，必须重跑 `--filter "FullyQualifiedName~Mbri"` 与
  11-seed 行为对照。
- **能力边界（按 2026-10-02 能力修复轮修订；不得超出证据宣称）**：
  巡台分级（A1 重标后）MEDIUM_CRUISE/FAST_ZONE 已可达，early-fire（批1 g/1000 锚定下
  全程 EDGE_AVOID）已消除；回台能力成立——官方 legacy 11-seed 中 mbri 掉台后物理再上台
  **60–68%**（builtin 95.9%），掉台中位 **2** vs builtin 43；得分能力**非零但弱**——
  mirror 内战我方得分中位 2.0（0–12，含 hunt 推块 BlockScore+3/登台读秒），但**同场对
  builtin 11/11 全负（得分中位 1 vs 13）**，不得宣称 mbri 整体优于 builtin；
  **默认控制器未切换**（决策包与切换风险含 RL 对手分布漂移，见
  `.trellis/tasks/10-01-mbri-fsm-port/report-capability.md` §4）。残余披露：A2 REMOUNT
  显式"回台成功"出口在 33 场中 0 次触发（回台实际经 REVERSE 结束灰度恢复分流，
  `MbriReentry.cs:389` / IR_WAIT `!Fall` 出口 `:427-429`）；P2 hunt/probe 已移植
  （视觉=ObjectSet 真值投影的特权观测，`MbriFsm.cs:43-49` 头注释+事件流双披露），
  真车铲子红外守卫仍为 no-op；mbri+MuJoCo 已验证不崩溃且确定性，但回台 seed 相关
  （走道滞留可达 120 s）、MuJoCo 场地灰度域未按 A1 流程单独校准。完整对照见
  `evidence/comparison-postfix.md`（本轮）与 `evidence/comparison.md`（修复前口径）。

## 物理后端契约（legacy 缺省 / mujoco 可选）

- 物理后端由场景 `physics.backend` 显式选择；**未写字段 = 旧二维物理逐位不变**，
  任何路径不得默默切换。FSM/`MatchEngine` 只依赖 `Sim.Core.IPhysicsBackend`，
  禁止直接引用 `Sim.Mujoco` 类型；装配走 `Sim.Hosting`（CLI/Godot 同一入口）。
- `Sim.Mujoco`：官方 C API 薄封装 + 按场景确定性生成 MJCF；每场独立
  `mjModel/mjData`，`Dispose` 必须释放；`v/w` 经有界车轮驱动进动力学，
  **禁止瞬移车体**伪造运动或登台。原生 DLL 哈希锁定（`runtimes/win-x64/native/`），
  不得要求用户装到系统目录。
- `rl-env` 是训练专用例外：一个 CLI 会话可按模型内容哈希复用只读 `mjModel`，
  但每次 reset 必须新建 `MatchEngine` 与 `mjData`。先释放当前 episode 数据，
  再释放共享模型；普通比赛仍每场独立模型和数据。训练桥的进程/文件/Gym IO
  留在 CLI 与 Python，不能进入 `Sim.Core`。
- `rl-env` 的 JSONL stdin/stdout 固定 UTF-8，不依赖 Windows 控制台代码页；
  Python 训练进程用 UTF-8 模式写 SB3 Monitor CSV，编码不符须启动时失败。
  SCORE_BLOCK 试点观测前 9 项顺序不变，末尾追加相对平台中心、按半边长
  归一化的我方 x/y；改维度后旧 PPO 模型必须重训，不能静默加载。
- SCORE_BLOCK PPO 的评测 seed 划分是**预注册契约**，唯一来源为
  `controllers/score_block_rl/splits.py`（`split_version = score-block-split-v4`）：
  当前训练 episode 池为 42/1000–1999；历史默认 PPO/首次 reset seed 为 20260925。
  RL v4 的训练 RNG seeds 为 20260927、20260928、20260929、20260930、20261001；
  PPO RNG 与仿真 episode seed 分离，
  五个预注册训练 seed 从注册训练池获得互不重叠的 episode seed 子集，并写入运行身份；
  3001–3010、4001–4010、5001–5020、6001–6050、7001–7020、8001–8050
  均已揭示。`--final-holdout` 永远只指 4001–4010；已揭示留出集必须显式
  `--analysis-only` 且 `gate_evidence_eligible=false`。v4 开发集为 9001–9020，
  唯一盲集为 10001–10050。评测入口拒绝 split 混用及自定义 seed 复用；正式
  盲验须先核验 v4 冻结记录，使用一次性运行索引。完整命令、字段与拒绝矩阵见
  [rl-split-contract.md](./rl-split-contract.md)。
- SCORE_BLOCK PPO 的性能证据由显式运行的 `controllers/score_block_rl/profile.py`
  生成；标准样本数、warm-up、原始轮落盘、无效轮标记及瓶颈门槛见
  [rl-profiling-contract.md](./rl-profiling-contract.md)。
- SCORE_BLOCK PPO 的**五 seed 训练吞吐门槛**由 `controllers/score_block_rl/run_throughput_suite.py`
  显式测量：五个固定训练 seed 各 ≥500k transitions 并发，三套墙钟中位数 ≤60 分钟且
  产物/fault/seed 隔离全部通过才给下游 Go。中断套件必须留记录并同条件重测，
  不得静默剔除；固定 seed 复现按 `policy.pth`/`policy.optimizer.pth`/`pytorch_variables.pth`
  内容哈希判断，不用整包 ZIP 哈希。完整字段、有效条件与错误矩阵见
  [rl-throughput-contract.md](./rl-throughput-contract.md)。
- 块出界归属只按"max 接触时刻处的**不同角色数**"判定（`PhysicsWorld.FinalizeBlockContacts`）：
  同一机器人的多个接触几何体不得被读成"双方同时接触"。修此判定会经
  `Gain`/`OppGain`/`HandleBuffScored` 反馈进对手 FSM，故它不是纯观测改动，
  跨版本轨迹只在首次归属变化前可比。
- `controllers/score_block_rl/evaluate.py::evaluation_gate` 的 `gate_passed` 必须同时满足：
  至少 1 次锁定目标真实 `BlockScore`、目标得分总数不低于同 seed FSM、我方 `Drop`
  不高于 FSM、且每个得分 seed 的 `position_event_cross_check is True`。开发集选模与
  最终集 `new_round_blind_gate_passed` 使用同一门槛，不能只在选模时检查得分可追溯性。
  合格者按目标得分多、掉台少、训练步数多排序；**禁用** `EvalCallback` 默认的
  平均回报选模。训练侧
  `CheckpointCallback` 只做无偏快照（每 51,200 个单环境 step，`n_envs=1` 下
  `save_freq` 即单环境步数，不可再除以 `n_envs`）。SB3 在 `verbose=0` 且无
  `tensorboard_log` 时不安装任何 logger writer，需要 `progress.csv` 诊断必须
  显式 `set_logger`。
- 新模式回放身份 `mujoco/<原生版本>/<模型内容哈希>`：不匹配明确拒绝；
  旧回放缺字段按旧模式解释；协议/快照/batch 演进**只加不改**（铁律 3 同样适用于
  `physicsBackend`/`physicsModelSha256`/`PhysicsPoses` 等新字段）。
- 传感器在两种模式下都是解析模型平面投影（`SensorSampler`+`FieldModel`），
  不消费 MuJoCo raycast；该差异是已知边界，写进交付报告，不得当作已三维化宣传。
- 改 `Sim.Mujoco`/MJCF 后必须重录新模式回放（模型哈希变化会正确拒绝旧回放），
  并跑 `src/Sim.Tests/MujocoIntegrationTests.cs` + `MujocoProtocolTests.cs`；
  旧基线回归用 `replays/seed-42.json`。新模式不得晋升 `fidelity.json`。
- **模型调校教训（09-25 登台修复沉淀，改车辆/场地几何前必读）**：
  1. hinge 执行器 `forcerange` 的单位是**铰链力矩 N·m**，不是轮面接触力
     （表面力 = 力矩/轮半径）——估算爬台阶需求时用 hub 扭矩口径。
  2. 力上限提高后，kv=1.0 速度伺服会把任何指令阶跃在第一帧变成满扭矩阶跃，
     整车抬头-砸地弹跳——执行器边界必须对 v 做一阶斜坡（镜像 legacy AccelK，
     只滤 v、w 瞬时），伺服增益不可随手调高（09-25 时 kv=0.25 满力只出现在
     近堵转误差处；09-30 已按 2342 真值标定 kv=τ_stall/ω_noload≈0.1369，
     见本文件电机建模节第 7 条）。
  3. 刚体圆柱轮**咬不住直角台阶**：低速绕角 pivot 打滑、高速被驱动力矩掀成
     轮抬抛体，与扭矩无关（0.3/2/3/6 N·m 行为一致，接触对 dump 实证爬升瞬间
     与台面零接触）——台沿需要 20° 倒角斜坡（`AppendChamfers`）。
  4. 底盘下缘不得与台面齐平（否则腹部搁台沿）；调车体高度时轮轴与车体 spawn
     必须同步移动，**不要**用 geom pos 偏移车体（实测冻结整车，原因未深究）。
  5. 传感器在物理步进**前**采样（读上一提交帧）——任何用"当前帧位姿"验证
     传感器读数的测试，在车辆快速越过台沿/边界时都会假性失败。
  6. **物理子步常量必须与 MJCF `option timestep` 同源**（09-30 时基回归教训）：
     `MujocoModel.MjcTimestep` 是唯一真值，`SubstepSeconds`、`SubstepsPerTick`
     （= `TickSeconds` / `MjcTimestep`，= 25）与 XML 的 `option timestep` 只能由它
     推导；不变量 `SubstepsPerTick × MjcTimestep == tickSeconds(0.05)`
     由 `src/Sim.Tests/MujocoTimebaseTests.cs` 钉住（含"每 tick 实际位移"的
     行为判别：duty=1 满档（v1 指令 1.5 ⇒ ctrl=ω_noload=12.566 rad/s，轮端无载
     上界 0.817 m/s）实测 ≈0.0356 m/tick，旧慢动作时基下同一车速只有 ≈0.0142）。
     09-29 只把 XML 字面量改成 0.002 而 C# 侧仍是 10×0.005，每裁判 tick 只积分
     0.02 s —— 物理时间流速变成比赛钟的 0.4×（慢动作），而 FSM 时限/传感器/接触
     时刻全按 0.05 s 记账，翻覆/得分/索敌基线整体失真且数字看似"更真实"。
     改时基先改 `MjcTimestep`；当时（批 1）MJCF 字节不变，v1/v2 模型哈希与
     legacy 回放身份不受影响——批 2 执行器真值标定后哈希已随之变更
     （守卫 `V1ModelSha256` 同步），legacy 回放身份仍不受影响。
  7. **轮驱动只认真车电机真值（2342）+ 按轮 duty 口径**（09-30 批 2）：真值单一
     来源是 `MujocoModel.MjcStallTorque`(1.72 N·m) / `MjcNoLoadSpeed`(120 rpm = 4π
     rad/s) / `MjcServoKv`(τ_stall/ω_noload ≈ 0.136873，推导而非标定)；旧工程值
     kv=0.25/±3.0 N·m/±80 rad/s 已删除。MJCF `<velocity>` 执行器
     τ=kv×(ctrl−qvel) 截断到 ±τ_stall 后**数学等价直流电机线性转速-扭矩曲线**；
     `SetControls` 按 duty=clamp(轮面线速度/MaxSpeed, −1, 1)（分母=车辆 MaxSpeed，
     **必须按轮计算** —— 单标量 |cmdV|/MaxSpeed 会把原地转向压成 duty=0，SEARCH
     闭环失效）折算 ctrl=duty×ω_noload = 可调压开环 PWM：起步扭矩=duty×τ_stall、
     空载转速=duty×ω_noload。断言钉在 `MujocoMotorModelTests`（kv/range/起步扭矩/
     原地转向 duty/电池接口惰性）。电池内阻压降只留 `MotorDriveOptions` 内部钩子，
     **默认禁用**：无实测电压-电流曲线不得填数、不进场景协议。
  8. **v1 mujoco 场景此后定位"工程验证"**（09-30）：v1 场景 maxSpeed=1.5、轮径
     0.065 需要 23.08 rad/s，而执行器真极速 12.566 rad/s ⇒ 实际极速 0.817 m/s，
     `Fsm.TimeScale`(1.5/maxSpeed) 仍按 1.5 算（时限相对偏紧，披露不改）；v2 真车
     场景 maxSpeed=0.408 与 ω_noload×r=0.408407 同源，TimeScale 与真极速自洽
     （口径差披露：实测稳态车速 0.355 m/s——hinge damping 平衡点比空载低 13%，
     时限相对稳态车速仍偏紧）。
     改 MJCF 后模型哈希必变（见上行重录纪律），行为基线一律以 v2 真车场景为准。

## 行为对齐参考

- 遗留原型 `D:/project/robocup/robot-simulator/wushu_ring_sim.html` 只读。
- 移植/对齐决策记录在 `docs/PORTING_NOTES.md`；新增有意差异必须追加条目。
- 基线再生成：`node tools/legacy-baseline.js` → `src/Sim.Tests/fixtures/`（禁止手改）。

## 保真度诚实性

对外声明保真度必须引用根 `fidelity.json`；未标定子系统（摩擦/碰撞/堵转/登台）
不得在文档或输出中暗示等同真机。

## 场地布局契约（arena-layout-v1）

- 一切场地几何（平台/走道/围栏/出发区/出生点/块位）以**场局部米制**存储；
  场局部→仿真世界的映射只有 `Sim.Core.FieldTransform` 一个实现。
  物理/传感器/渲染/相机一律经由它（或其宿主 `FieldModel` 的世界坐标入口），
  禁止在任何消费方手写旋转公式。
- **身份位姿逐位直通是兼容性门禁**：`IsIdentity` 短路返回原值（IEEE 精确），
  改任何几何相关代码后必须跑 `dotnet test` + `replay-check replays/...` 确认旧基线逐位一致。
- 新增几何能力先问：这是"场局部可表达"的吗？台壁/围栏求解保持场局部轴对齐，
  世界坐标修正只在边界处经 `FieldTransform` 进出。
- 编辑器/草稿只产 `Scenario` 数据并重建会话；任何编辑路径都不得触碰运行中的
  `MatchEngine`（比赛/回放进行中编辑必须被禁用）。
  **入口判定是纯函数 `godot/src/EditorGate.cs`（链进 `Sim.Tests`，矩阵回归
  `EditorGateTests`）**：`Prep` 与 `Ready` 都算"尚未发令"、都放行 —— 快照把两者同样
  报成 `MatchPhase.Prep`（界面显示"发令准备"），而内核在发令准备倒计时(60 s)走完后会
  **自行**从 `Prep` 转 `Ready`；只认 `Prep` 会让静置一分钟后的编辑器静默失效
  （2026-10-06 修复）。被拒绝时必须同时写日志与 HUD 提示（`HudPanel.ShowNotice`）：
  窗口版没有可见控制台，只写日志等于"按了没反应"。
- 渲染层（含 `.glb/.gltf` 外观模型、本地偏好文件、台面灰度纹理）永不进入
  `Scenario`/`Snapshot`/回放指纹。
- 运行时 `GltfDocument` 导入**不会**像编辑器导入器那样自动合成法线——缺 NORMAL 属性
  的网格在 Forward+ 下渲染全黑；必须经 `SurfaceTool.GenerateNormals` 兜底
  （见 `godot/src/RobotModelLoader.cs::EnsureNormals`）。
- 视觉 QA 以 `--capture` 视口像素分桶为机器可判定证据；桌面截图存 `godot/docs/`
  （其 `.import` 元数据不入库），调试用临时图不入库。
- 台面灰度显示：像素↔场局部轴契约由 `godot/src/FieldGrayTextureMap.cs` 单一实现
  （row 0 = 南、col 0 = 西），**显示与传感器是两种独立灰度语义**：传感器 0–1000
  永远是 `FieldModel.FieldGrayLocal`（L∞ 手绘模型，一字不改、永不进纹理）；
  显示用**官方效果图外观**（规则 PDF 第 10 页"四角纯黑→中心纯白"）——归一化欧氏
  径向渐变 `OfficialSurfaceLuminance`（中心白、四角黑、边中点 1−1/√2、等欧氏半径
  同亮度），几何红区红底白"武"优先覆盖，走道深灰属外场地面材质。旧 L∞ 方形显示
  会画出白色对角亮带，禁止以任何 `max`/`|dx|+|dy|` 形式回到显示层。材质必须
  Unshaded + 线性过滤，防止方向光/SSAO/探针制造假灰度带（关 SSAO/探针的 A/B
  capture 逐像素一致）。Godot 4 PlaneMesh(FACE_Y) 的顶点与 UV 翻转互相抵消，
  改动映射前先以代表性像素测试验证（`src/Sim.Tests/FieldGrayDisplayTests.cs`，
  必须覆盖等欧氏半径轴向/对角向，不能只测对称点同值）。

## 真实重启契约（restart-v1）

- `MatchEngine.RestartRobot` / `restart_robot:<role>` / `EventKind.Restart` 的
  签名、不变量、错误矩阵与测试点见 [restart-contract.md](./restart-contract.md)。
  旧 `restart:<role>:<kind>` 罚分命令逐位兼容，不得重解释。

## 标定契约（telemetry-v1）

- 真机数据 → 参数只能经 `calibrate` 命令的固定链路：遥测入口一次性严格校验
  （SI 单位、类型、时间戳、kind 必填字段），**校验失败不得产出报告或 patch**。
  拟合/验证数学全部在 `Sim.Calibration` 纯库内，CLI 只做 IO 编排。
- **拟合器与内核必须共享模型常数**（`PhysicsWorld.Gravity`/`BlockLinearDamping`）；
  改内核物理模型时同步改拟合模型，否则标定结果失真。
- **留出集门禁**：晋升 fidelity 需要 `set:"holdout"` 的独立试验达到目标误差；
  拟合集误差、合成数据、单次试验永远不够。`capture.source != "real"` 一律拒绝晋升。
- 标定只产**新场景/patch 文件**；官方场景、代码常量、运行中的引擎不受影响，
  旧回放逐位不变（晋升前后都须过 `replay-check` + `--parity-check`）。
- `mount` 门控只验证不拟合：真机成败与确定性门控的混淆矩阵误判率 >10% 或
  速度×角度覆盖不足时，必须如实报告"模型不足"，保持未标定。
- 标定原始遥测（`telemetry/data/`）与报告（`calibration/`）默认不入库，与
  replays 同理（可从数据重导）；审计需要时显式 `git add -f`。

## 传感器证据契约（sensor-calibration-v1）

- MBri 传感器判定模型（灰度/前 ADC/铲子）的证据导入与物理标定 telemetry-v1 **分线**：
  新 schema、新命令（`sensor-calibration import`），互不扩用。
- 只消费选择清单点名且表头精确匹配的文件；未选中文件必须列为 ignored、
  被拒文件必须带原因；禁止按列名猜文件。
- stored 模型 / 重算 / config.py 快照的差异只进 comparison 表，**永不自动合并**；
  不一致即压为 `evidence_only`/`rejected`。回放评估器必须是纯函数
  （无时钟/IO/随机），median 语义对齐 Python `statistics.median`。
- 灰度 CSV 无坐标 → 报告恒 `coordinateData=false`；禁止伪造成 FieldModel.GrayGridMap。
- 本产物不进入运行时（FieldModel/SensorSampler/FSM/官方场景/fidelity.json/回放
  字节不变）；运行时集成另立任务且只能消费人工接受的报告。

## 视觉证据契约（vision-replay-v1）

- 真实视觉回放证据与 telemetry-v1 / sensor-calibration-v1 同样**分线**：
  新 schema（vision-replay-v1）、新命令（`vision import|evaluate`）、新纯库
  `src/Sim.VisionReplay`（仅引用 Sim.Protocol）。rng 流纪律（回放适配器绝不
  消费 `context.Random`）、unknown 原因码、导入错误矩阵与 evidence_only 门禁
  见 [vision-replay-contract.md](./vision-replay-contract.md)。

## MuJoCo 域标定契约（domain-calib-v1，10-02 落地）

- 改轮-地接触参数 / 原地转向补偿 / MuJoCo 模型几何执行器 / 任何 MuJoCo 域行为钉值前，
  先读 [mujoco-domain-calibration-contract.md](./mujoco-domain-calibration-contract.md)。
- 速记：轮-地摩擦默认**每模型** v1=5.0 / v2=6.0、补偿 10.0（工程初值，扫描锚定）；
  v2 转向上限带 [1.0, 1.35] rad/s 是各向同性接触的天花板（建模缺口，禁用无锚点参数硬凑）；
  MJCF 字节变 ⇒ v1 哈希钉值重钉 + 旧 MuJoCo 回放/RL checkpoint 失效（政策：直接改现有
  v1/v2 并重训）；`fidelity.json` 不因标定晋升；legacy 零改动（replay-check 逐位 PASS）。
