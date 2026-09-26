# 实际执行命令记录

> 每次执行归一化/标定/回归命令后追加一条(命令 + 工具版本/提交号 + 退出码 +
> 关键输出摘要)。本表必须足以离线复现整条标定链。

| 日期 | 命令 | 工具版本/提交 | 退出码 | 结果摘要 |
|---|---|---|---|---|
| 2026-09-25 | `calibrate --input src/Sim.Tests/fixtures/telemetry-synthetic-v1.json --vehicle-id selfcheck-synthetic --out calibration/selfcheck-synthetic.json` | dotnet SDK 8.0.425 @ a8514ba | 0 | 五参数拟合+留出全过; 晋升全被拒(capture.source != real); fidelity 未动 |
| 2026-09-25 | `sensor-calibration import --data-dir src/Sim.Tests/fixtures/mbri-mini --manifest .../selection.manifest.json --out calibration/selfcheck-sensor.json` | 同上 | 0 | gray/frontAdc evidence_only, shovel rejected(带原因); fidelity.json 逐字节不变 |
