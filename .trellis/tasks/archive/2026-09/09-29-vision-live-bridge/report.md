# 实时 YOLO 桥接 — 实施报告（2026-09-29/30）

## 结论

两阶段全部落地，本任务验收 ①-⑧ **全过**：活源桥（`LiveVisionBridge` + `IVisionStreamSource` + `VisionStreamFrame` 契约）与既有 `VisionReplayAdapter` 的消费语义**结构性等价**（共享 `VisionFrameSelector`），CSV 模拟流场次自动产出 vision-replay-v1 证据包并可被既有 `vision evaluate` 确定性复现，`vision live` 报告给出链路指标 + `classifyRate=100` 基线摘要 diff，桌面三源装配（默认 `classifyRate` 位不变 / `visionReplay` / `liveBridge`）与外部进程源（stdout JSONL + `--realtime 1x` + `mbri_yolo_bridge.py --stub`）均落地。

终验门**本次实测**：`dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1` → **通过 496 / 跳过 1 / 失败 0 / 总计 497**；`replay-check replays/seed-42.json` → **PASS（scores 4:49，events 752/752）**。

关键量化事实（本次实测，见下节证据）：

- **等价是"三方同一指纹"**：同一 `hunt_drive` 会话、同场景 seed=42，①live CSV 桥场次、②既有离线 `vision evaluate`（换能器=冻结参照）、③进程桥 sidecar 重放的**事件指纹完全一致**：`343b1bb593af07a419de6695f6eb41c89dddab0a5c2e49ce3beefc66dc6a3a11`。
- **sidecar 可复现**：`vision evaluate --evidence <sidecar>` 两次复跑除 `generatedAt` 外**逐位一致**（`contentSha256` 相同，`policyFingerprint=343b1bb593af07a4…`）。
- **进程源冒烟真跑通**：stub 子进程 → 桥 → sidecar 全链墙钟 **2m4s**（1x 对齐 + 官方 120 s 场景），进程 `state=exited exitCode=0 故障=0(坏行 0)`，`realtime=1`，等价成立。
- 一处**未完成项**（非本次验收门）：`.trellis/spec/sim/vision-replay-contract.md` 未按 design/implement 批 6 第 1 条更新（详见"未完成与残余"）。

## 交付清单

新增（`git status` 实测）：

- `src/Sim.Core/`：`VisionStream.cs`（JSONL 契约 DTO）、`LiveVisionBridge.cs`、`VisionFrameSelector.cs`（选帧/窗口/原因码共享纯函数）、`ExternalProcessStreamSource.cs`
- `src/Sim.VisionReplay/`：`CsvStreamSource.cs`、`VisionSidecar.cs`、`VisionReplayFrames.cs`、`VisionEvidencePackage.cs`、`VisionLiveReport.cs`
- `src/Sim.Tests/`：`LiveVisionBridgeTests.cs`、`VisionStreamEquivalenceTests.cs`、`ReplayVisionGateTests.cs`、`VisionLiveCommandTests.cs`、`VisionSourceWiringTests.cs`、`VisionProcessLiveCommandTests.cs`、`ExternalProcessStreamSourceTests.cs`、`VisionStreamStub/`（stub 子进程 fixture）
- `tools/yolo-bridge/`：`mbri_yolo_bridge.py`、`selftest.py`、`README.md`
- 工件：`.trellis/tasks/09-29-vision-live-bridge/{prd,design,implement}.md`

修改：`src/Sim.Core/{MatchEngine.cs,VisionReplayAdapter.cs}`、`src/Sim.Hosting/MatchEngineHost.cs`、`src/Sim.Cli/{Program.cs,VisionCommand.cs}`、`src/Sim.VisionReplay/{VisionEvidenceBuilder.cs,VisionReplayIO.cs,VisionReplayManifest.cs,Sim.VisionReplay.csproj}`、`godot/src/{MatchSession.cs,DesktopLiveDriver.cs,DesktopSettings.cs,Main.cs,SettingsPanel.cs,HudPanel.cs,ParityCheck.cs}`、`godot/GodotSim.csproj`、`docs/CLI.md`、测试工程与既有测试同步。

产物（本地、gitignored）：`calibration/vision-live.json`、`calibration/vision-live-sidecar/`、`calibration/vision-live-sidecar-eval.json`、`calibration/vision-live-process.json`、`calibration/vision-live-process-sidecar/`。

## 验收逐条

### ① 等价性门（阶段 1 核心）— 通过

