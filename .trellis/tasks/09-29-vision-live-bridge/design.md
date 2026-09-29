# 技术设计：实时 YOLO 桥接（活源桥 + 真推理进程）

需求已拍板（见 `prd.md`），本文不重开需求，只写落地取舍。
本文按**批 1-6**组织（批 1 Sim.Core 桥架构与单测；批 2 CSV 模拟流源与等价/sidecar/门禁测试；批 3 CLI `vision live` 与报告；批 4 桌面三源装配与设置页；批 5 外部进程源与真推理脚本；批 6 工件与报告）。

## 0. 数据流总览

```
真车 YOLO 检测流（三种来源，都不含图像）
  A) 真车 CSV（73 列 hunt 方言）      ─┐
  B) 外部进程 stdout JSONL（真 yolo-ncnn-mbri / stub） ─┤
  C) 既有证据包（visionReplay，桌面/CLI 已有路径）      ─┘
        │  (A)(B) → CsvStreamSource / ExternalProcessStreamSource ：IVisionStreamSource（SimT 释放 / 墙钟到达）
        ▼
  LiveVisionBridge : IVisionAdapter（Sim.Core，RNG-free、不读 Target、台账复用 VisionReplayConsumeRecord）
        │ 唯一消费点 = FSM classify 阶段（src/Sim.Core/Fsm.cs:857）
        ▼
  整场比赛（MatchEngineHost.Create(scenario, adapter)，src/Sim.Hosting/MatchEngineHost.cs:10）
        ├─ 现场台账 Consumes → 报告（帧龄分布/stale 率/消费统计）
        ├─ sidecar：本场帧流 → vision-replay-v1 证据包 → 事后 VisionReplayAdapter 确定性重放
        └─ 基线对比：同 seed 默认 classifyRate=100 场次（摘要级 diff）
```

## 决策① 分层与项目边界

- **Sim.Core 放桥与契约**：`VisionStreamFrame`（JSONL/CSV 契约 DTO）、`IVisionStreamSource`（纯接口）、`LiveVisionBridge`。理由：桥必须能经既有唯一注入点（`MatchEngineHost.Create(scenario, visionAdapter)`）进入引擎，`IVisionAdapter` 定义在 Core（`src/Sim.Core/Fsm.cs:56-61`）。
- **`CsvStreamSource` 放 `src/Sim.VisionReplay`**：73 列方言解析（`src/Sim.VisionReplay/MbriVisionDialect.cs:19-35`）与归一化（`VisionEvidenceBuilder`）只此一份，Sim.Core 不反向依赖它。
  - **spec 冲突（须披露）**：`.trellis/spec/sim/vision-replay-contract.md:5-6` 写"新纯库（`src/Sim.VisionReplay`，仅引用 Sim.Protocol）"，`:84` 把"让 Sim.VisionReplay 引用 Sim.Calibration/Sim.Core"列为 Wrong。本决策改写该条（Sim.VisionReplay 增引 Sim.Core 以复用 `IVisionStreamSource`/`VisionStreamFrame`）；分线纪律保留：仍不引 Sim.Calibration、不扩用 sensor-calibration-v1。
- **`ExternalProcessStreamSource` 放 `src/Sim.Core`**（批 5 指令）：只用 `System.Diagnostics.Process` + `System.Text.Json`。
  - **spec 冲突（须披露）**：`vision-replay-contract.md:85` 把"在 Sim.Core 读文件/时钟"列为 Wrong；Sim.Core 现状确实零 IO/Process（全库 grep 无命中，`src/Sim.Core/Sim.Core.csproj` 仅引 Sim.Protocol）。处置：把纪律收窄为"**仿真内核路径**零 IO/时钟/进程；实时桥的进程 IO 单独成文件、只在 `PumpUntil` 被调用方驱动、不引入自己的时钟源"，批 6 更新 spec 写明。
- **编排（IO 写盘、CLI 参数、报告）在 Sim.Cli / godot**：`vision live` 子命令、报告写出、sidecar 落盘都在命令层，沿用 `VisionCommand.cs` 的"先全量预检、再原子写"惯例（`src/Sim.Cli/VisionCommand.cs:104-166`）。

## 决策② 等价性是结构性保证，不是"两处复制后祈祷"

`VisionReplayAdapter`（`src/Sim.Core/VisionReplayAdapter.cs:82`）已把三件事做对了，桥必须逐位等价：

