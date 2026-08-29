# Technical Design — 真实视觉回放评估（Phase A：evidence_only）

## 阶段范围

Phase A 只导入现有 MBri CSV（无逐帧真值）作为 `evidence_only` 回放证据，验证
数据链与策略消费；`fidelity.json` 视觉项保持 `random_stub` 不动。新采集+人工
标注（Phase B）另立任务，本设计只预留格式与报告字段，不实现采集/标注/晋升。

## 架构与边界

```
D:/project/robocup/MBri CSV（本地，不入库）
   │  vision import（新 CLI 命令，严格审计）
   ▼
src/Sim.VisionReplay（新纯库：schema、校验、归一化、纯函数评估器）
   │  vision-replay-v1 证据包（规范化帧记录，本地忽略目录）
   │  vision-replay-report-v1（入库：哈希/映射/指标/指纹/审计）
   ▼
src/Sim.Core：VisionReplayAdapter（纯、无 IO、无随机流）经
MatchEngine 新增的可选注入点进入 FSM，替换 ClassifyRateVision
   ▼
策略消费回放（CLI 驱动）→ 报告层 3（每帧消费原因 + FSM 转移 + 指纹）
```

- `Sim.VisionReplay` 仅引用 `Sim.Protocol`（JSON 序列化复用 `ProtocolJson`），
  不引用 `Sim.Calibration`——`分线` 契约（sensor-calibration-v1 与 telemetry-v1
  互不扩用）同样适用于本视觉线：新 schema、新命令、不扩用旧管道。通用工具
  （SHA-256、枚举审计表）在库内自持，不跨线引用。
- `Sim.Core` 不获得任何 IO/时钟/进程能力：适配器由调用方用内存中的帧序列构造。
- Godot Phase A 不改动（R5 只约束"若展示则消费同一报告"，本轮无展示需求）。

## 视觉回放格式（vision-replay-v1）

入库的是 schema + 清单 + 报告 + 最小测试 fixture；原始 CSV 留在本地忽略目录。

- `VisionReplayManifest`（输入清单）：`schemaVersion:"vision-replay-v1"`、
  `source:"mbri-csv"`、选中文件列表（本地路径 + SHA-256）、类别映射
  `{good→buff, bad→debuff}`（显式字段，禁止文件名推断）、帧尺寸、时间基准说明。
  敌方机器人不是 YOLO 类别（MBri 用 IR 接近探测），`opponent` 检测在 Phase A
  不存在——映射表中 `opponent` 记为 `unavailable(ir-probe)`，报告如实说明。
- `VisionFrameRecord`（归一化帧，JSONL 每行一帧）：`sequence`（int，服务帧号）、
  `timestampMs`（epoch ms）、`receivedAgeMs`、`status`（`target|no_target|error|
  no_data_or_stale`，源 `vision_status` 原样保留）、`error`、`fps`、
  `inferenceMs`、`frameWidth/Height`、`detections[]`（`classId`、`rawType`、
  `label`（映射后 `buff|debuff`）、`confidence`、`bbox[x1,y1,x2,y2]`（像素）、
  `centerX/centerY`（像素）、`offsetX/offsetY`（归一化，(center−w/2)/(w/2)，与
  MBri `config.py` 语义一致））、`selectedTargetIndex`（int?）。
  一行 CSV 一个 detection；`detection_index>0` 的行聚合进同一帧。
- 两个已知 CSV 方言：完整 hunt 57 列（有逐检测行）可导入；简化 `main_*` 方言
  （无 bbox/detection 明细）Phase A 拒绝导入并给出原因（列缺口清单），列入
  `ignoredFiles` 而非静默降级。
- 每个证据包计算内容指纹（不含生成时间戳），报告与回放头引用
  `evidenceId + evidenceSha256`。

## 导入与校验（严格审计，对齐 sensor-calibration import 惯例）

- 只消费清单点名的文件；表头必须与已知方言精确匹配（按列名集合判定方言）；
  未点名文件列 `ignoredFiles`，被拒文件必须带原因；禁止按列名猜语义。
- 校验矩阵：`sequence` 严格递增且无重复、时间戳单调非降、数值全部有限、
  `frame_width/height` 与清单一致、`vision_status` 枚举合法、`confidence∈[0,1]`、
  bbox 在帧内且 x1<x2/y1<y2、`offset∈[-1,1]`、`class_id∈{0,1}` 且与 `target_type`
  一致、`selected_target` 至多一个。任一违规 → 整文件 `rejected`（带行号与原因），
  不静默补值、不截断。
- `label` 列是实验名不是真值：导入报告恒 `groundTruth=false`，证据分级
  `evidence_only`；禁止从 `good_*`/`bad_*` 文件名生成逐帧真值。
- 产出：规范化 JSONL + 导入报告（使用/忽略/拒绝/哈希/方言/行数），报告入库存档，
  JSONL 证据留本地。校验失败 → 非零退出、零产出（先全量预检再写盘，原子写）。

## Sim.Core 注入点与确定性