- 测试面：`src/Sim.Tests/VisionStreamEquivalenceTests.cs:284-338` `[Theory] LiveBridge_ConsumesIdenticallyToTheReplayAdapter`，两条数据 `hunt_drive`（minServed=2）/ `good_recheck`（minServed=10、且要求至少一条 buff/debuff）；断言逐条逐字段（记录分叉时打印"第 i 条消费记录分叉"）+ 事件指纹/ticks/比分/结束原因逐位一致；**非空洞**由 `served >= minServed` 与"全部记录都必须消费到真实帧"（`:305-313`）显式把关；源交付集必须是会话帧前缀、锚点时间戳一致（`:315-321`）。
- 行为面（**本次实测**）：live CSV 桥（`calibration/vision-live.json` 的 `live.eventFingerprint`）= 既有离线 `vision evaluate --session hunt_drive_20260817_095205.csv`（我现场跑的 `tmp/archivist-check-hunt.json`）= 进程桥 sidecar 重放，三者同为 `343b1bb5…a3a11`；两侧比分同为 0:0。
- 报告内等价字段：`equivalence.consumptionSequenceMatches/eventFingerprintMatches/scoresMatch = true`，`liveLedgerSha256 == replayLedgerSha256 = 2644a97e0af7b3f4…`。

### ② 桥单测 — 通过

`LiveVisionBridgeTests.cs`（25 项）：空流→`no_frame`且不释放、首帧前→`no_frame`后锚定 SimT 0、会话首帧=SimT 0、stale 窗口在 `maxAgeMs` 处闭合、窗内最新帧 + 故障映射显式原因码、越界选中→`no_selection`、重复调用同帧（相机缓存语义）、按 role 独立记账；RNG-free/不读 Target 纪律由"抽流对照指纹必须不同"的既有反证模式覆盖（`VisionReplayAdapterTests.cs:273-288`）。`ExternalProcessStreamSourceTests.cs`（7 项）：非阻塞、进程退出后 stale 且有 reason 不抛异常、坏行记流故障、空负载成帧故障但流继续、Dispose 回收残留进程、不可启动命令显式失败。

### ③ sidecar 复现测试 — 通过

- 测试面：`VisionStreamEquivalenceTests.cs:342` `LiveSession_SidecarReplaysIdenticallyAndStaysConsumable`（含 `served >= 10` 的非空洞下界）、`:420` `Sidecar_RefusesUnanchoredMixedOrUnmappedFrames`（丢锚点/混会话/映射缺失必须被拒）。
- **本次实测**：`vision evaluate --evidence <stub 场次 sidecar>` 两次复跑，除 `generatedAt` 外逐位一致（`contentSha256` 相等，`policyFingerprint=343b1bb593af07a4…`，`classifyCalls=2`，比分 0:0，exit 0）。
- 包内容与审计语义（实测读包）：`frames.jsonl` 18 帧（会话帧前缀，含锚点 `timestampMs=1786931530037`）、`import-report.json` 的 `limitations[0]` 明写"包内为本场次源实际交付的帧流（源会话帧的前缀）……`files[0]` 统计描述源会话的全量解析结果，未被交付的帧不在包内"——即 design 决策③ 要求的"锚点必留、不裁剪不补帧"落地，且把 101/18 的口径差写进包内而非隐藏。

### ④ 录制门禁（VisionMode=liveBridge）— 通过

`ReplayVisionGateTests.cs`（6 项）：`LiveBridgeSession_WritesItsOwnVisionMode`、`EnsureRecordable_RefusesOnlyLiveBridge`（Theory 三态）、`CreateForReplay_RefusesLiveBridgeSessionWithSidecarGuidance`、`ParityCheck_FailsLiveBridgeSessionWithSidecarGuidance`、`CreateForReplay_StillAcceptsDefaultAndReplaySessions`（`visionReplay` 与默认路径不误伤）。拒绝信息指路 sidecar + `vision evaluate`。

### ⑤ 全量测试零回归 — 通过

- **本次实测**：`"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1` → `已通过! - 失败: 0，通过: 496，已跳过: 1，总计: 497，持续时间: 18 s`。
- 基线（本任务开工前实测）421 通过 + 1 Skip；批间计数（批次回执）：批1 448+1、批2 463+1、批3 474+1、批4 483+1、批5 494+1 → 终验 496+1（评审修复新增 2 例）。
- **本次实测**：`replay-check replays/seed-42.json` → `scores 4:49 (expected 4:49) events 752/752` + `PASS: replay reproduces the recorded match bit-for-bit.`
- **本次实测**：`git diff --check` 无输出（干净）；`src/Sim.Core/MatchEngine.cs:43` 仍为 `sim-core-1.0.4`（CoreVersion 未 bump，符合 additive 约束）。

