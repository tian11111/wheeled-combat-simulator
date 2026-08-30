# 架构

2026 RoboCup 武术擂台轮式对抗模拟器。目标：**可维护、可复现、可替换控制器**的桌面仿真。

## 分层与职责边界

```
┌──────────────────────────┐   ┌──────────────────────────┐
│ godot/  (Godot 4 .NET)   │   │ Sim.Cli  (.NET 8 无头)    │
│ 3D 展示壳: 渲染/相机/HUD/ │   │ 评测/回放: match / batch / │
│ 回放时间轴               │   │ replay-record / replay-check│
│ 只消费快照+发裁判指令     │   │ 可拉起 Python 策略进程    │
└──────────┬───────────────┘   └──────────┬───────────────┘
           │  同一 MatchEngine / 同一协议    │
           ▼                               ▼
      ┌──────────────────────────────────────────┐
      │ Sim.Core   确定性内核 (无引擎/无 IO 依赖) │
      │ 固定步长 0.05s · 种子化随机 · 规则裁判    │
      │ 传感器/感知 · 物理接触 · 事件流 · 快照    │
      └──────────────┬───────────────────────────┘
                     │ 版本化 DTO (camelCase JSON)
                     ▼
      ┌──────────────────────────────────────────┐
      │ Sim.Protocol   协议与校验                  │
      │ Observation / Action / Snapshot / Event /  │
      │ Scenario / ReplayHeader                    │
      └──────────────────────────────────────────┘
```

**单一权威**：比分、回放、AI 观测全部以 `Sim.Core` 的确定性 2D 模型为准。
Godot 物理仅用于可视摆放与可选诊断，**不参与判分**。未来若引入 3D 权威物理，
必须显式新增模式/版本并单独提供确定性与保真度证据，不属于当前范围。

## 确定性契约

- 固定步长 `tickSeconds = 0.05s`；同一种子 + 同一被接受的动作序列 ⇒
  逐位一致的事件流与比分。
- 随机数用 mulberry32 并按 `(seed, step, role, channel)` 派生独立传感器噪声流，
  增删一个传感器通道不会重排比赛逻辑里的随机序列。
- 渲染掉帧/客户端关闭 **不改变**仿真时钟：展示端用固定步长累加器推进内核。
- 反僵局铲刃微调：正面顶牛的同型机器人铲刃静差恒为 0，遗留楔入阈值永不触发 →
  对推死锁。楔入判定内给双方有效铲刃高度叠加慢速正弦微调（场景键
  `antiStallBladeAmp` 默认 0.006 m，**0=关闭逐位恢复旧行为**；周期
  `antiStallBladePeriodUs/Them` 默认 2.1/2.7 s），初相由 `(seed, role)` 哈希派生、
  时间源是比赛时钟——仍是比赛时间的确定函数，不消费比赛随机流
  （有意偏差，见 `docs/PORTING_NOTES.md` 第 2 节第 10 条）。

## 跨端一致性

桌面壳与无头端共用同一个 `MatchEngine`，因此同种子下进程完全一致。验证手段：

| 手段 | 命令 | 说明 |
| --- | --- | --- |
| 内核回归 | `dotnet test` | 265 个测试：规则、确定性、回放复现、跨端校验、视图适配、场地布局、标定闭环、视觉证据分线 |
| 无头复现 | `dotnet run --project src/Sim.Cli -- replay-check <file>` | 用记录的动作流逐位重放并比对 |
| 跨端一致 | `godot --headless --path godot -- --parity-check <file>` | Godot 壳按 CLI `replay-check` 语义比对最终比分/结束原因/末帧/事件指纹（无 Godot 时由 `CrossEndTests` 回归同一代码路径） |
| 视图适配 | `SnapshotViewTests` | 快照→渲染帧投影/插值不失真 |
| 布局一致 | `ArenaLayoutFlowTests` + 身份/旋转变换回归 | 编辑保存的 `arena-layout-v1` 场景在 CLI/Godot 驱动同一几何；identity 位姿逐位复现旧基线 |

Godot↔CLI 的同种子一致性已闭环：`godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json`
返回 PASS（比分 4:49、结束原因、末帧 2400、752 条事件指纹逐位一致）；旋转/平移后的场地回放
（`replays/rotated-seed42.json`，340 事件 2400 ticks）两端同样逐位一致 PASS。另见 `godot/README.md` 与 `src/Sim.Tests/CrossEndTests.cs`。

## 场地与位姿（arena-layout-v1）

场地几何单一来源是 `Scenario`：平台/出发区/能量块坐标一律以**场局部米制**表达，
`field.pose`（可选 `Pose2`）把场局部映射到仿真世界。`Sim.Core.FieldTransform` 是唯一
变换实现——`FieldModel` 公开接口按世界坐标入参，物理/传感器/快照在其内部转换；
台壁与围栏求解保持场局部轴对齐后再映回世界。协议演进是纯增量：
`layoutVersion` 缺省即传统身份布局，身份位姿与全部旧场景/回放**逐位一致**（回归门禁，非容差比较）。
桌面端 `LayoutDraft/LayoutEditor` 基于同一场景数据编辑布局，保存的 JSON 文件可被
CLI 与 Godot 直接加载复现；机器人 `.glb/.gltf` 外观模型仅存在于渲染层与本地偏好文件，
不进入 `Scenario`/`Snapshot`/回放指纹。

## 标定闭环（真机 → 参数 → 保真度）

物理参数（横向摩擦衰减、自转阻尼、块摩擦、恢复系数、堵转阈值、登台门控）的真值来自
**离线遥测标定**，而不是猜测：

