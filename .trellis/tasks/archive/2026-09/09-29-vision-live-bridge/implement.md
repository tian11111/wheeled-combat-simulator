# 执行计划

前置阅读：`prd.md`（需求与验收）、`design.md`（批 1-6 与决策①-⑦）、`.trellis/spec/sim/vision-replay-contract.md`（视觉分线契约；本任务会改写其中两条纪律，见 design 决策①）。

环境（本机实测可用，全文命令以此为准）：

```bash
export DOTNET_ROOT="$HOME/AppData/Local/Programs/robot-simulator-dotnet"   # 8.0.425
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"
py -3.12 -V    # Python 3.12.10
```

基线（**动手前先复跑一遍存底**）：

```bash
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1      # 期望: 通过 421 / 跳过 1 / 总计 422
"$DOTNET_ROOT/dotnet.exe" build godot/GodotSim.csproj -m:1              # 期望: 0 警告 0 错误
py -3.12 controllers/mbri_adapter_selftest.py                           # 期望: ALL ADAPTER SELFTESTS PASSED
python -X utf8 controllers/score_block_rl/selftest.py                   # 期望: 44 passed, 0 failed, 8 skipped
```

## 批 1：Sim.Core 桥架构与单测

1. 新增契约（建议单文件 `src/Sim.Core/VisionStream.cs`）：
   - `VisionStreamFrame`：决策④ 字段表；显式 `[JsonPropertyName]` 用真车 CSV 列名；`ToReplayFrame()` 剥掉审计字段（`t/received_age_ms/fps/inference_ms/frame_width/frame_height/class_id/target_type/bbox_*/arrival_sim_t`）→ 既有 `VisionReplayFrame`。
   - `IVisionStreamSource`：`int PumpUntil(double simTimeSeconds)` + `IReadOnlyList<VisionReplayFrame> Released { get; }`（升序）。
2. `LiveVisionBridge : IVisionAdapter`：`ModeName = "liveBridge"`；构造 `(IVisionStreamSource source, double maxAgeMs)`（`maxAgeMs` 必须正的有限值，先例 `VisionReplayAdapter.cs:117-120`）；`Classify` 先 `PumpUntil(context.T)`，再走共享选帧；`Consumes`/`LastByRole` 只读。
3. 选帧共享化（design 决策②首选）：把 `VisionReplayAdapter.cs:157-174`（窗口/stale）、`:288-307`（二分）、`:221-264`（原因码/记录）抽为 Sim.Core 内共享纯函数，两个适配器共用；抽取后参照实现行为必须不变。
4. 新增 `src/Sim.Tests/LiveVisionBridgeTests.cs`：SimT 0 对齐、stale 边界（`==maxAgeMs` 在窗 / `+ε` 出窗）、空流/单帧/重复时间戳/乱序、RNG-free 反证（照 `VisionReplayAdapterTests.cs:273-288`）、不读 Target（世界真值全错不影响输出）、`VisionMode` 默认路径不变。

验证：

```bash
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1 --filter "FullyQualifiedName~LiveVisionBridge"
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1          # 参照实现共享化不得回归
```

## 批 2：CsvStreamSource + 等价/sidecar/门禁测试