### ⑥ Godot 构建零错误 — 通过

**本次实测**：`"$DOTNET_ROOT/dotnet.exe" build godot/GodotSim.csproj -m:1` → `已成功生成。0 个警告 0 个错误`（4.97 s）。

### ⑦ py 自测 — 通过

**本次实测**：`py -3.12 tools/yolo-bridge/selftest.py` → `ALL YOLO BRIDGE SELFTESTS PASSED`，6 项：fixture 归一化（101 帧/13 目标）、JSONL 契约 + 逐帧 flush/节奏（帧间隔 219 ms）、重收组折叠（245 帧/26 重收）、坏输入→exit 1 零 stdout 带原因、真权重模式→exit 3 占位（无静默 stub 回退）、全流发射 + stderr 元数据。

### ⑧ 协议冒烟 — 通过

**本次实测**（stub 子进程推 JSONL → 桥消费 → sidecar 重放一致）：

```
dotnet run --project src/Sim.Cli -- vision live \
  --process "py -3.12 tools/yolo-bridge/mbri_yolo_bridge.py --stub src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv" \
  --realtime 1x --scenario scenarios/wushu-ring-2026.json --max-age-ms 500 --out <tmp> --force
```

实测输出：`源=yolo-bridge-process kind=process 帧=18 时长=3.197s 交付=18 被服务帧=2 未服务=16`、`进程: state=exited exitCode=0 故障=0(坏行 0) realtime=1`、`等价: 同帧数据经 sidecar 重读后消费序列、事件指纹与比分逐位一致 (live 2 / replay 2 条; 台账 2644a97e… vs 2644a97e…)`、`sidecar evidenceId=vr-51cbb0179ab08c82`、墙钟 `2m4.073s`、exit 0。sidecar 与批 5 产物 `vr-51cbb0179ab08c82` 同 id ⇒ 跨次运行确定性一致。CI 侧另有 `src/Sim.Tests/VisionProcessLiveCommandTests.cs:161` `ProcessSource_StubSubprocess_FullChain_SidecarReplayEquivalence`。

## 与设计的偏差与实证

1. **接口取双方法**：`IVisionStreamSource` 落为 `PumpUntil(double simTimeSeconds)` + `Released`（implement.md 的签名），而非单方法接口；桥在 `Classify` 内惰性 pump。语义与 design 决策④ 一致。
2. **sidecar 录"交付前缀"而非全量**：包内是"到末次 classify 为止源已交付的帧"（hunt 场次 18/101），不是整会话也不是"只录被服务帧"；锚点与会话首帧保留 ⇒ 复现成立。design 决策③ 的"只录被服务帧会平移基准"论证在测试中显式反证（`Sidecar_RefusesUnanchoredMixedOrUnmappedFrames` 拦下丢锚点）。
3. **基线 diff 口径实测**：`live 0:0 / ticks=2400 / 事件 22` vs `baseline(classifyRate=100) 4:49 / ticks=2400 / 事件 752`；diff 为 `(基线−live)`：比分 +4:+49、ticks 0、事件 +730。fixture 流仅 18.2 s（比赛 120 s），后段全 stale——这是如实结果，报告 `limitations` 已写"源时长短于比赛时长 ⇒ 其后 classify 按 stale 计入 unknown，不静默造帧"。
4. **hunt 会话只 classify 2 次是会话/场景的固有性质，不是桥引入的**：既有离线 `vision evaluate --session hunt_drive…`（本任务未改语义）同样 `classify调用=2(消费 2/unknown 2)`、比分 0:0、同一指纹；因此验收口的"非空洞"下界按会话分档（hunt=2、good_recheck=10，后者 82 次 classify、buff=1/debuff=1、交付 245/245），真实检测交付由 `good_recheck` 与 sidecar 用例覆盖。
5. **选帧共享化**：`VisionFrameSelector` 抽出后 `VisionReplayAdapter` 行为逐位不变，由既有 `VisionReplayAdapterTests` / `VisionReplayEvaluateTests`（E2E 指纹）与本次 replay-check 兜住。

## 独立评审与修复