| 语义 | 参照位置 | 桥的义务 |
|---|---|---|
| SimT → 流时间：`tMs = 首帧时间戳 + T*1000` | `VisionReplayAdapter.cs:132-134,157` | 源的会话首帧 = SimT 0（相对首帧的 ms 与 epoch 基准做差等价） |
| 选帧 = 时间窗内最新帧（二分） | `:161-174`、`:288-307` | 同一规则；`ageMs > maxAgeMs` → `stale`，`== maxAgeMs` 在窗内 |
| 消费记录字段/原因码 | `:42-63`、`:221-264`（`no_frame/stale/error/no_target/no_selection`） | 同一 `VisionReplayConsumeRecord` 台账 |

实现取向（**首选**）：把上述三段抽成 Sim.Core 内的共享纯函数（选帧二分 + 窗口/stale 判定 + 记录构造），`VisionReplayAdapter` 与 `LiveVisionBridge` 同时调用——等价成为结构性事实。抽取必须**行为不变**，由既有 `src/Sim.Tests/VisionReplayAdapterTests.cs`（含 RNG 零抽流反证）与 `VisionReplayEvaluateTests.cs`（E2E 指纹）钉住。
**备选**：若不动参照实现，则桥内实现同语义并由等价门逐位比对（同一批帧喂两个适配器）——功能等价但结构性保证弱一级，仅在首选被既有测试反驳时采用。

## 决策③ sidecar 记录范围 = 本场次源所交付的帧流（不是"只录被服务到的帧"）

- 证据包格式沿用 vision-replay-v1：`frames.jsonl` 为规范 `VisionFrameRecord`（`VisionReplayIO.SerializeFrames`，`src/Sim.VisionReplay/VisionReplayIO.cs:55-64`），加 `import-report.json`（合成合法 `VisionImportReport`：`files[0].path` 必须等于 frames 的 `session`，否则 `vision evaluate --evidence` 的默认会话选择会失配，见 `src/Sim.Cli/VisionCommand.cs:261-267`）。
- **必须包含源会话的首帧**（基准锚点）。原因：`VisionReplayAdapter` 的基准取包内首帧（`_sessionStartMs = _frames[0].TimestampMs`，`VisionReplayAdapter.cs:132-134`）。若只录"被服务到的帧"（首个被服务帧通常晚于流首帧数秒），复现时基准右移 Δ 秒 ⇒ 每次 classify 的 `tMs` 变早 ⇒ 窗口内最新帧选择与年龄全变 ⇒ 重放分叉。故 sidecar 录"本场次内被源交付的帧"，每帧在报告里标 served/unserved（不裁剪流，也不伪造帧）。
- 内容与"消费帧"的关系：被服务帧 ⊂ 交付帧；报告给出两者计数与差额，审计意义由报告承担，证据包保持规范形态（可被既有 `vision evaluate` 直接消费）。

## 决策④ JSONL 契约与时间语义

- 契约 DTO `VisionStreamFrame` 的 JSON 名**对齐真车 CSV 列**（显式 `[JsonPropertyName]`，先例 `src/Sim.Protocol/ReplayHeader.cs:50-55` 的 snake/camel 混排写法）；序列化复用 `ProtocolJson`（camelCase policy 被显式特性覆盖）：

| JSONL 字段 | 真车 CSV 列 | 说明 |
|---|---|---|
| `t`, `received_age_ms` | 同名列 | 服务侧自报值，**审计用**：桥的 age 由 SimT 自算，不采信服务侧自报 |
| `sequence`, `vision_timestamp_ms` | 同名列 | 帧号 + 采集主机 epoch ms（源据此换算到达时刻） |
| `vision_status`, `vision_error`, `selected_target` | 同名列 | 状态/错误/选中检测下标（状态枚举与 `VisionReplaySchemas.Statuses` 一致） |
| `detection_count` + `detections[]` | `detection_count` + 每检测一行 | 每检测一条（`detection_index` 顺序）；detection 内：`label`,`class_id`,`target_type`,`confidence`,`bbox_x1..y2`,`center_x/y`,`offset_x/y` |
| `frame_width/height`, `fps`, `inference_ms` | 同名列 | 帧元数据与链路指标来源 |
| （新增）`arrival_sim_t` | — | 源按（`vision_timestamp_ms` − 首帧）/1000 算出的到达 SimT，写出便于审计 |

