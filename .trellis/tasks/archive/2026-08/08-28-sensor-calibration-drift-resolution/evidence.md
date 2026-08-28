# MBri 传感器漂移收尾证据

输入：`calibration/mbri-summer-selection.manifest.json` + `D:/project/robocup/MBri/data/`。

报告指纹：`c52724365acc0283f02c864db6ea7818a246b6c3fe12e998c402b6309bfbd2da`。

## 三处漂移结论

| 模型/字段 | stored | recomputed | config | 批次/样本 | 结论 |
| --- | ---: | ---: | ---: | --- | --- |
| gray / `near_edge_enter` | 0.5 | 不可重算 | 0.35 | `mbri-20260818-gray`, `mbri-20260821-gray` / 30287 | `evidence_only`：原始 CSV 没有坐标与组标签，且跨批次，不能构造 GrayGridMap 或确认同批语义。 |
| front ADC / `diff_low` | -75.1 | -80.5 | — | `mbri-20260818-front-adc` / 7554 | `evidence_only`：重算差异超容差；stored 绝对差带与生产 ratio 判定是不同语义，不能择新覆盖。 |
| shovel / `hang_enter` | 668.5 | 1035.64 | 1134.1 | `mbri-20260818-shovel`, `mbri-20260821-shovel` / 6625 | `rejected`：跨批次且 `shovel_stage_instage.csv` 出现悬空断言，不能生成 runtime candidate。 |

铲子失败文件保留在报告中：`shovel_stage_instage.csv`（1980 行，1972 个可回放样本，原因 `stage 文件出现悬空断言`）。

## 来源与防误合并

报告为每个选中文件登记 SHA-256、角色、采集日期、批次和语义；manifest 本身也登记 SHA-256。未被 manifest 点名的 CSV 仍作为 ignored，不参与拟合。重复/漏登记批次会在导入前拒绝，防止把目录内容自动混合。

没有生成 runtime profile，也没有修改 `config.py`、`Sim.Core`、`SensorSampler` 或 FSM。`fidelity.json` 未被本任务改动。

## 验证

- 传感器聚焦测试：22/22 通过。
- 全量 .NET 测试：189/189 通过。
- `replay-check replays/godot-parity-seed42.json`：752/752 事件逐位一致。
