# 实时 YOLO 桥接：活源桥架构与真推理接入

## Goal

**用途 = 策略保真评估**：让 FSM 在仿真里吃"真车 YOLO 检测流"，而不是吃按概率掷骰的 `classifyRate` 桩（`src/Sim.Core/Fsm.cs:64-85`），从而在真实视觉质量下（真车 4fps 级帧率、200ms 级帧龄、误检/漏检/无目标帧）验证策略鲁棒性。仓库内可获得的最好质量样本 `src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv` 实测：fps p50 5.33、`received_age_ms` p50 11.9 / max 182.9、`inference_ms` p50 163.8 / max 592.0、状态分布 no_target 92 / target 16 / no_data_or_stale 5。

**桥接检测不接图像**：桥的输入是"每检测一条"的检测流（时间戳/状态/框/选中目标），输出是 FSM 消费的标准化检测（`Vision.Normalize`，`src/Sim.Core/Fsm.cs:110-130`）。不做渲染帧闭环（不把仿真画面喂给 YOLO），不动 RL 训练链路（`src/Sim.Cli/RlEnvCommand.cs`、`controllers/score_block_rl/` 不碰）。

**两阶段一个任务**：阶段 1 = 活源桥架构 + CSV 模拟流 + 等价验收（仓库内确定性可复现）；阶段 2 = 外部进程接真 yolo-ncnn-mbri 推理（素材在手可跑，权重不进仓库）。

**架构债对齐**：`IVisionAdapter` 的注释已承诺 "external YOLO bridges run outside the core and only refresh a cache (later dispatch)"（`src/Sim.Core/Fsm.cs:51-61`），但该 cache/dispatch 从未实现。本任务补齐这条语义，注入点仍是既有的唯一 classify 调用点（`src/Sim.Core/Fsm.cs:857`，SEARCH 的 classify 阶段），不改 FSM 决策逻辑与阈值。

## Requirements

- **R1 活源桥架构（Sim.Core）**：新增 `IVisionStreamSource`（帧源纯接口：无 IO、无时钟、无 RNG——仿真时间由调用方 `PumpUntil(simT)` 传入）与 `LiveVisionBridge : IVisionAdapter`（`ModeName = "liveBridge"`）。桥只做"取最新在窗帧"，输出台账复用既有 `VisionReplayConsumeRecord`（`src/Sim.Core/VisionReplayAdapter.cs:42-63`：Role/SimT/FrameSequence/AgeMs/Reason/Label/Confidence），便于事后审计与等价比对。三条纪律不得违反：**不调 `context.Random`**（Mulberry32 共享流不得位移）、**不读 `context.Target`**（模拟世界真值）、**不静默回退随机桩**。
- **R2 CSV 模拟流（阶段 1）**：`CsvStreamSource : IVisionStreamSource` 读真车 YOLO CSV（73 列 hunt 方言，`src/Sim.VisionReplay/MbriVisionDialect.cs:19-35`；class 0=good→buff / 1=bad→debuff），按 **SimT 缩放释放**：帧到达时刻 =（`vision_timestamp_ms` − 首帧）/1000，秒；`PumpUntil(simT)` 释放所有到达时刻 ≤ simT 的帧。放 `src/Sim.VisionReplay`（复用既有 73 列解析；Sim.Core 不反向依赖它）。
- **R3 等价性 = 验收门**：同帧数据、同 `maxAge` 下，`LiveVisionBridge + CsvStreamSource` 的消费记录序列必须与 `VisionReplayAdapter`（`src/Sim.Core/VisionReplayAdapter.cs:82`）**逐位一致**（帧序/年龄 ms/Label/Reason/Confidence 全字段），并连带断言事件指纹与比分一致。
- **R4 sidecar 录证与确定性重放**：live 场次（CSV 源与外部进程源同样）**自动**把该场次桥消费的帧流写成 vision-replay-v1 兼容证据包（`frames.jsonl` + `import-report.json`，规范序列化与原子写复用 `src/Sim.VisionReplay/VisionReplayIO.cs:55-117`），报告里写路径与 `evidenceId`/`evidenceSha256`；事后用既有 `VisionReplayAdapter` 加载该包重放，行为与现场一致（SimT 0 对齐语义一致）。
- **R5 报告基线**：`vision live` 报告 = ①链路质量指标（帧龄分布 p50/p95/max、stale 率、消费统计：classify 调用/消费/unknown 原因分布）②理想基线对比（同 seed、默认 `classifyRate=100` 视觉场次 `src/Sim.Core/SimParameters.cs:17` vs live 桥场次的**摘要级** diff：比分/ticks/doneReason/关键事件计数）③等价确认结论。两场 RNG 消费天然不同（只有 classifyRate 桩抽 `VisionContext.Random`），基线对比**只做摘要级、不做位对位**。
- **R6 装配双入口**：
  - **CLI 批量报告入口**：`vision live --source <CSV> --scenario <json> [--max-age-ms 500] --out <json> [--json] [--force]`；阶段 2 追加 `--process "<命令行>"` 与 `--realtime`。
  - **桌面目检入口**：桌面视觉源**三选一**——`classifyRate`（默认；不注入 adapter，行为逐位不变）/ `visionReplay`（证据包路径）/ `liveBridge`（CSV 路径 + maxAgeMs）；设置页可选可校验。**外部进程源仅 CLI，不进桌面**。