1. `src/Sim.VisionReplay/CsvStreamSource.cs`：`IVisionStreamSource` 实现；读单个 CSV 路径，复用 `MbriVisionDialect`（表头列集精确匹配）与既有归一化口径（预热行丢弃、重收帧聚合、多检测按 `detection_index` 聚合）；`PumpUntil` 按 SimT 缩放释放：到达时刻 =（`vision_timestamp_ms` − 首帧）/1000 s。
2. `src/Sim.VisionReplay` 增引 `Sim.Core`（design 决策①，spec 同步改写）；新增 CSV → `(CsvStreamSource, LiveVisionBridge)` 的装配工厂，批 3/批 4 共用。
3. sidecar 写出（`src/Sim.VisionReplay`）：`frames.jsonl` = 规范 `VisionFrameRecord`（`VisionReplayIO.SerializeFrames`），**必须含源会话首帧**（基准锚点，design 决策③）；`import-report.json` = 合法 `VisionImportReport`（`files[0].path == frames 的 session`、显式 `classMapping`、`frameWidth/Height`、`groundTruth=false`、`grade=evidence_only`、`evidenceId/Sha256`）。
4. 等价门测试（`src/Sim.Tests/VisionStreamEquivalenceTests.cs`）：同 fixture、同 `maxAgeMs=500`、同场景——
   - 路 A：`CsvStreamSource + LiveVisionBridge`；
   - 路 B：同 CSV 字节经 `VisionEvidenceBuilder`（import 路径）→ `VisionReplayAdapter`；
   - 断言两条 `Consumes` **逐条逐字段**一致（FrameSequence/AgeMs/Reason/Label/Confidence/SimT/Role），并断言 ticks/比分/事件指纹一致；
   - 再断言**非空洞**（至少 N 条 `FrameSequence != null` + 至少一条 buff/debuff）——两侧"全 stale"也会相等，必须显式排除这种假绿（实测：FSM 首次 classify 在 SimT≈3.1 s，fixture `hunt_drive` 流长 18.2 s，故前段必有被服务帧）。
5. sidecar 复现测试：live 一场 → 写包（临时目录）→ `VisionReplayAdapter` 载入重放 → 与 live 台账一致。
6. 门禁：加 header 校验助手（`Sim.Hosting`，录制与 `CreateForReplay` 共用）：`VisionMode=liveBridge` 拒绝并提示改用 sidecar；补三态测试（liveBridge 拒、visionReplay 过、default 过）。
7. fixture：`src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv`（101 帧；含三态与多检测）。测试自建临时证据目录，**不得依赖** `vision/evidence-mini` 存在（gitignored）。

验证：

```bash
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1 --filter "FullyQualifiedName~VisionStream|FullyQualifiedName~LiveVisionBridge|FullyQualifiedName~VisionReplay"
```

## 批 3：CLI `vision live` + 报告 + docs

1. `src/Sim.Cli/VisionCommand.cs` 加 `live` 子命令（`Run` 的 switch，`:32-37`）：`vision live --source <csv> --scenario <json> [--max-age-ms 500] --out <json> [--json] [--force]`；退出码 0/1/2 照现行（`:23-59`）。
2. 流程：先全量预检（CSV 存在、方言可判、场景 `Validate`、`--out` 可写），再建源+桥注入引擎跑整场（先例 `:286-291`）→ 同 seed 跑默认 `classifyRate=100` 基线场次（不注入 adapter）→ 写 sidecar → 写报告 → stdout 中文摘要（先例 `:402-410`）。
3. 报告：schema 常量 `vision-live-bridge-report-v1` 加在 `VisionReplaySchemas`；分区按 design 决策⑦（source/link/equivalence/sidecar/live/baseline/diff/grade）；指纹与原子写复用 `VisionReplayIO.Fingerprint/WriteAtomically`。
4. sidecar 默认路径 `<out 同目录>/<out 去扩展名>-sidecar/`，报告写绝对路径与 `evidenceId/Sha256`。
5. `docs/CLI.md` 补 `vision live` 节：SimT 缩放语义、sidecar 与 `vision evaluate --evidence` 的关系、基线口径（摘要级、非位对位）、退出码。
6. 测试：命令层用法校验（缺参/非法 `--max-age-ms`/输出已存在）用例 + 端到端小用例（fixture CSV 跑一场，断言报告分区与 sidecar 文件存在）。
7. 冒烟（必须真跑并留产物）：

```bash
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- vision live \
  --source src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv \
  --scenario scenarios/wushu-ring-2026.json \
  --max-age-ms 500 --out calibration/vision-live.json --force
```