两轮独立评审的 high/medium 缺陷**已修复并复测**（批次回执）；报告侧同步的验证为：全量 496+1 零失败、replay-check PASS、Godot 0/0、等价三方同指纹。
**本轮未修复 low 项 18 条**——批次回执只给出数量（"详见 findings"），本次材料未含其逐条清单，故无法在报告内复述其内容（据实披露，不作猜测）。

## 未完成与残余（必读）

1. **spec 未更新（唯一未完成项）**：design/implement 批 6 第 1 条要求更新 `.trellis/spec/sim/vision-replay-contract.md`（新增 live 桥条目 + 按决策①收窄"Sim.VisionReplay 仅引 Sim.Protocol"与"Sim.Core 零 IO"两条纪律）。**本次实测**：该文件无 `LiveVisionBridge`/`CsvStreamSource`/`IVisionStreamSource`/`vision live` 任何字样，`git status --porcelain .trellis/spec/` 为空 ⇒ 未改。代码已按 design 决策①落地（`Sim.VisionReplay` 已引用 Sim.Core），**spec 与实现现已不一致，需补**。
2. **真推理端到端未跑**（需用户环境）：真权重/相机/树莓派不在仓库（`D:/project/robocup/2026/MBri/rpi-yolo-pi4-int8-lto-8fps/` 的 `model.ncnn.bin|param` 按约定不入库）；仓库内只验证了接口 + 契约 + `--stub` 协议链路，`mbri_yolo_bridge.py` 真权重模式为 exit 3 占位 + README 接入点说明。
3. **桌面三源切换的人工目检未做**（需真实窗口）：设置页仅到构建级验证；批 4 实测 `godot --headless --path godot -- --settings-smoke` 因环境缺编辑器生成的运行配置（GodotSharp/runtimeconfig）在引擎初始化即崩溃，未能跑通。
4. **`Main.cs` 的 Godot 侧装配无自动测试**：`Main.cs`/`SettingsPanel.cs` 依赖 Godot 不进 Sim.Tests；无 Godot 侧装配逻辑只能靠构建 + 目检。`MatchSession`/`DesktopLiveDriver`/`DesktopSettings` 已被 Sim.Tests 链接并覆盖（`VisionSourceWiringTests` 3 项 + `DesktopSettingsTests`）。
5. **RL 训练链路未接视觉 = 计划边界**，不是缺口（本次未改 `controllers/`、`RlEnvCommand.cs`，`git status` 实测无改动）。
6. **既有性能断言偶发**：`TrainingResetPerformanceTests.HotResetP95_IsAtMostHalfOfColdCompileP95`（MuJoCo 冷/热 p95 比值）在批 5 时段记录 8 连跑 4 次失败、单独跑 3 次全过；本次全量单次运行通过。断言未被弱化、未改动。
7. **桌面预检失败策略是有意的**：源不可用（证据包缺文件/哈希不符/CSV 不可用）时 Main 会 `GD.PrintErr` + HUD 红色告警并回退默认 `classifyRate`，设置本身仍保存；`visionReplay` 的默认会话沿用 CLI 的 `files[0].path` 规则，桌面不能选会话。
8. **仓库卫生（本次）**：任务期间仓库根曾出现由 Windows 重定向误建的 `nul` 文件（我的一次 scratch 命令产生），已删除；`AGENTS.md` 的 2 行改动（"思考链也要用中文"）在本任务开工前即已存在（会话初始 git status 即为 `M AGENTS.md`），非本任务产物，未触碰。
9. **无 git 操作**：本次未执行任何 `git add`/`commit`；主会话将按批 1-6 分批提交。

## 后续建议

1. 补 `.trellis/spec/sim/vision-replay-contract.md`（残余 1），把 live 桥契约（`IVisionStreamSource`/`LiveVisionBridge`/`vision live`/sidecar/`VisionMode=liveBridge` 门禁）与两条纪律修订写进去，避免 spec 与实现长期分叉。
2. 用户环境跑一次真推理端到端（真权重对真车录像/相机，`--process` + `--realtime 1x`），把报告与 sidecar 与 stub 结果对照，验证 `--lead-ms` 补偿与迟到边界帧（报告 `equivalence.firstDivergence`）。
3. 桌面人工目检三源切换（F5 重置、设置页切源、HUD 告警路径），并把 `settings-smoke` 所需的 Godot 运行配置纳入环境准备步骤。
4. 若要提高 live 场次的可评估性：用更长的真车视觉录像（覆盖整个 120 s），否则 fixture 级短流的 stale 主导会压掉行为差异（本报告已如实呈现）。
