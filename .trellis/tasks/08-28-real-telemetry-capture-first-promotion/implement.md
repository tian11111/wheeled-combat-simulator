# 实施清单:真机遥测采集与首轮保真度晋升

> 2026-09-25:开放决策已确认为**混合式(先盘点再补)**,设计见
> [design.md](./design.md)。本轮(阶段 0-1)只做离线准备;真实采集与晋升
> (阶段 2-5)需要真机/真实日志,按清单顺序执行。每项以验证收尾。

## 阶段 0:离线准备(无需真机)

- [ ] 工具链自检①: `dotnet run --project src/Sim.Cli -- calibrate --input src/Sim.Tests/fixtures/telemetry-synthetic-v1.json --out calibration/selfcheck-synthetic.json` —— 报告正常产出,且合成来源被正确拒绝晋升(eligibility 全 false,理由含 `capture.source != real`)。
- [ ] 工具链自检②: `dotnet run --project src/Sim.Cli -- sensor-calibration import --data-dir src/Sim.Tests/fixtures/mbri-mini --manifest <fixture 内 selection manifest> --out calibration/selfcheck-sensor.json` —— exit 0,`fidelity.json` 逐字节不变。
- [ ] 写 `evidence/experiment-plan.md`:把 design.md §3 六类采集卡具体化为本轮方案(试验编号、速度/角度桶、安全注意、预估用时)。
- [ ] 写 `evidence/field-mapping.md` 骨架 + `evidence/field-mapping.json` 空表(待真实日志样本填充);`evidence/commands.md`、`evidence/review.md`、`evidence/regression.md`、`evidence/gaps.md` 模板。
- [ ] 回归确认:全套 `dotnet test`、`replay-check replays/seed-42.json`。
- [ ] 提交规划工件与脚手架;任务保持 `planning`。

## 阶段 1:日志盘点(需一份真实车端日志样本)

- [ ] 取得真实日志样本,逐字段对照 §3 采集卡需求评估(字段/频率/单位/时间轴)。
- [ ] `evidence/field-mapping.md` 填写:每个试验类型标 **够(可归一化)** / **缺(需最小采集辅助)** / **受影响参数**;形成 R3 要求的字段缺口报告。
- [ ] 按盘点结论修正 design.md §1/§2(转换器输入格式、路径②的具体范围)。
- [ ] 用户评审盘点结论(实质上是 PRD Open Decision 的最终落地确认)→ `task.py start` 激活任务(换新分支)。

## 阶段 2:归一化与采集(需真机)

- [ ] 实现归一化转换器(设计见 design.md §2):确定性、只整理不推断、带 SHA-256/版本记录;附最小往返测试。
- [ ] 按采集卡执行六类试验;每类当场记录 `telemetry/data/<vehicle>-<date>/raw-hash.tsv`。
- [ ] 缺口试验走路径②最小采集辅助;人工记录按 field-mapping 归一化。
- [ ] 归一化产物过 `Telemetry.Validate`;无效 trial 列出/修正/剔除并留痕,禁止静默补值。

## 阶段 3:首轮标定

- [ ] `calibrate --input <telemetry-v1> --vehicle-id <id> --base-scenario scenarios/wushu-ring-2026.json --out calibration/<vehicle>-<date>.json --emit-scenario scenarios/calibrated-<vehicle>.json`。
- [ ] 核对报告:各参数样本数/拟合/独立 holdout/覆盖/资格/失败原因逐项齐全;mount 仅验证(>10% 或覆盖不足 → 如实记"当前边界未获真机验证"或"模型不足")。

## 阶段 4:人工审核与选择性晋升

- [ ] `evidence/review.md`:原始证据、异常值处理、候选参数逐项结论。
- [ ] 仅对 eligible 子系统 `calibrate ... --update-fidelity --force`;部分晋升允许;未过项保持原等级并写入 `evidence/gaps.md`(含下一轮最小补采方案)。
- [ ] 若零项达标:任务以"模型/采集阻塞"结束,按 PRD AC3 创建后续修正任务,**不得宣称完成**。

## 阶段 5:回归与归档

- [ ] `dotnet test` 全过;`replay-check replays/seed-42.json`(及其他旧回放)逐位;Godot `--parity-check`;候选场景 smoke(`match --scenario scenarios/calibrated-<vehicle>.json --seed 42`)。
- [ ] 任一回归失败 → 不得提交晋升;修复/回退候选参数后重新生成报告。
- [ ] 提交审计证据(元数据/哈希/映射/命令/摘要/结论/回归/缺口),确认无原始/敏感数据误提交;`trellis-check` 全量复核;归档。

## 回滚点

- 阶段 2 前:纯规划,直接回退提交即可。
- 阶段 4 的 `--update-fidelity` 之前:候选参数只在报告/候选场景文件里,`fidelity.json` 未动,可直接重跑。
- `--update-fidelity` 后回归失败:回退 `fidelity.json` 提交(git revert),修复后重新走阶段 3-5。

## Stop Conditions

- 发现必须改标定算法/内核/holdout 门槛才能"通过" → 停止,那超出本任务边界(PRD Out of Scope),改为在 `gaps.md` 记录模型缺口并新立任务。
- 车端日志完全无法满足任何一类试验的最小字段/频率 → 停止阶段 1,回到开放决策重新选择采集方式。