- `MatchEngine` 新增可选注入：`public MatchEngine(Scenario scenario) ` 保持不变
  （所有现存构造点零改动）；新增
  `public MatchEngine(Scenario scenario, IVisionAdapter visionAdapter)`，null 视同
  默认（内部仍构造 `ClassifyRateVision`）。注入发生在 `MatchEngine.cs:80` 一处。
- 新适配器 `VisionReplayAdapter : IVisionAdapter`（Sim.Core，紧邻 Fsm.cs）：
  - 构造入参：帧序列（按 `sequence` 排序的 `VisionFrameRecord` 只读列表）、
    证据 ID/哈希、时间基准参数；`Id => "visionReplay"`。
  - `Classify(context)`：按"当前帧 = 上次消费帧之后的第一个 `timestampMs` 落入
    `[SimT−maxAgeMs, SimT]` 的帧"推进（时间映射与 age 阈值在构造时固定，纯函数）。
    无候选帧/`error`/`no_data_or_stale` → `unknown`（`Source` 记原因码
    `stale|error|no_frame`），**绝不**读 `VisionContext.Target` 制造"正确答案"，
    **绝不**消费 `context.Random`（随机桩才吃流；回放适配器不吃，避免扰动
    Mulberry32 共享流）。
  - 每次消费登记（帧号、检测、age、消费/跳过原因）进引擎只读观测：复用
    `VisionInfo.External`（`JsonElement?`，形状对齐 `LegacyAliasTests` 的
    `roles.{us,them}.frameId/detection/ageMs`），`VisionInfo.Mode = "visionReplay"`。
- 回放头：`BuildReplayHeader` 在注入适配器时写 `VisionMode = "visionReplay"`
  （自由字符串字段，协议校验只查非空——加性使用，不破坏旧读取方）；新增
  可空加性字段 `VisionEvidenceId`/`VisionEvidenceSha256`（`ReplayHeader` 尾部追加，
  默认 null，旧 JSON 逐位兼容）。`ReplayTick` 不加字段：复现凭
  "证据文件（哈希锁定）+ 场景 + 已录动作/命令"，逐帧检测由适配器确定性重放。
- 兼容门禁：默认构造路径逐位不变——`ClassifyRateVision` 仍吃同一个 rng 流，
  现有确定性测试、`replay-check`、Godot parity 必须原样通过；旧回放
  `visionMode:"default"` 语义与指纹不变。

## 两层评估（纯函数，无时钟/IO/随机）

1. **链路质量**（无真值即可算，Phase A 主要产出）：有效/过期/错误/无数据帧率、
   sequence 缺口直方图、FPS 分布（min/p50/p95）、推理延迟分布、目标保持时长、
   `buff/debuff` 选中抖动、首次有效检测延迟（相对 session 起点）。
2. **检测质量**：Phase A 无逐帧真值 → 报告该层标 `not_run(no_ground_truth)`；
   schema 预留混淆矩阵/P/R/F1/IoU 字段，Phase B 填充。
3. **策略消费**：以固定场景（建议 `scenarios/wushu-ring-2026.json` 与一个小改动
   场景各一）构造注入引擎跑完整比赛，逐帧记录：被哪个 FSM 状态消费/跳过原因、
   FSM 获得的标准化检测、关键状态转移序列、动作/事件/报告指纹。该层只证明
   视觉→策略数据流，不冒充比赛成绩；指纹用于"同证据重放逐位一致"断言。

## CLI（新命令 `vision`，子命令风格对齐 `sensor-calibration`）

- `vision import --manifest <json> --out <report.json>`：审计导入，产报告 +
  本地证据包路径；退出码 0/1/2，校验失败零产出。
- `vision evaluate --evidence <dir> --scenario <json> [--max-age-ms <ms>] --out <report.json>`：
  链路质量 + 策略消费回放；`--json` 全量报告，默认人类可读摘要
  （中文、逐块状态行，沿用现有 CLI 风格）。
- 报告 `vision-replay-report-v1`：源文件哈希、模型哈希（来自 manifest）、
  类别映射、过滤规则、三层结果、回放指纹、异常文件清单、
  `evidence_only` 结论与 Phase B 补采/补标清单。

## 保真度门禁

- Phase A 不触碰 `fidelity.json`（测试钉住仓库文件逐位不变，沿用
  `SensorCalibrationImportTests` 的做法）；报告结论恒为
  `vision=random_stub (evidence_only)`，并输出 Phase B 最小补采/补标清单
  （含：逐帧标注工具需求、dev/holdout session 级划分规则、预固定门槛建议）。
- Phase B 晋升机制（扩展 `CalibrateCommand` 资格子系统元组或走视觉线专用命令）
  属后续任务，本设计不实现。

## 兼容与回滚

最高风险是 `MatchEngine` 注入点与 rng 流纪律。落地顺序：先纯库+导入链（零内核
接触），再注入点（默认路径逐位不变为验收门），最后评估回放。若注入引入任何
既有 fixture/parity 回归：注入点独立可回退（默认构造不受影响），导入/评估命令
独立可禁用。禁止改旧 fixture 或基线来掩盖差异。