期望：`calibration/vision-live.json`（报告）+ `calibration/vision-live-sidecar/`（`frames.jsonl` + `import-report.json`）；fixture 流长仅 18.2 s，比赛 120 s ⇒ 报告后段 stale 率高、`diff` 与基线差异大，属预期（如实记录，不修数据）。随后用既有命令验证包可消费：

```bash
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- vision evaluate \
  --evidence calibration/vision-live-sidecar --scenario scenarios/wushu-ring-2026.json \
  --out calibration/vision-live-sidecar-eval.json --force
```

## 批 4：桌面三源装配 + 设置页

1. `godot/src/MatchSession.cs:34-38` 与 `ResetToLive`（`:146-160`）：加 `Func<IVisionAdapter?>? visionFactory`，两处建引擎都经工厂；`godot/src/DesktopLiveDriver.cs:153` 同理（构造入参透传）。
2. `src/Sim.VisionReplay` 抽证据包加载器（`vision evaluate` 的 `:226-290` 逻辑：哈希锁定 + 帧映射），CLI 与桌面共用；抽取后 `vision evaluate` 行为不变。
3. `godot/src/DesktopSettings.cs`：`VisionSettings`（`source` 默认 `classifyRate` / `evidencePath` / `csvPath` / `maxAgeMs` 默认 500）+ `Validate` 范围校验；照 `VehicleSettings.ApplyVehicleOverrides` 的"仅显式非默认源才生效"模式。
4. `godot/src/Main.cs`：按设置构造工厂并在 `ReplaceSession`（`:1082-1087`）、`ResetLiveSession`（`:1089-1098`）、`StartLiveDriverIfConfigured`（`:1100-1110`）三处装配；默认源 ⇒ null 工厂（行为逐位不变）。
5. `godot/src/SettingsPanel.cs`：加"视觉"页（源下拉/路径/maxAge），风格照 `BuildVehiclePage`；注意 `AddLabel` 已内部 `AddChild`。
6. 测试：默认源位不变、三源工厂装配、`VisionSettings` 默认值/范围校验（`Sim.Tests` 已链接 `MatchSession.cs`/`DesktopSettings.cs`/`DesktopLiveDriver.cs`，见 `src/Sim.Tests/Sim.Tests.csproj:31-38`）。

验证：

```bash
"$DOTNET_ROOT/dotnet.exe" build godot/GodotSim.csproj -m:1     # 零错误
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1
```

## 批 5：ExternalProcessStreamSource + mbri_yolo_bridge.py + --realtime + 冒烟

1. `src/Sim.Core/ExternalProcessStreamSource.cs`：`Process.Start`（`UseShellExecute=false`，命令串拆分沿用 `src/Sim.Controller/ExternalControllerBridge.cs:42-50,164`）；stdout 逐行读 JSONL → `VisionStreamFrame`；`PumpUntil` 非阻塞取已缓冲完整行；解析失败/流断裂/进程退出 → 帧级错误或 `stale`，不抛异常；会话基准 = 首个到达帧。XML 注释写明**子进程必须逐帧 flush**。
2. `tools/yolo-bridge/mbri_yolo_bridge.py`：`--stub <csv>`（读真车 CSV 按墙钟真实节奏推帧，无权重依赖）+ 真权重模式接口与文档接入点（素材：`D:/project/robocup/2026/MBri/rpi-yolo-pi4-int8-lto-8fps/`，`model/model.ncnn.bin|param` **不进仓库**）；输出严格照决策④ JSONL 契约，逐帧 flush。
3. `tools/yolo-bridge/selftest.py`：stub 模式自测（帧节奏、字段契约、flush、异常退出路径），风格照 `controllers/mbri_adapter_selftest.py`。
4. CLI：`--process "<命令行>"` 换源；`--realtime 1x` 把步进按墙钟 1x 对齐（非 1x 明确报错）；真推理源必须 `--realtime 1x`。`docs/CLI.md` 同步。
5. 协议冒烟：Sim.Tests 用例（stub 子进程 → 桥 → sidecar 重放一致，可 CI 跑）+ 一次真实 CLI `--process` 冒烟。
6. 外部进程源的真实节奏远慢于仿真快跑，1x 冒烟会按整场时长（默认 120 s）走墙钟；测试内用短场景（在测试里构造 `matchDuration` 小的 `Scenario`），不要为此新增场景文件或 CLI 选项。