- **R7 外部进程源（阶段 2）**：`ExternalProcessStreamSource : IVisionStreamSource` 启动子进程、stdout 逐行读 JSONL → 帧流。**帧字段对齐真车 CSV 列**：`t,label,confidence,vision_timestamp_ms,sequence,selected_target,vision_status,vision_error,frame_width,frame_height,detection_count,detection_index,class_id,target_type,bbox_x1..y2,center_x/y,offset_x/y,inference_ms,fps,received_age_ms`。子进程必须逐帧 flush（`python -u` / `flush=True`，写进 XML 文档注释）。流断裂/进程退出 → 明确状态（后续消费记 stale + reason），不抛异常炸引擎。**真推理源必须 `--realtime 1x` 跑**（引擎步进按墙钟 1x 对齐）——外部流按真实时间到达，快跑会全 stale。
  - `tools/yolo-bridge/mbri_yolo_bridge.py`：包裹 yolo-ncnn-mbri 推理循环输出上述 JSONL；`--stub <csv>` 模式（读真车 CSV 按墙钟真实节奏推帧、不依赖权重，本地可跑）；真权重模式只留接口 + 文档接入点（**权重/二进制不进仓库**）。
  - `tools/yolo-bridge/selftest.py`：stub 模式自测（`py -3.12`）。
- **R8 门禁与兼容**：
  - **live 桥场次拒绝普通 replay 录制**：`ReplayHeader.VisionMode` 写 `"liveBridge"`（字段位置 `src/Sim.Protocol/ReplayHeader.cs:66-67`，配合 `:88-97` 的 additive 证据字段），录制/复现路径给出明确拒绝并提示改用 sidecar 证据包（live 桥的帧到达依赖外部时序，动作流回放不可复现）。
  - **CoreVersion 不 bump**（additive：默认路径行为、rng 消费序、回放身份都不变）；`classifyRate` 默认路径逐位不变。
  - 证据诚实性沿用 Phase A：`groundTruth=false`、`grade=evidence_only`、不晋升 `fidelity.json`、不宣称识别准确率（`docs/CLI.md:209-211`）。
- **R9 不做**：渲染帧闭环、RL 训练链路改动、`fidelity.json` 晋升、真权重入库、FSM 阈值/决策逻辑改动、Legacy14/既有回放身份改动。

### 验收清单

