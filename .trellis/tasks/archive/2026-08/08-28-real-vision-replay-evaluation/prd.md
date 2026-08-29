# 真实视觉回放评估

## Goal

用真实相机视觉输出替代新评估流程中的 `ClassifyRateVision` 随机概率桩，建立版本化、可校验、可重复的视觉日志导入与离线回放评估链。评估必须区分“真实模型输出被成功回放”和“模型识别结果经人工真值证明有效”；只有独立真实 holdout 达标后，才允许更新 `fidelity.json` 的视觉保真度声明。

## Confirmed Baseline

- `fidelity.json` 当前将视觉标记为 `random_stub`，证据为 `ClassifyRateVision` 的确定性随机识别概率，并明确说明尚未接入真机视觉结果。
- `Sim.Core` 已有同步 `IVisionAdapter`、标准化 `VisionDetection` 和 `VisionContext` 边界，但 `MatchEngine` 当前固定构造 `ClassifyRateVision`，尚无注入真实回放适配器的入口。
- `Sim.Protocol` 的观测元数据和回放头已经允许 `external`/其他视觉模式，但 `ReplayTick` 目前不保存视觉帧或检测结果，因此仅写 `visionMode` 还不足以确定性复现真实视觉输入。
- 真机工程 `D:/project/robocup/MBri` 已有 NCNN YOLO 服务、`VisionClient` 以及真实运行 CSV。现有明细包含 sequence、时间戳、状态、FPS、推理耗时、类别、置信度、边界框、中心点和 offset，可作为首批导入兼容数据。
- 当前 MBri 目录没有原始图片或视频；CSV 中的 `label` 主要是实验名称，不是逐帧人工类别/边界框真值。因此这些日志只能证明链路行为和输出分布，不能单独计算可信的识别准确率或完成视觉保真度晋升。

## Requirements

### R1. 版本化视觉回放证据

- 定义独立的版本化视觉回放格式，记录 source、采集会话、模型名称/哈希、相机配置、时间基准、帧尺寸、sequence、时间戳、状态/错误、推理延迟、检测列表和选中目标。
- 检测至少包含标准化类别、置信度、边界框、中心点和归一化横向偏移；`good`/`bad`/敌方机器人到 `buff`/`debuff`/`opponent` 的映射必须显式、可审计。
- 若包含人工真值，真值与模型输出必须分字段保存；数据按完整采集 session 划分 development/holdout，禁止把同一连续视频的相邻帧随机拆到两侧。
- 原始图片、视频和大体积日志默认保留在本地忽略目录；仓库提交 schema、清单、SHA-256、脱敏元数据、报告和最小测试 fixture。

### R2. MBri 日志导入与严格校验

- 提供离线导入入口，将现有 MBri 视觉 CSV 规范化为回放格式，并列出使用、忽略和拒绝的文件及原因。
- 校验 sequence/时间顺序、有限数、帧尺寸、状态枚举、类别映射、置信度范围、边界框范围和重复帧；禁止静默补造缺失检测或真值。
- 旧 CSV 缺少逐帧真值时必须标记为 `evidence_only`，不得因为文件名含 `good`/`bad` 就自动生成逐帧目标真值。

### R3. 确定性真实视觉回放适配器

- 为 `MatchEngine` 增加显式视觉适配器/视觉模式注入边界，真实回放模式按已记录的 sequence/时间确定性提供检测。
- 适配器不得读取 `VisionContext.Target` 中的模拟器目标类别或位置来制造“正确答案”；世界真值只能用于独立评估对照，不能进入模型输入。
- 缺帧、过期、错误或无目标必须返回明确的 `unknown`/故障结果；真实回放模式绝不静默回退到随机概率桩。
- 保留旧场景和旧回放的原 `ClassifyRateVision` 行为与事件指纹；新回放必须记录视觉模式、证据 ID/哈希及足够的帧输入，使同一输入重复运行得到相同检测消费顺序、动作和报告指纹。

### R4. 两层评估

