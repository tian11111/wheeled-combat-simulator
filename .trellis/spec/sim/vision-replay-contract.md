# 视觉回放证据契约（vision-replay-v1，Phase A evidence_only）

> 来源：任务 08-28-real-vision-replay-evaluation。真实视觉证据链与
> telemetry-v1 / sensor-calibration-v1 **分线**：新 schema（`vision-replay-v1`）、
> 新命令（`vision import|evaluate`）、新纯库（`src/Sim.VisionReplay`，仅引用
> Sim.Protocol），互不扩用。Phase A 只验证数据链与策略消费；
> `fidelity.json` 视觉项保持 `random_stub`。改动适配器、注入点、回放头
> 视觉字段或 `vision` 命令前必读本文件。

## 1. Scope / Trigger

- 触发：`VisionReplayAdapter`、`MatchEngine` 视觉注入、`ReplayHeader` 视觉
  字段、`vision` CLI 命令、vision-replay schema 的任何改动。
- 跨层契约：CSV 导入（审计）→ 证据包（哈希锁定）→ Sim.Core 纯适配器 →
  CLI 评估报告，四层语义必须一致。

## 2. Signatures

- 引擎：`new MatchEngine(Scenario)` 不变（默认路径逐位兼容）；加性
  `new MatchEngine(Scenario, IVisionAdapter?)`，null ⇒ 内部构造
  `ClassifyRateVision`（唯一注入点，MatchEngine 原硬构造行）。
- 适配器：`VisionReplayAdapter`（Sim.Core，纯、无 IO）：构造入参为按
  sequence 排序的只读帧列表、证据 ID/SHA-256、`maxAgeMs`；`ModeName =>
  "visionReplay"`（单一来源常量）；选帧 = 每 role 取时间窗
  `[SimT−maxAgeMs, SimT]` 内最新帧（相机缓存语义；与最初"游标推进"设计的
  偏差已记录在 XML 注释）。
- CLI：`vision import --manifest <json> --out <report.json>`；
  `vision evaluate --evidence <dir> --scenario <json> [--max-age-ms <ms>]
  [--session <n>] --out <report.json>`。退出码 0/1/2；校验失败零产出
  （先全量预检再原子写）。
- 回放头：加性可空 `VisionEvidenceId` / `VisionEvidenceSha256`（记录尾部
  追加、成对 64-hex 校验；null ⇒ 旧 JSON 逐位兼容）。

## 3. Contracts（不变量）

- **rng 流纪律**：回放适配器绝不消费 `context.Random`——随机桩才吃
  Mulberry32 共享流；新视觉路径不得改变既有流的消费序列，否则旧回放
  逐位破坏。
- 适配器绝不读 `VisionContext.Target`（模拟世界真值）制造"正确答案"；
  缺帧/过期/错误/无目标 → `unknown` + 原因码
  `no_frame|stale|error|no_target|no_selection`，绝不静默回退随机桩。
- 证据无逐帧真值 ⇒ 恒 `groundTruth=false`、分级 `evidence_only`；禁止从
  文件名（`good_*`/`bad_*`）推断真值。Phase A 不晋升 `fidelity.json`
  （测试钉住仓库文件逐位不变）。
- 原始 MBri CSV 不入库；fixture 只收数百行级摘录且 SHA-256 钉住；证据包
  与评估输出走 gitignored `vision/`。
- 时间映射固定 SimT 0 = 会话首帧；证据时长短于比赛 ⇒ 其余 classify 如实
  计 `stale` 并在报告 limitations 说明（不得隐藏）。
- FSM classify→buff 分支对非 BlockRuntime 目标的防护是委托 `ScoreTick`
  既有兜底（定序取台上 buff 块，无 rng），不是在渲染/FSM 层复刻规则。

## 4. Validation & Error Matrix（导入/评估）

| 条件 | 行为 |
|---|---|
| 表头与已知方言不精确匹配 | 该文件 `rejected`（列缺口清单）；未点名文件 `ignoredFiles` |
| 同 sequence 重复接收、负载逐位相同 | collapse 为 `duplicateReceives`，保留最新 age |
| 重复接收负载不一致 | 整文件拒绝（带行号） |
| confidence∉[0,1]、bbox 越界/倒置、offset∉[-1,1]、class_id/target_type 不一致、多个 selected_target | 整文件拒绝（带行号），禁止静默补值 |
| 证据哈希 ≠ 导入报告 / 报告缺 evidenceSha256 / 非法 --max-age-ms | evaluate exit 1、零产出 |

## 5. Good/Base/Bad Cases

- Good：mini fixture 导入 → 评估 → 同证据同场景重放指纹逐位一致；报告含
  链路质量 + 策略消费两层，检测质量层 `not_run(no_ground_truth)`，附
  Phase B 补采/补标清单。
- Base：损坏 CSV（哈希不符或字段违规）→ 非零退出 + 零产出。
- Bad：把 `evidence_only` 报告当识别准确率宣称；为好看放宽校验或缩短
  stale 计数；在 Core 直接读 CSV/文件。

## 6. Tests Required

- `VisionReplayImportTests`（校验矩阵、方言、哈希锁、groundTruth 钉住）、
  `VisionReplayAdapterTests`（选帧/原因码/零抽流 + 抽流反证 + 世界真值
  不影响）、`VisionReplayEvaluateTests`（E2E 指纹一致、损坏输入零产出）、
  `FidelityJson_StaysByteIdentical`，以及既有 replay-check / Godot parity
  逐位回归。fixture：`src/Sim.Tests/fixtures/mbri-vision-mini/`（与 sensor
  线 `mbri-mini/` 分开）。

## 7. Wrong vs Correct

### Wrong

- 让 Sim.VisionReplay 引用 Sim.Calibration/Sim.Core 复用工具或管道。
- 在 Sim.Core 读文件/时钟；适配器画 `context.Random`；从世界真值生成检测。
- 把 evidence_only 结果写入 fidelity.json 或宣称识别准确率。

### Correct

- 分线新库 + 纯适配器 + 内存注入 + 哈希锁定证据 + 审计报告；晋升留给
  Phase B（真值 holdout 门禁），机制届时另立任务实现。