**① 等价性门（阶段 1 核心）**
- [ ] 新单测：同 fixture CSV、同 maxAge，`LiveVisionBridge + CsvStreamSource` 与 `VisionReplayAdapter` 的消费记录**逐位一致**（断言到每条记录的每个字段：FrameSequence/AgeMs/Reason/Label/Confidence），并断言两场的 ticks/比分/事件指纹一致。
- [ ] 断言**非空洞**：至少 N 条记录 `FrameSequence != null` 且至少一条交付 buff/debuff——否则"两侧都空"会假绿（实测参照：mini 证据包默认会话 245 帧中 24 帧被服务，首次服务 SimT≈3.1 s、末次≈44.5 s）。
- [ ] 断言失败信息能指名第几条记录、哪个字段分叉（不是弱化的摘要等值）。

**② 桥单测**
- [ ] SimT 0 = 源首帧对齐（首帧之前 classify → `no_frame`；首帧之后无帧 → `stale`）；边界闭合：`ageMs == maxAgeMs` 在窗、`> maxAgeMs` 判 stale。
- [ ] RNG-free 反证：与抽流的适配器对照，事件指纹必须不同（照 `src/Sim.Tests/VisionReplayAdapterTests.cs:273-288` 的反证写法）；不读 Target：构造"世界真值全错"的帧流，桥输出仍只由帧决定。
- [ ] 异常路径：空流、时间戳重复/乱序、单帧流。
- [ ] `ReplayHeader.VisionMode == "liveBridge"`；默认路径（不注入）仍 `"default"` 且序列化字节里不出现 liveBridge/证据字段。

**③ sidecar 复现测试**
- [ ] live 场次自动产出 sidecar 证据包（`frames.jsonl` + `import-report.json`），报告记录路径与 evidenceId/Sha256。
- [ ] 用 `VisionReplayAdapter` 加载该 sidecar 重放，消费记录与 live 场次一致（SimT 0 对齐语义一致）。

**④ 录制门禁**
- [ ] live 桥场次（`VisionMode=liveBridge`）走普通 replay 录制被拒绝，错误信息指名改用 sidecar；`visionReplay` / 默认路径录制不受影响。

**⑤ 回归**
- [ ] `dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1` 全量通过，基线 **421 通过 + 1 Skip 零回归**（本目录写档前实测：`已通过! - 失败: 0，通过: 421，已跳过: 1，总计: 422`）。
- [ ] `replay-check replays/seed-42.json` 逐位 PASS（legacy 身份与行为不变）。

**⑥ Godot 构建零错误**
- [ ] `dotnet build godot/GodotSim.csproj -m:1` → 0 错误 0 警告（本目录写档前实测：`已成功生成。0 个警告 0 个错误`）。

**⑦ py 自测**
- [ ] `py -3.12 tools/yolo-bridge/selftest.py` 通过（stub 模式：帧节奏、字段契约、flush 行为、异常退出）。

**⑧ 协议冒烟**
- [ ] stub 子进程推 JSONL → 桥消费 → sidecar 重放一致（至少一条能在 CI 跑的用例），并有一次真实 CLI `--process` 冒烟产出报告。

## Notes

- 阶段 1（批 1-3、批 4）不依赖任何权重/相机，全在仓库内可跑可测；阶段 2（批 5）的真权重端到端在用户环境执行，仓库内只保接口、契约与 stub 自测。
- 本任务与既有 vision 分线的边界：不改 `vision import` / `vision evaluate` 的语义与产物（`src/Sim.Cli/VisionCommand.cs`），`vision live` 是新增子命令；sidecar 产出的包必须能被既有 `vision evaluate` 直接消费。
- 真车 CSV、sidecar 证据包、`calibration/*` 输出按既有 `.gitignore` 走本地目录（`vision/*`、`calibration/*` 需 `git add -f` 才入库）。
- 报告层一律 `evidence_only`：live 桥证明的是"策略在真视觉质量下的行为"，不是识别准确率、也不是真实比赛成绩。
- 技术取舍与批 1-6 组织见 `design.md`；步骤、命令与验证矩阵见 `implement.md`。