```
真机试验导出 telemetry-v1 (SI, 严格入口校验)
   → Sim.Calibration 拟合 (fit 集) + 留出验证 (holdout 集) + mount 混淆矩阵
   → 报告 (输入 SHA-256 + contentSha256, 双列指标, 晋升条件与原因)
   → 推荐 patch → 新场景文件 (官方场景/旧回放逐位不变)
   → --update-fidelity 显式登记 (仅 holdout 达标且 capture.source=real)
```

- 算法数值等价迁移自遗留 `sim_calibrate.js`，以固定 fixture 回归（合成数据可恢复
  遗留预设参数，但**合成数据永不晋升** fidelity）。
- 登台门控 `MOUNT_V_MIN`/`MOUNT_ANGLE_MAX` 已是显式场景参数；标定器对它是
  **验证而非拟合**——真机成败与确定性门控对不上时如实报告"模型不足"，保持未标定。
- 契约细节与采集规范见 `telemetry/README.md`，命令见 `docs/CLI.md`。

## 保真度

见根目录 [`fidelity.json`](../fidelity.json)。规则与场地布局已验证；场地灰度为手绘、视觉为随机桩、
摩擦/碰撞/堵转/登台未标定。**模拟结果不能直接宣称为真机成绩。**

## 视觉证据分线（vision-replay-v1，evidence_only）

真实视觉回放是独立于 telemetry-v1 / sensor-calibration-v1 的**第三条离线证据线**，
新 schema、新命令（`vision import` / `vision evaluate`）、互不扩用：

```
MBri CSV（本地忽略目录, 不入库）
   → Sim.VisionReplay 严格导入 (方言按表头列集精确匹配; 校验矩阵全过才写盘, 原子写)
   → vision-replay-v1 证据包 (frames.jsonl, evidenceId+SHA256 哈希锁定) + 导入报告
   → Sim.Core VisionReplayAdapter (纯, 无 IO/时钟/随机) 经 MatchEngine(Scenario, IVisionAdapter)
     注入 → 策略消费回放 → vision-replay-report-v1 (链路质量 + 策略消费 + 指纹)
```

- 注入边界是**加性**的：`MatchEngine(Scenario)` 保持不变（classifyRate 随机桩路径
  逐位不变）；回放路径下适配器**绝不消费共享 Mulberry32 流**、绝不读取
  `VisionContext.Target` 世界真值制造答案，缺帧/过期/错误返回显式 unknown 原因码。
- 回放头新增可空加性字段 `visionEvidenceId`/`visionEvidenceSha256`；默认路径
  两者为 null（旧回放 JSON 逐位兼容）。`ReplayTick` 不加字段：同证据复现凭
  哈希锁定的证据包 + 场景 + 已录动作。
- Phase A 只回放模型自身 CSV 输出：报告恒 `groundTruth=false`、分级
  `evidence_only`、结论 `vision=random_stub (evidence_only)`，**不触发
  fidelity.json 晋升**；检测质量层（混淆矩阵/P/R/F1/IoU）为 Phase B（新采集 +
  人工标注 holdout）预留字段。
- `Sim.VisionReplay` 仅引用 `Sim.Protocol`（复用 ProtocolJson），不引用
  `Sim.Core`/`Sim.Calibration`；Sim.Core 通过自有的 `VisionReplayFrame`
  只读记录接收帧序列，内核不获得任何 IO 能力。

## 关键文件

- `src/Sim.Core/MatchEngine.cs` — 比赛内核（发令/暂停/判罚与真实重启/单步/快照/回放头）。
- `src/Sim.Core/FieldTransform.cs` — 场局部↔仿真世界唯一变换（纯函数，身份路径逐位直通）。
- `src/Sim.Core/{Physics,Sensors,Fsm,RuntimeState,DeterministicRandom,Js}.cs` — 物理/传感器/状态机/随机数。
- `src/Sim.Protocol/` — 版本化协议 DTO 与 JSON 校验（含 `arena-layout-v1` 布局字段与 `telemetry-v1` 遥测契约）。
- `src/Sim.Calibration/` — 纯标定库：拟合器、分解层、mount 门控评估、报告指纹。
- `src/Sim.VisionReplay/` — 视觉证据分线纯库：vision-replay-v1 schema、MBri 导入校验、链路质量指标、报告指纹。
- `src/Sim.Cli/Program.cs` — 无头命令；`PythonBridge.cs` — 外部策略进程适配；`VisionCommand.cs` — `vision import/evaluate`。
- `src/Sim.Cli/{BatchCommand,BatchExecutor,BatchFingerprint}.cs` — AI agent 无头批量仿真：严格预检、有界 worker pool（每场独立场景副本/引擎/控制器进程）、`sim-batch-result-v1` JSONL 与稳定指纹；并发编排全部留在 CLI 层，`Sim.Core` 不感知并行。
- `godot/src/SnapshotView.cs` — 快照→渲染帧（无 Godot 依赖，可单测）。
- `godot/src/MatchSession.cs` — 会话门面：固定步长实况 + 回放重构/缓存/导航（无 Godot 依赖，可单测）。
- `godot/src/ParityCheck.cs` — 跨端一致性校验（无 Godot 依赖，可单测）。
- `godot/src/LayoutDraft.cs` — 布局编辑模型：快照式撤销/重做 + 拖拽分组 + 校验 + 原子保存（无 Godot 依赖，可单测）。
- `godot/src/{LayoutEditor,ArenaVisualizer,HudPanel,MatchCamera,RobotModelLoader}.cs` — 桌面壳入口、编辑交互与渲染/HUD/相机/模型导入。
- `scenarios/*.json` — 固定布局回归场景；`replays/` — 回放文件；`test-data/` — 渲染层测试资产（glTF）。
