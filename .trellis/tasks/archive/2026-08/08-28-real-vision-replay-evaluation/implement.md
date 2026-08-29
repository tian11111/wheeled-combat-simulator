# Implementation Plan — 真实视觉回放评估（Phase A）

1. **纯库骨架与 schema**：新建 `src/Sim.VisionReplay`（仅引用 Sim.Protocol）；
   定义 `vision-replay-v1` 清单/帧记录/导入报告与 `vision-replay-report-v1`
   记录（含 Phase B 预留字段）；实现 Sha256Hex、内容指纹（不含生成时间戳）、
   原子写出工具。校验：`dotnet build`。
2. **导入与校验链**：MBri CSV 方言识别（完整 hunt 57 列可导入；`main_*` 简化
   方言与未知表头 → 拒绝/忽略并给原因）、逐文件校验矩阵（sequence/时间戳/
   有限数/帧尺寸/状态枚举/类别映射/置信度/bbox/offset/selected_target 唯一）、
   detection 行聚合成帧、`evidence_only` 分级。零产出纪律：先全量预检再写盘。
   新增测试：合法导入、每种违规拒绝（带行号）、方言拒绝、
   `groundTruth=false` 钉住、文件名不含真值推断。
3. **Sim.Core 注入点**：`MatchEngine` 加性构造重载（scenario, IVisionAdapter），
   默认路径逐位不变；`VisionReplayAdapter`（纯、按 sequence/时间确定性选帧、
   缺帧/过期/错误 → unknown + 原因码、不读 `VisionContext.Target` 制造答案、
   不消费 `context.Random`）；`BuildPerception` 写
   `VisionInfo.Mode="visionReplay"` + `External` 消费登记；`BuildReplayHeader`
   写 `VisionMode` 与加性可空 `VisionEvidenceId/VisionEvidenceSha256`。
   新增测试：适配器选帧/过期/错误矩阵、"不吃随机流"断言（注入运行与
   逐位期望比对）、同证据同场景重放指纹一致、`ClassifyRateVision` 默认路径
   回归（既有确定性测试原样通过）。
4. **CLI `vision` 命令**：`import`/`evaluate` 子命令（对齐
   `SensorCalibrationCommand` 结构：预检→构建→原子写→中文审计行；退出码
   0/1/2）；`evaluate` 实现链路质量指标（纯函数）+ 策略消费回放
   （注入引擎跑完整比赛，逐帧消费原因 + 状态转移 + 指纹）+ 报告生成
   （`evidence_only` 结论 + Phase B 补采清单）。更新 `docs/CLI.md`。
5. **测试 fixture**：从 `D:/project/robocup/MBri/data/` 摘取 1–2 个小体积完整
   方言 CSV（截取数百行）+ `selection.manifest.json` 存入
   `src/Sim.Tests/fixtures/mbri-vision-mini/`（与 sensor 线 `mbri-mini/` 分开，
   不复用）；fixture 驱动 import→evaluate 端到端测试与指纹断言。
   新增回归：`fidelity.json` 逐位不变。
6. **收尾与文档**：`docs/ARCHITECTURE.md` 增补视觉证据分线说明；
   `docs/PORTING_NOTES.md` 如有有意差异则追加；确认 Godot 无改动；
   全量验证。

## Validation

```bash
dotnet test --no-restore
dotnet run --project src/Sim.Cli -- replay-check src/Sim.Tests/fixtures/restart-replay-seed42.json
# Godot（路径见上一任务 implement.md）
godot --headless --path godot -- --parity-check D:\project\robot-simulator\src\Sim.Tests\fixtures\godot-parity-seed42.json
godot --headless --path godot -- --edit-smoke
# 新命令端到端（本地 CSV，不提交原始文件）
dotnet run --project src/Sim.Cli -- vision import --manifest <mini-manifest> --out <tmp-report>
dotnet run --project src/Sim.Cli -- vision evaluate --evidence <tmp-evidence> --scenario scenarios/wushu-ring-2026.json --out <tmp-report>
```

- 门禁：215+ 全部测试通过；既有 replay-check/parity 逐位通过（默认视觉路径
  未受扰动）；vision import/evaluate 对损坏输入非零退出且零产出；
  同证据重放两次指纹一致；`fidelity.json` 与旧 fixture 逐位不变。
- 证据 fixture 最小化（数百行/文件），不入库原始 MBri 大文件。

## Risky files

- `src/Sim.Core/MatchEngine.cs`（ctor 重载 + BuildPerception/BuildReplayHeader）：
  rng 流纪律是最高风险，任何对 `ClassifyRateVision` 流位置的扰动都会破坏旧回放。
- `src/Sim.Core/Fsm.cs`（仅注释级改动，`Vision.Normalize` 复用，不改白名单）。
- `src/Sim.Cli/Program.cs`（新命令分派，旧命令路径不动）。
- `src/Sim.VisionReplay/*`（新库，独立可回退）。

## Rollback points

- 步骤 1–2（纯库+导入链）独立成立，失败可整段回退而不触碰内核。
- 步骤 3 注入点单独成提交：若 parity/确定性回归且无法立即修复，仅回退该提交，
  导入/评估链保留为离线审计工具。
- 步骤 4 CLI 新命令独立可禁用（分派处一行）。

## 明确不做（Phase A）

- 不实现采集/标注工具、真值评估、holdout 门槛、fidelity 晋升（Phase B 另立任务）。
- 不修改 `Sim.Calibration` / sensor-calibration 管道（分线）。
- 不让 Godot 展示视觉回放（无 UI 需求；如后续需要只消费报告/快照）。
- 不修改 `ClassifyRateVision` 语义或 `SimParameters.ClassifyRate`。
