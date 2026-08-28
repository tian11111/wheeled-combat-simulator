# 解决 MBri 传感器三处漂移

## Goal

按批次解析灰度、前 ADC、铲子模型漂移；生成可审计结论，不自动覆盖运行时参数。

## Confirmed Evidence

- 输入报告：`calibration/mbri-summer-sensor-report.json`。
- 灰度 `near_edge_enter`：stored `0.5`，config `0.35`；原始灰度没有坐标，不能构造 `GrayGridMap`。
- 前 ADC `diff_low`：stored `-75.1`，全量重算约 `-80.5`；stored 绝对差模型与生产 ratio 模型不是同一语义。
- 铲子 `hang_enter`：stored `668.5`，重算约 `1035.6`，config `1134.1`；当前铲子回放存在失败文件，不能直接生成 runtime candidate。

## Requirements

- R1：按选择 manifest、文件哈希、采集日期/批次和模型类型分组，禁止把目录中所有 CSV 自动混合。
- R2：为每处漂移列出 stored、recomputed、config、适用批次、样本数、回放结果和不确定性。
- R3：分别判断“模型重算错误、批次混合、语义不同、数据质量不足”四类原因；不能只选择数值较新的候选。
- R4：只有在用户确认批次和语义后，才允许生成独立的 runtime profile；默认产物保持 `evidence_only` 或 `rejected`。
- R5：新增回归测试，保证同一输入的报告指纹稳定、错误数据零输出、旧 seed-42 回放不变。

## Acceptance Criteria

- [x] 三处漂移各有一条可审计结论和来源文件清单。
- [x] 铲子回放失败被解释并修复或明确拒绝，不能被隐藏。
- [x] 未确认的候选不会改动 `config.py`、`Sim.Core`、`SensorSampler` 或 FSM。
- [x] 相关测试和 seed-42 replay-check 通过。

## Out Of Scope

- 不把 MBri 灰度 CSV 直接升级为场地 `fieldGray`。
- 不在本任务内重写运行时传感器模型或重新定义传感器协议。
