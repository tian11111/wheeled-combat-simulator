# PORTING_NOTES — 遗留 HTML 内核 → Sim.Core 移植决策记录

来源：`D:\project\robocup\robot-simulator\wushu_ring_sim.html`（内嵌 JS CORE，约 2978 行）。
本文件记录移植到 `src/Sim.Core` 时所有**有意的行为差异与补充决策**。凡未列出的行为均按遗留实现逐行对齐；
固定 seed + 相同动作序列必须产生逐位一致的事件流与比分（`MatchEngineTests.Determinism_*` 守护）。

## 1. 结构映射

| 遗留 (HTML/JS) | Sim.Core | 说明 |
| --- | --- | --- |
| `stepSimExt` | `MatchEngine.StepSimExt` | 7 步流水线顺序不变：观测→动作→计时/计分→传感器→FSM→物理→掉台/读秒/消极判定 |
| `resetAll` | `MatchEngine` 构造函数 | 起始位姿、块默认坐标、PREP 倒计时全部一致；构造后即采样一次传感器（对应 `resetAll` 尾部） |
| `armFor` / `pauseMatch` / `resumeMatch` / `restartFor` | `Arm` / `Pause` / `Resume` / `RestartPenalty` | 发令事件文案逐字保留 |
| `scoringTickFor` | `ScoringTickFor` | 手动模式走 `StepSimExt` 内联分支（同遗留） |
| `fsmTickFor` / 状态机 S 枚举 | `FsmController.FsmTickFor` / `FsmState` | MOUNT/SEARCH/ATTACK/SCORE/RECOVER 全部子状态机保留 |
| 物理接触/铲楔/台阶姿态 | `PhysicsWorld` | 定步长 0.05s，扫掠圆接触、成对解算顺序不变 |
| `sampleSensorsFor` | `SensorSampler.SampleSensorsFor` | 逻辑别名 gF/gB/gL/gR… 经 `LegacySensors` 与 rawSens 双通道暴露 |
| `respawnBlock` | `MatchEngine.RespawnBlock` | 20 次尝试，拒绝距任一机器人 <0.8m 或落入中央 1.6–2.2 区域 |

## 2. 有意差异（协议层补充，不改变比赛语义）

1. **EventKind 扩展（增量式）**：遗留日志没有结构化类型，新增 `EventKind.Fsm`（状态机迁移行，如 `[fsm] MOUNT_RING: …`）与
   `EventKind.ScoreClock`（登台读秒 +1）。既有 kind（Drop/Mount/BlockScore/Inactivity/RestartPenalty/
   SimultaneousDrop/Timeout/Arm/Pause/Resume）语义与遗留一一对应。
2. **cls 白名单修正**：遗留日志类除 `us/them` 外还有 `score/warn/sim`；协议校验白名单已补齐
   （`Event.IsKnownLegacyClass`），否则合法遗留轨迹无法通过校验。
3. **事件消息加名字前缀**：遗留 `logFor` 输出 `[${r.name}] ${msg}`；Core 事件存裸消息，
   `[我方]/[对手]` 前缀在 `CoreEvent.ToProtocolEvent` 统一补齐，保持日志文本可逐字比对。
4. **requestId 归属**：请求 id 单调计数器属于观测构造（`BuildObservation`），超时/迟到匹配与零动作回退
   属于适配层（Sim.Cli Python 桥），内核只保证"非有限动作 → RobotAction.Zero 回退且不入回放"。
5. **手动模式受消极判罚约束**：遗留 `updateInactivity` 对 manual 同样生效（Manual 不在豁免列表）；
   内核保持一致——静止在台上 10s 的外部策略同样被判 +1（测试 `Inactivity_ManualStationaryOnStage_*` 固定此语义）。
6. **obs 中 `onPlatform` 语义**：观测/快照中的 onPlatform 取 `PhysicsWorld.OnStage`（含骑线悬挂判定的整车足迹判定），
   与遗留裁判用 `wasOn` 判定同一函数；能量块的 onPlatform 仍是中心点 `FieldModel.OnPlatform`（与遗留一致）。