- 无人工真值也可输出链路质量：有效/过期/错误率、sequence 缺口、FPS、推理延迟分布、目标保持时间、类别/目标抖动和首次有效检测延迟。
- 有人工真值时输出检测质量：按 `buff`、`debuff`、`opponent` 和 `unknown` 的混淆矩阵、precision、recall、F1、误检/漏检；存在框真值时增加 IoU 和中心/offset 误差。
- 输出策略消费结果：每帧被消费/跳过原因、FSM 获得的标准化检测、关键动作/状态转移和确定性指纹。该结果用于验证视觉到策略的数据流，不得冒充真实物理比赛成绩。
- 报告清楚区分 development 与从未用于阈值选择的 session 级 holdout，并给出每项门槛、样本覆盖、通过/失败和限制。

### R5. CLI 与审计报告

- 提供无头命令完成 import、validate、evaluate/replay，输入和输出路径显式，错误输入非零退出且不产生看似成功的报告。
- 报告记录源文件哈希、模型哈希、数据划分、类别映射、过滤规则、指标、回放指纹、异常文件和人工审核结论。
- Godot 如展示视觉回放，只消费同一报告/快照数据，不单独实现另一套分类或指标算法。

### R6. 视觉保真度晋升

- 仅回放模型自身 CSV 输出时，视觉状态保持 `random_stub` 或标记为明确的 `evidence_only`，不得宣称识别准确率已验证。
- 只有真实相机数据具备独立人工标注 holdout、覆盖目标与无目标场景、通过预先固定的质量门槛，并完成人工审核后，才允许更新 `fidelity.json`。
- 未通过项保留原状态，并生成最小补采/补标清单；不得降低门槛或把 development 数据重新命名为 holdout。

### R7. 回归与兼容

- 新增 schema、导入、校验、帧匹配、错误/过期、类别归一化、数据泄漏和确定性指纹测试。
- 现有 `.NET` 测试、seed-42 replay-check、Godot parity 继续通过；旧 fixture 不因新增视觉模式被重写。

## Out of Scope

- 本任务不训练、量化或重新导出 YOLO 模型，也不修改 MBri 的相机/电机在线控制逻辑。
- 不让 `Sim.Core` 直接持有摄像头、启动 NCNN 进程或读取原始图片；采集和模型推理仍在核心外部。
- 不用真实视觉日志推断摩擦、碰撞、堵转、登台或最终比赛得分。
- 不提交完整原始图片/视频或包含敏感设备信息的大体积日志。

## Acceptance Criteria

- [ ] 至少一批 MBri 真实视觉 CSV 可被严格导入和复验，所有源文件、映射、拒绝原因和哈希可追溯。
- [ ] `MatchEngine` 可显式使用真实视觉回放适配器；真实模式下缺失/错误帧不会调用随机桩，也不会读取模拟世界目标类别制造检测。
- [ ] 同一视觉证据与场景重复回放时，帧消费、标准化检测、策略动作/状态转移和报告指纹完全一致。
- [ ] 报告同时覆盖链路质量和策略消费结果；有人工真值时还覆盖逐类检测指标与 session 级独立 holdout。
- [ ] 未提供逐帧人工真值的现有 CSV 被诚实标记为 `evidence_only`，不会触发 `fidelity.json` 晋升。
- [ ] 只有满足真实标注 holdout 门槛的视觉配置才完成保真度晋升；失败时保持原状态并输出补采/补标计划。
- [ ] 全量自动化测试、CLI replay-check 和 Godot parity 通过，旧随机桩回放保持兼容。

## Open Decision

- ~~首轮是否包含新采集并人工标注原始相机帧/视频，还是只导入现有无逐帧真值的 CSV 作为 `evidence_only` 回放？~~
- **已确认（2026-08-29）**：采用分阶段方案。Phase A 只导入现有无逐帧真值的 MBri CSV 作为 `evidence_only` 回放，验证数据链与策略消费，不触碰 `fidelity.json`；新采集相机帧/视频并人工标注真值为 Phase B，届时才具备可信准确率与保真度晋升门禁。Phase B 的采集/标注/门槛细节在 Phase A 验收后另行细化，不得阻塞 Phase A。

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- Lightweight tasks can remain PRD-only.
- For complex tasks, add `design.md` for technical design and `implement.md` for execution planning before `task.py start`.
- 阶段决策已确认（2026-08-29）：Phase A 仅现有 CSV `evidence_only` 回放，Phase B 新采集+人工标注；本 PRD 验收标准即 Phase A 验收标准（其中"只有满足真实标注 holdout 门槛的视觉配置才完成保真度晋升"在 Phase A 体现为不晋升并输出 Phase B 补采清单）。