- 图像永不进入契约：无像素/无帧缓冲字段。
- **SimT 缩放释放**（CSV 源）：`PumpUntil(simT)` 释放到达时刻 ≤ simT 的帧；因此"真车 18.2 s 的流"在 120 s 的比赛里前 18.2 s 有帧、之后按 `stale` 计（与 `vision evaluate` 的既有时基契约一致，`docs/CLI.md:204-205`）。
- **墙钟 1x**（外部进程源）：真推理帧按墙钟到达，`--realtime 1x` 是**正确性条件**（快跑 ⇒ 窗口内无新帧 ⇒ 全 stale，得到的是假阴性报告）；CLI 只接受显式 1x，其它值明确报错。
- 外部进程 stdout 必须**逐帧 flush**（`python -u` / `flush=True`），否则桥看到的是一大块缓冲 ⇒ 到达时刻成簇。该要求写进 `ExternalProcessStreamSource` 的 XML 文档注释与 `tools/yolo-bridge/README`。流断裂/进程退出：缓冲内完整行继续可用，之后消费记 `stale`/`no_frame` 并带 reason，不抛异常。

## 决策⑤ 桌面三源装配与默认位不变

- 注入点缺口：`MatchSession` 构造恒 `MatchEngineHost.Create(scenario)`（`godot/src/MatchSession.cs:34-38`），`ResetToLive` 重建引擎（`:146-160`），`DesktopLiveDriver.Run` 也自建引擎（`godot/src/DesktopLiveDriver.cs:153`）。**两处引擎归属点都要接**，且必须传**工厂**（`Func<IVisionAdapter?>`）而不是实例：适配器带每场台账与基准，不能跨场复用。
- 三源语义（`DesktopSettings.Vision`）：
  - `classifyRate`（默认）：工厂为 null ⇒ 引擎内部 `new ClassifyRateVision(_params)`（`src/Sim.Core/MatchEngine.cs:107`）⇒ **行为逐位不变**；
  - `visionReplay`：证据包目录 → `VisionReplayAdapter`；
  - `liveBridge`：CSV 路径 + maxAgeMs → `CsvStreamSource + LiveVisionBridge`；
  - 外部进程源**不进桌面**（CLI-only，已拍板）。
- 复用而非复制：`vision evaluate` 现有的"哈希锁定读证据包 + 映射为回放帧"逻辑（`src/Sim.Cli/VisionCommand.cs:226-290`）抽为 `Sim.VisionReplay` 的加载器（如 `VisionEvidencePackage.Load(dir)`），CLI 与桌面共用；抽取后 `vision evaluate` 行为不变由其既有测试与 E2E 指纹钉住。
- 设置页：照"小车"页先例（`godot/src/SettingsPanel.cs:175-182` 的 tab 装配、`BuildVehiclePage` 的字段/校验风格、`DesktopSettings.Validate` 的范围校验）。**已知坑**：`SettingsPanel.AddLabel` 内部已 `AddChild`，外部不得重复 AddChild。

## 决策⑥ 门禁：liveBridge 场次不可作为 replay 录制/复现

- live 桥的帧到达依赖外部时序（CSV 是确定性的，但"live 桥"这个概念在回放里不可由动作流复现），因此 `VisionMode` 写 `"liveBridge"`（`src/Sim.Protocol/ReplayHeader.cs:66-67`，语义为自由字符串，`Validate` 只查非空 `:134`），并在**录制**与**按回放复现**两条路径拒绝：
  - 录制：CLI `replay-record`（`src/Sim.Cli/Program.cs:162-190`）与 godot 录制/parity 路径（`godot/src/Main.cs:928-945`、`godot/src/ParityCheck.cs:33`）在写文件前校验 header；
  - 复现：`MatchEngineHost.CreateForReplay`（`src/Sim.Hosting/MatchEngineHost.cs:29-62`）是 CLI `replay-check` 与 godot 载入回放的共同入口。
- 拒绝信息必须**指路**：改用 sidecar 证据包 + `VisionReplayAdapter`（`vision evaluate --evidence <sidecar>`）做确定性复现。
- 反例保护：`visionReplay` 与默认路径的录制/复现**不得**被该门禁波及（测试要覆盖三态）。

## 决策⑦ 报告 schema 与基线口径

