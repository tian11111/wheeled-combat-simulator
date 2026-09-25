# 真机遥测采集与首轮保真度晋升

## Goal

针对一台明确标识的真机，完成一轮可审计的受控实验与 `telemetry-v1` 遥测采集，使用仓库现有离线标定链生成校准报告和场景参数，并仅将通过独立 holdout 门槛的物理子系统从当前保真度等级晋升。未达标项必须保留原等级并给出证据化原因，不允许用手调参数或降低门槛制造“已标定”结果。

## Confirmed Baseline

- 仓库已经具备 `telemetry/template.telemetry-v1.json`、遥测校验、参数拟合、holdout 评估、校准报告、场景输出和 `fidelity.json` 更新能力；本任务不重写标定算法。
- 当前仓库没有可用于晋升的 `capture.source = "real"` 真机物理遥测，上一轮审计因此没有晋升摩擦、碰撞、堵转或登台。
- 当前相关等级为：摩擦、碰撞、堵转 `uncalibrated`，登台 `hand_drawn`。
- 原始真机数据放在被 Git 忽略的 `telemetry/data/`；仓库内提交可复核的任务证据、校准报告和经过人工审核的配置变化。

## Requirements

### R1. 采集对象与可追溯性

- 锁定一台真机及其机械/控制配置，记录 vehicle ID、采集日期、操作者、场地表面、电池/供电状态、轮胎/履带状态和载荷等会影响复现的条件。
- 遥测声明 `capture.source = "real"`，全部使用 SI 单位；每个 trial ID 唯一，并显式标注 `fit` 或 `holdout`，同一段数据不得同时用于拟合和验收。
- 保留原始数据文件哈希、规范化后文件哈希、标定命令和工具版本/提交号，确保结果可追踪到本轮采集。

### R2. 六类受控实验覆盖

- `lateral_coast`：用于横向摩擦，fit 与 holdout 各不少于 2 个 trial；每个有效 trial 至少 4 对相邻衰减样本，建议覆盖至少 3 个初速度。
- `angular_coast`：用于角阻尼，fit 与 holdout 各不少于 2 个 trial；每个有效 trial 至少 4 对相邻衰减样本。
- `block_push`：用于方块摩擦，fit 与 holdout 分离；每个有效 trial 至少 4 对相邻衰减样本，并覆盖多个约 0.5–2 m/s 的初速度。
- `collision`：用于恢复系数，fit 与 holdout 分离，每组至少 3 次有效碰撞，并记录碰撞法向前后速度。
- `stall`：用于堵转阈值，fit 与 holdout 各至少 6 个带正/负标签的样本，覆盖堵转与非堵转情况。
- `mount`：只验证现有登台边界，不参与拟合；holdout 至少 12 个 trial，同时包含成功/失败，覆盖至少 3 个速度×角度桶且每桶至少 2 个样本。

### R3. 数据校验与缺陷处理

- 在拟合前通过现有 `Telemetry.Validate`/CLI 校验；无效 trial 必须被列出、回到源数据修正或明确剔除，禁止静默补值。
- 若当前真机日志缺少必要字段，必须先报告字段缺口及其对应的受影响参数，不得从不相关传感器数据推断物理参数。
- 时间戳、坐标系、速度符号、碰撞法向和登台 `vn`/`vt` 定义必须与 `Sim.Protocol` 契约一致。

### R4. 首轮离线标定

- 使用现有 `dotnet run --project src/Sim.Cli -- calibrate ...` 流程生成校准报告和候选 calibrated scenario。
- 报告必须包含各参数的拟合样本数、拟合结果、独立 holdout 指标、覆盖检查、晋升资格和失败原因。
- 登台 holdout 错误率超过 10% 或覆盖不足时，保持原等级，并将结论记录为“当前边界未获真机验证”或“模型不足”，不得调整验收门槛。

### R5. 人工审核与选择性晋升

- 在执行 `--force --update-fidelity` 前人工审核原始证据、异常值处理、报告和候选参数。
- 仅对 `capture.source = "real"`、数据覆盖充分且独立 holdout 达标的子系统更新 `fidelity.json`；允许首轮只有部分子系统晋升。
- 未通过的子系统保持当前等级，并在报告中形成可直接用于下一轮采集的缺口清单。

### R6. 回归验证

- 校准后运行仓库现有自动化测试、基线 replay-check、Godot/Sim.Core parity 检查以及候选场景 smoke 测试。
- 验证失败时不得提交保真度晋升；修复或回退候选参数后重新生成报告。

### R7. 审计交付物

- 任务目录中记录：实验方案、采集元数据、数据哈希、字段映射、执行命令、校准报告摘要、人工审核结论、回归结果和未晋升项缺口。
- 原始遥测默认不提交；提交的报告或摘要不得包含敏感设备标识，但必须足以复现标定过程。

## Out of Scope

- 重写 `Sim.Core` 物理模型或替换现有标定算法。
- 为通过验收而修改 holdout 阈值、复用 fit 数据或手工覆盖校准结论。
- 将视觉灰度、视觉随机桩或规则场地图纸保真度混入本轮物理晋升。
- 是否新增真机实时采集/通信/转换程序，等待下方唯一开放决策确认。

## Acceptance Criteria

- [ ] 一台配置锁定的真机完成六类受控实验，规范化数据通过 `telemetry-v1` 校验，且 fit/holdout 无样本泄漏。
- [ ] 生成可追溯的校准报告和候选 calibrated scenario，报告逐项列出摩擦、碰撞、堵转、登台的覆盖、holdout 和资格结论。
- [ ] 至少一个物理子系统在真实 holdout 上满足现有门槛并完成保真度晋升；若没有任何一项达标，则任务不得宣称完成，而应以“模型/采集阻塞”结束并创建后续修正任务。
- [ ] 所有未达标项保持原等级，失败原因及下一轮最小补采方案已记录。
- [ ] 人工审核完成，自动化测试、replay-check、parity 与候选场景 smoke 均通过。
- [ ] 任务证据可以从已提交的元数据和哈希追溯到本轮本地原始遥测，且仓库中没有误提交原始/敏感数据。

## Open Decision

- 本任务是否包含新增真车实时采集/通信/格式转换程序，还是以现有车端日志或人工实验记录为输入，离线整理为 `telemetry-v1`？该选择会直接改变硬件集成、时间同步和验收范围。

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- Lightweight tasks can remain PRD-only.
- For complex tasks, add `design.md` for technical design and `implement.md` for execution planning before `task.py start`.
- 当前保持 `planning`；开放决策确认后再编写 `design.md` 与 `implement.md`，本阶段不修改产品代码。