验证：

```bash
py -3.12 tools/yolo-bridge/selftest.py
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- vision live \
  --process "py -3.12 tools/yolo-bridge/mbri_yolo_bridge.py --stub src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv" \
  --realtime 1x --scenario scenarios/wushu-ring-2026.json \
  --out calibration/vision-live-process.json --force
```

## 批 6：工件与报告

1. 更新 `.trellis/spec/sim/vision-replay-contract.md`：新增 live 桥条目；按 design 决策① 收窄两条旧纪律（Sim.VisionReplay 可引 Sim.Core；"Sim.Core 零 IO"限定为仿真内核路径）。
2. 写 `report.md`：结论先行 + 验收①-⑧ 逐条证据（命令 + 实测输出）、等价门结果、sidecar 复现结果、基线 diff 数值、Godot 构建与 py 自测输出、独立评审缺陷与修复、未覆盖项披露。

## 验证矩阵（全绿才算完成）

```bash
export DOTNET_ROOT="$HOME/AppData/Local/Programs/robot-simulator-dotnet"
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"

# 1) 全量单测（基线 421 通过 + 1 Skip，零回归）
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1

# 2) legacy 身份与行为不变
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- replay-check replays/seed-42.json

# 3) Godot 构建零错误
"$DOTNET_ROOT/dotnet.exe" build godot/GodotSim.csproj -m:1

# 4) CLI 冒烟：CSV 模拟流一场 + sidecar + 既有 evaluate 复跑
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- vision live \
  --source src/Sim.Tests/fixtures/mbri-vision-mini/hunt_drive_20260817_095205.csv \
  --scenario scenarios/wushu-ring-2026.json --max-age-ms 500 \
  --out calibration/vision-live.json --force

# 5) py 自测
py -3.12 tools/yolo-bridge/selftest.py

# 6) 差异卫生
git diff --check
```

外加批 5 的 `--process` + `--realtime 1x` 冒烟（见批 5 命令）。

## 硬约束

- **RNG 纪律**：桥不调 `context.Random`、不读 `context.Target`；默认路径（不注入）rng 消费序逐位不变。
- **等价门不许弱化**：逐条逐字段断言，禁止退化为"计数相等"或"行为相似"。
- **CoreVersion 不 bump**；`ReplayHeader` 不加字段（复用既有 `VisionMode` 与 `VisionEvidenceId/Sha256`）。
- **sidecar 必含源会话首帧**（基准锚点）；证据包必须能被既有 `vision evaluate --evidence` 直接消费。
- **liveBridge 场次不得作为普通 replay 录制/复现**；拒绝信息必须指路 sidecar；不得为通过测试放宽门禁。
- **不做**：渲染帧闭环、RL 训练链路改动、FSM 阈值/决策逻辑改动、`fidelity.json` 晋升、真权重入库、旧 fixture/旧回放/基线数字的"修绿"。
- 沿用 CLI 惯例：先全量预检、再原子写；stdout 只放结果与摘要，诊断走 stderr；退出码 0/1/2。

## 交付

- 代码：批 1-5 的实现与测试（含 `tools/yolo-bridge/`）。
- 文档：`docs/CLI.md` 的 `vision live` 节、`.trellis/spec/sim/vision-replay-contract.md` 更新。
- 报告：本任务 `report.md`（含 ⑧ 项验收证据与未覆盖披露）。
- 产物（本地、gitignored，按需 `git add -f`）：`calibration/vision-live.json`、`calibration/vision-live-sidecar/`、`calibration/vision-live-process.json`。