7. **场地可位姿化（arena-layout-v1，遗留无此概念）**：协议新增可选 `layoutVersion` 与 `field.pose`
   （场局部→仿真世界的平移+绕竖轴旋转）。遗留只有固定身份布局；缺省（无 pose）时
   `FieldTransform` 身份直通，所有旧场景/回放逐位一致。几何求解统一在场局部进行，
   台壁/围栏仍是轴对齐方形假设，仅整体位姿可变；旋转布局下掉台方位词（东南西北）
   与 `FallDir` 按**场局部**罗盘解释（场地自身的南/北），保证事件消息随场地旋转不变形。
8. **布局编辑的"固定坐标"效应**：编辑器进入时把种子随机放置的能量块固化为场景
   `blocks[].x/y`（`ScenarioWithResolvedBlocks`），此后同 seed 复现同一布局；这与遗留
   每局重掷块位不同，是有意差异（编辑语义要求块位置可寻址、可保存）。
9. **真实重启 `RestartRobot(role)`（遗留无此操作）**：遗留 `restartFor` 只有判罚语义
   （+4/+3，不改状态）。新增显式 `RestartRobot`：目标机器人经 `FieldTransform` 回到场景
   出发点并清理运动/传感器/FSM 瞬态（武装、`MOUNT_RING`，已结束的机器人比赛进行中可复活），
   按 2026 规则"裁判同意的重启 = 对方 +3"计分（+4 保留在 legacy "未经同意"判罚 kind），
   被重启方计一次判罚；比赛时钟、另一台机器人与场上能量块不动。仅在
   RUNNING/PAUSED 接受，Prep/Ready/Finished 无副作用拒绝；事件 `EventKind.Restart`（遗留
   枚举位既有、首次启用）+ 附加命令 `restart_robot:<role>`。旧 `restart:<role>:<kind>`
   判罚路径与其解码逐字节保留，旧回放绝不重解释。桌面端 R/T 改走真实重启（原判罚语义
   仍经回放命令可用）。
10. **反僵局铲刃微调（anti-stall blade oscillation，遗留无此机制）**：遗留 CORE 中同型
    机器人正面顶牛时铲刃静差恒为 0，楔入阈值 `|aBlade−bBlade|>0.004` 永假 →
    `WedgedFront`/`FrontLoad`→降推力路径对同型车永不生效，正面全速对推无限锁死
    （实测 60 s 原地不动），与真实比赛观察不符，属有意偏差。`PhysicsWorld.ResolveRobotPair`
    楔入分支（`facing>0.6π`）内给双方有效铲刃高度各叠加慢速正弦微调
    `A·sin(2π·SimT/P + φ)`：`A=0.006 m`（场景键 `antiStallBladeAmp`，**0=关闭且逐位恢复
    旧行为**）、`P=2.1/2.7 s`（`antiStallBladePeriodUs/Them`，互质拍频让双方交替获得
    楔入优势），初相 `φ=(hashString32("anti-stall:{seed}:{role}")/2³²)·2π` 由
    `MatchEngine` 构造 `PhysicsWorld` 时注入——独立哈希派生，**不消费 Mulberry32
    比赛流**；时间源是与读秒/FSM 同源的 `a.Fsm.SimT`。阈值 0.004 与
    `WedgedFront→FrontLoad→thrust` 下游全部不变；非正面/无接触/块体/传感器/掉台路径
    零改动（回归测试 `AntiStallmateTests` 锁定）。实测：修改前 60 s 锁死；修改后首帧
    接触（≈0.65 s）即楔入，10 s 内被楔方被推离僵持位置 >0.5 m。seed-42 官方 FSM 回放
    逐位不变（该局轨迹不受影响，`godot-parity-seed42.json` 重录后除 createdAt 外逐字节
    一致）；非同型车的楔入时机会被微调轻微推移——有意行为，更贴近真实。

## 3. 数值与随机数

- `mulberry32`/`mix32`/`hashString32` 按 JS `Math.imul` 的 int32 语义移植（`Js.Imul`），种子经 `Number(s)|0`
  截断为 int32（`unchecked((int)scenario.Seed)`）。
- 传感器噪声流仍按 `(seed, step, role, channel)` 独立派生，增删通道不会重排比赛 rng 序列。
- 计时收敛：`st.Timer < 1e-9` 视为 0，避免浮点残差导致无头/API 的 done 判定分叉。

## 4. 已知边界（诚实声明，见 fidelity.json 后续工作）

- 场地灰度表为手绘模型（hand_drawn），视觉分类是概率桩（classifyRate），非真实相机感知。
- Godot 渲染壳不在确定性承诺范围内；一致性以快照 JSON 逐位相等为准。
