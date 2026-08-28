# 完成真机遥测保真度晋升

## Goal

用真实 telemetry-v1 数据完成摩擦、碰撞、堵转和登台的留出验证；仅达标项显式晋升 fidelity。

## Confirmed Facts

- `src/Sim.Calibration` 和 CLI `calibrate` 已提供拟合、留出集、登台混淆矩阵及来源指纹流程。
- 当前仓库已有合成夹具，但合成来源不能触发真实保真度晋升。
- `fidelity.json` 当前 friction/collision/stall 为 `uncalibrated`，mount 为 `hand_drawn`。

## Requirements

- R1：扫描并登记真实 telemetry-v1 输入；严格校验 SI 单位、时间单调性、位姿/速度字段、实验类型和样本量。
- R2：对摩擦、碰撞、堵转、登台分别输出 fit/holdout 指标、样本覆盖和失败原因。
- R3：仅 `source=real` 且达到现有阈值的子系统允许生成 profile 或更新 `fidelity.json`；合成数据只能验证工具链。
- R4：生成的场景/profile 必须保持官方场景和旧 seed-42 回放逐位兼容。
- R5：若没有真实物理遥测，必须以缺失字段/缺失试验类型为证据完成阻断报告，不得伪造参数或把状态改成 calibrated。

## Acceptance Criteria

- [x] 真实数据不存在时不执行晋升；若后续提供真实数据，仍按现有阈值逐子系统显式晋升。
- [x] 报告明确列出真实来源、六类试验和 fit/holdout 证据的缺失项，`fidelity.json` 保持诚实状态。
- [x] `dotnet test`、CLI replay-check、标定合成回归和报告指纹检查通过。

## Out Of Scope

- 不从 MBri 传感器 CSV 推断摩擦、碰撞、堵转或登台物理参数。
- 不为了通过门禁修改验收阈值、拆分留出集或重复使用训练样本。