- 新 schema 常量 `vision-live-bridge-report-v1` 加在 `VisionReplaySchemas`（先例：`src/Sim.VisionReplay/VisionReplayManifest.cs:15,18` 的两个常量），报告写出沿用 `VisionReplayIO.Fingerprint`（`generatedAt` 不入哈希）+ `WriteAtomically` 的惯例，保证"同输入同指纹"。
- 报告分区：
  1. `source`：源种类（csv/process）、路径与 SHA-256、会话名、帧数、时间跨度、`classMapping`（显式 `good→buff / bad→debuff`，禁止沉默默认）、`realtime`；
  2. `link`：帧龄分布（p50/p95/max）、**stale 率** = `unknown(stale)/classifyCalls`、消费统计（classify/消费/unknown 原因分布、FSM 检测分布）、服务侧自报 fps/`inference_ms` 分布；
  3. `equivalence`：等价确认（live 台账指纹 vs `VisionReplayAdapter` 台账指纹、结论、比对帧数）；
  4. `sidecar`：目录、`frames.jsonl`/`import-report.json`、`evidenceId`/`evidenceSha256`、交付帧数/served/unserved、重放复现结论；
  5. `live` 与 `baseline`：ticks/比分/doneReason/**关键事件计数**（`EventKind`：Mount/Drop/Recover/BlockOff/BlockScore/Penalty/RestartPenalty/SimultaneousDrop/Inactivity/Incapacitated/Timeout/End，`src/Sim.Protocol/Event.cs:8-48`）/事件总数/事件指纹；
  6. `diff`：摘要级差异（比分差、ticks 差、事件计数差），并**明写**"不做位对位：两场 RNG 消费天然不同（只有 classifyRate 桩抽 `context.Random`，`src/Sim.Core/Fsm.cs:73-75`）"；
  7. `grade/groundTruth/limitations`：恒 `evidence_only` / `false`；不做识别准确率宣称、不晋升 `fidelity.json`。
- 基线口径：**同 seed、同场景、唯一变量是视觉源**——基线场次用默认参数（`classifyRate` 默认 100，`src/Sim.Core/SimParameters.cs:17`）且不注入 adapter。

## 批 1：Sim.Core 桥架构与单测

范围：`src/Sim.Core` 新增桥与契约（决策①②④的 Core 部分）+ 单测。

- 契约：`VisionStreamFrame`（含 `ToReplayFrame()`：剥审计字段 → 既有 `VisionReplayFrame`，让桥的工作集与参照适配器同型）；`IVisionStreamSource { int PumpUntil(double simTimeSeconds); IReadOnlyList<VisionReplayFrame> Released { get; } }`。
- `LiveVisionBridge : IVisionAdapter`：`ModeName = "liveBridge"`；`Classify` 先 `PumpUntil(context.T)`，再按共享选帧语义取窗内最新帧；`Consumes`/`LastByRole` 对外只读（对齐 `VisionReplayAdapter.cs:148-152`）。
- 纪律测试（照 `src/Sim.Tests/VisionReplayAdapterTests.cs:273-288` 的反证写法）：抽流对照指纹必须不同；帧流独立于 `context.Target`。
- 边界测试：SimT 0 = 首帧；`ageMs == maxAgeMs` 在窗、`maxAgeMs + ε` → stale；空流/单帧流/重复时间戳/乱序。
- 退出条件：`dotnet test --filter "FullyQualifiedName~LiveVisionBridge"` 全绿，且全量测试零回归。

## 批 2：CsvStreamSource + 等价/sidecar/门禁测试

范围：`src/Sim.VisionReplay`（`CsvStreamSource`、sidecar 写出、CSV→桥的装配工厂）+ `src/Sim.Hosting`/`Sim.Cli` 的门禁助手 + 测试。

- `CsvStreamSource : IVisionStreamSource`：读单个 CSV 路径（`--source <csv>` 形态）；复用 `MbriVisionDialect`（方言语种判定：表头列集精确匹配，`MbriVisionDialect.cs:51-60`）与既有归一化（预热行/重收帧聚合口径与 import 一致，`src/Sim.VisionReplay/VisionEvidenceBuilder.cs:27-31`）；`PumpUntil` 按 SimT 缩放释放（到达 = （`vision_timestamp_ms` − 首帧）/1000 s）。
- **等价门（本批核心验收）**：同一 CSV 两侧同源不同路——①`CsvStreamSource + LiveVisionBridge`；②`VisionEvidenceBuilder`（import 路径）→ `VisionReplayAdapter`——同场景同 `maxAgeMs` 跑整场，**逐条**消费记录全字段一致 + 事件指纹/比分一致。断言必须是逐字段逐记录（不是计数摘要），且必须**非空洞**（至少 N 条 `FrameSequence != null` + 至少一条 buff/debuff）：两侧都是"全 stale"也能相等，要显式排除这种假绿。实测时基参照（既有 `vision evaluate` 在 mini 包默认会话上的运行）：FSM 首次 classify 在 SimT≈3.1 s、末次≈44.5 s，245 帧中 24 帧被服务。
- sidecar：`frames.jsonl`（规范 `VisionFrameRecord`）+ `import-report.json`（合法 `VisionImportReport`：`files[0].path == session`、`groundTruth=false`、`grade=evidence_only`、`evidenceId/Sha256` 由 frames 字节计算）；写出后**用 `VisionReplayAdapter` 加载重放**并与 live 台账比对（复现测试）。
- 门禁测试：`VisionMode=liveBridge` → 录制/复现被拒绝且提示指路；`visionReplay`、默认路径三态不被误伤。
- fixture：`src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv`（113 行 → 101 帧；含 `no_target`/`target`/`no_data_or_stale` 三态与多检测行，够覆盖等价门）。测试自建临时证据目录（`vision/*` 是 gitignored，测试不得依赖 `vision/evidence-mini` 在工作区存在）。

## 批 3：CLI `vision live` + 报告 + docs

范围：`src/Sim.Cli`（`VisionCommand` 加 `live` 子命令）+ `docs/CLI.md`。

- 命令面：`vision live --source <csv> --scenario <json> [--max-age-ms 500] --out <json> [--json] [--force]`；退出码照 `vision` 惯例（0 报告已写 / 1 校验·IO / 2 用法，`src/Sim.Cli/VisionCommand.cs:23-59`）；`--max-age-ms` 默认沿用 `DefaultMaxAgeMs = 500`（`:21`），非法值显式报错（先例 `:196-203`）。
- 流程：预检（CSV 存在/方言可判/场景 `Validate`/输出可写，先于任何写出）→ 建源与桥 → 注入引擎跑整场（先例 `:286-291`）→ 跑同 seed 基线场次 → 写 sidecar → 写报告 → stdout 摘要（中文、逐块状态行，先例 `:402-410`）。
- sidecar 路径默认 `<out 同目录>/<out 去扩展名>-sidecar/`，报告里写绝对路径；`--json` 打印全量报告。
- docs：`docs/CLI.md` 补 `vision live` 节（SimT 缩放语义、sidecar、基线口径、退出码），并说明与 `vision import|evaluate` 的分工。

## 批 4：桌面三源装配 + 设置页

范围：`godot/src/MatchSession.cs`、`godot/src/DesktopLiveDriver.cs`、`godot/src/Main.cs`、`godot/src/DesktopSettings.cs`、`godot/src/SettingsPanel.cs`（+ `Sim.Tests` 链接回归）。

- `MatchSession(scenario, Func<IVisionAdapter?>? visionFactory = null)`：ctor 与 `ResetToLive` 都经工厂建适配器；`DesktopLiveDriver` 同理（`DesktopLiveDriver.cs:153`）。默认 null 工厂 = 现状逐位不变。
- `DesktopSettings.Vision`（`VisionSettings` 记录：`source` 枚举默认 `classifyRate`、`evidencePath`、`csvPath`、`maxAgeMs` 默认 500）+ `Validate` 范围校验 + 只在显式非默认源时构造工厂（照 `VehicleSettings.ApplyVehicleOverrides` 的"仅作用于相关场景"模式）。
- `SettingsPanel` 加"视觉"页（下拉/路径/maxAge），风格照 `BuildVehiclePage`；注意 `AddLabel` 的 parent 坑。
- 测试：`Sim.Tests` 已链接 `MatchSession.cs`/`DesktopSettings.cs`/`DesktopLiveDriver.cs`（`src/Sim.Tests/Sim.Tests.csproj:31-38`）——默认源位不变、三源工厂装配、设置校验都在无 Godot 环境可测；`Main.cs` 依赖 Godot 进不了 Sim.Tests，作为**已披露缺口**写进报告。
- 构建门：`dotnet build godot/GodotSim.csproj -m:1` 零错误。

## 批 5：ExternalProcessStreamSource + mbri_yolo_bridge.py + --realtime + 冒烟

范围：`src/Sim.Core`（进程源）、`src/Sim.Cli`（`--process`/`--realtime`）、`tools/yolo-bridge/`（Python）、协议冒烟测试。

- `ExternalProcessStreamSource : IVisionStreamSource`：`Process.Start`（`UseShellExecute=false`，命令串拆分沿用 `src/Sim.Controller/ExternalControllerBridge.cs:42-50,164` 的 `SplitCommand` 惯例）+ stdout 逐行读；**非阻塞** `PumpUntil`（Windows 管道语义：只取已缓冲的完整行，`Peek`/`ReadLine` 或等价）；JSONL 解析失败 → 记帧级错误而不是崩；会话基准 = 首个到达帧（首帧之前 classify → `no_frame`）。
- `tools/yolo-bridge/mbri_yolo_bridge.py`：包裹 yolo-ncnn-mbri 推理循环 → 输出决策④的 JSONL（逐帧 flush）。两种模式：`--stub <csv>`（读真车 CSV 按墙钟真实节奏推帧，无权重依赖，本地/CI 可跑）；真权重模式留接口 + 文档接入点（用户素材：`D:/project/robocup/2026/MBri/rpi-yolo-pi4-int8-lto-8fps/`（`README.txt`：YOLO26n 320 NCNN full INT8、JSON Lines 输出；`model/model.ncnn.bin|param` 不进本仓库），原生帧的 `status/timestamp_ms/target{type,confidence,center_x…}` 需归一化为本仓库 CSV 列名契约）。
- `tools/yolo-bridge/selftest.py`：stub 模式自测（帧节奏、字段契约、flush 行为、异常/退出路径），风格照 `controllers/mbri_adapter_selftest.py`。
- CLI：`--process "<命令行>"` 换源；`--realtime 1x` 把引擎步进按墙钟 1x 对齐（外部流按真实时间到达；非 1x 明确拒绝）。真推理源必须带 `--realtime 1x`。
- 协议冒烟（可被 CI 跑）：repo 内 stub 子进程推 JSONL → 桥消费 → sidecar 重放一致（Sim.Tests 用例）+ 一次真实 CLI `--process` 冒烟产出报告。

## 批 6：工件与报告

- 更新 `.trellis/spec/sim/vision-replay-contract.md`：新增 live 桥条目（`IVisionStreamSource`/`LiveVisionBridge`/`vision live`/sidecar/`VisionMode=liveBridge` 门禁），并按决策①收窄两条旧纪律（Sim.VisionReplay 可引 Sim.Core；"Sim.Core 零 IO"限定为仿真内核路径）。
- `report.md`：结论先行 + ⑧ 项验收逐条证据（命令与输出）、等价门结果、sidecar 复现结果、基线 diff 数值、Godot 构建与 py 自测输出、真权重端到端**未跑**的披露（需用户环境）、评审缺陷与修复。

## 兼容与回滚

- 默认路径位不变是硬门：不注入 adapter 的 `MatchEngine` 行为、rng 消费序、回放身份、`replay-check replays/seed-42.json` 逐位一致。
- CoreVersion 不 bump（additive）；`ReplayHeader` 不新增字段（`VisionMode` 是既有自由字符串，`VisionEvidenceId/Sha256` 已有）。
- 回滚粒度：`vision live` 是独立子命令、sidecar 独立目录、桌面三源默认关闭 ⇒ 任一批可单独停用而不影响现状。
- 禁止为了"好看"改旧 fixture、旧回放或基线数字；`vision import|evaluate` 的语义与产物不动。

## 风险与披露

- **等价门最脆处**：CSV 归一化复用是否真的同源（预热行/重收帧/多检测聚合）。若复用出错，等价门会红——这正是它作为验收门的价值。
- **外部进程源不可复现**：真推理的到达时序依赖墙钟与设备，所以 live 桥场次以 sidecar 证据包（帧流）而非动作流回放作为证据；报告只做摘要级基线对比。
- **桌面 `Main.cs` 装配无自动测试**（Godot 依赖）：只能靠 Godot 构建 + 人工目检，报告如实披露。
- **真权重端到端不在仓库内跑**（权重/相机/树莓派在用户环境）：本地只跑 stub 契约链；报告披露未覆盖范围。
